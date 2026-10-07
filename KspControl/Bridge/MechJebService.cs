using System;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// Serves mechjeb.status, flight.autopilot_status and the four flight.autopilot_* mutations on the main thread, inside the queued observation
    /// drain. A mutation either refuses with a reason and a no-effect envelope, or registers a job and answers its envelope. The long work is
    /// <see cref="AutopilotRunner"/>'s, one step per frame. Plans finish inside admission and answer a terminal envelope.
    /// </summary>
    internal sealed class MechJebService
    {
        private readonly ExecutionAuthority authority;
        private readonly AutopilotRunner runner;
        private readonly AutopilotJobs jobs;
        private readonly IMechJebPort mechjeb;
        private readonly IAutopilotFlightPort flight;
        private readonly Func<string> worldEpoch;
        private readonly Func<DateTime> utcNow;

        public MechJebService(ExecutionAuthority authority, AutopilotRunner runner, AutopilotJobs jobs, IMechJebPort mechjeb, IAutopilotFlightPort flight, Func<string> worldEpoch, Func<DateTime> utcNow = null)
        {
            this.authority = authority; this.runner = runner; this.jobs = jobs; this.mechjeb = mechjeb; this.flight = flight;
            this.worldEpoch = worldEpoch; this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public bool Running { get { return runner.Busy; } }

        public BridgeResponse Handle(BridgeRequest request)
        {
            switch (request.Operation)
            {
                case AutopilotOperations.MechJebStatus: return MechJebStatus(request);
                case AutopilotOperations.Status: return Status(request);
                case AutopilotOperations.Ascent: return Mutate(request, AutopilotKind.Ascent);
                case AutopilotOperations.ExecuteNode: return Mutate(request, AutopilotKind.ExecuteNode);
                case AutopilotOperations.PlanCircularize: return Mutate(request, AutopilotKind.PlanCircularize);
                case AutopilotOperations.PlanHohmann: return Mutate(request, AutopilotKind.PlanHohmann);
                default: return Refuse(request, ControlReasons.OperationUnavailable, null);
            }
        }

        // ---------------------------------------------------------------- reads

        private BridgeResponse MechJebStatus(BridgeRequest request)
        {
            var data = mechjeb.ReadStatus();
            try
            {
                var telemetry = flight.InFlight ? flight.Read() : null;
                data["flight"] = telemetry == null ? JValue.CreateNull() : (JToken)new JObject
                {
                    ["vesselId"] = telemetry.VesselId, ["body"] = telemetry.BodyName, ["altitudeMeters"] = Finite(telemetry.AltitudeMeters),
                    ["apoapsisMeters"] = Finite(telemetry.ApoapsisMeters), ["periapsisMeters"] = Finite(telemetry.PeriapsisMeters),
                    ["atmosphereTopMeters"] = Finite(telemetry.AtmosphereTopMeters), ["situation"] = telemetry.Situation, ["maneuverNodes"] = telemetry.ManeuverNodes
                };
            }
            catch (Exception) { data["flight"] = JValue.CreateNull(); }
            var last = jobs.Last;
            data["autopilotJob"] = last == null ? JValue.CreateNull() : (JToken)new JObject
            {
                ["requestId"] = last.RequestId, ["operation"] = AutopilotJob.OperationName(last.Kind), ["status"] = last.Status, ["phase"] = last.Phase,
                ["reasonCode"] = last.ReasonCode == null ? JValue.CreateNull() : (JToken)last.ReasonCode
            };
            data["mutationAuthority"] = "lease_and_grant_required";
            data["grantOperation"] = AutopilotOperations.Effect;
            data["readOnly"] = true;
            return new BridgeResponse { RequestId = request.RequestId, Status = "completed", WorldEpoch = worldEpoch(), Data = data };
        }

        private BridgeResponse Status(BridgeRequest request)
        {
            string requestId;
            var problem = Text(request.Arguments ?? new JObject(), "requestId", out requestId);
            if (problem != null || !OperationLimits.IsRequestId(requestId)) return Refuse(request, ControlReasons.InvalidArgument, "requestId must match [A-Za-z0-9_-]{8,128}");
            var job = jobs.Get(requestId);
            if (job == null) return Fail(request, OperationReasons.JobUnknown, new JObject { ["requestId"] = requestId, ["detail"] = "this bridge session has no such job" });
            return Envelope(request, job);
        }

        // ---------------------------------------------------------------- mutations

        private BridgeResponse Mutate(BridgeRequest request, AutopilotKind kind)
        {
            var args = request.Arguments ?? new JObject();
            string requestId;
            var problem = Text(args, "requestId", out requestId);
            if (problem == null && !OperationLimits.IsRequestId(requestId)) problem = "requestId must match [A-Za-z0-9_-]{8,128}";
            double altitude = 0, inclination = 0; bool autostage = false, all = false; string body = null;
            if (problem == null)
            {
                switch (kind)
                {
                    case AutopilotKind.Ascent: problem = ReadAscentArguments(args, out altitude, out inclination, out autostage); break;
                    case AutopilotKind.ExecuteNode: problem = Bool(args, "all", out all); break;
                    case AutopilotKind.PlanHohmann:
                        problem = Text(args, "targetBodyName", out body);
                        if (problem == null && !AutopilotLimits.IsBodyName(body)) problem = "targetBodyName must be 1.." + AutopilotLimits.BodyNameMax + " letters, digits, spaces, apostrophes, dashes or underscores";
                        break;
                }
            }
            if (problem != null) return Refuse(request, ControlReasons.InvalidArgument, problem);
            if (kind == AutopilotKind.Ascent && args["autoWarp"] != null && args["autoWarp"].Type != JTokenType.Null)
            {
                if (args["autoWarp"].Type != JTokenType.Boolean) return Refuse(request, ControlReasons.InvalidArgument, "autoWarp must be a boolean");
                if ((bool)args["autoWarp"]) return Refuse(request, AutopilotReasons.UnsupportedOption, "MechJeb 2.15 has no warp setting for the ascent autopilot, so autoWarp=true cannot be honoured; time warp is a separate, explicit action");
            }
            if (string.IsNullOrEmpty(request.LeaseId)) return Refuse(request, ControlReasons.LeaseRequired, null);
            if (!ControlLimits.IsLeaseId(request.LeaseId)) return Refuse(request, ControlReasons.LeaseInvalid, null);
            var leaseId = request.LeaseId.ToLowerInvariant();

            var fingerprint = OperationHash.Sha256Hex(request.Operation + "|" + leaseId + "|" + altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + inclination.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + "|" + autostage + "|" + all + "|" + body);
            var existing = jobs.Get(requestId);
            if (existing != null)
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)) return Refuse(request, OperationReasons.RequestIdConflict, "this requestId was used for a different request");
                return Envelope(request, existing);
            }

            // Authority: the lease, the grant's flight.autopilot family (FLIGHT facility), the vessel the lease is bound to. The recipient is that lease's entity
            // ("vessel:<guid>"); the grant's "vessel:*" matches it. The revision is whatever the authority published this frame.
            var leaseEntity = authority.DescribeLease(leaseId)?.Entity;
            var effects = new[] { new ClassifiedEffect(AutopilotOperations.Effect, leaseEntity ?? AutopilotOperations.EntityWildcard, 0) };
            ExecutionTicket ticket;
            try { ticket = authority.Admit(leaseId, authority.PublishedRevision, effects); }
            catch (InvalidOperationException error) { return RefuseAdmission(request, MapAdmission(error.Message), error.Message == "authority_unavailable" ? null : error.Message, leaseId); }

            if (runner.Busy) return Refuse(request, AutopilotReasons.AutopilotBusy, "an autopilot job is already running; stop it or wait for it to end");
            if (!flight.InFlight) return Refuse(request, AutopilotReasons.FlightUnavailable, "not in the flight scene");
            var telemetry = flight.Read();
            if (telemetry == null) return Refuse(request, AutopilotReasons.FlightUnavailable, "there is no active vessel");
            if (!string.Equals(leaseEntity, AutopilotOperations.EntityPrefix + telemetry.VesselId, StringComparison.Ordinal))
                return Refuse(request, AutopilotReasons.VesselChanged, "the lease is bound to another vessel than the active one; acquire a new lease");

            var job = new AutopilotJob
            {
                RequestId = requestId, Kind = kind, Fingerprint = fingerprint, LeaseId = leaseId, Ticket = ticket, Effects = effects, VesselId = telemetry.VesselId,
                TargetAltitudeMeters = altitude, InclinationDegrees = inclination, Autostage = autostage, All = all, TargetBodyName = body, Last = telemetry,
                CreatedUtc = utcNow(), UpdatedUtc = utcNow()
            };

            if (kind == AutopilotKind.PlanCircularize || kind == AutopilotKind.PlanHohmann) return Plan(request, job);

            var refusal = CheckMechJeb(request, job, telemetry);
            if (refusal != null) return refusal;
            jobs.Add(job);
            runner.Start(job);
            return Envelope(request, job);
        }

        private BridgeResponse CheckMechJeb(BridgeRequest request, AutopilotJob job, FlightTelemetry telemetry)
        {
            var kind = job.Kind;
            var caps = mechjeb.Capabilities;
            if (!caps.Installed) return Refuse(request, AutopilotReasons.MechJebUnavailable, "MechJeb is not installed or its core type was not found");
            if (!caps.VersionSupported) return Refuse(request, AutopilotReasons.MechJebVersionUnsupported, "installed MechJeb " + caps.Version + "; this adapter supports 2.15.x");
            if (!mechjeb.HasCore(telemetry.VesselId)) return Refuse(request, AutopilotReasons.MechJebUnavailable, "the active vessel carries no MechJeb core (part module)");
            if (kind == AutopilotKind.Ascent && !(caps.Has("ascent") && caps.Has("ascentSettings"))) return Refuse(request, AutopilotReasons.MechJebModuleUnavailable, "ascent module members were not found");
            if (kind == AutopilotKind.ExecuteNode && !caps.Has("node")) return Refuse(request, AutopilotReasons.MechJebModuleUnavailable, "node executor members were not found");
            var competitors = mechjeb.FindCompetitors(telemetry.VesselId, null);
            if (competitors.Count > 0) return Refuse(request, AutopilotReasons.CompetingController, "another controller is engaged: " + string.Join(", ", competitors), new JObject { ["competitors"] = new JArray(competitors) });
            if (kind == AutopilotKind.ExecuteNode && telemetry.ManeuverNodes <= 0) return Refuse(request, AutopilotReasons.NoManeuverNode, "the active vessel has no maneuver node");
            if (kind == AutopilotKind.Ascent && telemetry.Orbiting && telemetry.PeriapsisMeters > telemetry.OrbitFloorMeters)
                return Refuse(request, AutopilotReasons.NotApplicable, "the vessel is already in orbit around " + telemetry.BodyName);
            if (kind == AutopilotKind.Ascent && telemetry.SafeAltitudeMeters > 0 && job.TargetAltitudeMeters <= telemetry.SafeAltitudeMeters)
                return Refuse(request, ControlReasons.InvalidArgument, "targetAltitudeMeters must be above the safe orbit altitude of " + telemetry.BodyName + " (" + Math.Round(telemetry.SafeAltitudeMeters) + " m)");
            return null;
        }

        private BridgeResponse Plan(BridgeRequest request, AutopilotJob job)
        {
            if (runner.Busy) return Refuse(request, AutopilotReasons.AutopilotBusy, "an autopilot job is already running");
            PlanOutcome outcome;
            try { outcome = job.Kind == AutopilotKind.PlanCircularize ? flight.PlanCircularize() : flight.PlanHohmann(job.TargetBodyName); }
            catch (Exception) { outcome = PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "the maneuver node could not be created"); }
            jobs.Add(job);
            job.UpdatedUtc = utcNow(); job.CompletedUtc = job.UpdatedUtc; job.Phase = "done";
            if (outcome.Ok)
            {
                job.Status = JobStatuses.Completed; job.Dispatched = true; job.Plan = outcome.Data; job.EffectsApplied.Add("maneuver_node_created");
                job.Last = flight.Read() ?? job.Last;
            }
            else { job.Status = JobStatuses.Failed; job.ReasonCode = outcome.Reason; job.Detail = outcome.Detail; }
            return Envelope(request, job);
        }

        // ---------------------------------------------------------------- arguments and responses

        private static string ReadAscentArguments(JObject args, out double altitude, out double inclination, out bool autostage)
        {
            altitude = 0; inclination = 0; autostage = false;
            var a = args["targetAltitudeMeters"];
            if (a == null || a.Type != JTokenType.Integer) return "targetAltitudeMeters is required and must be an integer";
            var altitudeValue = (long)a;
            if (altitudeValue < AutopilotLimits.AltitudeMinMeters || altitudeValue > AutopilotLimits.AltitudeMaxMeters) return "targetAltitudeMeters must be " + AutopilotLimits.AltitudeMinMeters + ".." + AutopilotLimits.AltitudeMaxMeters;
            altitude = altitudeValue;
            var i = args["inclinationDegrees"];
            if (i == null || (i.Type != JTokenType.Integer && i.Type != JTokenType.Float)) return "inclinationDegrees is required and must be a number";
            inclination = (double)i;
            if (double.IsNaN(inclination) || double.IsInfinity(inclination) || inclination < AutopilotLimits.InclinationMinDegrees || inclination > AutopilotLimits.InclinationMaxDegrees)
                return "inclinationDegrees must be " + AutopilotLimits.InclinationMinDegrees + ".." + AutopilotLimits.InclinationMaxDegrees;
            return Bool(args, "autostage", out autostage);
        }

        private static string Bool(JObject args, string name, out bool value)
        {
            value = false;
            var token = args[name];
            if (token == null || token.Type != JTokenType.Boolean) return name + " is required and must be a boolean";
            value = (bool)token;
            return null;
        }

        private static string Text(JObject args, string name, out string value)
        {
            value = null;
            var token = args[name];
            if (token == null || token.Type != JTokenType.String) return name + " is required and must be a string";
            value = (string)token;
            return string.IsNullOrEmpty(value) ? name + " must not be empty" : null;
        }

        private static JToken Finite(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? JValue.CreateNull() : new JValue(value); }

        private static string MapAdmission(string code)
        {
            switch (code)
            {
                case "stale_revision": return OperationReasons.StaleRevision;
                case "editor_unavailable": return AutopilotReasons.FlightUnavailable;
                case "effect_denied_or_unknown": return OperationReasons.GrantOperationDenied;
                case "clock_regressed": return ControlReasons.ClockRegressed;
                case "authority_unavailable": return null; // resolved by the caller from the lease's own failure reason
                default: return ControlReasons.AuthorityRevoked;
            }
        }

        private BridgeResponse Refuse(BridgeRequest request, string reason, string detail, JObject extra = null)
        {
            var data = new JObject { ["phase"] = "admission", ["notDispatched"] = true, ["dispatched"] = false };
            var args = request.Arguments;
            if (args != null && args["requestId"] != null && args["requestId"].Type == JTokenType.String) data["requestId"] = (string)args["requestId"];
            if (detail != null) data["detail"] = detail;
            if (extra != null) data.Merge(extra);
            return Fail(request, reason, data);
        }

        private BridgeResponse RefuseAdmission(BridgeRequest request, string reasonOrNull, string detail, string leaseId)
        {
            return Refuse(request, reasonOrNull ?? authority.LeaseFailureReason(leaseId), detail);
        }

        private BridgeResponse Fail(BridgeRequest request, string reason, JObject data)
        {
            return new BridgeResponse { RequestId = request.RequestId, Status = JobStatuses.Failed, ReasonCode = reason, WorldEpoch = worldEpoch(), Data = data ?? new JObject() };
        }

        private BridgeResponse Envelope(BridgeRequest request, AutopilotJob job)
        {
            return new BridgeResponse
            {
                RequestId = request.RequestId, Status = job.Status, ReasonCode = job.Terminal && job.Status != JobStatuses.Completed ? job.ReasonCode : null,
                WorldEpoch = worldEpoch(), Data = job.ToEnvelope()
            };
        }
    }
}
