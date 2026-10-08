using System;
using KspControl.Contracts;

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
        private readonly INavigationPort navigation;

        public AutopilotJob Current { get; private set; }
        public bool Busy { get { return Current != null && !Current.Terminal; } }

        public AutopilotRunner(ExecutionAuthority authority, IEditorContextSource source, IMechJebPort mechjeb, IAutopilotFlightPort flight, Func<long> clock, Func<DateTime> utcNow = null, AutopilotOptions options = null, INavigationPort navigation = null)
        {
            this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.mechjeb = mechjeb ?? throw new ArgumentNullException(nameof(mechjeb));
            this.flight = flight ?? throw new ArgumentNullException(nameof(flight));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.options = options ?? new AutopilotOptions();
            this.navigation = navigation;
        }

        /// <summary>Configures and engages MechJeb for an admitted ascent or node job. A failure ends the job and releases whatever was engaged.</summary>
        public void Start(AutopilotJob job)
        {
            Current = job; job.StartedAt = clock(); job.Phase = "engaging";
            try
            {
                if (job.Kind == AutopilotKind.WarpTo)
                {
                    // Nothing changes yet: the first step picks the rate. The warp is the runner's from here until the job ends.
                    if (navigation == null) throw new MechJebException(ControlReasons.OperationUnavailable, "the navigation layer is not wired");
                    job.Phase = "warping";
                }
                else if (job.Kind == AutopilotKind.Ascent)
                {
                    job.Settings = mechjeb.ConfigureAscent(job.VesselId, job.TargetAltitudeMeters, job.InclinationDegrees, job.Autostage);
                    job.Dispatched = true; job.EffectsApplied.Add("ascent_settings_written");
                    mechjeb.EngageAscent(job.VesselId, job.User);
                    job.Engaged = true; job.EffectsApplied.Add("ascent_engaged");
                    job.Phase = "ascending";
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
            if (telemetry == null) { Finish(job, JobStatuses.Failed, AutopilotReasons.FlightUnavailable, "the flight scene or the vessel is gone"); return; }
            if (!string.Equals(telemetry.VesselId, job.VesselId, StringComparison.Ordinal)) { Finish(job, JobStatuses.Failed, AutopilotReasons.VesselChanged, "the active vessel changed"); return; }
            job.Last = telemetry; job.UpdatedUtc = utcNow();

            // 3. Never fight a person: a hand on the controls ends the job and starts the takeover cooldown.
            if (flight.HumanInputDetected())
            {
                if (++job.HumanFrames >= options.HumanDebounceFrames) { Takeover(job, "flight controls were used by a person"); return; }
            }
            else job.HumanFrames = 0;

            // 4. Someone else engaged a controller (a person in the MechJeb window, AtmosphereAutopilot). A warp drives no control, so it is not scanned:
            // a controller that starts burning raises the throttle, which ends the warp.
            if (job.Kind != AutopilotKind.WarpTo && options.ScanEveryFrames > 0 && job.Frames % options.ScanEveryFrames == 0)
            {
                var others = mechjeb.FindCompetitors(job.VesselId, job.User, job.Kind == AutopilotKind.Ascent);
                if (others.Count > 0) { Takeover(job, "another controller engaged: " + string.Join(",", others)); return; }
            }

            if (job.Kind == AutopilotKind.Ascent) StepAscent(job, telemetry);
            else if (job.Kind == AutopilotKind.WarpTo) StepWarp(job);
            else StepNode(job, telemetry);
        }

        private void StepAscent(AutopilotJob job, FlightTelemetry t)
        {
            var reading = mechjeb.ReadAscent(job.VesselId, job.User);
            job.ModuleStatus = reading.Status;
            if (reading.OtherUsers > 0) { Takeover(job, "another user engaged the ascent module"); return; }
            var now = clock();
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

        /// <summary>
        /// One warp step: stop at the target minus the lead, or when the throttle rises, the mode leaves rails or a person drops the warp to real time.
        /// Otherwise pick the highest rate that cannot overshoot (within the cap, the altitude limit and any refused rate) and request it through the stock setter.
        /// </summary>
        private void StepWarp(AutopilotJob job)
        {
            var w = navigation.ReadWarp();
            if (w == null) { Finish(job, JobStatuses.Failed, AutopilotReasons.FlightUnavailable, "the vessel is gone"); return; }
            if (!string.Equals(w.VesselId, job.VesselId, StringComparison.Ordinal)) { Finish(job, JobStatuses.Failed, AutopilotReasons.VesselChanged, "the active vessel changed"); return; }
            job.LastWarp = w;
            var remaining = job.StopUniversalTime - w.UniversalTime;
            if (w.Throttle > 0.001) { Finish(job, JobStatuses.Failed, FlightReasons.WarpThrottleActive, "the throttle rose above zero during the warp; the warp was stopped"); return; }
            if (!w.Rails) { Finish(job, JobStatuses.Failed, NavigationReasons.WarpModePhysics, "time warp left rails mode; the warp was stopped"); return; }
            if (!(remaining > 0))
            {
                if (w.Index > 0) { Request(job, 0, true); job.Phase = "arriving"; return; }
                job.OvershootSeconds = -remaining;
                Finish(job, JobStatuses.Completed, null, "arrived: the warp stopped at the target minus the lead");
                return;
            }
            var sinceRequest = job.Frames - job.WarpRequestedFrame;
            // A person (or the game) dropped the warp to real time after a raised rate took: theirs to decide, so the job ends without raising it again.
            if (job.WarpAchieved && w.Index == 0 && job.WarpRequested > 0 && sinceRequest > options.WarpVetoFrames)
            { Finish(job, JobStatuses.Cancelled, "warp_stopped_externally", "time warp was returned to real time by someone else; the job ended"); return; }
            if (job.WarpRequested > w.Index && sinceRequest >= options.WarpVetoFrames)
            {
                // The rate did not take: LunaMP's warp rules or the stock limits refused it. Remember the ceiling and retry later.
                job.WarpCeiling = w.Index; job.WarpCeilingFrame = job.Frames; job.WarpRequested = w.Index;
                if (w.Index == 0 && !job.WarpAchieved) { Finish(job, JobStatuses.Failed, FlightReasons.WarpDenied, "the game refused rails warp (LunaMP applies the server's warp rules to the stock setter); real time is unchanged"); return; }
            }
            if (job.WarpCeiling != int.MaxValue && job.Frames - job.WarpCeilingFrame >= options.WarpCeilingResetFrames) job.WarpCeiling = int.MaxValue;
            if (w.Index > 0 && w.Index == job.WarpRequested) job.WarpAchieved = true;
            var wanted = NavigationMath.ChooseWarpIndex(w.Rates, remaining, job.WarpCeiling, NavigationLimits.MaxWarpToRate, w.AltitudeLimitIndex, options.WarpMarginRealSeconds);
            if (wanted != w.Index && (wanted != job.WarpRequested || sinceRequest >= options.WarpVetoFrames)) Request(job, wanted, wanted < w.Index);
            if (clock() - job.StartedAt > options.WarpTimeoutMs) { Finish(job, JobStatuses.Failed, AutopilotReasons.Timeout, "the target was not reached within the time allowed"); return; }
            job.Phase = "warping";
        }

        private void Request(AutopilotJob job, int index, bool instant)
        {
            job.Dispatched = true; job.WarpRequested = index; job.WarpRequestedFrame = job.Frames; job.WarpChanges++;
            if (job.WarpChanges == 1) job.EffectsApplied.Add("warp_started");
            navigation.SetWarpRate(index, instant);
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
            if (job.Kind == AutopilotKind.WarpTo)
            {
                // The warp was the runner's: whatever ended the job, return to real time. The throttle was never the runner's to cut.
                if (!job.Dispatched || navigation == null) return;
                try
                {
                    var w = navigation.ReadWarp();
                    if (w == null || w.Index > 0) navigation.SetWarpRate(0, true);
                    job.EffectsApplied.Add("warp_stopped");
                }
                catch (Exception) { job.EffectsApplied.Add("warp_stop_failed"); }
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
