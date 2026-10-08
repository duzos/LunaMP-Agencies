using System;
using System.Collections.Generic;
using KspControl.Contracts;

namespace KspControl.Bridge
{
    /// <summary>
    /// The only navigation code that touches KSP: the patched-conic chain, the solver's flight plan, stock maneuver nodes and the stock rate setter.
    /// Thin on purpose; every decision lives in the pure services. Main thread only. Not linked into the unit tests (live-verification list in the README).
    /// Every member used is checked against Assembly-CSharp: PatchedConicSolver.maneuverNodes/flightPlan/AddManeuverNode/UpdateFlightPlan,
    /// ManeuverNode.UT/DeltaV/OnGizmoUpdated/RemoveSelf, Orbit.StartUT/EndUT/ApA/PeA/patchStart/EndTransition/nextPatch/activePatch, TimeWarp.SetRate.
    /// </summary>
    internal sealed class UnityNavigationPort : INavigationPort
    {
        private readonly Func<Vessel, bool> mayInspect;
        public UnityNavigationPort(Func<Vessel, bool> mayInspect) { this.mayInspect = mayInspect ?? (v => false); }

        public bool InFlight { get { return HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null; } }

        private static Vessel Active() { return HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null; }

        public TrajectoryReading ReadTrajectory(int maxPatches)
        {
            var vessel = Active();
            if (vessel == null) return null;
            if (!mayInspect(vessel)) return new TrajectoryReading { Owned = false };
            var orbit = vessel.orbit;
            var reading = new TrajectoryReading
            {
                Owned = true, VesselId = vessel.id.ToString(), Body = vessel.mainBody == null ? null : vessel.mainBody.bodyName, Situation = vessel.situation.ToString(),
                UniversalTime = Planetarium.GetUniversalTime(),
                TimeToApoapsis = orbit == null ? double.NaN : orbit.timeToAp, TimeToPeriapsis = orbit == null ? double.NaN : orbit.timeToPe,
                Eccentricity = orbit == null ? double.NaN : orbit.eccentricity
            };
            bool truncated;
            reading.Coast = Chain(orbit, maxPatches, out truncated); reading.CoastTruncated = truncated;
            var solver = vessel.patchedConicSolver;
            reading.SolverAvailable = solver != null && solver.maneuverNodes != null;
            reading.FlightPlanningUnlocked = FlightPlanningUnlocked();
            if (!reading.SolverAvailable) return reading;
            var nodes = Sorted(solver);
            reading.NodeCount = nodes.Count;
            for (var i = 0; i < nodes.Count && i < NavigationLimits.MaxNodes; i++) reading.Nodes.Add(Describe(nodes[i]));
            if (nodes.Count > 0 && solver.flightPlan != null)
            {
                foreach (var patch in solver.flightPlan)
                {
                    if (patch == null) continue;
                    if (reading.Planned.Count >= maxPatches) { reading.PlannedTruncated = true; break; }
                    reading.Planned.Add(Patch(patch));
                }
            }
            return reading;
        }

        /// <summary>The vessel's own chain: each patch's nextPatch while the patch ends in an encounter or an escape and the next one was computed this frame.</summary>
        private static List<OrbitPatch> Chain(Orbit first, int maxPatches, out bool truncated)
        {
            var result = new List<OrbitPatch>();
            truncated = false;
            var orbit = first;
            while (orbit != null)
            {
                if (result.Count >= maxPatches) { truncated = true; break; }
                result.Add(Patch(orbit));
                var continues = orbit.patchEndTransition == Orbit.PatchTransitionType.ENCOUNTER || orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE;
                orbit = continues && orbit.nextPatch != null && orbit.nextPatch.activePatch ? orbit.nextPatch : null;
            }
            return result;
        }

        private static OrbitPatch Patch(Orbit orbit)
        {
            var body = orbit.referenceBody;
            return new OrbitPatch
            {
                ReferenceBody = body == null ? null : body.bodyName,
                StartUniversalTime = orbit.StartUT, EndUniversalTime = orbit.EndUT,
                ApoapsisAltitude = orbit.eccentricity < 1 && body != null ? orbit.ApA : double.NaN,
                PeriapsisAltitude = body == null ? double.NaN : orbit.PeA,
                InclinationDegrees = orbit.inclination, Eccentricity = orbit.eccentricity,
                StartTransition = orbit.patchStartTransition.ToString(), EndTransition = orbit.patchEndTransition.ToString(),
                AtmosphereTopMeters = body != null && body.atmosphere ? body.atmosphereDepth : 0
            };
        }

        /// <summary>Stock DeltaV is (radial out, normal, prograde): the frame CheckNextManeuver builds from the velocity and the orbit normal.</summary>
        internal static ManeuverNodeInfo Describe(ManeuverNode node)
        {
            return new ManeuverNodeInfo { UniversalTime = node.UT, Radial = node.DeltaV.x, Normal = node.DeltaV.y, Prograde = node.DeltaV.z };
        }

        private static List<ManeuverNode> Sorted(PatchedConicSolver solver)
        {
            var nodes = new List<ManeuverNode>();
            foreach (var node in solver.maneuverNodes) if (node != null) nodes.Add(node);
            // The solver sorts by time on every flight-plan update; sort again so the index never depends on when that last ran.
            nodes.Sort((a, b) => a.UT.CompareTo(b.UT));
            return nodes;
        }

        private static bool FlightPlanningUnlocked()
        {
            try
            {
                var variables = GameVariables.Instance;
                return variables == null || variables.UnlockedFlightPlanning(ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl));
            }
            catch (Exception) { return true; }
        }

        private static PatchedConicSolver Solver(out string problem)
        {
            problem = null;
            var vessel = Active();
            if (vessel == null) { problem = "there is no active vessel"; return null; }
            if (vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes == null) { problem = "the vessel has no patched-conic solver"; return null; }
            return vessel.patchedConicSolver;
        }

        public NodeEditOutcome AddNode(double universalTime, double prograde, double normal, double radial)
        {
            string problem;
            var solver = Solver(out problem);
            if (solver == null) return NodeEditOutcome.Fail(problem);
            var node = solver.AddManeuverNode(universalTime);
            if (node == null) return NodeEditOutcome.Fail("the solver did not create the node");
            // OnGizmoUpdated sets DeltaV and UT, runs UpdateFlightPlan and refreshes an attached gizmo: the edit takes effect at once.
            node.OnGizmoUpdated(new Vector3d(radial, normal, prograde), universalTime);
            return new NodeEditOutcome { Ok = true, Index = Sorted(solver).IndexOf(node) };
        }

        public NodeEditOutcome UpdateNode(int index, double universalTime, double prograde, double normal, double radial)
        {
            string problem;
            var solver = Solver(out problem);
            if (solver == null) return NodeEditOutcome.Fail(problem);
            var nodes = Sorted(solver);
            if (index < 0 || index >= nodes.Count) return NodeEditOutcome.Fail("there is no node " + index);
            var node = nodes[index];
            node.OnGizmoUpdated(new Vector3d(radial, normal, prograde), universalTime);
            return new NodeEditOutcome { Ok = true, Index = Sorted(solver).IndexOf(node) };
        }

        public NodeEditOutcome DeleteNodes(int index)
        {
            string problem;
            var solver = Solver(out problem);
            if (solver == null) return NodeEditOutcome.Fail(problem);
            var nodes = Sorted(solver);
            if (index >= nodes.Count) return NodeEditOutcome.Fail("there is no node " + index);
            // RemoveSelf detaches the gizmo and the map target, then the solver removes the node and re-plans.
            if (index >= 0) nodes[index].RemoveSelf();
            else for (var i = nodes.Count - 1; i >= 0; i--) nodes[i].RemoveSelf();
            return new NodeEditOutcome { Ok = true };
        }

        public WarpReading ReadWarp()
        {
            var vessel = Active();
            if (vessel == null) return null;
            var fetch = TimeWarp.fetch;
            var rails = TimeWarp.WarpMode == TimeWarp.Modes.HIGH;
            var body = vessel.mainBody;
            var reading = new WarpReading
            {
                VesselId = vessel.id.ToString(), UniversalTime = Planetarium.GetUniversalTime(), Rails = rails,
                Index = TimeWarp.CurrentRateIndex, Rate = TimeWarp.CurrentRate,
                Rates = fetch == null || fetch.warpRates == null ? new float[0] : (float[])fetch.warpRates.Clone(),
                Throttle = vessel.ctrlState == null ? 0 : vessel.ctrlState.mainThrottle,
                AltitudeMeters = vessel.altitude, AtmosphereTopMeters = body != null && body.atmosphere ? body.atmosphereDepth : 0,
                Situation = vessel.situation.ToString()
            };
            if (fetch != null && body != null)
            {
                try { reading.AltitudeLimitIndex = vessel.LandedOrSplashed ? reading.Rates.Length - 1 : fetch.GetMaxRateForAltitude(vessel.altitude, body); }
                catch (Exception) { reading.AltitudeLimitIndex = -1; }
            }
            return reading;
        }

        /// <summary>The stock setter, never the fields: LunaMP's Harmony prefix on TimeWarp.SetRate applies the server's warp rules and may veto it.</summary>
        public void SetWarpRate(int index, bool instant) { TimeWarp.SetRate(index, instant, false); }
    }
}
