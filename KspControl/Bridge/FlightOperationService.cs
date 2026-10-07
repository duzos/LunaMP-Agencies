using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// The flight mutations (plan: Tool contract, D. Flight). Each runs entirely inside one queued main-thread drain, so there is no gap in which
    /// the game can change between the checks and the callback: parse, lease, classify every effect, admit, check the preconditions, then for
    /// each callback revalidate immediately before it fires, dispatch, and read the state back. The envelope reports what was observed; a
    /// callback that ran but whose effect cannot be confirmed is <c>indeterminate</c> and is never replayed blindly. Terminal results are
    /// remembered by request id so a retry after a lost reply returns the same answer instead of acting twice (a toggle would otherwise flip back).
    /// </summary>
    internal sealed class FlightOperationService
    {
        private const int MaxRemembered = 64;
        private readonly IFlightPort port;
        private readonly FlightControlGuard guard;
        private readonly ExecutionAuthority authority;
        private readonly IEditorContextSource source;
        private readonly Func<string> worldEpoch;
        private readonly Func<DateTime> utcNow;
        private readonly List<Remembered> remembered = new List<Remembered>();

        private sealed class Remembered { public string RequestId, Fingerprint; public BridgeResponse Response; }

        public FlightOperationService(IFlightPort port, FlightControlGuard guard, ExecutionAuthority authority, IEditorContextSource source, Func<string> worldEpoch, Func<DateTime> utcNow = null)
        {
            this.port = port; this.guard = guard; this.authority = authority; this.source = source; this.worldEpoch = worldEpoch;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        // ---------------------------------------------------------------- parsed intents

        private sealed class Intent
        {
            public string Operation, RequestId;
            public double? Throttle, ThrottleDelta;
            public bool? Sas, Rcs, Gear, Lights, Brakes;
            public int ExpectedStage = -1;
            public string Group;
            public bool? State;
            public int RateIndex;
            public JObject Requested = new JObject();
        }

        private sealed class Run
        {
            public Intent Intent;
            public BridgeRequest Request;
            public string LeaseId, Fingerprint;
            public ExecutionTicket Ticket;
            public FlightSnapshot Before;
            public FlightEffectPlan Plan;
            public bool Dispatched, Noop;
            public readonly JArray Applied = new JArray();
            public string Status = JobStatuses.Completed, Reason, Detail;
            public JObject Extra = new JObject();
        }

        public BridgeResponse Handle(BridgeRequest request)
        {
            var args = request.Arguments ?? new JObject();
            Intent intent;
            var problem = Parse(request.Operation, args, out intent);
            if (problem != null) return Refuse(request, intent == null ? null : intent.RequestId, ControlReasons.InvalidArgument, problem, null);
            var run = new Run { Intent = intent, Request = request };
            if (string.IsNullOrEmpty(request.LeaseId)) return Refuse(request, intent.RequestId, ControlReasons.LeaseRequired, null, null);
            if (!ControlLimits.IsLeaseId(request.LeaseId)) return Refuse(request, intent.RequestId, ControlReasons.LeaseInvalid, null, null);
            run.LeaseId = request.LeaseId.ToLowerInvariant();
            run.Fingerprint = OperationHash.Sha256Hex(intent.Operation + "|" + run.LeaseId + "|" + args.ToString(Newtonsoft.Json.Formatting.None));
            var existing = remembered.FirstOrDefault(r => r.RequestId == intent.RequestId);
            if (existing != null)
            {
                if (!string.Equals(existing.Fingerprint, run.Fingerprint, StringComparison.Ordinal))
                    return Refuse(request, intent.RequestId, OperationReasons.RequestIdConflict, "this requestId was used for a different request", null);
                return Replay(request, existing.Response);
            }

            // The vessel gate: the disclosure decision belongs to the port (facade v1), the lease binds to the vessel this reports.
            if (!port.InFlight) return Refuse(request, intent.RequestId, FlightReasons.FlightUnavailable, "not in the flight scene", null);
            var snapshot = port.Read();
            if (snapshot == null) return Refuse(request, intent.RequestId, FlightReasons.FlightUnavailable, "no active vessel", null);
            if (!snapshot.Owned) return Refuse(request, intent.RequestId, FlightReasons.VesselNotOwned, null, null);
            if (!snapshot.Controllable) return Refuse(request, intent.RequestId, FlightReasons.VesselNotControllable, "the active vessel has no control source", null);
            run.Before = snapshot;

            run.Plan = Classify(intent, snapshot);
            if (!run.Plan.Allowed)
                return Refuse(request, intent.RequestId, FlightReasons.TooManyEffects, "the request would produce too many effects, so it is refused before any callback runs",
                    new JObject { ["unclassified"] = new JArray(run.Plan.Unclassified.Take(32)) });
            try { run.Ticket = authority.Admit(run.LeaseId, authority.PublishedRevision, run.Plan.Effects); }
            catch (InvalidOperationException error) { return Refuse(request, intent.RequestId, MapAdmission(error.Message, run.LeaseId), error.Message == "authority_unavailable" ? null : error.Message, null); }
            LeaseContext context;
            try { context = source.CurrentContext(); } catch (Exception) { return Refuse(request, intent.RequestId, FlightReasons.FlightUnavailable, "the scene is changing", null); }
            if (!string.Equals(context.Entity, FlightEffectClassifier.EntityOf(snapshot.VesselId), StringComparison.Ordinal))
                return Refuse(request, intent.RequestId, FlightReasons.FlightUnavailable, "the lease entity and the active vessel differ", null);

            string detail; JObject extra;
            var refusal = Precheck(intent, snapshot, out detail, out extra);
            if (refusal != null) return Refuse(request, intent.RequestId, refusal, detail, extra);

            try { Execute(run); }
            catch (Exception) { Fail(run, JobStatuses.Indeterminate, OperationReasons.OperationError, "an unexpected error stopped the request; read flight_state before retrying"); }
            return Finish(run);
        }

        // ---------------------------------------------------------------- argument parsing

        private static readonly string[] SetControlNames = { "requestId", "throttle", "throttleDelta", "sas", "rcs", "gear", "lights", "brakes" };

        private static string Parse(string operation, JObject args, out Intent intent)
        {
            intent = new Intent { Operation = operation };
            string[] allowed;
            switch (operation)
            {
                case FlightOperations.SetControls: allowed = SetControlNames; break;
                case FlightOperations.Stage: allowed = new[] { "requestId", "expectedStage" }; break;
                case FlightOperations.ActionGroup: allowed = new[] { "requestId", "group", "state" }; break;
                case FlightOperations.Abort: allowed = new[] { "requestId" }; break;
                case FlightOperations.Warp: allowed = new[] { "requestId", "rateIndex" }; break;
                default: return "unsupported operation";
            }
            foreach (var property in args.Properties())
                if (Array.IndexOf(allowed, property.Name) < 0) return property.Name + " is not an argument of " + operation;
            var id = args["requestId"];
            if (id == null || id.Type != JTokenType.String || !OperationLimits.IsRequestId((string)id)) return "requestId must match [A-Za-z0-9_-]{8,128}";
            intent.RequestId = (string)id;
            switch (operation)
            {
                case FlightOperations.SetControls:
                {
                    string error;
                    double? value;
                    if ((error = Number(args, "throttle", 0, 1, out value)) != null) return error; intent.Throttle = value;
                    if ((error = Number(args, "throttleDelta", -1, 1, out value)) != null) return error; intent.ThrottleDelta = value;
                    if (intent.Throttle.HasValue && intent.ThrottleDelta.HasValue) return "throttle and throttleDelta cannot both be given";
                    bool? flag;
                    if ((error = Flag(args, "sas", out flag)) != null) return error; intent.Sas = flag;
                    if ((error = Flag(args, "rcs", out flag)) != null) return error; intent.Rcs = flag;
                    if ((error = Flag(args, "gear", out flag)) != null) return error; intent.Gear = flag;
                    if ((error = Flag(args, "lights", out flag)) != null) return error; intent.Lights = flag;
                    if ((error = Flag(args, "brakes", out flag)) != null) return error; intent.Brakes = flag;
                    if (!intent.Throttle.HasValue && !intent.ThrottleDelta.HasValue && !intent.Sas.HasValue && !intent.Rcs.HasValue && !intent.Gear.HasValue && !intent.Lights.HasValue && !intent.Brakes.HasValue)
                        return "give at least one of throttle, throttleDelta, sas, rcs, gear, lights, brakes";
                    foreach (var property in args.Properties()) if (property.Name != "requestId") intent.Requested[property.Name] = property.Value.DeepClone();
                    return null;
                }
                case FlightOperations.Stage:
                {
                    var token = args["expectedStage"];
                    if (token == null || token.Type != JTokenType.Integer) return "expectedStage is required and must be an integer";
                    var stage = (long)token;
                    if (stage < 0 || stage > FlightLimits.MaxStageNumber) return "expectedStage must be 0.." + FlightLimits.MaxStageNumber;
                    intent.ExpectedStage = (int)stage; intent.Requested["expectedStage"] = intent.ExpectedStage;
                    return null;
                }
                case FlightOperations.ActionGroup:
                {
                    var token = args["group"];
                    if (token == null || token.Type != JTokenType.String || !FlightLimits.IsActionGroup((string)token)) return "group must be one of " + string.Join(", ", FlightLimits.ActionGroups);
                    intent.Group = (string)token; intent.Requested["group"] = intent.Group;
                    bool? flag;
                    var error = Flag(args, "state", out flag);
                    if (error != null) return error;
                    intent.State = flag;
                    intent.Requested["state"] = flag.HasValue ? (JToken)new JValue(flag.Value) : JValue.CreateNull();
                    return null;
                }
                case FlightOperations.Abort: return null;
                default:
                {
                    var token = args["rateIndex"];
                    if (token == null || token.Type != JTokenType.Integer) return "rateIndex is required and must be an integer";
                    var index = (long)token;
                    if (index < 0 || index > FlightLimits.MaxRateIndex) return "rateIndex must be 0.." + FlightLimits.MaxRateIndex;
                    intent.RateIndex = (int)index; intent.Requested["rateIndex"] = intent.RateIndex;
                    return null;
                }
            }
        }

        private static string Number(JObject args, string name, double min, double max, out double? value)
        {
            value = null;
            var token = args[name];
            if (token == null) return null;
            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) return name + " must be a number";
            var number = (double)token;
            if (double.IsNaN(number) || double.IsInfinity(number) || number < min || number > max)
                return name + " must be " + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture);
            value = number; return null;
        }

        private static string Flag(JObject args, string name, out bool? value)
        {
            value = null;
            var token = args[name];
            if (token == null) return null;
            if (token.Type != JTokenType.Boolean) return name + " must be true or false";
            value = (bool)token; return null;
        }

        // ---------------------------------------------------------------- classification and preconditions

        private FlightEffectPlan Classify(Intent intent, FlightSnapshot snapshot)
        {
            switch (intent.Operation)
            {
                case FlightOperations.Stage:
                    // KSP's CurrentStage is the last stage activated; ActivateNextStage fires the parts of CurrentStage - 1, and also toggles the Stage action group.
                    var next = FlightEffectClassifier.Plan(snapshot.VesselId, port.PartsInStage(intent.ExpectedStage - 1), true);
                    var stageGroup = FlightEffectClassifier.Plan(snapshot.VesselId, port.GroupBindings("Stage"), false);
                    return FlightEffectClassifier.Merge(next, stageGroup);
                case FlightOperations.ActionGroup:
                    return FlightEffectClassifier.Plan(snapshot.VesselId, port.GroupBindings(intent.Group), false);
                case FlightOperations.Abort:
                    return FlightEffectClassifier.Plan(snapshot.VesselId, port.GroupBindings("Abort"), false);
                case FlightOperations.SetControls:
                    var bound = new List<FlightPartAction>();
                    foreach (var group in RequestedGroups(intent)) bound.AddRange(port.GroupBindings(group.Key) ?? new FlightPartAction[0]);
                    return FlightEffectClassifier.Plan(snapshot.VesselId, bound, false);
                default:
                    return FlightEffectClassifier.Plan(snapshot.VesselId, null, false);
            }
        }

        /// <summary>The throttle a set_controls request would leave: the explicit value, or the commanded (else current) throttle plus the delta, clamped.</summary>
        private float WantedThrottle(Intent intent, FlightSnapshot s)
        {
            if (intent.Throttle.HasValue) return (float)intent.Throttle.Value;
            var basis = guard.Engaged ? guard.CommandedThrottle : (float)s.Controls.Throttle;
            if (!intent.ThrottleDelta.HasValue) return basis;
            return (float)Math.Max(0, Math.Min(1, basis + intent.ThrottleDelta.Value));
        }

        private static List<KeyValuePair<string, bool>> RequestedGroups(Intent intent)
        {
            var groups = new List<KeyValuePair<string, bool>>();
            if (intent.Sas.HasValue) groups.Add(new KeyValuePair<string, bool>("SAS", intent.Sas.Value));
            if (intent.Rcs.HasValue) groups.Add(new KeyValuePair<string, bool>("RCS", intent.Rcs.Value));
            if (intent.Gear.HasValue) groups.Add(new KeyValuePair<string, bool>("Gear", intent.Gear.Value));
            if (intent.Lights.HasValue) groups.Add(new KeyValuePair<string, bool>("Light", intent.Lights.Value));
            if (intent.Brakes.HasValue) groups.Add(new KeyValuePair<string, bool>("Brakes", intent.Brakes.Value));
            return groups;
        }

        /// <summary>Returns a refusal reason when the request cannot run against this state, else null. Also re-run immediately before each callback.</summary>
        private string Precheck(Intent intent, FlightSnapshot s, out string detail, out JObject extra)
        {
            detail = null; extra = null;
            switch (intent.Operation)
            {
                case FlightOperations.Stage:
                    if (s.Controls.CurrentStage != intent.ExpectedStage)
                    {
                        detail = "the vessel is at stage " + s.Controls.CurrentStage + ", not the expected " + intent.ExpectedStage;
                        extra = new JObject { ["currentStage"] = s.Controls.CurrentStage, ["expectedStage"] = intent.ExpectedStage };
                        return FlightReasons.StageMismatch;
                    }
                    if (s.Controls.CurrentStage <= 0) { detail = "there is no stage left to activate"; return FlightReasons.NoStageToActivate; }
                    if (port.StagingLocked) { detail = "staging is locked right now"; return FlightReasons.StagingLocked; }
                    return null;
                case FlightOperations.Warp:
                    return PrecheckWarp(intent, s, out detail, out extra);
                case FlightOperations.SetControls:
                    if (s.Warp.CurrentIndex > 0 && WantedThrottle(intent, s) > 0.001f)
                    { detail = "time warp is active; return to real time before raising the throttle"; return FlightReasons.WarpThrottleActive; }
                    return null;
                default:
                    return null;
            }
        }

        private string PrecheckWarp(Intent intent, FlightSnapshot s, out string detail, out JObject extra)
        {
            detail = null; extra = null;
            var w = s.Warp;
            if (intent.RateIndex >= w.Rates.Length)
            {
                detail = "rateIndex " + intent.RateIndex + " is not in the " + w.Mode + " rate table (0.." + (w.Rates.Length - 1) + ")";
                return ControlReasons.InvalidArgument;
            }
            var rate = w.Rates[intent.RateIndex];
            extra = new JObject { ["mode"] = w.Mode, ["requestedRate"] = rate, ["capEffectiveRate"] = FlightLimits.MaxWarpRate, ["capPhysicsRate"] = FlightLimits.MaxPhysicsWarpRate };
            if (w.Mode == "physics" ? rate > FlightLimits.MaxPhysicsWarpRate : rate > FlightLimits.MaxWarpRate)
            { detail = "the requested rate " + rate.ToString(CultureInfo.InvariantCulture) + "x is above the cap for " + w.Mode + " warp"; return FlightReasons.WarpAboveCap; }
            if (w.Mode != "physics" && w.AltitudeLimitIndex >= 0 && intent.RateIndex > w.AltitudeLimitIndex)
            { detail = "the altitude allows rate index " + w.AltitudeLimitIndex + " at most"; extra["altitudeLimitIndex"] = w.AltitudeLimitIndex; return FlightReasons.WarpAboveCap; }
            if (intent.RateIndex > 0 && (s.Controls.Throttle > 0.001 || guard.CommandedThrottle > 0.001f))
            { detail = "the throttle is above zero; cut it before warping"; return FlightReasons.WarpThrottleActive; }
            return null;
        }

        private static string MapAdmissionCode(string code)
        {
            switch (code)
            {
                case "stale_revision": return OperationReasons.StaleRevision;
                case "editor_unavailable": case FlightReasons.FlightUnavailable: return FlightReasons.FlightUnavailable;
                case "effect_denied_or_unknown": return OperationReasons.GrantOperationDenied;
                case "clock_regressed": return ControlReasons.ClockRegressed;
                case "stale_context": return OperationReasons.SceneChanged;
                case "effects_changed": return "effects_changed";
                default: return ControlReasons.AuthorityRevoked;
            }
        }

        private string MapAdmission(string code, string leaseId)
        { return code == "authority_unavailable" ? authority.LeaseFailureReason(leaseId) : MapAdmissionCode(code); }

        // ---------------------------------------------------------------- execution

        /// <summary>Rechecks the authority, the vessel, the classification and the preconditions immediately before one callback.</summary>
        private bool Revalidate(Run run)
        {
            FlightSnapshot fresh;
            try { fresh = port.Read(); } catch (Exception) { fresh = null; }
            if (!port.InFlight || fresh == null || !fresh.Owned || !string.Equals(fresh.VesselId, run.Before.VesselId, StringComparison.Ordinal))
            { Fail(run, run.Dispatched ? JobStatuses.Indeterminate : JobStatuses.Failed, OperationReasons.SceneChanged, "the active vessel changed before the callback"); return false; }
            var plan = Classify(run.Intent, fresh);
            if (!plan.Allowed)
            { Fail(run, run.Dispatched ? JobStatuses.Indeterminate : JobStatuses.Failed, FlightReasons.TooManyEffects, "the request now produces too many effects"); return false; }
            try { authority.ValidateForDispatch(run.Ticket, source.CurrentContext(), source.CurrentBinding(), plan.Effects); }
            catch (InvalidOperationException error)
            { Fail(run, run.Dispatched ? JobStatuses.Indeterminate : JobStatuses.Failed, MapAdmission(error.Message, run.LeaseId), error.Message); return false; }
            string detail; JObject extra;
            var refusal = Precheck(run.Intent, fresh, out detail, out extra);
            if (refusal != null) { Fail(run, run.Dispatched ? JobStatuses.Indeterminate : JobStatuses.Failed, refusal, detail); if (extra != null) run.Extra.Merge(extra); return false; }
            return true;
        }

        private static void Fail(Run run, string status, string reason, string detail)
        { run.Status = status; run.Reason = reason; run.Detail = detail; }

        private void Execute(Run run)
        {
            switch (run.Intent.Operation)
            {
                case FlightOperations.SetControls: SetControls(run); break;
                case FlightOperations.Stage: Stage(run); break;
                case FlightOperations.ActionGroup: ActionGroup(run); break;
                case FlightOperations.Abort: Abort(run); break;
                case FlightOperations.Warp: Warp(run); break;
            }
        }

        private void SetControls(Run run)
        {
            var intent = run.Intent;
            // The throttle goes first: if a later step fails, the one change that matters most to a burning vessel has already been made.
            if (intent.Throttle.HasValue || intent.ThrottleDelta.HasValue)
            {
                var wanted = WantedThrottle(intent, run.Before);
                if (!Revalidate(run)) return;
                run.Dispatched = true;
                if (!guard.Engage(wanted)) { Fail(run, JobStatuses.Indeterminate, "throttle_unavailable", "the fly-by-wire hook could not be installed or the lease ended"); return; }
                var observed = port.Read();
                var reading = observed == null ? double.NaN : observed.Controls.Throttle;
                run.Applied.Add("throttle=" + wanted.ToString("0.###", CultureInfo.InvariantCulture));
                if (double.IsNaN(reading) || Math.Abs(reading - wanted) > 0.011)
                { Fail(run, JobStatuses.Indeterminate, FlightReasons.NotConfirmed, "the vessel reports throttle " + (double.IsNaN(reading) ? "unknown" : reading.ToString("0.###", CultureInfo.InvariantCulture)) + " after the write"); return; }
            }
            foreach (var group in RequestedGroups(intent))
            {
                if (run.Status != JobStatuses.Completed) return;
                var current = port.GetGroup(group.Key);
                if (current == group.Value) { run.Applied.Add(group.Key + "=" + Lower(group.Value) + " (already)"); continue; }
                if (!Revalidate(run)) return;
                run.Dispatched = true;
                var observed = port.SetGroup(group.Key, group.Value);
                run.Applied.Add(group.Key + "=" + Lower(group.Value));
                if (observed != group.Value) { Fail(run, JobStatuses.Indeterminate, FlightReasons.NotConfirmed, group.Key + " reads " + Lower(observed) + " after the setter; read flight_state and do not repeat blindly"); return; }
            }
            if (!run.Dispatched) run.Noop = true;
        }

        private void Stage(Run run)
        {
            if (!Revalidate(run)) return;
            var before = run.Before.Controls.CurrentStage;
            run.Dispatched = true;
            port.ActivateNextStage();
            var after = port.Read();
            var stageAfter = after == null ? before : after.Controls.CurrentStage;
            run.Applied.Add("stage " + before + " activated");
            run.Extra["stageBefore"] = before; run.Extra["stageAfter"] = stageAfter;
            if (stageAfter >= before) Fail(run, JobStatuses.Indeterminate, FlightReasons.NotConfirmed, "the stage number did not advance; read flight_state and do not repeat blindly");
        }

        private void ActionGroup(Run run)
        {
            var intent = run.Intent;
            var current = port.GetGroup(intent.Group);
            run.Extra["stateBefore"] = current;
            if (intent.State.HasValue && intent.State.Value == current) { run.Noop = true; run.Applied.Add(intent.Group + "=" + Lower(current) + " (already)"); return; }
            if (!Revalidate(run)) return;
            run.Dispatched = true;
            var observed = intent.State.HasValue ? port.SetGroup(intent.Group, intent.State.Value) : port.ToggleGroup(intent.Group);
            run.Extra["stateAfter"] = observed;
            run.Applied.Add(intent.Group + (intent.State.HasValue ? "=" + Lower(intent.State.Value) : " toggled"));
            var confirmed = intent.State.HasValue ? observed == intent.State.Value : observed != current;
            if (!confirmed) Fail(run, JobStatuses.Indeterminate, FlightReasons.NotConfirmed, intent.Group + " reads " + Lower(observed) + " afterwards; read flight_state and do not repeat blindly");
        }

        private void Abort(Run run)
        {
            if (!Revalidate(run)) return;
            run.Dispatched = true;
            port.FireAbort();
            var fired = port.GetGroup("Abort");
            run.Applied.Add("abort fired");
            run.Extra["abortGroup"] = fired;
            if (!fired) Fail(run, JobStatuses.Indeterminate, FlightReasons.NotConfirmed, "the abort group does not read active afterwards; read flight_state");
        }

        private void Warp(Run run)
        {
            var intent = run.Intent;
            var before = run.Before.Warp;
            if (intent.RateIndex == before.CurrentIndex) { run.Noop = true; run.Applied.Add("warp index " + intent.RateIndex + " (already)"); return; }
            if (!Revalidate(run)) return;
            run.Dispatched = true;
            port.SetWarpIndex(intent.RateIndex);
            var after = port.Read();
            var index = after == null ? before.CurrentIndex : after.Warp.CurrentIndex;
            run.Extra["warpIndexBefore"] = before.CurrentIndex; run.Extra["warpIndexAfter"] = index;
            run.Applied.Add("warp index " + intent.RateIndex + " requested");
            if (index == intent.RateIndex) { guard.NoteWarp(index > 0); return; }
            if (index == before.CurrentIndex)
                Fail(run, JobStatuses.Failed, FlightReasons.WarpDenied, "the game refused the rate change (LunaMP applies the server's warp rules to the stock setter); the rate is unchanged");
            else Fail(run, JobStatuses.Indeterminate, FlightReasons.NotConfirmed, "the warp index is " + index + " after requesting " + intent.RateIndex);
        }

        private static string Lower(bool value) { return value ? "true" : "false"; }

        // ---------------------------------------------------------------- responses

        private BridgeResponse Finish(Run run)
        {
            var intent = run.Intent;
            var data = new JObject
            {
                ["operation"] = intent.Operation.Substring("flight.".Length), ["requestId"] = intent.RequestId, ["phase"] = "done",
                ["notDispatched"] = !run.Dispatched, ["dispatched"] = run.Dispatched, ["noop"] = run.Noop,
                ["entity"] = FlightEffectClassifier.EntityOf(run.Before.VesselId), ["requested"] = intent.Requested,
                ["effects"] = new JArray(run.Plan.Effects.Take(32).Select(e => (JToken)(e.Operation + " " + e.Recipient))), ["effectCount"] = run.Plan.Effects.Length,
                ["consequential"] = new JArray(run.Plan.Consequential.Take(32)), ["unclassified"] = new JArray(run.Plan.Unclassified.Take(32)), ["applied"] = run.Applied,
                ["observed"] = Observed(), ["completedUtc"] = utcNow().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            };
            if (run.Detail != null) data["detail"] = run.Detail;
            if (run.Status == JobStatuses.Indeterminate) data["reconcile"] = "read flight_state and compare it with 'requested' before any retry; this requestId stays reserved";
            data.Merge(run.Extra);
            var response = new BridgeResponse
            {
                RequestId = run.Request.RequestId, Status = run.Status, ReasonCode = run.Status == JobStatuses.Completed ? null : run.Reason,
                WorldEpoch = worldEpoch(), Revision = authority.PublishedRevision, Data = data
            };
            // Only a request that reached a callback (or completed as a no-op) is remembered: a refusal leaves the id free.
            if (run.Dispatched || run.Noop) Remember(intent.RequestId, run.Fingerprint, response);
            return response;
        }

        private JToken Observed()
        {
            FlightSnapshot s;
            try { s = port.Read(); } catch (Exception) { s = null; }
            if (s == null) return JValue.CreateNull();
            var c = s.Controls;
            return new JObject
            {
                ["throttleFraction"] = FlightStateService.Finite(c.Throttle), ["sas"] = c.Sas, ["rcs"] = c.Rcs, ["gear"] = c.Gear, ["lights"] = c.Lights, ["brakes"] = c.Brakes,
                ["stage"] = c.CurrentStage, ["warpRateIndex"] = s.Warp.CurrentIndex, ["warpEffectiveRate"] = FlightStateService.Finite(s.Warp.CurrentRate),
                ["flyByWireHeld"] = guard.Engaged
            };
        }

        private void Remember(string requestId, string fingerprint, BridgeResponse response)
        {
            remembered.Add(new Remembered { RequestId = requestId, Fingerprint = fingerprint, Response = response });
            while (remembered.Count > MaxRemembered) remembered.RemoveAt(0);
        }

        private BridgeResponse Replay(BridgeRequest request, BridgeResponse stored)
        {
            var copy = new BridgeResponse
            {
                RequestId = request.RequestId, Status = stored.Status, ReasonCode = stored.ReasonCode, WorldEpoch = worldEpoch(), Revision = authority.PublishedRevision,
                Data = (JObject)stored.Data.DeepClone()
            };
            copy.Data["replayed"] = true;
            return copy;
        }

        private BridgeResponse Refuse(BridgeRequest request, string requestId, string reason, string detail, JObject extra)
        {
            var data = new JObject { ["phase"] = "admission", ["notDispatched"] = true, ["dispatched"] = false };
            if (requestId != null) data["requestId"] = requestId;
            if (detail != null) data["detail"] = detail;
            if (extra != null) data.Merge(extra);
            return new BridgeResponse { RequestId = request.RequestId, Status = JobStatuses.Failed, ReasonCode = reason, WorldEpoch = worldEpoch(), Data = data };
        }
    }
}
