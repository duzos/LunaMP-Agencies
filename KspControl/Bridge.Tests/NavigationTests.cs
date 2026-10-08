using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class NavigationMathTests
    {
        private const double Now = 1000000;

        private static NavigationMath.TimeResolution Resolve(string reference, double? seconds, double eccentricity = 0.01)
        { return NavigationMath.ResolveTime(reference, seconds, Now, 600, 1200, eccentricity); }

        [TestMethod] public void EachTimeReferenceResolvesToAUniversalTime()
        {
            Assert.AreEqual(Now + 5000, Resolve("absolute", Now + 5000).UniversalTime);
            Assert.AreEqual(Now + 90, Resolve("in_seconds", 90).UniversalTime);
            Assert.AreEqual(Now + 600, Resolve("apoapsis", null).UniversalTime);
            Assert.AreEqual(Now + 590, Resolve("apoapsis", -10).UniversalTime);
            Assert.AreEqual(Now + 1230, Resolve("periapsis", 30).UniversalTime);
        }

        [TestMethod] public void AnOpenOrbitHasNoApoapsisTime()
        {
            var r = Resolve("apoapsis", null, eccentricity: 1.2);
            Assert.AreEqual(NavigationReasons.TimeUnavailable, r.Reason);
            Assert.IsTrue(Resolve("periapsis", null, eccentricity: 1.2).Ok);
        }

        [TestMethod] public void ATimeInThePastOrTooFarAheadIsInvalid()
        {
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("absolute", Now - 1).Reason);
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("absolute", Now + 0.5).Reason);
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("absolute", Now + NavigationLimits.MaxHorizonSeconds + 1).Reason);
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("in_seconds", 0.2).Reason);
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("apoapsis", -700).Reason, "an offset that lands before now");
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("apoapsis", NavigationLimits.MaxOffsetSeconds + 1).Reason);
        }

        [TestMethod] public void MissingOrUnknownTimeArgumentsAreInvalid()
        {
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("absolute", null).Reason);
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("in_seconds", null).Reason);
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("node", 5).Reason);
            Assert.AreEqual(ControlReasons.InvalidArgument, Resolve("absolute", double.NaN).Reason);
        }

        [TestMethod] public void DeltaVIsBoundedPerComponentAndAsAMagnitude()
        {
            Assert.IsNull(NavigationLimits.CheckDeltaV(860, 0, 0));
            Assert.IsNull(NavigationLimits.CheckDeltaV(-3000, 0, 0));
            Assert.IsNotNull(NavigationLimits.CheckDeltaV(3000.1, 0, 0));
            Assert.IsNotNull(NavigationLimits.CheckDeltaV(2000, 2000, 2000));
            Assert.IsNotNull(NavigationLimits.CheckDeltaV(double.NaN, 0, 0));
        }

        [DataTestMethod]
        [DataRow(1000000.0, int.MaxValue, 100000f, 7, 7)]
        [DataRow(200000.0, int.MaxValue, 100000f, 7, 6)]
        [DataRow(100.0, int.MaxValue, 100000f, 7, 2)]
        [DataRow(2.0, int.MaxValue, 100000f, 7, 0)]
        [DataRow(1000000.0, int.MaxValue, 1000f, 7, 5)]
        [DataRow(1000000.0, int.MaxValue, 100000f, 4, 4)]
        [DataRow(1000000.0, 3, 100000f, 7, 3)]
        [DataRow(1000000.0, int.MaxValue, 100000f, 0, 0)]
        public void TheWarpRateIsTheHighestThatCannotOvershoot(double remaining, int ceiling, float cap, int altitudeLimit, int expected)
        {
            Assert.AreEqual(expected, NavigationMath.ChooseWarpIndex(FakeNavigationPort.StockRates, remaining, ceiling, cap, altitudeLimit, 3));
        }

        [TestMethod] public void AnUnknownAltitudeLimitDoesNotCapTheRate()
        { Assert.AreEqual(7, NavigationMath.ChooseWarpIndex(FakeNavigationPort.StockRates, 1e7, int.MaxValue, 100000f, -1, 3)); }

        [TestMethod] public void NothingLeftMeansRealTime()
        {
            Assert.AreEqual(0, NavigationMath.ChooseWarpIndex(FakeNavigationPort.StockRates, 0, int.MaxValue, 100000f, 7, 3));
            Assert.AreEqual(0, NavigationMath.ChooseWarpIndex(FakeNavigationPort.StockRates, -5, int.MaxValue, 100000f, 7, 3));
            Assert.AreEqual(0, NavigationMath.ChooseWarpIndex(new float[0], 1e6, int.MaxValue, 100000f, 7, 3));
        }
    }

    [TestClass]
    public class OrbitPredictionTests
    {
        internal static OrbitPatch P(string body, double start, double end, double pe, string endTransition, double ecc = 0.5, double ap = 12000000, string startTransition = "INITIAL", double atmosphere = 0)
        {
            return new OrbitPatch { ReferenceBody = body, StartUniversalTime = start, EndUniversalTime = end, PeriapsisAltitude = pe, ApoapsisAltitude = ecc < 1 ? ap : double.NaN, Eccentricity = ecc, InclinationDegrees = 0.1, StartTransition = startTransition, EndTransition = endTransition, AtmosphereTopMeters = atmosphere };
        }

        /// <summary>The flight plan of a good free return: Kerbin transfer, Mun flyby, back to Kerbin with periapsis in the atmosphere.</summary>
        internal static List<OrbitPatch> FreeReturn(double now, double munPeriapsis = 30000, double returnPeriapsis = 35000)
        {
            return new List<OrbitPatch>
            {
                P("Kerbin", now, now + 1000, 99000, "MANEUVER", 0.01, 101000, "INITIAL", 70000),
                P("Kerbin", now + 1000, now + 21000, 99000, "ENCOUNTER", 0.96, 11500000, "MANEUVER", 70000),
                P("Mun", now + 21000, now + 40000, munPeriapsis, "ESCAPE", 1.4, 0, "ENCOUNTER"),
                P("Kerbin", now + 40000, now + 90000, returnPeriapsis, "FINAL", 0.97, 11000000, "ESCAPE", 70000)
            };
        }

        private static TrajectoryReading Reading(List<OrbitPatch> coast, List<OrbitPatch> planned, params ManeuverNodeInfo[] nodes)
        {
            return new TrajectoryReading { Owned = true, VesselId = "vessel-1", Body = "Kerbin", Situation = "ORBITING", UniversalTime = 1000, SolverAvailable = true, Coast = coast, Planned = planned, Nodes = nodes.ToList(), NodeCount = nodes.Length };
        }

        [TestMethod] public void AFreeReturnPlanReportsTheMunEncounterAndTheReturnPeriapsis()
        {
            var coast = new List<OrbitPatch> { P("Kerbin", 1000, 2000, 99000, "MANEUVER", 0.01, 101000, "INITIAL", 70000) };
            var data = OrbitPrediction.Build(Reading(coast, FreeReturn(1000), new ManeuverNodeInfo { UniversalTime = 2000, Prograde = 860 }));
            var a = data["assessment"];
            Assert.AreEqual("withNodes", (string)a["basis"]);
            Assert.AreEqual(true, (bool)a["munEncounter"]);
            Assert.AreEqual(30000.0, (double)a["munClosestApproachAltitudeMetres"]);
            Assert.AreEqual(35000.0, (double)a["returnPeriapsisKerbin"]);
            Assert.AreEqual(true, (bool)a["returnsIntoAtmosphere"]);
            Assert.AreEqual(false, (bool)a["munImpact"]);
            Assert.AreEqual(2, (int)a["encounterPatchIndex"]); Assert.AreEqual(3, (int)a["returnPatchIndex"]);
            Assert.AreEqual(21000.0, (double)a["timeToSoiChangeSeconds"]);
            Assert.AreEqual(false, (bool)data["withoutNodes"]["assessment"]["munEncounter"], "the coast ends at the node");
            Assert.AreEqual(true, (bool)data["withoutNodes"]["endsAtFirstNode"]);
            Assert.AreEqual(JTokenType.Null, data["timeToSoiChangeSeconds"].Type, "the coast has no SOI change");
            var node = data["nodes"][0];
            Assert.AreEqual(860.0, (double)node["deltaV"]["progradeMetresPerSecond"]); Assert.AreEqual(860.0, (double)node["deltaVMagnitudeMetresPerSecond"]); Assert.AreEqual(1000.0, (double)node["secondsFromNow"]);
            var mun = data["withNodes"]["patches"][2];
            Assert.AreEqual("Mun", (string)mun["referenceBody"]); Assert.AreEqual(JTokenType.Null, mun["apoapsisAltitudeMetres"].Type, "an open patch has no apoapsis");
            Assert.AreEqual("ESCAPE", (string)mun["endTransition"]); Assert.AreEqual(41000.0, (double)mun["endUniversalTimeSeconds"]);
        }

        [TestMethod] public void WithoutNodesTheCoastIsTheBasis()
        {
            var coast = new List<OrbitPatch> { P("Kerbin", 1000, 50000, 99000, "ENCOUNTER", 0.96), P("Mun", 50000, 60000, -5000, "FINAL", 1.3) };
            var data = OrbitPrediction.Build(Reading(coast, new List<OrbitPatch>()));
            Assert.AreEqual(JTokenType.Null, data["withNodes"].Type);
            Assert.AreEqual("withoutNodes", (string)data["assessment"]["basis"]);
            Assert.AreEqual(true, (bool)data["assessment"]["munImpact"]);
            Assert.AreEqual(JTokenType.Null, data["assessment"]["returnPeriapsisKerbin"].Type);
            Assert.AreEqual(49000.0, (double)data["timeToSoiChangeSeconds"]);
            Assert.AreEqual(false, (bool)data["withoutNodes"]["endsAtFirstNode"]);
            Assert.AreEqual(JTokenType.Null, data["withoutNodes"]["patches"][1]["endUniversalTimeSeconds"].Type, "KSP stores the period in EndUT of an open final patch");
            Assert.AreEqual(true, (bool)data["withoutNodes"]["patches"][1]["periapsisBelowSurface"]);
        }

        [TestMethod] public void ReturnAboveTheAtmosphereIsNotACapture()
        {
            var data = OrbitPrediction.Build(Reading(new List<OrbitPatch>(), FreeReturn(1000, returnPeriapsis: 250000), new ManeuverNodeInfo { UniversalTime = 2000, Prograde = 850 }));
            Assert.AreEqual(false, (bool)data["assessment"]["returnsIntoAtmosphere"]);
        }

        [TestMethod] public void PatchesAndNodesAreBounded()
        {
            var many = Enumerable.Range(0, 9).Select(i => P(i % 2 == 0 ? "Kerbin" : "Mun", i * 100, i * 100 + 100, 1000, "ENCOUNTER")).ToList();
            var nodes = Enumerable.Range(0, 20).Select(i => new ManeuverNodeInfo { UniversalTime = 2000 + i }).ToArray();
            var data = OrbitPrediction.Build(Reading(many, many, nodes));
            Assert.AreEqual(NavigationLimits.MaxPatches, ((JArray)data["withoutNodes"]["patches"]).Count);
            Assert.AreEqual(true, (bool)data["withoutNodes"]["truncated"]);
            Assert.AreEqual(NavigationLimits.MaxNodes, ((JArray)data["nodes"]).Count);
            Assert.AreEqual(true, (bool)data["nodesTruncated"]); Assert.AreEqual(20, (int)data["nodeCount"]);
        }

        [TestMethod] public void NonFiniteNumbersAreNull()
        {
            var coast = new List<OrbitPatch> { P("Kerbin", double.NaN, double.PositiveInfinity, double.NaN, "FINAL") };
            var data = OrbitPrediction.Build(Reading(coast, new List<OrbitPatch>()));
            var patch = data["withoutNodes"]["patches"][0];
            Assert.AreEqual(JTokenType.Null, patch["startUniversalTimeSeconds"].Type); Assert.AreEqual(JTokenType.Null, patch["periapsisAltitudeMetres"].Type);
        }

        private sealed class Port : INavigationPort
        {
            public bool InFlightValue = true; public TrajectoryReading Reading;
            public bool InFlight { get { return InFlightValue; } }
            public TrajectoryReading ReadTrajectory(int maxPatches) { return Reading; }
            public WarpReading ReadWarp() { return null; }
            public NodeEditOutcome AddNode(double a, double b, double c, double d) { throw new InvalidOperationException(); }
            public NodeEditOutcome UpdateNode(int i, double a, double b, double c, double d) { throw new InvalidOperationException(); }
            public NodeEditOutcome DeleteNodes(int i) { throw new InvalidOperationException(); }
            public void SetWarpRate(int i, bool instant) { throw new InvalidOperationException(); }
        }

        [TestMethod] public void ThePredictionAnswersOnlyForAnOwnedVesselInFlight()
        {
            var port = new Port();
            var service = new OrbitPredictionService(port, () => "epoch-1");
            port.InFlightValue = false; Assert.AreEqual(FlightReasons.FlightUnavailable, service.Predict().Reason);
            port.InFlightValue = true; port.Reading = null; Assert.AreEqual(FlightReasons.FlightUnavailable, service.Predict().Reason);
            port.Reading = new TrajectoryReading { Owned = false }; Assert.AreEqual(FlightReasons.VesselNotOwned, service.Predict().Reason);
            port.Reading = Reading(new List<OrbitPatch> { P("Kerbin", 1000, 2000, 99000, "FINAL") }, new List<OrbitPatch>());
            var result = service.Predict();
            Assert.IsNull(result.Reason); Assert.AreEqual("epoch-1", (string)result.Data["epoch"]); Assert.AreEqual("vessel:vessel-1", (string)result.Data["vessel"]["entity"]);
        }

        [TestMethod] public void FlightStateCarriesTheNextNodeAndTheSoiTiming()
        {
            var service = new FlightStateService(null, () => "epoch-1");
            var snapshot = new FlightSnapshot
            {
                Owned = true, VesselId = "vessel-1", UniversalTime = 1000, NodeCount = 2, NextNode = new ManeuverNodeInfo { UniversalTime = 1600, Prograde = 600, Normal = 0, Radial = 800 },
                Orbit = new FlightOrbit { ReferenceBody = "Kerbin", TimeToSoiChange = 21000, PatchEndTransition = "ENCOUNTER" }
            };
            var data = service.Build(snapshot);
            Assert.AreEqual(2, (int)data["maneuver"]["nodeCount"]);
            Assert.AreEqual(1000.0, (double)data["maneuver"]["nextNode"]["deltaVMagnitudeMetresPerSecond"]);
            Assert.AreEqual(600.0, (double)data["maneuver"]["nextNode"]["secondsFromNow"]);
            Assert.AreEqual(21000.0, (double)data["orbit"]["timeToSoiChangeSeconds"]);
            snapshot.NextNode = null; snapshot.NodeCount = 0; snapshot.Orbit.TimeToSoiChange = double.NaN;
            data = service.Build(snapshot);
            Assert.AreEqual(JTokenType.Null, data["maneuver"]["nextNode"].Type); Assert.AreEqual(JTokenType.Null, data["orbit"]["timeToSoiChangeSeconds"].Type);
        }
    }

    [TestClass]
    public class NodeToolTests
    {
        private AutopilotRig rig;
        private FakeNavigationPort nav;

        [TestInitialize] public void Setup()
        {
            rig = new AutopilotRig(); nav = rig.Navigation;
            // A Mun encounter with a good free return only for a first-node prograde burn of 855..865 m/s.
            nav.PlanFor = nodes => nodes[0].Prograde >= 855 && nodes[0].Prograde <= 865 ? OrbitPredictionTests.FreeReturn(nav.UniversalTime) : new List<OrbitPatch> { OrbitPredictionTests.P("Kerbin", nav.UniversalTime, nav.UniversalTime + 90000, 99000, "FINAL", 0.9) };
        }

        private BridgeRequest Create(string id = "node-c-0001", string reference = "apoapsis", double? seconds = null, double? prograde = 860, double? normal = null, double? radial = null, string lease = "default")
        {
            var args = new JObject { ["requestId"] = id, ["timeReference"] = reference };
            if (seconds.HasValue) args["timeSeconds"] = seconds.Value;
            if (prograde.HasValue) args["prograde"] = prograde.Value;
            if (normal.HasValue) args["normal"] = normal.Value;
            if (radial.HasValue) args["radial"] = radial.Value;
            return rig.Request(AutopilotOperations.NodeCreate, args, lease);
        }

        private BridgeRequest Update(string id, int index, double? prograde = null, string reference = null, double? seconds = null)
        {
            var args = new JObject { ["requestId"] = id, ["nodeIndex"] = index };
            if (prograde.HasValue) args["prograde"] = prograde.Value;
            if (reference != null) args["timeReference"] = reference;
            if (seconds.HasValue) args["timeSeconds"] = seconds.Value;
            return rig.Request(AutopilotOperations.NodeUpdate, args);
        }

        private BridgeRequest Delete(string id, int? index = null, bool? all = null)
        {
            var args = new JObject { ["requestId"] = id };
            if (index.HasValue) args["nodeIndex"] = index.Value;
            if (all.HasValue) args["all"] = all.Value;
            return rig.Request(AutopilotOperations.NodeDelete, args);
        }

        private static void Refused(BridgeResponse response, string reason)
        {
            Assert.AreEqual("failed", response.Status, response.Data.ToString());
            Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.AreEqual(true, (bool)response.Data["notDispatched"]);
        }

        [TestMethod] public void CreatingANodeAtTheApoapsisAppliesItAndReturnsThePrediction()
        {
            var apoapsis = nav.UniversalTime + nav.TimeToApoapsis;
            var response = rig.Service.Handle(Create());
            Assert.AreEqual("completed", response.Status, response.Data.ToString());
            Assert.AreEqual("node_create", (string)response.Data["operation"]);
            Assert.AreEqual(false, (bool)response.Data["notDispatched"]);
            Assert.AreEqual(1, nav.Edits.Count);
            Assert.AreEqual(apoapsis, nav.Nodes[0].UniversalTime); Assert.AreEqual(860.0, nav.Nodes[0].Prograde); Assert.AreEqual(0.0, nav.Nodes[0].Normal);
            var plan = response.Data["plan"];
            Assert.AreEqual(0, (int)plan["node"]["index"]);
            Assert.AreEqual(apoapsis, (double)plan["node"]["universalTimeSeconds"]);
            Assert.AreEqual(true, (bool)plan["prediction"]["assessment"]["munEncounter"]);
            Assert.AreEqual(35000.0, (double)plan["prediction"]["assessment"]["returnPeriapsisKerbin"]);
            CollectionAssert.Contains(((JArray)response.Data["effects"]).Select(e => (string)e).ToArray(), "maneuver_node_created");
        }

        [TestMethod] public void AnAgentCanIterateTheBurnUntilTheFreeReturnIsRight()
        {
            Assert.AreEqual("completed", rig.Service.Handle(Create(prograde: 840)).Status);
            var miss = rig.Service.Handle(Update("node-u-0001", 0, prograde: 850));
            Assert.AreEqual(false, (bool)miss.Data["plan"]["prediction"]["assessment"]["munEncounter"]);
            Assert.AreEqual(840.0, (double)miss.Data["plan"]["node"]["before"]["deltaV"]["progradeMetresPerSecond"]);
            var hit = rig.Service.Handle(Update("node-u-0002", 0, prograde: 858));
            Assert.AreEqual("completed", hit.Status);
            Assert.AreEqual(true, (bool)hit.Data["plan"]["prediction"]["assessment"]["munEncounter"]);
            var moved = rig.Service.Handle(Update("node-u-0003", 0, reference: "in_seconds", seconds: 900));
            Assert.AreEqual(nav.UniversalTime + 900, nav.Nodes[0].UniversalTime);
            Assert.AreEqual(858.0, nav.Nodes[0].Prograde, "a time change keeps the delta-v");
            Assert.AreEqual("completed", moved.Status);
        }

        [TestMethod] public void DeletingOneOrAllNodes()
        {
            rig.Service.Handle(Create("node-c-0001", "in_seconds", 500));
            rig.Service.Handle(Create("node-c-0002", "in_seconds", 300, prograde: 100));
            Assert.AreEqual(100.0, nav.Nodes.OrderBy(n => n.UniversalTime).First().Prograde, "indices are sorted by time");
            var one = rig.Service.Handle(Delete("node-d-0001", index: 0));
            Assert.AreEqual("completed", one.Status); Assert.AreEqual(1, nav.Nodes.Count); Assert.AreEqual(860.0, nav.Nodes[0].Prograde);
            var all = rig.Service.Handle(Delete("node-d-0002", all: true));
            Assert.AreEqual("completed", all.Status); Assert.AreEqual(0, nav.Nodes.Count);
            Assert.AreEqual(JTokenType.Null, all.Data["plan"]["prediction"]["withNodes"].Type);
            Refused(rig.Service.Handle(Delete("node-d-0003", all: true)), AutopilotReasons.NoManeuverNode);
        }

        [TestMethod] public void EditingANodeThatIsNotThereIsRefused()
        {
            Refused(rig.Service.Handle(Update("node-u-0001", 0, prograde: 10)), AutopilotReasons.NoManeuverNode);
            rig.Service.Handle(Create());
            Refused(rig.Service.Handle(Update("node-u-0002", 1, prograde: 10)), NavigationReasons.NodeNotFound);
            Refused(rig.Service.Handle(Delete("node-d-0001", index: 3)), NavigationReasons.NodeNotFound);
            Assert.AreEqual(1, nav.Edits.Count);
        }

        [TestMethod] public void InvalidArgumentsAreRefusedBeforeTheGameIsTouched()
        {
            Refused(rig.Service.Handle(Create(prograde: 3001)), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Create(prograde: 2000, normal: 2000, radial: 2000)), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Create(reference: "soon")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Create(reference: "absolute")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Create(reference: "absolute", seconds: 10)), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(rig.Request(AutopilotOperations.NodeCreate, new JObject { ["requestId"] = "node-c-0009", ["timeReference"] = "apoapsis", ["extra"] = 1 })), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Update("node-u-0001", 0)), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Update("node-u-0002", 99, prograde: 1)), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Delete("node-d-0001")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Delete("node-d-0002", index: 0, all: true)), ControlReasons.InvalidArgument);
            rig.Service.Handle(Create("node-c-0100", prograde: 2900));
            // The merged node (2900 prograde kept, 1000 normal added) exceeds the magnitude bound.
            Refused(rig.Service.Handle(rig.Request(AutopilotOperations.NodeUpdate, new JObject { ["requestId"] = "node-u-0004", ["nodeIndex"] = 0, ["normal"] = 1000 })), ControlReasons.InvalidArgument);
            Assert.AreEqual(1, nav.Edits.Count, string.Join(",", nav.Edits));
        }

        [TestMethod] public void TheLeaseAndTheGrantFamilyAreRequired()
        {
            Refused(rig.Service.Handle(Create(lease: null)), ControlReasons.LeaseRequired);
            rig = new AutopilotRig(operations: new[] { FlightEffects.Family });
            Refused(rig.Service.Handle(Create()), OperationReasons.GrantOperationDenied);
            Assert.AreEqual(0, rig.Navigation.Edits.Count);
        }

        [TestMethod] public void TheGameMustAllowFlightPlanning()
        {
            nav.Solver = false; Refused(rig.Service.Handle(Create("node-c-0001")), AutopilotReasons.PlanUnavailable);
            nav.Solver = true; nav.PlanningUnlocked = false; Refused(rig.Service.Handle(Create("node-c-0002")), NavigationReasons.FlightPlanningLocked);
            nav.PlanningUnlocked = true; nav.Owned = false; Refused(rig.Service.Handle(Create("node-c-0003")), FlightReasons.VesselNotOwned);
            nav.Owned = true; nav.Eccentricity = 1.3; Refused(rig.Service.Handle(Create("node-c-0004")), NavigationReasons.TimeUnavailable);
            Assert.AreEqual(0, nav.Edits.Count);
        }

        [TestMethod] public void NodesAreBounded()
        {
            for (var i = 0; i < NavigationLimits.MaxNodes; i++) nav.Nodes.Add(new ManeuverNodeInfo { UniversalTime = nav.UniversalTime + 100 + i });
            Refused(rig.Service.Handle(Create()), NavigationReasons.TooManyNodes);
        }

        [TestMethod] public void AGameFailureDuringTheEditIsIndeterminate()
        {
            nav.EditThrows = true;
            var response = rig.Service.Handle(Create());
            Assert.AreEqual("indeterminate", response.Status); Assert.AreEqual(NavigationReasons.NodeEditFailed, response.ReasonCode);
            Assert.AreEqual(false, (bool)response.Data["notDispatched"]);
            nav.EditThrows = false; nav.EditFails = true;
            var failed = rig.Service.Handle(Create("node-c-0002"));
            Assert.AreEqual("failed", failed.Status); Assert.AreEqual(true, (bool)failed.Data["notDispatched"]);
        }

        [TestMethod] public void ARetryReplaysAndADifferentRequestUnderTheSameIdConflicts()
        {
            var first = rig.Service.Handle(Create());
            var again = rig.Service.Handle(Create());
            Assert.AreEqual(first.Data.ToString(), again.Data.ToString());
            Assert.AreEqual(1, nav.Edits.Count, "the retry did not add a second node");
            Refused(rig.Service.Handle(Create(prograde: 861)), OperationReasons.RequestIdConflict);
            var status = rig.Service.Handle(new BridgeRequest { RequestId = "w", Operation = AutopilotOperations.Status, Arguments = new JObject { ["requestId"] = "node-c-0001" } });
            Assert.AreEqual("completed", status.Status);
        }

        [TestMethod] public void ALeaseOnAnotherVesselIsRefused()
        {
            rig.Flight.Telemetry.VesselId = "vessel-2";
            Refused(rig.Service.Handle(Create()), AutopilotReasons.VesselChanged);
        }

        [TestMethod] public void WithoutTheNavigationLayerTheOperationsAreUnavailable()
        {
            var service = new MechJebService(rig.Authority, rig.Runner, rig.Jobs, rig.MechJeb, rig.Flight, () => "e", () => rig.Utc);
            Refused(service.Handle(Create()), ControlReasons.OperationUnavailable);
        }
    }

    [TestClass]
    public class WarpToTests
    {
        private AutopilotRig rig;
        private FakeNavigationPort nav;

        [TestInitialize] public void Setup() { rig = new AutopilotRig(); nav = rig.Navigation; }

        private BridgeRequest Warp(string target = "node", double? seconds = null, double? lead = null, int? index = null, string id = "warp-0001")
        {
            var args = new JObject { ["requestId"] = id, ["target"] = target };
            if (seconds.HasValue) args["timeSeconds"] = seconds.Value;
            if (lead.HasValue) args["leadSeconds"] = lead.Value;
            if (index.HasValue) args["nodeIndex"] = index.Value;
            return rig.Request(AutopilotOperations.WarpTo, args);
        }

        private void Node(double secondsAhead, double prograde = 860) { nav.Nodes.Add(new ManeuverNodeInfo { UniversalTime = nav.UniversalTime + secondsAhead, Prograde = prograde }); }

        private static void Refused(BridgeResponse response, string reason)
        {
            Assert.AreEqual("failed", response.Status, response.Data.ToString());
            Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.AreEqual(true, (bool)response.Data["notDispatched"]);
        }

        [TestMethod] public void WarpToANodeStopsAtTheLeadWithoutOvershooting()
        {
            Node(200000);
            var start = nav.UniversalTime;
            var response = rig.Service.Handle(Warp(lead: 60));
            Assert.AreEqual("running", response.Status, response.Data.ToString());
            Assert.AreEqual(start + 200000 - 60, (double)response.Data["warp"]["stopUniversalTimeSeconds"]);
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Completed, job.Status, job.Detail);
            Assert.AreEqual(0, nav.Index, "back to real time");
            Assert.IsTrue(nav.UniversalTime >= start + 200000 - 60, "reached the stop time");
            Assert.IsTrue(job.OvershootSeconds >= 0 && job.OvershootSeconds < 1, "overshoot " + job.OvershootSeconds);
            Assert.IsTrue(nav.RateCalls.Any(c => c.StartsWith("6:")), "used a high rate: " + string.Join(",", nav.RateCalls));
            Assert.IsFalse(nav.RateCalls.Any(c => c.StartsWith("7:")), "100000x would overshoot 200000 s within the margin");
            CollectionAssert.Contains(job.EffectsApplied, "warp_started");
        }

        [TestMethod] public void ALongTransferUsesTheTopRailsRate()
        {
            Node(2000000);
            Assert.AreEqual("running", rig.Service.Handle(Warp()).Status);
            rig.Run(3, 100);
            Assert.AreEqual(7, nav.Index, "100000x, above flight_warp's 1000x cap: warp_to stops itself");
            Assert.AreEqual(JobStatuses.Completed, rig.RunToEnd().Status);
        }

        [TestMethod] public void WarpToTheNextSoiChange()
        {
            nav.Coast = new List<OrbitPatch> { OrbitPredictionTests.P("Kerbin", nav.UniversalTime, nav.UniversalTime + 50000, 99000, "ENCOUNTER", 0.96), OrbitPredictionTests.P("Mun", nav.UniversalTime + 50000, nav.UniversalTime + 70000, 30000, "ESCAPE", 1.4) };
            var start = nav.UniversalTime;
            var response = rig.Service.Handle(Warp("soi", lead: 0));
            Assert.AreEqual(start + 50000, (double)response.Data["warp"]["targetUniversalTimeSeconds"]);
            Assert.AreEqual(JobStatuses.Completed, rig.RunToEnd().Status);
            Assert.IsTrue(nav.UniversalTime >= start + 50000);
        }

        [TestMethod] public void WarpToAnAbsoluteOrRelativeTime()
        {
            var start = nav.UniversalTime;
            var response = rig.Service.Handle(Warp("in_seconds", seconds: 5000, lead: 0));
            Assert.AreEqual(start + 5000, (double)response.Data["warp"]["targetUniversalTimeSeconds"]);
            Assert.AreEqual(JobStatuses.Completed, rig.RunToEnd().Status);
            var abs = rig.Service.Handle(Warp("absolute", seconds: nav.UniversalTime + 3000, lead: 100, id: "warp-0002"));
            Assert.AreEqual("running", abs.Status);
            Assert.AreEqual(JobStatuses.Completed, rig.RunToEnd().Status);
        }

        [TestMethod] public void AdmissionRefusesWhatCannotWarp()
        {
            Refused(rig.Service.Handle(Warp(id: "warp-0001")), AutopilotReasons.NoManeuverNode);
            Node(30);
            Refused(rig.Service.Handle(Warp(lead: 60, id: "warp-0002")), NavigationReasons.WarpTargetReached);
            Refused(rig.Service.Handle(Warp(index: 2, id: "warp-0003")), NavigationReasons.NodeNotFound);
            Refused(rig.Service.Handle(Warp("soi", id: "warp-0004")), NavigationReasons.NoSoiChange);
            nav.Nodes.Clear(); Node(100000);
            nav.Rails = false; Refused(rig.Service.Handle(Warp(id: "warp-0005")), NavigationReasons.WarpModePhysics);
            nav.Rails = true; nav.Throttle = 0.5; Refused(rig.Service.Handle(Warp(id: "warp-0006")), FlightReasons.WarpThrottleActive);
            nav.Throttle = 0; nav.Situation = "FLYING"; nav.Altitude = 50000; Refused(rig.Service.Handle(Warp(id: "warp-0007")), NavigationReasons.WarpNotAllowedHere);
            nav.Situation = "ORBITING"; nav.Altitude = 100000; nav.AltitudeLimit = 0; Refused(rig.Service.Handle(Warp(id: "warp-0008")), NavigationReasons.WarpNotAllowedHere);
            Assert.AreEqual(0, nav.RateCalls.Count);
            Assert.IsFalse(rig.Runner.Busy);
        }

        [TestMethod] public void WarpArgumentsAreBounded()
        {
            Node(100000);
            Refused(rig.Service.Handle(Warp("later", id: "warp-0001")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Warp(lead: 3601, id: "warp-0002")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Warp(lead: -1, id: "warp-0003")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Warp("node", seconds: 5, id: "warp-0004")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Warp("soi", index: 0, id: "warp-0005")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Warp("absolute", id: "warp-0006")), ControlReasons.InvalidArgument);
            Refused(rig.Service.Handle(Warp("in_seconds", seconds: NavigationLimits.MaxHorizonSeconds + 1, id: "warp-0007")), ControlReasons.InvalidArgument);
        }

        [TestMethod] public void ARisingThrottleStopsTheWarp()
        {
            Node(500000);
            rig.Service.Handle(Warp());
            rig.Run(5, 100);
            Assert.IsTrue(nav.Index > 0);
            nav.Throttle = 1;
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Failed, job.Status); Assert.AreEqual(FlightReasons.WarpThrottleActive, job.ReasonCode);
            Assert.AreEqual(0, nav.Index); Assert.AreEqual("0:instant", nav.RateCalls.Last());
        }

        [TestMethod] public void AVetoedWarpIsWarpDeniedAndNothingChanges()
        {
            Node(500000);
            nav.VetoAll = true;
            rig.Service.Handle(Warp());
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Failed, job.Status); Assert.AreEqual(FlightReasons.WarpDenied, job.ReasonCode);
            Assert.AreEqual(0, nav.Index);
            Assert.IsTrue(nav.RateCalls.Count <= 2, "the refused rate was not spammed: " + string.Join(",", nav.RateCalls));
        }

        [TestMethod] public void AClampedRateBecomesTheCeilingAndTheWarpStillArrives()
        {
            Node(20000);
            nav.AcceptUpTo = 4;
            rig.Service.Handle(Warp());
            rig.Run(rig.Options.WarpVetoFrames + 5, 100);
            Assert.AreEqual(4, rig.Runner.Current.WarpCeiling);
            Assert.AreEqual(JobStatuses.Running, rig.Runner.Current.Status);
            rig.Options.WarpTimeoutMs = long.MaxValue;
            Assert.AreEqual(JobStatuses.Completed, rig.RunToEnd(100, 50000).Status);
        }

        [TestMethod] public void APersonStoppingTheWarpEndsTheJob()
        {
            Node(5000000);
            rig.Service.Handle(Warp());
            rig.Run(rig.Options.WarpVetoFrames + 5, 100);
            nav.Index = 0;
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Cancelled, job.Status); Assert.AreEqual("warp_stopped_externally", job.ReasonCode);
            Assert.AreEqual(0, nav.Index);
        }

        [TestMethod] public void StopOrALostLeaseReturnsToRealTime()
        {
            Node(500000);
            rig.Service.Handle(Warp());
            rig.Run(5, 100);
            Assert.IsTrue(nav.Index > 0);
            rig.Authority.Stop();
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Cancelled, job.Status);
            Assert.AreEqual(0, nav.Index); CollectionAssert.Contains(job.EffectsApplied, "warp_stopped");
        }

        [TestMethod] public void TheStopButtonAbortsTheWarpAtOnce()
        {
            Node(500000);
            rig.Service.Handle(Warp());
            rig.Run(5, 100);
            rig.Runner.Abort(AutopilotReasons.StoppedByRequest);
            Assert.AreEqual(JobStatuses.Cancelled, rig.Runner.Current.Status); Assert.AreEqual(0, nav.Index);
        }

        [TestMethod] public void HumanFlightInputIsATakeover()
        {
            Node(500000);
            rig.Service.Handle(Warp());
            rig.Run(5, 100);
            rig.Flight.Human = true;
            var job = rig.RunToEnd();
            Assert.AreEqual(OperationReasons.HumanInputDuringOperation, job.ReasonCode); Assert.AreEqual(0, nav.Index);
        }

        [TestMethod] public void AVesselSwitchEndsTheWarp()
        {
            Node(500000);
            rig.Service.Handle(Warp());
            rig.Run(5, 100);
            nav.VesselId = "vessel-2";
            var job = rig.RunToEnd();
            Assert.AreEqual(AutopilotReasons.VesselChanged, job.ReasonCode);
        }

        [TestMethod] public void TheWarpTimesOut()
        {
            Node(500000);
            nav.AcceptUpTo = 1;
            rig.Options.WarpTimeoutMs = 2000;
            rig.Service.Handle(Warp());
            var job = rig.RunToEnd();
            Assert.AreEqual(AutopilotReasons.Timeout, job.ReasonCode); Assert.AreEqual(0, nav.Index);
        }

        [TestMethod] public void OneJobAtATime()
        {
            Node(500000);
            rig.Service.Handle(Warp());
            var busy = rig.Service.Handle(rig.Request(AutopilotOperations.NodeCreate, new JObject { ["requestId"] = "node-c-0001", ["timeReference"] = "apoapsis", ["prograde"] = 10 }));
            Refused(busy, AutopilotReasons.AutopilotBusy);
            Refused(rig.Service.Handle(rig.ExecuteNode()), AutopilotReasons.AutopilotBusy);
            var status = rig.Service.Handle(new BridgeRequest { RequestId = "w", Operation = AutopilotOperations.Status, Arguments = new JObject { ["requestId"] = "warp-0001" } });
            Assert.AreEqual("running", status.Status); Assert.AreEqual("warp_to", (string)status.Data["operation"]);
            Assert.IsNotNull(status.Data["warp"]["remainingSeconds"]);
        }
    }
}
