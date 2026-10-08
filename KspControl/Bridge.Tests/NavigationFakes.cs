using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;

namespace KspControl.BridgeTests
{
    /// <summary>
    /// A scriptable navigation port: a clock that rails warp advances (UT += rate x real seconds per frame), a node list the edits change, and a flight plan
    /// computed from the nodes by <see cref="PlanFor"/>. Rate requests can be vetoed or clamped like LunaMP's prefix and the stock limits do.
    /// </summary>
    internal sealed class FakeNavigationPort : INavigationPort
    {
        public static readonly float[] StockRates = { 1f, 5f, 10f, 50f, 100f, 1000f, 10000f, 100000f };
        public bool InFlightValue = true, Owned = true, Solver = true, PlanningUnlocked = true, Rails = true;
        public string VesselId = "vessel-1";
        public double UniversalTime = 1000000, TimeToApoapsis = 600, TimeToPeriapsis = 1200, Eccentricity = 0.01, Throttle, Altitude = 100000, AtmosphereTop = 70000;
        public string Situation = "ORBITING";
        public int Index, AltitudeLimit = 7;
        public List<OrbitPatch> Coast = new List<OrbitPatch> { new OrbitPatch { ReferenceBody = "Kerbin", StartUniversalTime = 1000000, EndUniversalTime = 1002000, ApoapsisAltitude = 101000, PeriapsisAltitude = 99000, Eccentricity = 0.01, StartTransition = "INITIAL", EndTransition = "FINAL", AtmosphereTopMeters = 70000 } };
        public List<ManeuverNodeInfo> Nodes = new List<ManeuverNodeInfo>();
        public Func<List<ManeuverNodeInfo>, List<OrbitPatch>> PlanFor = nodes => new List<OrbitPatch>();
        /// <summary>Every SetWarpRate call as "index:instant".</summary>
        public List<string> RateCalls = new List<string>();
        public List<string> Edits = new List<string>();
        /// <summary>Rate requests above this index are clamped to it (the stock limits). Below zero: every request is ignored (LunaMP's veto).</summary>
        public int AcceptUpTo = int.MaxValue;
        public bool VetoAll, EditThrows, EditFails;
        public int Reads;

        public bool InFlight { get { return InFlightValue; } }

        public void Advance(double realSeconds) { UniversalTime += StockRates[Index] * realSeconds; }

        public TrajectoryReading ReadTrajectory(int maxPatches)
        {
            Reads++;
            if (!InFlightValue) return null;
            if (!Owned) return new TrajectoryReading { Owned = false };
            var sorted = Nodes.OrderBy(n => n.UniversalTime).ToList();
            var planned = sorted.Count == 0 ? new List<OrbitPatch>() : PlanFor(sorted) ?? new List<OrbitPatch>();
            return new TrajectoryReading
            {
                Owned = true, VesselId = VesselId, Body = "Kerbin", Situation = Situation, UniversalTime = UniversalTime,
                TimeToApoapsis = TimeToApoapsis, TimeToPeriapsis = TimeToPeriapsis, Eccentricity = Eccentricity,
                SolverAvailable = Solver, FlightPlanningUnlocked = PlanningUnlocked,
                Coast = Coast.Take(maxPatches).ToList(), CoastTruncated = Coast.Count > maxPatches,
                Planned = planned.Take(maxPatches).ToList(), PlannedTruncated = planned.Count > maxPatches,
                Nodes = sorted.Take(16).Select(Copy).ToList(), NodeCount = sorted.Count
            };
        }

        public WarpReading ReadWarp()
        {
            if (!InFlightValue) return null;
            return new WarpReading
            {
                VesselId = VesselId, UniversalTime = UniversalTime, Rails = Rails, Index = Index, Rate = StockRates[Index], Rates = (float[])StockRates.Clone(),
                AltitudeLimitIndex = AltitudeLimit, Throttle = Throttle, AltitudeMeters = Altitude, AtmosphereTopMeters = AtmosphereTop, Situation = Situation
            };
        }

        private static ManeuverNodeInfo Copy(ManeuverNodeInfo n) { return new ManeuverNodeInfo { UniversalTime = n.UniversalTime, Prograde = n.Prograde, Normal = n.Normal, Radial = n.Radial }; }

        private NodeEditOutcome Check()
        {
            if (EditThrows) throw new InvalidOperationException("solver exploded");
            return EditFails ? NodeEditOutcome.Fail("the solver did not create the node") : null;
        }

        public NodeEditOutcome AddNode(double universalTime, double prograde, double normal, double radial)
        {
            var failed = Check(); if (failed != null) return failed;
            var node = new ManeuverNodeInfo { UniversalTime = universalTime, Prograde = prograde, Normal = normal, Radial = radial };
            Nodes.Add(node); Edits.Add("add:" + universalTime + ":" + prograde + ":" + normal + ":" + radial);
            return new NodeEditOutcome { Ok = true, Index = Nodes.OrderBy(n => n.UniversalTime).ToList().IndexOf(node) };
        }

        public NodeEditOutcome UpdateNode(int index, double universalTime, double prograde, double normal, double radial)
        {
            var failed = Check(); if (failed != null) return failed;
            var node = Nodes.OrderBy(n => n.UniversalTime).ToList()[index];
            node.UniversalTime = universalTime; node.Prograde = prograde; node.Normal = normal; node.Radial = radial;
            Edits.Add("update:" + index + ":" + universalTime + ":" + prograde + ":" + normal + ":" + radial);
            return new NodeEditOutcome { Ok = true, Index = Nodes.OrderBy(n => n.UniversalTime).ToList().IndexOf(node) };
        }

        public NodeEditOutcome DeleteNodes(int index)
        {
            var failed = Check(); if (failed != null) return failed;
            if (index < 0) Nodes.Clear(); else Nodes.Remove(Nodes.OrderBy(n => n.UniversalTime).ToList()[index]);
            Edits.Add("delete:" + index);
            return new NodeEditOutcome { Ok = true };
        }

        public void SetWarpRate(int index, bool instant)
        {
            RateCalls.Add(index + ":" + (instant ? "instant" : "smooth"));
            if (VetoAll) return;
            Index = Math.Max(0, Math.Min(Math.Min(index, AcceptUpTo), StockRates.Length - 1));
        }
    }
}
