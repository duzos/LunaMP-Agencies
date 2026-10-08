using System.Collections.Generic;

namespace KspControl.Bridge
{
    // Pure data and the port of the navigation layer (orbit prediction, maneuver nodes, rails warp to a time). No Unity or KSP types, so this
    // file links into the unit tests; UnityNavigationPort is the only implementation. Every member is called on the main thread.

    /// <summary>One patched-conic patch, read from a KSP Orbit. Altitudes are above the reference body's sea level.</summary>
    internal sealed class OrbitPatch
    {
        public string ReferenceBody { get; set; }
        public double StartUniversalTime { get; set; }
        public double EndUniversalTime { get; set; }
        /// <summary>NaN on an open (hyperbolic) patch.</summary>
        public double ApoapsisAltitude { get; set; }
        public double PeriapsisAltitude { get; set; }
        public double InclinationDegrees { get; set; }
        public double Eccentricity { get; set; }
        /// <summary>The stock Orbit.PatchTransitionType names: INITIAL, FINAL, ENCOUNTER, ESCAPE, MANEUVER, IMPACT.</summary>
        public string StartTransition { get; set; }
        public string EndTransition { get; set; }
        /// <summary>The reference body's atmosphere height, 0 when it has none.</summary>
        public double AtmosphereTopMeters { get; set; }
    }

    /// <summary>A stock maneuver node. The delta-v is in the node frame: prograde, normal, radial (out), metres per second.</summary>
    internal sealed class ManeuverNodeInfo
    {
        public double UniversalTime { get; set; }
        public double Prograde { get; set; }
        public double Normal { get; set; }
        public double Radial { get; set; }
    }

    /// <summary>Everything flight.orbit_prediction and the node tools need, read in one frame.</summary>
    internal sealed class TrajectoryReading
    {
        public string VesselId { get; set; }
        /// <summary>The disclosure decision (facade v1): nothing else is filled for a vessel the agency does not own.</summary>
        public bool Owned { get; set; }
        public string Body { get; set; }
        public string Situation { get; set; }
        public double UniversalTime { get; set; }
        /// <summary>Of the orbit the vessel is on now.</summary>
        public double TimeToApoapsis { get; set; }
        public double TimeToPeriapsis { get; set; }
        public double Eccentricity { get; set; }
        /// <summary>The vessel has a patched-conic solver (maneuver nodes are possible at all).</summary>
        public bool SolverAvailable { get; set; }
        /// <summary>The career Mission Control level allows flight planning (always true outside career).</summary>
        public bool FlightPlanningUnlocked { get; set; } = true;
        /// <summary>The vessel's own orbit chain (vessel.orbit and its nextPatch links). KSP ends it at the first maneuver node.</summary>
        public List<OrbitPatch> Coast { get; set; } = new List<OrbitPatch>();
        public bool CoastTruncated { get; set; }
        /// <summary>The solver's flight plan with every node applied. Empty when there is no node.</summary>
        public List<OrbitPatch> Planned { get; set; } = new List<OrbitPatch>();
        public bool PlannedTruncated { get; set; }
        /// <summary>Sorted by time (the solver's order): the index is the node index the node tools take.</summary>
        public List<ManeuverNodeInfo> Nodes { get; set; } = new List<ManeuverNodeInfo>();
        public int NodeCount { get; set; }
    }

    /// <summary>The warp state and the few vessel values a warp step needs.</summary>
    internal sealed class WarpReading
    {
        public string VesselId { get; set; }
        public double UniversalTime { get; set; }
        /// <summary>True in rails (HIGH) mode, false in physics (LOW) mode.</summary>
        public bool Rails { get; set; } = true;
        public int Index { get; set; }
        public double Rate { get; set; }
        public float[] Rates { get; set; } = new float[0];
        /// <summary>The highest rails index the altitude allows, or -1 when unknown.</summary>
        public int AltitudeLimitIndex { get; set; } = -1;
        public double Throttle { get; set; }
        public double AltitudeMeters { get; set; }
        public double AtmosphereTopMeters { get; set; }
        public string Situation { get; set; }
    }

    internal sealed class NodeEditOutcome
    {
        public bool Ok { get; set; }
        /// <summary>The node's index after the edit (sorted by time), or -1.</summary>
        public int Index { get; set; } = -1;
        public string Detail { get; set; }
        public static NodeEditOutcome Fail(string detail) { return new NodeEditOutcome { Ok = false, Detail = detail }; }
    }

    /// <summary>The game side of the navigation tools: the active vessel only, re-read on every call. Never throws for a missing vessel.</summary>
    internal interface INavigationPort
    {
        bool InFlight { get; }
        /// <summary>The active vessel's trajectory with up to <paramref name="maxPatches"/> patches per chain, or null when there is no vessel.</summary>
        TrajectoryReading ReadTrajectory(int maxPatches);
        /// <summary>The warp state, or null when there is no vessel.</summary>
        WarpReading ReadWarp();
        /// <summary>Adds a stock node and updates the flight plan at once.</summary>
        NodeEditOutcome AddNode(double universalTime, double prograde, double normal, double radial);
        /// <summary>Sets the time and delta-v of the node at <paramref name="index"/> and updates the flight plan at once.</summary>
        NodeEditOutcome UpdateNode(int index, double universalTime, double prograde, double normal, double radial);
        /// <summary>Removes the node at <paramref name="index"/>, or every node when index is negative.</summary>
        NodeEditOutcome DeleteNodes(int index);
        /// <summary>Calls the stock rate setter (LunaMP's prefix may veto it). Read the warp state afterwards.</summary>
        void SetWarpRate(int index, bool instant);
    }
}
