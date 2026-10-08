using System;
using System.Collections.Generic;
using System.Linq;

namespace KspControl.Bridge
{
    // Pure types and the port of the crew recovery job. No Unity or KSP types: the phase machine runs against fakes in the unit tests and only
    // RecoveryUnity.cs touches the game.

    /// <summary>What the recovery job needs to know about one part. Flags are derived from the part's modules and resources.</summary>
    internal sealed class RecoveryPart
    {
        public string PartId { get; set; }
        public string Name { get; set; }
        /// <summary>The stage that activates the part (Part.inverseStage), used only when <see cref="StagingOn"/>.</summary>
        public int InverseStage { get; set; }
        public bool StagingOn { get; set; }
        /// <summary>Carries at least one kerbal.</summary>
        public bool Crewed { get; set; }
        /// <summary>Has ModuleCommand (pods and probe cores).</summary>
        public bool Command { get; set; }
        public bool Parachute { get; set; }
        /// <summary>Has ModuleAblator (heat shields).</summary>
        public bool HeatShield { get; set; }
        /// <summary>Has a ModuleDecouplerBase (decouplers and separators): activating its stage splits the vessel.</summary>
        public bool Separator { get; set; }
        /// <summary>An engine, or a non-command part holding liquid fuel, oxidizer or solid fuel: what separation should leave behind.</summary>
        public bool Propulsion { get; set; }
    }

    internal sealed class RecoveryChute
    {
        public string PartId { get; set; }
        public string Name { get; set; }
        /// <summary>ModuleParachute.deploymentState: STOWED, ACTIVE (armed), SEMIDEPLOYED, DEPLOYED or CUT.</summary>
        public string State { get; set; }
        /// <summary>ModuleParachute.deploymentSafeState: SAFE, RISKY, UNSAFE or NONE.</summary>
        public string Safety { get; set; }
        /// <summary>ModuleParachute.automateSafeDeploy: an armed chute opens once (int)Safety is at most this. 0 opens only when SAFE, 2 opens even when UNSAFE.</summary>
        public int AutomateSafeDeploy { get; set; }
    }

    /// <summary>One frame of the recovered vessel, read by its id (never "whatever is active").</summary>
    internal sealed class RecoveryReading
    {
        public string VesselId { get; set; }
        public string Body { get; set; }
        public string Situation { get; set; }
        public bool HasAtmosphere { get; set; }
        public double AtmosphereTopMeters { get; set; }
        public double AltitudeMeters { get; set; }
        /// <summary>Height above the terrain, or above the sea surface over water (Vessel.radarAltitude).</summary>
        public double HeightAboveTerrainMeters { get; set; }
        public double SurfaceSpeed { get; set; }
        public double VerticalSpeed { get; set; }
        public double ApoapsisMeters { get; set; }
        public double PeriapsisMeters { get; set; }
        /// <summary>Seconds to the apoapsis, NaN on an open orbit.</summary>
        public double TimeToApoapsis { get; set; }
        public bool Orbiting { get; set; }
        /// <summary>The last stage activated (StageManager.CurrentStage); the next stage fires the parts whose InverseStage is one less.</summary>
        public int CurrentStage { get; set; }
        /// <summary>Time warp rate index (rails or physics). Nothing is burned or staged while it is above zero.</summary>
        public int WarpIndex { get; set; }
        /// <summary>Living kerbals aboard (roster status neither dead nor missing).</summary>
        public int CrewCount { get; set; }
        /// <summary>The root part is a command or crewed part, so the vessel id survives separation.</summary>
        public bool RootIsCommand { get; set; }
        public bool StagingLocked { get; set; }
        public List<RecoveryPart> Parts { get; set; } = new List<RecoveryPart>();
        public List<RecoveryChute> Chutes { get; set; } = new List<RecoveryChute>();

        public bool Landed { get { return Situation == "LANDED" || Situation == "SPLASHED"; } }
        public IEnumerable<RecoveryPart> NextStageParts { get { return Parts.Where(p => p.StagingOn && p.InverseStage == CurrentStage - 1); } }
        public IEnumerable<RecoveryChute> UsableChutes { get { return Chutes.Where(c => c.State != "CUT"); } }
    }

    /// <summary>The game side of the recovery job. Main thread only; the Unity implementation is RecoveryUnity.cs.</summary>
    internal interface IRecoveryVesselPort
    {
        /// <summary>That vessel this frame, or null when it no longer exists or is not loaded.</summary>
        RecoveryReading Read(string vesselId);
        /// <summary>The part actions the next stage would trigger, for the flight effect classifier (same source as flight_stage).</summary>
        IList<FlightPartAction> StageActions(int stage);
        IList<FlightPartAction> StageGroupBindings();
        /// <summary>The stock staging call (StageManager.ActivateNextStage), the one flight_stage makes.</summary>
        void ActivateNextStage();
        /// <summary>Arms the parachute on that part (ModuleParachute.Deploy). Returns false when the part or module is gone.</summary>
        bool ArmChute(string vesselId, string partId);
        /// <summary>Commands the main throttle through the bridge's fly-by-wire guard. False when the guard refused (no lease, no vessel).</summary>
        bool SetThrottle(float value);
        /// <summary>Zeroes the throttle and removes the fly-by-wire hold. Idempotent, never throws.</summary>
        void ReleaseThrottle(string vesselId);
    }

    /// <summary>Which stage the separation phase may fire next. Pure: decided from one reading.</summary>
    internal static class RecoverySeparation
    {
        /// <summary>
        /// Null when the next stage should be fired, otherwise why separation stops here. The next stage fires only when propulsion is still
        /// attached and the stage has a decoupler and no crewed, command, parachute or heat-shield part: a parachute stage waits for the chute
        /// phase (and is armed per part, never staged), a heat shield's own decoupler is never fired.
        /// </summary>
        public static string Assess(RecoveryReading r, out bool propulsionRemains)
        {
            propulsionRemains = r.Parts.Any(p => p.Propulsion && !p.Crewed && !p.Command);
            if (!propulsionRemains) return "only_recovery_parts_remain";
            if (r.CurrentStage <= 0) return "no_stage_left";
            return AssessStage(r.NextStageParts.ToList());
        }

        private static string AssessStage(List<RecoveryPart> next)
        {
            if (next.Any(p => p.Crewed || p.Command)) return "next_stage_has_crew_or_command";
            if (next.Any(p => p.Parachute)) return "next_stage_has_parachute";
            if (next.Any(p => p.HeatShield)) return "next_stage_has_heat_shield";
            if (!next.Any(p => p.Separator)) return "next_stage_does_not_separate";
            return null;
        }

        /// <summary>
        /// Admission preview: walks the stages from the next one down and lists those separation could fire before the first one it must not.
        /// Which parts each decoupler drops is not known before it fires, so this is a candidate list; the live phase re-decides every stage.
        /// </summary>
        public static List<int> Candidates(RecoveryReading r, out string stopsAt)
        {
            var result = new List<int>();
            stopsAt = "no_stage_left";
            for (var stage = r.CurrentStage - 1; stage >= 0; stage--)
            {
                var s = stage;
                var why = AssessStage(r.Parts.Where(p => p.StagingOn && p.InverseStage == s).ToList());
                if (why != null) { stopsAt = why + ":" + stage; break; }
                result.Add(stage);
            }
            return result;
        }
    }
}
