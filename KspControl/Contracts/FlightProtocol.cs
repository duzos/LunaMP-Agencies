using System;

namespace KspControl.Contracts
{
    /// <summary>Flight operations served on the queued main-thread path. State is read-only; the rest need a lease and a grant listing flight.control.</summary>
    public static class FlightOperations
    {
        public const string State = "flight.state";
        public const string SetControls = "flight.set_controls";
        public const string Stage = "flight.stage";
        public const string ActionGroup = "flight.action_group";
        public const string Abort = "flight.abort";
        public const string Warp = "flight.warp";
        /// <summary>Operations that change the game. They are reachable only through the host journal.</summary>
        public static readonly string[] Mutations = { SetControls, Stage, ActionGroup, Abort, Warp };
        public static bool IsMutation(string operation) { return Array.IndexOf(Mutations, operation) >= 0; }
    }

    /// <summary>The grant operation family and the effect names the authority classifies for flight.</summary>
    public static class FlightEffects
    {
        /// <summary>One family covers every flight mutation: the grant lists it, the authority's effect map knows it.</summary>
        public const string Family = "flight.control";
        /// <summary>Not a known effect on purpose: an effect the classifier cannot name is refused by the authority.</summary>
        public const string Unclassified = "flight.unclassified";
        /// <summary>The grant facility that selects flight (the lease entity is "vessel:&lt;guid&gt;" of the active vessel).</summary>
        public const string Facility = "FLIGHT";
        public const string EntityWildcard = "vessel:*";
        public const string EntityPrefix = "vessel:";
        /// <summary>The journal entity used by the host for every flight mutation.</summary>
        public const string JournalEntity = "flight:vessel";
    }

    /// <summary>Bounds shared by host validation and bridge checks for the flight tools.</summary>
    public static class FlightLimits
    {
        /// <summary>The highest effective rails warp the bridge will request.</summary>
        public const float MaxWarpRate = 1000f;
        /// <summary>Physics warp is never raised above real time.</summary>
        public const float MaxPhysicsWarpRate = 1f;
        public const int MaxStageNumber = 999;
        public const int MaxRateIndex = 7;
        public static readonly string[] ActionGroups =
        {
            "Gear", "Light", "Brakes", "SAS", "RCS",
            "Custom01", "Custom02", "Custom03", "Custom04", "Custom05", "Custom06", "Custom07", "Custom08", "Custom09", "Custom10"
        };
        public static bool IsActionGroup(string name) { return Array.IndexOf(ActionGroups, name) >= 0; }
    }

    /// <summary>Reason codes produced by the flight path, beyond the control and operation codes.</summary>
    public static class FlightReasons
    {
        public const string FlightUnavailable = "flight_unavailable";
        public const string VesselNotOwned = "owned_active_vessel_unavailable";
        public const string VesselNotControllable = "vessel_not_controllable";
        public const string StageMismatch = "stage_mismatch";
        public const string NoStageToActivate = "no_stage_to_activate";
        public const string StagingLocked = "staging_locked";
        public const string UnclassifiedEffect = "unclassified_effect";
        public const string TooManyEffects = "too_many_effects";
        public const string WarpDenied = "warp_denied";
        public const string WarpAboveCap = "warp_above_cap";
        public const string WarpThrottleActive = "warp_while_thrusting";
        public const string NothingRequested = "nothing_requested";
        public const string ThrottleConflict = "throttle_conflict";
        public const string NotConfirmed = "state_not_confirmed";
    }
}
