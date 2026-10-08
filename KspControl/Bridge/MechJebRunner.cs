using System;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// Advances the one running autopilot job a step per frame on the main thread. Every frame it revalidates the authority, the vessel and the
    /// controls before it reads MechJeb, so a Stop, a lost lease, a scene change, a vessel switch or a hand on the controls ends the job and
    /// releases MechJeb before anything else happens. Releasing is idempotent and never throws.
    /// </summary>
    internal sealed class AutopilotRunner
    {
        private readonly ExecutionAuthority authority;
        private readonly IEditorContextSource source;
        private readonly IMechJebPort mechjeb;
        private readonly IAutopilotFlightPort flight;
        private readonly Func<long> clock;
        private readonly Func<DateTime> utcNow;
        private readonly AutopilotOptions options;
        /// <summary>The flight_stage staging path (classifier, stock ActivateNextStage). Null: the ascent never fires a stage itself.</summary>
        private readonly IFlightPort staging;
        /// <summary>The recovered vessel's reads, staging, parachutes and throttle. Null: recovery jobs fail at start.</summary>
        private readonly IRecoveryVesselPort recovery;

        public AutopilotJob Current { get; private set; }
        public bool Busy { get { return Current != null && !Current.Terminal; } }

        public AutopilotRunner(ExecutionAuthority authority, IEditorContextSource source, IMechJebPort mechjeb, IAutopilotFlightPort flight, Func<long> clock, Func<DateTime> utcNow = null, AutopilotOptions options = null, IFlightPort staging = null, IRecoveryVesselPort recovery = null)
        {
            this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.mechjeb = mechjeb ?? throw new ArgumentNullException(nameof(mechjeb));
            this.flight = flight ?? throw new ArgumentNullException(nameof(flight));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.options = options ?? new AutopilotOptions();
            this.staging = staging;
            this.recovery = recovery;
        }

        /// <summary>Configures and engages MechJeb for an admitted ascent or node job. A failure ends the job and releases whatever was engaged.</summary>
        public void Start(AutopilotJob job)
        {
            Current = job; job.StartedAt = clock(); job.Phase = "engaging";
            try
            {
                if (job.Kind == AutopilotKind.Ascent)
                {
                    job.Settings = mechjeb.ConfigureAscent(job.VesselId, job.TargetAltitudeMeters, job.InclinationDegrees, job.Autostage);
                    job.Dispatched = true; job.EffectsApplied.Add("ascent_settings_written");
                    mechjeb.EngageAscent(job.VesselId, job.User);
                    job.Engaged = true; job.EffectsApplied.Add("ascent_engaged");
                    job.Phase = "ascending";
                }
                else if (job.Kind == AutopilotKind.Recover)
                {
                    if (recovery == null) throw new MechJebException(AutopilotReasons.FlightUnavailable, "the recovery port is not available");
                    // Nothing is touched here: the machine takes the attitude and the throttle on its first step, after the per-frame checks.
                    job.Recovery = new RecoveryMachine(job.VesselId, job.User, job.RecoveryRequest, mechjeb, recovery, clock, options.Recovery, job.EffectsApplied);
                    job.Dispatched = true; job.Engaged = true;
                    job.Phase = "starting";
                }
                else
                {
                    job.StartNodes = flight.Read()?.ManeuverNodes ?? 0;
                    job.Dispatched = true;
                    job.SavedAutowarp = mechjeb.EngageNode(job.VesselId, job.User, job.All);
                    job.Engaged = true; job.EffectsApplied.Add(job.All ? "node_executor_engaged_all" : "node_executor_engaged_one");
                    job.Phase = "executing_node";
                }
                job.UpdatedUtc = utcNow();
            }
            catch (MechJebException error) { Finish(job, JobStatuses.Failed, error.Code, error.Message == error.Code ? null : error.Message); }
            catch (Exception) { Finish(job, JobStatuses.Failed, AutopilotReasons.EngageFailed, "MechJeb rejected the request"); }
        }

        /// <summary>The per-frame step. Never throws.</summary>
        public void Update()
        {
            var job = Current;
            if (job == null || job.Terminal) return;
            try { Step(job); }
            catch (MechJebException error) { Finish(job, JobStatuses.Failed, error.Code, error.Message == error.Code ? null : error.Message); }
            catch (Exception) { Finish(job, JobStatuses.Failed, AutopilotReasons.EngageFailed, "an unexpected error while reading MechJeb; the module was released"); }
        }

        /// <summary>Ends the running job at once (Stop, teardown). The authority has normally been revoked already.</summary>
        public void Abort(string reason)
        {
            var job = Current;
            if (job == null || job.Terminal) return;
            Finish(job, JobStatuses.Cancelled, reason ?? AutopilotReasons.StoppedByRequest, null);
        }

        private void Step(AutopilotJob job)
        {
            job.Frames++;
            // 1. Authority first: a Stop or a lost lease must not wait for anything else.
            try { authority.ValidateForDispatch(job.Ticket, source.CurrentContext(), source.CurrentBinding(), job.Effects); }
            catch (InvalidOperationException error)
            {
                // "authority_unavailable" means the lease or grant is gone; the authority knows why (stop, takeover, expiry).
                var reason = error.Message == "authority_unavailable" ? authority.LeaseFailureReason(job.LeaseId) : Code(error.Message);
                Finish(job, JobStatuses.Cancelled, reason, null); return;
            }
            catch (Exception) { Finish(job, JobStatuses.Cancelled, "authority_unavailable", null); return; }

            // 2. The vessel and the scene.
            var telemetry = flight.InFlight ? flight.Read() : null;
            if (telemetry == null || !string.Equals(telemetry.VesselId, job.VesselId, StringComparison.Ordinal))
            {
                // A capsule destroyed in reentry or on impact makes KSP switch vessels: say so rather than "vessel changed".
                if (job.Kind == AutopilotKind.Recover && VesselGone(job)) { Finish(job, JobStatuses.Failed, AutopilotReasons.VesselLost, LostDetail(job)); return; }
                if (telemetry == null) { Finish(job, JobStatuses.Failed, AutopilotReasons.FlightUnavailable, "the flight scene or the vessel is gone"); return; }
                Finish(job, JobStatuses.Failed, AutopilotReasons.VesselChanged, "the active vessel changed"); return;
            }
            job.Last = telemetry; job.UpdatedUtc = utcNow();

            // 3. Never fight a person: a hand on the controls ends the job and starts the takeover cooldown.
            if (flight.HumanInputDetected())
            {
                if (++job.HumanFrames >= options.HumanDebounceFrames) { Takeover(job, "flight controls were used by a person"); return; }
            }
            else job.HumanFrames = 0;

            // 4. Someone else engaged a controller (a person in the MechJeb window, AtmosphereAutopilot).
            if (options.ScanEveryFrames > 0 && job.Frames % options.ScanEveryFrames == 0)
            {
                var others = mechjeb.FindCompetitors(job.VesselId, job.User, job.Kind == AutopilotKind.Ascent);
                if (others.Count > 0) { Takeover(job, "another controller engaged: " + string.Join(",", others)); return; }
            }

            if (job.Kind == AutopilotKind.Ascent) StepAscent(job, telemetry);
            else if (job.Kind == AutopilotKind.Recover) StepRecover(job);
            else StepNode(job, telemetry);
        }

        private void StepRecover(AutopilotJob job)
        {
            var reading = recovery.Read(job.VesselId);
            if (reading == null) { Finish(job, JobStatuses.Failed, AutopilotReasons.VesselLost, LostDetail(job)); return; }
            var outcome = job.Recovery.Step(reading);
            job.Phase = job.Recovery.Phase;
            switch (outcome.Verdict)
            {
                case RecoveryVerdict.Completed: Finish(job, JobStatuses.Completed, null, outcome.Detail); break;
                case RecoveryVerdict.Failed: Finish(job, JobStatuses.Failed, outcome.Reason, outcome.Detail); break;
                case RecoveryVerdict.Takeover: Takeover(job, outcome.Detail); break;
            }
        }

        private bool VesselGone(AutopilotJob job)
        {
            try { return recovery != null && recovery.Read(job.VesselId) == null; } catch (Exception) { return true; }
        }

        private static string LostDetail(AutopilotJob job)
        {
            var last = job.Recovery == null ? null : job.Recovery.Last;
            if (last == null) return "the recovered vessel no longer exists";
            return "the recovered vessel no longer exists; last seen at " + Math.Round(last.AltitudeMeters) + " m, " + last.SurfaceSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m/s surface speed";
        }

        private void StepAscent(AutopilotJob job, FlightTelemetry t)
        {
            var reading = mechjeb.ReadAscent(job.VesselId, job.User);
            job.ModuleStatus = reading.Status;
            if (reading.OtherUsers > 0) { Takeover(job, "another user engaged the ascent module"); return; }
            var now = clock();
            if (ShouldIgnite(job, t, reading, now)) Ignite(job);
            var ended = !reading.Enabled || !reading.OwnUserPresent;
            var inOrbit = t.Orbiting && t.PeriapsisMeters > t.OrbitFloorMeters;
            if (!job.OrbitReached)
            {
                if (ended)
                {
                    // MechJeb is done: judge the orbit as it is now, there is nothing left to wait for.
                    if (inOrbit) { job.OrbitReached = true; job.AscentFinished = true; Finish(job, JobStatuses.Completed, null, "periapsis is above the orbit floor and MechJeb ended its ascent"); return; }
                    // Engaged through the ascent window, an end before orbit is the Disengage button or MechJeb clearing its users at a normal end just short of the
                    // floor: indistinguishable, so it is not a takeover (no lease revocation, no cooldown). The job ends cancelled and the throttle is cut.
                    if (reading.ViaWindow) { Finish(job, JobStatuses.Cancelled, AutopilotReasons.AscentDisengaged, "the ascent module was switched off before the orbit was reached (a person pressed Disengage, or MechJeb ended)"); return; }
                    Finish(job, JobStatuses.Failed, AutopilotReasons.AscentEndedWithoutOrbit, "MechJeb ended or was switched off before periapsis cleared the orbit floor"); return;
                }
                if (inOrbit)
                {
                    if (job.OrbitSince < 0) job.OrbitSince = now;
                    else if (now - job.OrbitSince >= options.OrbitConfirmMs) { job.OrbitReached = true; job.OrbitReachedAt = now; job.Phase = "orbit_reached"; }
                }
                else job.OrbitSince = -1;
            }
            if (job.OrbitReached)
            {
                if (ended) { job.AscentFinished = true; Finish(job, JobStatuses.Completed, null, "periapsis is above the atmosphere and MechJeb ended its ascent"); }
                else if (now - job.OrbitReachedAt >= options.FinishGraceMs)
                { job.AscentFinished = false; Finish(job, JobStatuses.Completed, null, "periapsis is above the atmosphere; MechJeb was still refining the orbit and was released"); }
                return;
            }
            if (now - job.StartedAt > options.AscentTimeoutMs) { Finish(job, JobStatuses.Failed, AutopilotReasons.Timeout, "no orbit within the time allowed"); return; }
            job.Phase = t.AltitudeMeters < 1000 ? "launch" : t.AltitudeMeters < t.OrbitFloorMeters ? "ascending" : "coasting_to_circularize";
        }

        /// <summary>
        /// MechJeb's staging controller waits for the first staging while the vessel is PRELAUNCH ("Awaiting liftoff"), so an agent ascent would sit on
        /// the pad. With autostage requested the bridge fires the first stage once, after MechJeb has had a moment to take the controls.
        /// </summary>
        private bool ShouldIgnite(AutopilotJob job, FlightTelemetry t, AscentReading reading, long now)
        {
            if (job.IgniteAttempted || !job.Ignite || !job.Autostage || !reading.Enabled || !reading.OwnUserPresent) return false;
            var waiting = string.Equals(t.Situation, "PRELAUNCH", StringComparison.Ordinal)
                || (reading.Status != null && reading.Status.IndexOf("liftoff", StringComparison.OrdinalIgnoreCase) >= 0 && t.AltitudeMeters < 1000);
            return waiting && now - job.StartedAt >= options.IgniteDelayMs;
        }

        /// <summary>Fires the next stage through the same port and classifier as flight_stage. Unknown modules are reported, never a refusal.</summary>
        private void Ignite(AutopilotJob job)
        {
            var info = new JObject();
            job.Ignition = info;
            if (staging == null) { job.IgniteAttempted = true; job.IgnitedByBridge = false; info["detail"] = "staging_unavailable"; return; }
            var before = staging.Read();
            if (before == null || !before.Owned || !string.Equals(before.VesselId, job.VesselId, StringComparison.Ordinal))
            { job.IgniteAttempted = true; job.IgnitedByBridge = false; info["detail"] = "the vessel could not be read for staging"; return; }
            // A staging lock can be momentary (scene settling): try again next frame, the job keeps reporting why.
            if (staging.StagingLocked) { info["detail"] = "staging_locked; retrying"; return; }
            job.IgniteAttempted = true;
            var stage = before.Controls.CurrentStage;
            info["stageBefore"] = stage;
            if (stage <= 0) { job.IgnitedByBridge = false; info["detail"] = "no_stage_to_activate"; return; }
            var plan = FlightEffectClassifier.Merge(FlightEffectClassifier.Plan(job.VesselId, staging.PartsInStage(stage - 1), true),
                FlightEffectClassifier.Plan(job.VesselId, staging.GroupBindings("Stage"), false));
            info["consequential"] = new JArray(plan.Consequential.Take(32));
            info["unclassified"] = new JArray(plan.Unclassified.Take(32));
            if (!plan.Allowed) { job.IgnitedByBridge = false; info["detail"] = "too_many_effects"; return; }
            staging.ActivateNextStage();
            job.EffectsApplied.Add("first_stage_fired_by_bridge");
            var after = staging.Read();
            var stageAfter = after == null ? stage : after.Controls.CurrentStage;
            info["stageAfter"] = stageAfter;
            job.IgnitedByBridge = stageAfter < stage;
            if (stageAfter >= stage) info["detail"] = "the stage number did not advance";
        }

        private void StepNode(AutopilotJob job, FlightTelemetry t)
        {
            var reading = mechjeb.ReadNode(job.VesselId, job.User);
            job.NodeState = reading.State;
            if (reading.OtherUsers > 0) { Takeover(job, "another user engaged the node executor"); return; }
            if (!reading.Enabled || !reading.OwnUserPresent)
            {
                var done = job.All ? t.ManeuverNodes == 0 : t.ManeuverNodes < job.StartNodes;
                if (done) Finish(job, JobStatuses.Completed, null, job.All ? "all maneuver nodes were executed" : "one maneuver node was executed");
                else Finish(job, JobStatuses.Failed, AutopilotReasons.NodeExecutionEnded, "the node executor stopped before the node was consumed");
                return;
            }
            if (clock() - job.StartedAt > options.NodeTimeoutMs) { Finish(job, JobStatuses.Failed, AutopilotReasons.Timeout, "the node was not executed within the time allowed"); return; }
            job.Phase = string.IsNullOrEmpty(reading.State) ? "executing_node" : reading.State.ToLowerInvariant();
        }

        private void Takeover(AutopilotJob job, string detail)
        {
            try { authority.HumanTakeover(); } catch (Exception) { /* the release below must still happen */ }
            Finish(job, JobStatuses.Cancelled, OperationReasons.HumanInputDuringOperation, detail);
        }

        /// <summary>Marks the job terminal and releases MechJeb and the throttle. The order matters: release first, so a failing read afterwards cannot leave it engaged.</summary>
        private void Finish(AutopilotJob job, string status, string reason, string detail)
        {
            if (job.Terminal) return;
            Release(job, status != JobStatuses.Completed);
            job.Status = status; job.ReasonCode = reason; job.Detail = detail;
            job.Phase = "done"; job.UpdatedUtc = utcNow(); job.CompletedUtc = job.UpdatedUtc;
        }

        private void Release(AutopilotJob job, bool cutThrottle)
        {
            if (job.Kind == AutopilotKind.Recover)
            {
                // The machine owns its attitude hold and throttle: every ending (touchdown included) cuts the throttle and lets go of MechJeb.
                var burning = job.Recovery != null && job.Recovery.Throttling;
                if (job.Recovery != null) job.Recovery.Release();
                // The flight guard leaves the throttle alone when it saw the takeover first, so a job ending mid-burn also cuts the stock throttle itself.
                if (burning) { try { flight.CutThrottle(job.VesselId); job.EffectsApplied.Add("throttle_cut_stock"); } catch (Exception) { } }
                if (job.Engaged) job.EffectsApplied.Add("released");
                job.Engaged = false;
                return;
            }
            if (job.Kind != AutopilotKind.Ascent && job.Kind != AutopilotKind.ExecuteNode) return;
            if (job.Engaged || job.Dispatched)
            {
                try { if (job.Kind == AutopilotKind.Ascent) mechjeb.DisengageAscent(job.VesselId, job.User); else mechjeb.DisengageNode(job.VesselId, job.User, job.SavedAutowarp); job.EffectsApplied.Add("released"); }
                catch (Exception) { job.EffectsApplied.Add("release_failed"); }
                job.Engaged = false;
            }
            // A finished job leaves the throttle as MechJeb set it; every other ending cuts it, so no engine keeps burning unattended.
            if (!cutThrottle || (!job.Dispatched && !job.Engaged)) return;
            try { mechjeb.ThrustOff(job.VesselId); } catch (Exception) { }
            try { flight.CutThrottle(job.VesselId); job.EffectsApplied.Add("throttle_cut"); } catch (Exception) { }
        }

        /// <summary>Exception messages in this assembly are reason codes. Anything else is hidden.</summary>
        private static string Code(string message)
        {
            if (message == ControlReasons.EditorUnavailable) return AutopilotReasons.FlightUnavailable;
            if (string.IsNullOrEmpty(message) || message.Length > 64) return "authority_unavailable";
            foreach (var c in message) if (!((c >= 'a' && c <= 'z') || c == '_')) return "authority_unavailable";
            return message;
        }
    }
}
