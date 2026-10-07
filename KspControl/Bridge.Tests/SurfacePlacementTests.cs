using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>P2.8: the two-pass surface placement inside one job, on the fake editor.</summary>
    [TestClass]
    public class SurfacePlacementTests
    {
        private const string RadialGraph = "{\"name\":\"Radial One\",\"facility\":\"VAB\",\"root\":\"pod\",\"parts\":[{\"id\":\"pod\",\"part\":\"mk1pod.v2\"},"
            + "{\"id\":\"tank\",\"part\":\"fuelTankSmall\",\"parent\":\"pod\",\"parentNode\":\"bottom\",\"node\":\"top\"},"
            + "{\"id\":\"engine\",\"part\":\"liquidEngine.v2\",\"parent\":\"tank\",\"parentNode\":\"bottom\",\"node\":\"top\"},"
            + "{\"id\":\"rd\",\"part\":\"radialDecoupler\",\"parent\":\"tank\",\"symmetry\":2,\"surface\":{\"heightOffset\":0,\"angleDegrees\":0}},"
            + "{\"id\":\"srb\",\"part\":\"solidBooster1-1\",\"parent\":\"rd\",\"surface\":{\"heightOffset\":0,\"angleDegrees\":270}}]}";

        private OperationRig rig;
        private readonly List<string> loaded = new List<string>();

        [TestInitialize]
        public void Setup()
        {
            rig = new OperationRig();
            rig.Policy = new ConstructionSupportPolicy(new[] { "mk1pod.v2", "fuelTankSmall", "liquidEngine.v2", "radialDecoupler", "solidBooster1-1" });
            rig.Reader.Parts["radialDecoupler"] = Srf("radialDecoupler", new[] { "ModuleAnchoredDecoupler" });
            rig.Reader.Parts["solidBooster1-1"] = Srf("solidBooster1-1", new[] { "ModuleEngines" }, new CatalogNodeSource { Id = "top", Position = new[] { 0.0, 3.92, 0.0 }, Orientation = new[] { 0.0, 1.0, 0.0 }, Size = 1 });
            rig.Port.OnLoad = (f, path) => loaded.Add(rig.Files.Text(path));
        }

        private static CatalogPartSource Srf(string name, string[] modules, params CatalogNodeSource[] nodes)
        {
            return new CatalogPartSource
            {
                Name = name, KspCategory = "Coupling", Buildable = true, TechAvailable = true, ModelPurchased = true, ModuleNames = modules, StackNodes = nodes,
                SurfaceNode = new CatalogNodeSource { Id = "srfAttach", Position = new[] { 0.0, 0.0, 0.0 }, Orientation = new[] { 1.0, 0.0, 0.0 }, Size = 0 },
                AttachRules = new CatalogAttachRulesSource { Stack = true, AllowStack = true, Srf = true, AllowSrf = true }
            };
        }

        private OperationJob Start(string graph = RadialGraph)
        {
            var response = rig.Apply(graph);
            Assert.AreEqual("running", response.Status, response.ReasonCode + " " + response.Data);
            return rig.Runner.Current;
        }

        /// <summary>Live radii by the part they are measured on: the tank is wider than the provisional 0.625 m, the decoupler is small.</summary>
        private void LiveRadii(OperationJob job, double tank = 0.9, double decoupler = 0.2)
        {
            var layout = job.Plan.Plan.Layout;
            rig.Port.RadiusOf = (cid, h) => layout.Parts.Where(p => p.Cid == cid).Select(p => (double?)(p.Source.Id == "tank" ? tank : decoupler)).FirstOrDefault();
            rig.Port.SurfaceNodeOf = cid => new Pure.SurfaceNodeDefinition { Position = new Pure.Vector(0, 0, 0), Orientation = new Pure.Vector(1, 0, 0) };
        }

        private static double Horizontal(Pure.ConfigNode craft, string partName, int index)
        {
            var parts = craft.Children("PART").ToList();
            var tank = parts.First(p => p.First("part").StartsWith("fuelTankSmall_", StringComparison.Ordinal));
            var tankPos = tank.First("pos").Split(','); var target = parts.Where(p => p.First("part").StartsWith(partName + "_", StringComparison.Ordinal)).ElementAt(index).First("pos").Split(',');
            Func<string, double> d = t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
            return Math.Sqrt(Math.Pow(d(target[0]) - d(tankPos[0]), 2) + Math.Pow(d(target[2]) - d(tankPos[2]), 2));
        }

        [TestMethod]
        public void ASurfacePlanIsLoadedTwiceAndTheSecondLoadUsesTheMeasuredRadius()
        {
            var job = Start(); LiveRadii(job);
            var admittedHash = job.Plan.PlanHash;
            var phases = new List<string>();
            for (var i = 0; i < 4000 && !job.Terminal; i++)
            {
                rig.Frame(50);
                var name = OperationJob.PhaseName(job.Phase); if (phases.Count == 0 || phases[phases.Count - 1] != name) phases.Add(name);
                if (job.Phase == OperationPhase.SurfaceMeasure) Assert.IsTrue(rig.Port.OperationLockHeld, "locks stay held between the two loads");
            }
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            CollectionAssert.AreEqual(new[] { "snapshot", "staging", "dispatch", "settle", "surface_measure", "dispatch", "settle", "verify", "post_unlock_grace", "thumbnail_settle", "finalizing", "done" },
                phases.Where(p => p != "locking").ToList());
            Assert.AreEqual(2, rig.Port.LoadCalls);
            Assert.AreEqual(2, loaded.Count);
            Assert.AreNotEqual(loaded[0], loaded[1], "the second craft carries the calibrated transforms");
            Assert.AreEqual(0.625, Horizontal(Pure.ConfigText.Parse(loaded[0]), "radialDecoupler", 0), 1e-4, "pass 1 uses the provisional radius");
            Assert.AreEqual(0.9, Horizontal(Pure.ConfigText.Parse(loaded[1]), "radialDecoupler", 0), 1e-4, "pass 2 uses the measured tank radius");
            Assert.AreEqual(0.9, Horizontal(Pure.ConfigText.Parse(loaded[1]), "radialDecoupler", 1), 1e-4, "the counterpart too");
            Assert.AreEqual(1.1, Horizontal(Pure.ConfigText.Parse(loaded[1]), "solidBooster1-1", 0), 1e-4, "the booster sits on the measured decoupler surface");
            Assert.AreEqual(loaded[1], rig.Port.Craft, "the editor holds pass 2");
            Assert.AreEqual(admittedHash, job.ToEnvelope()["planHash"].ToString(), "the admitted plan hash is kept");
            Assert.AreEqual(1, rig.Files.Data.Keys.Count(k => k.Contains("recovery") && k.EndsWith(".craft")), "one job, one snapshot");
            Assert.IsFalse(rig.Files.Exists(rig.Paths.StagingPath("apply-0001").FullPath), "staging is removed at the terminal state");
            CollectionAssert.IsSubsetOf(new[] { "snapshot_taken", "craft_load_dispatched", "surface_calibrated", "surface_clearance_verified", "craft_replaced" }, ((JArray)job.ToEnvelope()["effects"]).Select(t => (string)t).ToList());
            Assert.IsTrue(rig.Authority.LeaseHeld, "no takeover: both loads were attributed to the operation");
            Assert.IsFalse(job.TakeoverDuringGrace);
        }

        [TestMethod]
        public void MeasurementsAreTakenOnTheLiveParentsAtTheirAttachHeights()
        {
            var job = Start(); LiveRadii(job);
            rig.RunToEnd();
            // Sites: the tank (one site, shared by both decouplers) and each decoupler (one per booster) in pass 1, then every surface part once in the clearance gate.
            Assert.AreEqual(3 + 4, rig.Port.MeasureCalls);
            Assert.AreEqual(2, rig.Port.NodeReads, "the live srfAttachNode of each distinct child part, read once");
        }

        [TestMethod]
        public void ALiveSurfaceNodeOffsetMovesTheChildOutwardByThatOffset()
        {
            var job = Start(); LiveRadii(job);
            rig.Port.SurfaceNodeOf = cid => new Pure.SurfaceNodeDefinition { Position = new Pure.Vector(0.2, 0, 0), Orientation = new Pure.Vector(1, 0, 0) };
            rig.RunToEnd();
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.AreEqual(0.9 + 0.2, Horizontal(Pure.ConfigText.Parse(loaded[1]), "radialDecoupler", 0), 1e-4);
        }

        [TestMethod]
        public void AClearanceGapOverFiveCentimetresAfterThePassTwoLoadFailsAndRestores()
        {
            var job = Start(); LiveRadii(job);
            var layout = job.Plan.Plan.Layout;
            var calls = 0;
            // The three pass-1 measurements are right; after the second load the tank reports a surface 8 cm away from where the child was placed.
            rig.Port.RadiusOf = (cid, h) => { calls++; var p = layout.Parts.First(x => x.Cid == cid); return calls <= 3 ? (p.Source.Id == "tank" ? 0.9 : 0.2) : (p.Source.Id == "tank" ? 0.98 : 0.2); };
            rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("geometry_mismatch_after_load", job.ReasonCode);
            StringAssert.Contains(job.Detail, "surface_clearance");
            Assert.IsTrue(job.ToEnvelope()["verifyProblems"].Any(), "problems are listed");
            Assert.AreEqual("restored", job.Restore.Result); Assert.AreEqual(3, rig.Port.LoadCalls, "pass 1, pass 2, then the snapshot");
            Assert.AreEqual(3, rig.Port.Parts); Assert.AreEqual("Probe", rig.Port.Name);
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
        }

        [TestMethod]
        public void AGapUnderFiveCentimetresPasses()
        {
            var job = Start(); LiveRadii(job);
            var layout = job.Plan.Plan.Layout; var calls = 0;
            rig.Port.RadiusOf = (cid, h) => { calls++; var p = layout.Parts.First(x => x.Cid == cid); var r = p.Source.Id == "tank" ? 0.9 : 0.2; return calls <= 3 ? r : r + 0.0499; };
            rig.RunToEnd();
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
        }

        [TestMethod]
        public void AParentThatCannotBeMeasuredFailsAfterPassOneAndRestores()
        {
            var job = Start();
            rig.Port.RadiusOf = null; rig.Port.SurfaceNodeOf = cid => new Pure.SurfaceNodeDefinition { Position = new Pure.Vector(0, 0, 0), Orientation = new Pure.Vector(1, 0, 0) };
            rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("geometry_mismatch_after_load", job.ReasonCode); StringAssert.Contains(job.Detail, "surface_unmeasured");
            Assert.AreEqual("restored", job.Restore.Result); Assert.AreEqual(2, rig.Port.LoadCalls, "pass 1 and the snapshot only: pass 2 never ran");
            Assert.IsFalse(job.EffectsApplied.Contains("surface_calibrated"));
        }

        [TestMethod]
        public void AnUnreadableChildSurfaceNodeFailsAfterPassOneAndRestores()
        {
            var job = Start(); LiveRadii(job);
            rig.Port.SurfaceNodeOf = null;
            rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("geometry_mismatch_after_load", job.ReasonCode); StringAssert.Contains(job.Detail, "surface_node_unreadable");
            Assert.AreEqual("restored", job.Restore.Result); Assert.AreEqual(2, rig.Port.LoadCalls);
        }

        [TestMethod]
        public void ANonsenseRadiusIsRefusedByTheCalibrationNotLoaded()
        {
            var job = Start(); LiveRadii(job, tank: 400);
            rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("geometry_mismatch_after_load", job.ReasonCode); StringAssert.Contains(job.Detail, "surface_measurement_invalid");
            Assert.AreEqual(2, rig.Port.LoadCalls);
        }

        [TestMethod]
        public void ACraftWithoutSurfacePartsIsLoadedOnceAndNeverMeasured()
        {
            var response = rig.Apply(OperationRig.TwoStageGraph);
            Assert.AreEqual("running", response.Status);
            var job = rig.Runner.Current; rig.RunToEnd();
            Assert.AreEqual("completed", job.Status);
            Assert.AreEqual(1, rig.Port.LoadCalls); Assert.AreEqual(0, rig.Port.MeasureCalls); Assert.AreEqual(0, rig.Port.NodeReads);
        }

        [TestMethod]
        public void ASurfacePartIsStillRefusedAtAdmissionWhileItsPartsAreUnverified()
        {
            rig.Policy = null; // production table: the radial parts have no structural-load evidence yet
            var request = new BridgeRequest { RequestId = "w", Operation = EditorOperations.ApplyCraft, LeaseId = rig.Lease, Arguments = new JObject
            { ["requestId"] = "apply-0001", ["graph"] = RadialGraph, ["expectedPlanHash"] = new string('a', 64), ["expectedRevision"] = rig.Token() } };
            var response = rig.Service.Handle(request);
            Assert.AreEqual("part_construction_unverified", response.ReasonCode);
            Assert.AreEqual(0, rig.Port.LoadCalls);
        }
    }
}
