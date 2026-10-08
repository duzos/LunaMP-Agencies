using System;

namespace KspControl.Contracts
{
    /// <summary>
    /// Bounds of the navigation tools (flight_orbit_prediction, flight_node_*, flight_warp_to). Shared by host validation and the bridge.
    /// The operation names live in <see cref="FlightOperations"/> (the read) and <see cref="AutopilotOperations"/> (the mutations).
    /// </summary>
    public static class NavigationLimits
    {
        /// <summary>Per component and as a magnitude: a node larger than this is refused.</summary>
        public const double MaxDeltaVMetersPerSecond = 3000;
        /// <summary>Patches reported per trajectory.</summary>
        public const int MaxPatches = 6;
        /// <summary>Maneuver nodes reported, and the most a vessel may carry when the bridge adds one.</summary>
        public const int MaxNodes = 16;
        public const int MaxNodeIndex = MaxNodes - 1;
        /// <summary>A node or a warp target must be at least this far in the future.</summary>
        public const double MinLeadSeconds = 1;
        /// <summary>A node or a warp target at most this far in the future (about 463 Kerbin days).</summary>
        public const double MaxHorizonSeconds = 10000000;
        /// <summary>An offset from the apoapsis or periapsis time.</summary>
        public const double MaxOffsetSeconds = 86400;
        public const double MaxLeadSeconds = 3600;
        public const double DefaultLeadSeconds = 60;
        /// <summary>
        /// The highest effective rails rate flight_warp_to requests: the top of the stock rails table. Every rate change goes through the stock
        /// <c>TimeWarp.SetRate</c>, so LunaMP's warp rules (server warp mode, subspace sync, spectating) still allow or refuse each step, and the stock
        /// altitude and atmosphere limits clamp it. flight_warp keeps its 1000x cap because nothing stops a manual rate; warp_to stops itself.
        /// </summary>
        public const float MaxWarpToRate = 100000f;
        /// <summary>The longest real time a warp job may run.</summary>
        public const long WarpTimeoutMs = 30 * 60 * 1000L;

        public static readonly string[] TimeReferences = { "absolute", "in_seconds", "apoapsis", "periapsis" };
        public static readonly string[] WarpTargets = { "node", "soi", "absolute", "in_seconds", "apoapsis", "periapsis" };
        public static bool IsTimeReference(string value) { return Array.IndexOf(TimeReferences, value) >= 0; }
        public static bool IsWarpTarget(string value) { return Array.IndexOf(WarpTargets, value) >= 0; }
        public static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        /// <summary>Null when the delta-v components are within bounds, else the invalid_argument detail.</summary>
        public static string CheckDeltaV(double prograde, double normal, double radial)
        {
            if (!IsFinite(prograde) || !IsFinite(normal) || !IsFinite(radial)) return "delta-v components must be finite numbers";
            if (Math.Abs(prograde) > MaxDeltaVMetersPerSecond || Math.Abs(normal) > MaxDeltaVMetersPerSecond || Math.Abs(radial) > MaxDeltaVMetersPerSecond)
                return "each delta-v component must be -" + MaxDeltaVMetersPerSecond + ".." + MaxDeltaVMetersPerSecond + " m/s";
            if (Math.Sqrt(prograde * prograde + normal * normal + radial * radial) > MaxDeltaVMetersPerSecond) return "the delta-v magnitude must be at most " + MaxDeltaVMetersPerSecond + " m/s";
            return null;
        }

        /// <summary>Null when timeSeconds fits the reference (absolute: a UT; in_seconds: seconds ahead; apoapsis/periapsis: an optional offset), else the detail.</summary>
        public static string CheckTime(string reference, double? timeSeconds)
        {
            if (timeSeconds.HasValue && !IsFinite(timeSeconds.Value)) return "timeSeconds must be a finite number";
            switch (reference)
            {
                case "absolute":
                    if (!timeSeconds.HasValue) return "timeSeconds (the universal time of the node) is required with timeReference absolute";
                    return timeSeconds.Value < 0 ? "timeSeconds must be a universal time, 0 or more" : null;
                case "in_seconds":
                    if (!timeSeconds.HasValue) return "timeSeconds (seconds from now) is required with timeReference in_seconds";
                    return timeSeconds.Value < MinLeadSeconds || timeSeconds.Value > MaxHorizonSeconds ? "timeSeconds must be " + MinLeadSeconds + ".." + MaxHorizonSeconds + " with in_seconds" : null;
                case "apoapsis": case "periapsis":
                    return timeSeconds.HasValue && Math.Abs(timeSeconds.Value) > MaxOffsetSeconds ? "timeSeconds (an offset from the " + reference + ") must be -" + MaxOffsetSeconds + ".." + MaxOffsetSeconds : null;
                default:
                    return "timeReference must be one of " + string.Join(", ", TimeReferences);
            }
        }
    }

    /// <summary>Reason codes of the navigation tools, beyond the control, operation, flight and autopilot codes.</summary>
    public static class NavigationReasons
    {
        public const string NodeNotFound = "node_not_found";
        public const string TooManyNodes = "too_many_nodes";
        public const string FlightPlanningLocked = "flight_planning_locked";
        public const string TimeUnavailable = "time_unavailable";
        public const string NodeEditFailed = "node_edit_failed";
        public const string NoSoiChange = "no_soi_change";
        public const string WarpModePhysics = "warp_mode_physics";
        public const string WarpNotAllowedHere = "warp_not_allowed_here";
        public const string WarpTargetReached = "warp_target_reached";
    }
}
