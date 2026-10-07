using System;
using System.Text.RegularExpressions;

namespace KspControl.Contracts
{
    /// <summary>
    /// The MechJeb autopilot family. One grant operation (<see cref="Effect"/>) covers every mutation below; the status reads need no lease.
    /// The entity is the flight scene, not a vessel: the bridge binds each job to the vessel that was active at admission and ends the job if that changes.
    /// </summary>
    public static class AutopilotOperations
    {
        /// <summary>The grant operation family listed in the grant's operations. The bridge classifies every mutation below as this effect.</summary>
        public const string Effect = "flight.autopilot";
        /// <summary>The lease entity of the flight scene; the grant's entities include it when the grant lists <see cref="Effect"/>.</summary>
        public const string Entity = "scene:FLIGHT";
        /// <summary>The effect recipient the bridge grants for <see cref="Effect"/>.</summary>
        public const string Recipient = "flight:vessel";

        public const string MechJebStatus = "mechjeb.status";
        public const string Status = "flight.autopilot_status";
        public const string Ascent = "flight.autopilot_ascent";
        public const string ExecuteNode = "flight.autopilot_execute_node";
        public const string PlanCircularize = "flight.autopilot_plan_circularize";
        public const string PlanHohmann = "flight.autopilot_plan_hohmann";

        /// <summary>Reads on the queued observation path.</summary>
        public static readonly string[] Reads = { MechJebStatus, Status };
        /// <summary>Operations that change the game. Reachable only through the host journal.</summary>
        public static readonly string[] Mutations = { Ascent, ExecuteNode, PlanCircularize, PlanHohmann };
    }

    public static class AutopilotLimits
    {
        public const int AltitudeMinMeters = 70000;
        public const int AltitudeMaxMeters = 500000;
        public const int InclinationMinDegrees = -180;
        public const int InclinationMaxDegrees = 180;
        public const int BodyNameMax = 64;
        private static readonly Regex BodyName = new Regex("^[A-Za-z0-9 _'-]{1,64}$", RegexOptions.CultureInvariant);
        public static bool IsBodyName(string value) { return value != null && BodyName.IsMatch(value); }
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
    }
}
