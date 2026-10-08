using System;
using System.Text.RegularExpressions;

namespace KspControl.Contracts
{
    /// <summary>
    /// The MechJeb autopilot family. One grant operation (<see cref="Effect"/>) covers every mutation below; the status reads need no lease.
    /// The lease entity is the active vessel ("vessel:<guid>"): the bridge binds each job to that vessel at admission and ends the job if it changes.
    /// </summary>
    public static class AutopilotOperations
    {
        /// <summary>The grant operation family listed in the grant's operations. The bridge classifies every mutation below as this effect.</summary>
        public const string Effect = "flight.autopilot";
        // Same flight lease model as the other flight tools: the grant's FLIGHT facility selects flight, the lease entity is "vessel:<guid>" of the
        // active vessel (so a vessel switch revokes the lease), and the grant maps the facility to the wildcard "vessel:*". The facility, entity
        // prefix/wildcard and journal entity are FlightEffects' constants: one lease model, one definition.

        public const string MechJebStatus = "mechjeb.status";
        public const string Status = "flight.autopilot_status";
        public const string Ascent = "flight.autopilot_ascent";
        public const string ExecuteNode = "flight.autopilot_execute_node";
        public const string PlanCircularize = "flight.autopilot_plan_circularize";
        public const string PlanHohmann = "flight.autopilot_plan_hohmann";
        /// <summary>Crew recovery: deorbit, separation, reentry, parachutes, touchdown. A long job like the ascent.</summary>
        public const string Recover = "flight.autopilot_recover";
        // Stock flight-plan editing and rails warp (P3 navigation). Same family: a grant that may have MechJeb burn the engines may also edit the
        // flight plan and warp to the burn, and one runner serialises a warp with any MechJeb job, so the two can never race.
        public const string NodeCreate = "flight.node_create";
        public const string NodeUpdate = "flight.node_update";
        public const string NodeDelete = "flight.node_delete";
        public const string WarpTo = "flight.warp_to";

        /// <summary>Reads on the queued observation path.</summary>
        public static readonly string[] Reads = { MechJebStatus, Status };
        /// <summary>Operations that change the game. Reachable only through the host journal.</summary>
        public static readonly string[] Mutations = { Ascent, ExecuteNode, PlanCircularize, PlanHohmann, Recover, NodeCreate, NodeUpdate, NodeDelete, WarpTo };
    }

    public static class AutopilotLimits
    {
        public const int AltitudeMinMeters = 70000;
        public const int AltitudeMaxMeters = 500000;
        public const int InclinationMinDegrees = -180;
        public const int InclinationMaxDegrees = 180;
        public const int BodyNameMax = 64;
        /// <summary>Added to the body's orbit floor (atmosphere top or safe altitude) for the lowest target the bridge accepts.</summary>
        public const int OrbitMarginMeters = 5000;
        private static readonly Regex BodyName = new Regex("^[A-Za-z0-9 _'-]{1,64}$", RegexOptions.CultureInvariant);
        public static bool IsBodyName(string value) { return value != null && BodyName.IsMatch(value); }
    }

    /// <summary>Bounds of flight_recover, shared by host validation and the bridge.</summary>
    public static class RecoveryLimits
    {
        /// <summary>Periapsis the deorbit burn aims under. 30 km puts a Kerbin-like reentry well inside the atmosphere without a brutal entry.</summary>
        public const int TargetPeriapsisDefaultMeters = 30000;
        public const int TargetPeriapsisMinMeters = -50000;
        /// <summary>Also capped below the atmosphere top of the current body by the bridge.</summary>
        public const int TargetPeriapsisMaxMeters = 60000;
        /// <summary>Height above the terrain (or sea) under which parachutes are armed if they have not been armed as safe already.</summary>
        public const int ArmAltitudeDefaultMeters = 10000;
        public const int ArmAltitudeMinMeters = 1000;
        public const int ArmAltitudeMaxMeters = 30000;
        /// <summary>Under this height every stowed parachute is armed whatever its safety reading: a torn chute beats none.</summary>
        public const int LastResortArmMeters = 2000;
        public const string BurnAtNow = "now";
        public const string BurnAtApoapsis = "apoapsis";
        public static bool IsBurnAt(string value) { return value == BurnAtNow || value == BurnAtApoapsis; }
    }

    /// <summary>Reason codes of the autopilot path, beyond the control and operation codes.</summary>
    public static class AutopilotReasons
    {
        public const string FlightUnavailable = "flight_unavailable";
        public const string MechJebUnavailable = "mechjeb_unavailable";
        public const string MechJebVersionUnsupported = "mechjeb_version_unsupported";
        public const string MechJebModuleUnavailable = "mechjeb_module_unavailable";
        public const string CompetingController = "competing_controller";
        public const string UnsupportedOption = "unsupported_option";
        public const string AutopilotBusy = "autopilot_busy";
        public const string VesselChanged = "vessel_changed";
        public const string EngageFailed = "engage_failed";
        public const string AscentEndedWithoutOrbit = "ascent_ended_without_orbit";
        public const string NodeExecutionEnded = "node_execution_ended_early";
        public const string Timeout = "autopilot_timeout";
        public const string NoManeuverNode = "no_maneuver_node";
        public const string TargetUnavailable = "target_unavailable";
        public const string PlanUnavailable = "plan_unavailable";
        public const string StoppedByRequest = "stopped";
        public const string NotApplicable = "not_applicable";
        /// <summary>The ascent module was switched off before the orbit was reached (MechJeb's Disengage button, or MechJeb ending its own ascent). Not a takeover.</summary>
        public const string AscentDisengaged = "ascent_disengaged";
        /// <summary>flight_recover on a vessel with no usable parachute.</summary>
        public const string NoParachute = "no_parachute";
        /// <summary>The deorbit burn stopped lowering the periapsis (no thrust, no fuel) while it was still above the atmosphere.</summary>
        public const string DeorbitFailed = "deorbit_failed";
        /// <summary>MechJeb's attitude controller did not bring the vessel within the alignment tolerance in time.</summary>
        public const string AttitudeNotReached = "attitude_not_reached";
        /// <summary>The recovered vessel no longer exists (destroyed on impact or in reentry).</summary>
        public const string VesselLost = "vessel_lost";
        /// <summary>Touchdown with fewer living crew than at the start.</summary>
        public const string CrewLost = "crew_lost";
        public const string ThrottleUnavailable = "throttle_unavailable";
    }
}
