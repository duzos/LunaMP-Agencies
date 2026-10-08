using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>Timing and tolerances of the recovery phase machine. Tests shorten them.</summary>
    internal sealed class RecoveryOptions
    {
        /// <summary>The deorbit burn starts once MechJeb's attitudeAngleFromTarget is at most this.</summary>
        public double AlignToleranceDegrees = 5;
        /// <summary>A burn drifting further than this off retrograde is paused until the vessel is aligned again.</summary>
        public double BurnAbortAngleDegrees = 20;
        public long AlignTimeoutMs = 3 * 60 * 1000L;
        public long BurnTimeoutMs = 15 * 60 * 1000L;
        /// <summary>A burn that has not lowered the periapsis for this long has no thrust (no fuel, no active engine).</summary>
        public long NoProgressMs = 10 * 1000L;
        /// <summary>burnAt=apoapsis starts the burn this many seconds before the apoapsis.</summary>
        public double ApoapsisLeadSeconds = 20;
        /// <summary>Within this distance of the target periapsis the burn throttles down to <see cref="TaperThrottle"/>.</summary>
        public double TaperMeters = 5000;
        public float TaperThrottle = 0.25f;
        /// <summary>Above the atmosphere top plus this, reentry coasts with the attitude released (saves electric charge on a long coast).</summary>
        public double HoldAboveAtmosphereMeters = 5000;
        /// <summary>Under this altitude the attitude is released so the capsule weathervanes on its own.</summary>
        public double ReleaseAttitudeAltitudeMeters = 20000;
        /// <summary>After firing a stage, wait this long for the vessel to split before judging the next one.</summary>
        public long StageSettleMs = 1500;
        public int MaxSeparationStages = 8;
        public long ChuteRetryMs = 2000;
        public int ChuteAttempts = 3;
        public long TimeoutMs = 2 * 60 * 60 * 1000L;
    }

    internal sealed class RecoveryRequest
    {
        public double TargetPeriapsisMeters = RecoveryLimits.TargetPeriapsisDefaultMeters;
        public bool BurnAtApoapsis;
        public double ArmAltitudeMeters = RecoveryLimits.ArmAltitudeDefaultMeters;
    }

    internal enum RecoveryVerdict { Continue, Completed, Failed, Takeover }

    internal sealed class RecoveryOutcome
    {
        public RecoveryVerdict Verdict { get; private set; }
        public string Reason { get; private set; }
        public string Detail { get; private set; }
        public static readonly RecoveryOutcome Continue = new RecoveryOutcome { Verdict = RecoveryVerdict.Continue };
        public static RecoveryOutcome Of(RecoveryVerdict verdict, string reason, string detail) { return new RecoveryOutcome { Verdict = verdict, Reason = reason, Detail = detail }; }
    }

    /// <summary>
    /// The crew recovery phase machine: deorbit (hold retrograde, burn until the periapsis is under the target), separation (fire stages that only drop
    /// propulsion), reentry (hold surface retrograde, heat shield first, down to 20 km), parachutes (arm each chute as soon as it is safe, or below the arm
    /// altitude when stock will hold it until safe, or below 2 km regardless) and touchdown. Pure: everything it does goes through the MechJeb port and the
    /// recovery port, and the runner calls <see cref="Step"/> once per frame after it has revalidated the authority, the vessel and the controls.
    /// </summary>
    internal sealed class RecoveryMachine
    {
        public const string DeorbitAlign = "deorbit_align", DeorbitWait = "deorbit_wait_apoapsis", DeorbitBurn = "deorbit_burn", Separation = "separation",
            Coast = "coast", Reentry = "reentry", Descent = "descent", Landed = "landed";

        private sealed class ChuteMemo { public int Attempts; public long LastAt; public string Reason; public bool Confirmed, Warned; public double Height; }

        private readonly string vesselId;
        private readonly object user;
        private readonly RecoveryRequest request;
        private readonly IMechJebPort mechjeb;
        private readonly IRecoveryVesselPort port;
        private readonly Func<long> clock;
        private readonly RecoveryOptions options;
        private readonly List<string> effects;
        private readonly Dictionary<string, ChuteMemo> chutes = new Dictionary<string, ChuteMemo>(StringComparer.Ordinal);

        public string Phase { get; private set; } = "starting";
        public RecoveryReading Last { get; private set; }
        public List<string> Warnings { get; } = new List<string>();
        public JArray StagesFired { get; } = new JArray();
        public bool DeorbitSkipped { get; private set; }
        public string SeparationResult { get; private set; }
        public double? ImpactSpeed { get; private set; }
        public int CrewAtStart { get; private set; } = -1;
        public string HeldDirection { get { return held; } }
        public float Throttle { get { return throttleEngaged ? throttle : 0f; } }
        /// <summary>The machine holds the throttle right now (a deorbit burn is under way).</summary>
        public bool Throttling { get { return throttleEngaged; } }

        private string held;
        private float throttle;
        private bool throttleEngaged, burnStarted;
        private double angle = double.NaN, bestPeriapsis, lastAirborneSpeed = double.NaN;
        private long startedAt = -1, alignSince, burnStartedAt, lastProgressAt, lastStageAt = -1;
        private int stageBeforeFire;

        public RecoveryMachine(string vesselId, object user, RecoveryRequest request, IMechJebPort mechjeb, IRecoveryVesselPort port, Func<long> clock, RecoveryOptions options, List<string> effects)
        {
            this.vesselId = vesselId; this.user = user; this.request = request ?? new RecoveryRequest();
            this.mechjeb = mechjeb ?? throw new ArgumentNullException(nameof(mechjeb));
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.options = options ?? new RecoveryOptions();
            this.effects = effects ?? new List<string>();
        }

        // ---------------------------------------------------------------- the step

        /// <summary>One frame. MechJeb and port exceptions propagate: the runner fails the job and calls <see cref="Release"/>.</summary>
        public RecoveryOutcome Step(RecoveryReading r)
        {
            var now = clock();
            if (startedAt < 0) Begin(r, now);
            Last = r;
            if (r.Landed) return Touchdown(r);
            lastAirborneSpeed = r.SurfaceSpeed;
            if (now - startedAt > options.TimeoutMs) return Fail(AutopilotReasons.Timeout, "the vessel did not land within the time allowed");

            RecoveryOutcome outcome;
            switch (Phase)
            {
                case DeorbitAlign: case DeorbitWait: case DeorbitBurn: outcome = StepDeorbit(r, now); break;
                case Separation: outcome = StepSeparation(r, now); break;
                case Coast: case Reentry: outcome = StepReentry(r); break;
                default: outcome = RecoveryOutcome.Continue; break;
            }
            if (outcome.Verdict != RecoveryVerdict.Continue) return outcome;
            if (Phase == Coast || Phase == Reentry || Phase == Descent) ArmChutes(r, now, false);
            else if (Phase == Separation) ArmChutes(r, now, true);
            return outcome;
        }

        private void Begin(RecoveryReading r, long now)
        {
            startedAt = now; CrewAtStart = r.CrewCount;
            if (r.PeriapsisMeters > request.TargetPeriapsisMeters) { Phase = DeorbitAlign; alignSince = now; }
            else { DeorbitSkipped = true; Phase = Separation; }
        }

        // ---------------------------------------------------------------- (a) deorbit

        private RecoveryOutcome StepDeorbit(RecoveryReading r, long now)
        {
            if (r.PeriapsisMeters <= request.TargetPeriapsisMeters)
            {
                ThrottleOff();
                if (burnStarted) effects.Add("deorbit_burn_complete");
                Phase = Separation;
                return RecoveryOutcome.Continue;
            }
            // Nothing burns under time warp: wait for real time (an agent may warp to the apoapsis).
            if (r.WarpIndex > 0) { ThrottleOff(); if (Phase == DeorbitBurn) Phase = DeorbitAlign; alignSince = now; return RecoveryOutcome.Continue; }
            Hold(AttitudeDirections.OrbitRetrograde);
            var lost = CheckAttitude(out var attitude);
            if (lost != null) { ThrottleOff(); return lost; }
            var aligned = attitude.Enabled && attitude.AngleFromTargetDegrees <= options.AlignToleranceDegrees;

            if (Phase == DeorbitBurn)
            {
                if (!(attitude.AngleFromTargetDegrees <= options.BurnAbortAngleDegrees)) { ThrottleOff(); Phase = DeorbitAlign; alignSince = now; return RecoveryOutcome.Continue; }
                var refused = SetThrottle(r.PeriapsisMeters - request.TargetPeriapsisMeters < options.TaperMeters ? options.TaperThrottle : 1f);
                if (refused != null) return refused;
                if (r.PeriapsisMeters < bestPeriapsis - 1) { bestPeriapsis = r.PeriapsisMeters; lastProgressAt = now; }
                else if (now - lastProgressAt > options.NoProgressMs)
                {
                    ThrottleOff();
                    if (r.HasAtmosphere && r.PeriapsisMeters < r.AtmosphereTopMeters)
                    {
                        Warn("deorbit_short: the burn stopped lowering the periapsis at " + Meters(r.PeriapsisMeters) + ", inside the atmosphere; continuing with a shallower reentry");
                        Phase = Separation; return RecoveryOutcome.Continue;
                    }
                    return Fail(AutopilotReasons.DeorbitFailed, "the burn stopped lowering the periapsis at " + Meters(r.PeriapsisMeters) + " (out of fuel or no active engine); the vessel stays in orbit");
                }
                if (now - burnStartedAt > options.BurnTimeoutMs) { ThrottleOff(); return Fail(AutopilotReasons.Timeout, "the deorbit burn took too long"); }
                return RecoveryOutcome.Continue;
            }

            if (!aligned)
            {
                if (Phase != DeorbitAlign) { Phase = DeorbitAlign; alignSince = now; }
                if (now - alignSince > options.AlignTimeoutMs)
                    return Fail(AutopilotReasons.AttitudeNotReached, "MechJeb did not bring the vessel within " + options.AlignToleranceDegrees.ToString(CultureInfo.InvariantCulture) + " degrees of retrograde (angle " + Degrees(attitude.AngleFromTargetDegrees) + ")");
                return RecoveryOutcome.Continue;
            }
            if (request.BurnAtApoapsis && !burnStarted && r.Orbiting && !double.IsNaN(r.TimeToApoapsis) && r.TimeToApoapsis > options.ApoapsisLeadSeconds)
            { Phase = DeorbitWait; alignSince = now; return RecoveryOutcome.Continue; }

            Phase = DeorbitBurn;
            if (!burnStarted) { burnStarted = true; burnStartedAt = now; }
            lastProgressAt = now; bestPeriapsis = r.PeriapsisMeters;
            return SetThrottle(r.PeriapsisMeters - request.TargetPeriapsisMeters < options.TaperMeters ? options.TaperThrottle : 1f) ?? RecoveryOutcome.Continue;
        }

        // ---------------------------------------------------------------- (b) separation

        private RecoveryOutcome StepSeparation(RecoveryReading r, long now)
        {
            if (r.WarpIndex > 0) return RecoveryOutcome.Continue;
            if (lastStageAt >= 0)
            {
                if (now - lastStageAt < options.StageSettleMs) return RecoveryOutcome.Continue;
                lastStageAt = -1;
                if (r.CurrentStage >= stageBeforeFire) { Warn("separation_stalled: stage " + (stageBeforeFire - 1) + " was fired but the stage number did not advance"); return EndSeparation(r, "stage_did_not_advance", true); }
            }
            bool propulsion;
            var why = RecoverySeparation.Assess(r, out propulsion);
            // The vessel id belongs to the root part: if the root is below a decoupler the crew would fly off as a new, unwatched vessel.
            if (why == null && !r.RootIsCommand) why = "root_part_is_not_the_command_part";
            if (why == null && StagesFired.Count >= options.MaxSeparationStages) why = "stage_limit";
            if (why == null && r.StagingLocked) why = "staging_locked";
            if (why == null)
            {
                var stage = r.CurrentStage - 1;
                var plan = FlightEffectClassifier.Merge(FlightEffectClassifier.Plan(vesselId, port.StageActions(stage), true), FlightEffectClassifier.Plan(vesselId, port.StageGroupBindings(), false));
                if (!plan.Allowed) why = "too_many_effects";
                else
                {
                    stageBeforeFire = r.CurrentStage;
                    port.ActivateNextStage();
                    lastStageAt = now;
                    effects.Add("stage_fired:" + stage);
                    StagesFired.Add(new JObject { ["stage"] = stage, ["altitudeMeters"] = Finite(r.AltitudeMeters), ["consequential"] = new JArray(plan.Consequential.Take(16)), ["unclassified"] = new JArray(plan.Unclassified.Take(16)) });
                    return RecoveryOutcome.Continue;
                }
            }
            return EndSeparation(r, why, propulsion);
        }

        private RecoveryOutcome EndSeparation(RecoveryReading r, string why, bool propulsionRemains)
        {
            SeparationResult = why;
            if (propulsionRemains && why != "stage_did_not_advance") Warn("separation_incomplete: " + why + "; engines or fuel tanks are still attached");
            if (!r.Parts.Any(p => p.Crewed || p.Command)) Warn("no_crew_or_command_part: the recovered vessel has no crewed or command part left");
            Phase = r.AltitudeMeters > r.AtmosphereTopMeters + options.HoldAboveAtmosphereMeters ? Coast : Reentry;
            return RecoveryOutcome.Continue;
        }

        // ---------------------------------------------------------------- (c) reentry

        private RecoveryOutcome StepReentry(RecoveryReading r)
        {
            if (r.AltitudeMeters < options.ReleaseAttitudeAltitudeMeters)
            {
                ReleaseAttitude();
                Phase = Descent;
                return RecoveryOutcome.Continue;
            }
            if (r.AltitudeMeters > r.AtmosphereTopMeters + options.HoldAboveAtmosphereMeters)
            {
                // A long coast to the interface: let go so the reaction wheels do not drain the battery.
                if (held != null) ReleaseAttitude();
                Phase = Coast;
                return RecoveryOutcome.Continue;
            }
            Phase = Reentry;
            Hold(AttitudeDirections.SurfaceRetrograde);
            AttitudeReading attitude;
            return CheckAttitude(out attitude) ?? RecoveryOutcome.Continue;
        }

        // ---------------------------------------------------------------- (d) parachutes

        private void ArmChutes(RecoveryReading r, long now, bool lastResortOnly)
        {
            foreach (var chute in r.Chutes)
            {
                if (chute == null || chute.PartId == null) continue;
                ChuteMemo memo;
                chutes.TryGetValue(chute.PartId, out memo);
                if (chute.State != "STOWED")
                {
                    if (memo != null && !memo.Confirmed) { memo.Confirmed = true; effects.Add("chute_armed:" + chute.PartId); }
                    continue;
                }
                if (memo != null && now - memo.LastAt < options.ChuteRetryMs) continue;
                if (memo != null && memo.Attempts >= options.ChuteAttempts)
                {
                    if (!memo.Warned) { memo.Warned = true; Warn("chute_not_arming: part " + chute.PartId + " stays STOWED after " + memo.Attempts + " attempts (shielded from the airstream?)"); }
                    continue;
                }
                var why = ArmReason(chute, r, lastResortOnly);
                if (why == null) continue;
                if (memo == null) chutes[chute.PartId] = memo = new ChuteMemo();
                memo.Attempts++; memo.LastAt = now; memo.Reason = why; memo.Height = r.HeightAboveTerrainMeters;
                if (!port.ArmChute(vesselId, chute.PartId)) Warn("chute_missing: part " + chute.PartId + " could not be armed");
            }
        }

        /// <summary>
        /// Why to arm this chute now, or null. Stock opens an armed chute only once its safety reading is within automateSafeDeploy (0: SAFE only), so arming
        /// early is safe whenever stock will hold it; the Mk16 then semi-deploys at its minimum pressure and opens fully at its deploy altitude.
        /// </summary>
        private string ArmReason(RecoveryChute chute, RecoveryReading r, bool lastResortOnly)
        {
            if (!r.HasAtmosphere) return null;
            var height = double.IsNaN(r.HeightAboveTerrainMeters) ? r.AltitudeMeters : r.HeightAboveTerrainMeters;
            if (height < RecoveryLimits.LastResortArmMeters) return "last_resort";
            if (lastResortOnly || r.AltitudeMeters >= r.AtmosphereTopMeters) return null;
            if (chute.Safety == "SAFE") return "safe";
            if (height < request.ArmAltitudeMeters && chute.Safety == "RISKY") return "risky_below_arm_altitude";
            if (height < request.ArmAltitudeMeters && chute.AutomateSafeDeploy < 2) return "below_arm_altitude_stock_waits_for_safe";
            return null;
        }

        // ---------------------------------------------------------------- (e) touchdown

        private RecoveryOutcome Touchdown(RecoveryReading r)
        {
            ThrottleOff(); ReleaseAttitude();
            ImpactSpeed = double.IsNaN(lastAirborneSpeed) ? r.SurfaceSpeed : lastAirborneSpeed;
            Phase = Landed;
            var detail = r.Situation.ToLowerInvariant() + " at " + ImpactSpeed.Value.ToString("0.0", CultureInfo.InvariantCulture) + " m/s with " + r.CrewCount + " of " + CrewAtStart + " crew alive";
            if (r.CrewCount < CrewAtStart) return Fail(AutopilotReasons.CrewLost, detail);
            return RecoveryOutcome.Of(RecoveryVerdict.Completed, null, detail);
        }

        // ---------------------------------------------------------------- attitude and throttle

        private void Hold(string direction)
        {
            if (held == direction) return;
            mechjeb.HoldAttitude(vesselId, user, direction);
            held = direction;
            effects.Add("attitude_hold:" + direction);
        }

        /// <summary>Our hold must still be there: a person switching SmartASS off (attitudeDeactivate clears every user) or another user is a takeover.</summary>
        private RecoveryOutcome CheckAttitude(out AttitudeReading attitude)
        {
            attitude = mechjeb.ReadAttitude(vesselId, user);
            angle = attitude.AngleFromTargetDegrees;
            if (attitude.OtherUsers > 0) return RecoveryOutcome.Of(RecoveryVerdict.Takeover, OperationReasons.HumanInputDuringOperation, "another user took MechJeb's attitude controller");
            if (!attitude.OwnUserPresent) { held = null; return RecoveryOutcome.Of(RecoveryVerdict.Takeover, OperationReasons.HumanInputDuringOperation, "the attitude hold was removed (SmartASS switched off or another MechJeb action)"); }
            return null;
        }

        private void ReleaseAttitude()
        {
            if (held == null) return;
            held = null; angle = double.NaN;
            try { mechjeb.ReleaseAttitude(vesselId, user); effects.Add("attitude_released"); }
            catch (Exception) { effects.Add("attitude_release_failed"); }
        }

        private RecoveryOutcome SetThrottle(float value)
        {
            if (!port.SetThrottle(value)) { throttleEngaged = false; return Fail(AutopilotReasons.ThrottleUnavailable, "the fly-by-wire throttle could not be held (lease ended or no vessel)"); }
            if (!throttleEngaged) effects.Add("throttle_engaged");
            throttleEngaged = true; throttle = value;
            return null;
        }

        private void ThrottleOff()
        {
            if (!throttleEngaged) return;
            throttleEngaged = false; throttle = 0f;
            try { port.ReleaseThrottle(vesselId); effects.Add("throttle_cut"); } catch (Exception) { effects.Add("throttle_cut_failed"); }
        }

        /// <summary>Cuts the throttle and releases the attitude hold. Idempotent, never throws. The runner calls it on every ending.</summary>
        public void Release()
        {
            try { ThrottleOff(); } catch (Exception) { }
            try { ReleaseAttitude(); } catch (Exception) { }
        }

        private RecoveryOutcome Fail(string reason, string detail) { return RecoveryOutcome.Of(RecoveryVerdict.Failed, reason, detail); }

        private void Warn(string text) { if (!Warnings.Contains(text) && Warnings.Count < 32) Warnings.Add(text); }

        // ---------------------------------------------------------------- envelope

        public JObject Describe()
        {
            var result = new JObject
            {
                ["phase"] = Phase, ["deorbitSkipped"] = DeorbitSkipped,
                ["attitude"] = new JObject { ["held"] = held == null ? JValue.CreateNull() : (JToken)held, ["angleFromTargetDegrees"] = Finite(angle) },
                ["throttle"] = Throttle,
                ["stagesFired"] = StagesFired.DeepClone(),
                ["separation"] = SeparationResult == null ? JValue.CreateNull() : (JToken)SeparationResult,
                ["warnings"] = new JArray(Warnings),
                ["crewAtStart"] = CrewAtStart,
                ["impactSpeedMetersPerSecond"] = ImpactSpeed.HasValue ? Finite(ImpactSpeed.Value) : JValue.CreateNull()
            };
            var r = Last;
            if (r == null) return result;
            result["crewAlive"] = r.CrewCount;
            var list = new JArray();
            foreach (var chute in r.Chutes.Take(16))
            {
                ChuteMemo memo; chutes.TryGetValue(chute.PartId ?? "", out memo);
                list.Add(new JObject
                {
                    ["partId"] = chute.PartId, ["name"] = chute.Name, ["state"] = chute.State, ["safety"] = chute.Safety,
                    ["armedByBridge"] = memo != null, ["armReason"] = memo == null ? JValue.CreateNull() : (JToken)memo.Reason,
                    ["armedAtHeightMeters"] = memo == null ? JValue.CreateNull() : Finite(memo.Height)
                });
            }
            result["chutes"] = list;
            result["chutesArmed"] = r.Chutes.Count(c => c.State != "STOWED" && c.State != "CUT");
            result["chutesOpen"] = r.Chutes.Count(c => c.State == "SEMIDEPLOYED" || c.State == "DEPLOYED");
            result["telemetry"] = new JObject
            {
                ["situation"] = r.Situation, ["altitudeMeters"] = Finite(r.AltitudeMeters), ["heightAboveTerrainMeters"] = Finite(r.HeightAboveTerrainMeters),
                ["surfaceSpeedMetersPerSecond"] = Finite(r.SurfaceSpeed), ["verticalSpeedMetersPerSecond"] = Finite(r.VerticalSpeed),
                ["apoapsisMeters"] = Finite(r.ApoapsisMeters), ["periapsisMeters"] = Finite(r.PeriapsisMeters), ["atmosphereTopMeters"] = Finite(r.AtmosphereTopMeters),
                ["stage"] = r.CurrentStage, ["warpRateIndex"] = r.WarpIndex
            };
            return result;
        }

        private static JToken Finite(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? JValue.CreateNull() : new JValue(Math.Round(value, 2)); }
        private static string Meters(double value) { return double.IsNaN(value) ? "unknown" : Math.Round(value).ToString(CultureInfo.InvariantCulture) + " m"; }
        private static string Degrees(double value) { return double.IsNaN(value) ? "unknown" : value.ToString("0.0", CultureInfo.InvariantCulture); }
    }
}
