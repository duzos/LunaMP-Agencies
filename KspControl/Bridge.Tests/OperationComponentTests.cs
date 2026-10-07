using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

#pragma warning disable CS0414, CS0169, CS0649
namespace KspControl.BridgeTests
{
    public class ReadonlyWithSetter
    {
        private string vesselNameAtLastSave = "Old";
        private readonly string vesselNameAtLastSave_Sanitized = "Old";
        private int undoLevel = 3, undoIndexAtLastSave = 3;
        public int SetterCalls;
        private void SetLastSanitizedSaveName() { SetterCalls++; }
    }

    [TestClass]
    public class SnapshotStoreTests
    {
        private OperationRig rig;
        private SnapshotStore store;
        private int counter;

        [TestInitialize] public void Setup()
        {
            rig = new OperationRig(lease: false);
            store = new SnapshotStore(rig.Files, rig.Paths, () => rig.Utc.AddSeconds(++counter), () => "snapshot" + counter.ToString("D4"));
        }

        private SnapshotRecord Take(string name = "Probe", bool unsaved = false)
        {
            rig.Port.Name = name; rig.Port.UnsavedValue = unsaved;
            var outcome = store.Take(new EditorCraft(Pure.ConfigText.Parse(rig.Port.Craft), rig.Port.ReadUi()), rig.Port, "VAB");
            Assert.IsNotNull(outcome.Record, outcome.Problem);
            return outcome.Record;
        }

        [TestMethod] public void ATakenSnapshotIsVerifiedAndRecordsEverythingNeededToRestoreIt()
        {
            rig.Port.UnsavedValue = true; rig.Port.SaveState = new SaveFields("Probe", "Probe", 9, 7);
            var record = Take(unsaved: true);
            Assert.AreEqual(3, record.PartCount); Assert.AreEqual("true", record.WasUnsaved); Assert.AreEqual("VAB", record.Facility);
            Assert.AreEqual("Probe", record.UiName); Assert.AreEqual("Probe", record.VesselNameAtLastSave); Assert.AreEqual(9, record.UndoLevel); Assert.AreEqual(7, record.UndoIndexAtLastSave);
            Assert.AreEqual(1, record.Crew.Count);
            Assert.IsNull(store.Verify(record));
            var reloaded = store.TryGet(record.SnapshotId);
            Assert.IsNotNull(reloaded); Assert.AreEqual(record.Sha256, reloaded.Sha256); Assert.AreEqual(record.Fingerprint, reloaded.Fingerprint); Assert.AreEqual(record.CraftPath, reloaded.CraftPath);
            Assert.AreEqual(record.CreatedUtc, reloaded.CreatedUtc);
            Assert.AreEqual(Pure.CraftPaths.IsId(record.SnapshotId), true);
            Assert.IsTrue(record.CraftPath.EndsWith("kc-snap-" + record.SnapshotId + ".craft"));
        }

        [TestMethod] public void UnknownUnsavedStateAndUnavailableFieldsAreRecordedAsSuch()
        {
            rig.Port.UnsavedValue = null; rig.Port.Caps.SaveOverwriteGuard = false;
            var record = store.Take(new EditorCraft(Pure.ConfigText.Parse(rig.Port.Craft), rig.Port.ReadUi()), rig.Port, "VAB").Record;
            Assert.AreEqual("unknown", record.WasUnsaved); Assert.IsTrue(record.CountsAsUnsaved, "unknown is treated as unsaved");
            Assert.IsNull(record.VesselNameAtLastSave); Assert.IsNull(record.UndoLevel);
            var json = record.ToJson(); Assert.AreEqual("unavailable", (string)json["vesselNameAtLastSave"]); Assert.AreEqual("unknown", (string)json["wasUnsaved"]);
            var back = store.TryGet(record.SnapshotId); Assert.IsNull(back.VesselNameAtLastSave); Assert.AreEqual("unknown", back.WasUnsaved);
        }

        [TestMethod] public void TheHeaderCarriesTheUiNameDescriptionAndFlagNotTheStaleShipName()
        {
            rig.Port.Name = "Typed in the field"; rig.Port.Description = "My description"; rig.Port.Flag = "Squad/Flags/falcon";
            var capture = rig.Tracker.CaptureGuarded(); Assert.AreEqual("Probe", capture.Craft.First("ship"), "SaveShip still has the stale ship name");
            var record = store.Take(capture, rig.Port, "VAB").Record;
            var written = Pure.ConfigText.Parse(rig.Files.Text(record.CraftPath));
            Assert.AreEqual("Typed in the field", written.First("ship")); Assert.AreEqual("My description", written.First("description")); Assert.AreEqual("Squad/Flags/falcon", written.First("missionFlag"));
            Assert.AreEqual(record.Fingerprint, SnapshotStore.Fingerprint(capture.Craft, capture.Ui));
        }

        [TestMethod] public void TheFingerprintIgnoresVolatileKeysAndTracksRealChanges()
        {
            var ui = new EditorUi("Probe", "", "Squad/Flags/default");
            var a = Pure.ConfigText.Parse(EditorFake.CraftText(cryoTime: "1")); var b = Pure.ConfigText.Parse(EditorFake.CraftText(cryoTime: "99999")); var c = Pure.ConfigText.Parse(EditorFake.CraftText(moduleValue: "11"));
            Assert.AreEqual(SnapshotStore.Fingerprint(a, ui), SnapshotStore.Fingerprint(b, ui));
            Assert.AreNotEqual(SnapshotStore.Fingerprint(a, ui), SnapshotStore.Fingerprint(c, ui));
            Assert.AreNotEqual(SnapshotStore.Fingerprint(a, ui), SnapshotStore.Fingerprint(a, new EditorUi("Other", "", "Squad/Flags/default")));
        }

        [TestMethod] public void VerifyReportsTamperingAndMissingFiles()
        {
            var record = Take();
            rig.Files.Put(record.CraftPath, "tampered"); Assert.AreEqual("snapshot_hash_mismatch", store.Verify(record));
            rig.Files.Delete(record.CraftPath); Assert.AreEqual("snapshot_file_missing", store.Verify(record));
        }

        [TestMethod] public void TryGetRejectsBadIdsGarbageAndMismatchedRecords()
        {
            var record = Take();
            Assert.IsNull(store.TryGet(null)); Assert.IsNull(store.TryGet("short")); Assert.IsNull(store.TryGet("../../etc/passwd")); Assert.IsNull(store.TryGet("nosuchsnap1"));
            var meta = rig.Paths.RecoveryMetaPath(record.SnapshotId).FullPath;
            rig.Files.Put(meta, "not json"); Assert.IsNull(store.TryGet(record.SnapshotId));
            rig.Files.Put(meta, record.ToJson().ToString().Replace(record.SnapshotId, "otherid0001")); Assert.IsNull(store.TryGet(record.SnapshotId), "the id inside must match the file");
            rig.Files.Put(meta, record.ToJson().ToString().Replace("\"facility\": \"VAB\"", "\"facility\": \"XYZ\"")); Assert.IsNull(store.TryGet(record.SnapshotId));
        }

        [TestMethod] public void ARecoveryWorkspaceNamedControlIsDenied()
        {
            var denied = new Pure.CraftPaths(rig.KspRoot, "control", p => false);
            var s = new SnapshotStore(rig.Files, denied, () => rig.Utc, () => "snapshot0001");
            var outcome = s.Take(new EditorCraft(Pure.ConfigText.Parse(rig.Port.Craft), rig.Port.ReadUi()), rig.Port, "VAB");
            Assert.IsNull(outcome.Record); Assert.AreEqual("path_outside_save", outcome.Problem);
            Assert.AreEqual(0, rig.Files.Writes.Count);
        }

        [TestMethod] public void RecentIsNewestFirstAndBounded()
        {
            for (var i = 0; i < 4; i++) Take("Craft" + i);
            var recent = store.Recent(3);
            CollectionAssert.AreEqual(new[] { "Craft3", "Craft2", "Craft1" }, recent.Select(r => r.UiName).ToList());
        }

        [TestMethod] public void PruneKeepsTheNewestAndNeverTheLastUnsavedSnapshotOfACraft()
        {
            var oldUnsaved = Take("Alpha", unsaved: true);
            for (var i = 0; i < 5; i++) Take("Beta" + i, unsaved: false);
            var removed = store.Prune(3);
            Assert.IsTrue(rig.Files.Exists(oldUnsaved.CraftPath), "the only snapshot of an unsaved craft survives retention");
            Assert.AreEqual(2, removed.Count, "6 snapshots, keep 3, protect 1 old one: only the two oldest saved ones go");
            var left = store.Recent(100); Assert.AreEqual(4, left.Count);
            Assert.IsTrue(left.Any(r => r.UiName == "Alpha"));
            Assert.AreEqual(0, rig.Files.Data.Keys.Count(k => removed.Any(id => k.Contains(id))), "both the craft and its metadata are removed");
        }

        [TestMethod] public void ANewerUnsavedSnapshotOfTheSameCraftReleasesTheOlderOne()
        {
            var first = Take("Alpha", unsaved: true); var second = Take("Alpha", unsaved: true);
            for (var i = 0; i < 3; i++) Take("Filler" + i);
            store.Prune(2);
            Assert.IsFalse(rig.Files.Exists(first.CraftPath)); Assert.IsTrue(rig.Files.Exists(second.CraftPath));
        }

        [TestMethod] public void SnapshotsAreRetainedFiftyDeepByDefault() { Assert.AreEqual(50, SnapshotStore.DefaultRetention); }
    }

    [TestClass]
    public class ThumbnailHousekeeperTests
    {
        private MemoryFiles files;
        private Pure.CraftPaths paths;
        private ThumbnailHousekeeper house;
        private List<DeclaredOutput> declared;
        private string root = Path.Combine(Path.GetTempPath(), "kc-thumb-ksp");

        [TestInitialize] public void Setup()
        {
            files = new MemoryFiles(); paths = new Pure.CraftPaths(root, "TestSave", files.IsReparsePoint); house = new ThumbnailHousekeeper(files, paths, "VAB"); declared = new List<DeclaredOutput>();
        }

        private string T(string stem) { return paths.ThumbnailFile("VAB", stem); }

        [TestMethod] public void StagingAndRecoveryNamedThumbnailsAreDeletedAndNothingElseIs()
        {
            var human = T("Human Ship"); files.Put(human, "human"); var humanKc = T("kc-human"); files.Put(humanKc, "mine");
            house.Watch(new[] { "kc-apply-0001" }, new[] { "Probe One" }, "apply-0001");
            files.Put(T("kc-apply-0001"), "gen"); files.Put(T("kc-snap-snap0001"), "x"); // a recovery thumbnail we did not register
            house.Watch(new[] { "kc-snap-snap0001" }, new string[0], "rs-snap0001");
            files.Put(T("Unrelated"), "someone else");
            Assert.IsTrue(house.Observed());
            house.Finish(declared);
            Assert.IsFalse(files.Exists(T("kc-apply-0001"))); Assert.IsFalse(files.Exists(T("kc-snap-snap0001")));
            Assert.AreEqual("human", files.Text(human), "an untouched human thumbnail is untouched"); Assert.AreEqual("mine", files.Text(humanKc), "a kc- lookalike that is not ours stays");
            Assert.IsTrue(files.Exists(T("Unrelated")), "unrelated new files are reported, never deleted");
            Assert.IsTrue(declared.Any(d => d.Action == "reported" && d.Path == T("Unrelated")));
            Assert.AreEqual(2, declared.Count(d => d.Action == "deleted" && d.Class == "thumbnail"));
        }

        [TestMethod] public void AShipNamedThumbnailIsRestoredFromItsBackup()
        {
            files.Put(T("Probe One"), "human original");
            house.Watch(new[] { "kc-apply-0001" }, new[] { "Probe One" }, "apply-0001");
            Assert.IsTrue(files.Exists(paths.ThumbnailBackupPath("apply-0001").FullPath), "backed up before dispatch");
            files.Put(T("Probe One"), "generated by the load");
            house.Finish(declared);
            Assert.AreEqual("human original", files.Text(T("Probe One")));
            Assert.IsFalse(files.Exists(paths.ThumbnailBackupPath("apply-0001").FullPath), "the backup is removed afterwards");
            Assert.IsTrue(declared.Any(d => d.Action == "restored"));
        }

        [TestMethod] public void ANewShipNamedThumbnailWithoutABackupIsDeleted()
        {
            house.Watch(new[] { "kc-apply-0001" }, new[] { "Probe One" }, "apply-0001");
            files.Put(T("Probe One"), "generated"); house.Finish(declared);
            Assert.IsFalse(files.Exists(T("Probe One"))); Assert.IsTrue(declared.Any(d => d.Action == "deleted" && d.Detail.Contains("no_backup")));
        }

        [TestMethod] public void NothingWrittenMeansNotObservedAndNothingToDo()
        {
            house.Watch(new[] { "kc-apply-0001" }, new[] { "Probe One" }, "apply-0001");
            Assert.IsFalse(house.Observed()); house.Finish(declared); Assert.AreEqual(0, declared.Count);
        }

        [TestMethod] public void AThumbnailWeDidNotChangeIsNotObservedEvenIfItIsNamedAfterTheShip()
        {
            files.Put(T("Probe One"), "human");
            house.Watch(new string[0], new[] { "Probe One" }, "apply-0001");
            Assert.IsFalse(house.Observed(), "unchanged since the baseline");
        }

        [TestMethod] public void AReparsePointThumbnailIsLeftAlone()
        {
            house.Watch(new[] { "kc-apply-0001" }, new string[0], "apply-0001");
            files.Put(T("kc-apply-0001"), "link"); files.Reparse.Add(T("kc-apply-0001"));
            house.Finish(declared);
            Assert.IsTrue(files.Exists(T("kc-apply-0001"))); Assert.IsTrue(declared.Any(d => d.Detail == "reparse_point_left_alone"));
        }

        [TestMethod] public void AFailingDeleteIsReportedNotThrown()
        {
            house.Watch(new[] { "kc-apply-0001" }, new string[0], "apply-0001");
            files.Put(T("kc-apply-0001"), "gen"); files.FailDelete = p => true;
            house.Finish(declared);
            Assert.IsTrue(declared.Any(d => d.Action == "reported" && d.Detail.StartsWith("housekeeping_failed")));
        }

        [TestMethod] public void OnlyTheSavesOwnFacilityThumbnailsAreConsidered()
        {
            house.Watch(new[] { "kc-apply-0001" }, new string[0], "apply-0001");
            files.Put(Path.Combine(paths.ThumbnailsDirectory, "OtherSave_VAB_kc-apply-0001.png"), "x"); files.Put(Path.Combine(paths.ThumbnailsDirectory, "TestSave_SPH_kc-apply-0001.png"), "x");
            Assert.IsFalse(house.Observed()); house.Finish(declared); Assert.AreEqual(0, declared.Count);
        }

        [TestMethod] public void ThumbnailNamesRefuseTraversalAndOddStems()
        {
            Assert.IsNull(paths.ThumbnailFile("VAB", "../x")); Assert.IsNull(paths.ThumbnailFile("VAB", "a/b")); Assert.IsNull(paths.ThumbnailFile("VAB", "")); Assert.IsNull(paths.ThumbnailFile("XYZ", "ok"));
            StringAssert.EndsWith(paths.ThumbnailFile("VAB", "ok"), "TestSave_VAB_ok.png");
        }
    }

    [TestClass]
    public class PlanVerifierTests
    {
        private OperationRig rig;
        private Pure.StructuralCraft plan;

        [TestInitialize] public void Setup()
        {
            rig = new OperationRig(lease: false);
            var prepared = ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, rig.Port.Header, rig.Port.ReadUi(), () => 1, "VAB");
            Assert.IsTrue(prepared.Ok, prepared.Reason + prepared.Detail); plan = prepared.Plan.Craft;
        }

        private Pure.ConfigNode Live() { return Pure.ConfigText.Parse(plan.ToText()); }
        private static string Codes(List<VerifyProblem> problems) { return string.Join("; ", problems.Select(p => p.ToString())); }

        private static Pure.ConfigNode Edit(Pure.ConfigNode node, int part, string key, string value, bool remove = false)
        {
            var p = node.Children("PART").ElementAt(part);
            for (var i = 0; i < p.Entries.Count; i++)
                if (p.Entries[i].IsValue && p.Entries[i].Key == key) { if (remove) p.Entries.RemoveAt(i); else p.Entries[i] = new Pure.ConfigEntry(key, value); return node; }
            if (!remove) p.AddValue(key, value);
            return node;
        }

        [TestMethod] public void TheStagedCraftVerifiesAgainstItselfAndAgainstAnyExtraNativeNodes()
        {
            Assert.AreEqual(0, PlanVerifier.Verify(plan, Live(), 0.0, true).Count);
            var withModules = Live(); withModules.Children("PART").First().AddNode("MODULE").AddValue("name", "ModuleProbe");
            Assert.AreEqual(0, PlanVerifier.Verify(plan, withModules, null, null).Count, "native modules are not the plan's business");
        }

        [TestMethod] public void ADifferentPartCountIsAStructureMismatch()
        {
            var live = Live(); live.Entries.RemoveAt(live.Entries.FindLastIndex(e => !e.IsValue));
            var problems = PlanVerifier.Verify(plan, live, null, null);
            Assert.AreEqual("structure_mismatch_after_load", problems[0].Code); StringAssert.Contains(problems[0].Detail, "part_count");
        }

        [TestMethod] public void AMissingOrRenamedPartIsAStructureMismatch()
        {
            var live = Edit(Live(), 1, "part", "fuelTankSmall_999999");
            Assert.IsTrue(PlanVerifier.Verify(plan, live, null, null).Any(p => p.Detail.StartsWith("missing_part")));
            var renamed = Edit(Live(), 1, "part", "otherTank_" + plan.Parts[1].Cid);
            Assert.IsTrue(PlanVerifier.Verify(plan, renamed, null, null).Any(p => p.Detail.StartsWith("part_name")));
        }

        [DataTestMethod]
        [DataRow("istg", "9")] [DataRow("dstg", "4")] [DataRow("sidx", "3")] [DataRow("sqor", "2")] [DataRow("sepI", "5")] [DataRow("attm", "1")]
        public void EveryStagingIntegerIsChecked(string key, string value)
        {
            var problems = PlanVerifier.Verify(plan, Edit(Live(), 1, key, value), null, null);
            Assert.AreEqual(1, problems.Count, Codes(problems)); StringAssert.Contains(problems[0].Detail, key); Assert.AreEqual("structure_mismatch_after_load", problems[0].Code);
        }

        [TestMethod] public void MissingStagingValuesAreAMismatch()
        {
            Assert.IsTrue(PlanVerifier.Verify(plan, Edit(Live(), 0, "istg", null, remove: true), null, null).Any(p => p.Detail.Contains("istg")));
        }

        [TestMethod] public void LinksAndAttachPartnersAreChecked()
        {
            var live = Live(); var tank = live.Children("PART").ElementAt(1);
            tank.Entries.RemoveAll(e => e.IsValue && e.Key == "link");
            Assert.IsTrue(PlanVerifier.Verify(plan, live, null, null).Any(p => p.Detail.StartsWith("link")));
            var attn = Live(); var pod = attn.Children("PART").First();
            pod.Entries.RemoveAll(e => e.IsValue && e.Key == "attN");
            Assert.IsTrue(PlanVerifier.Verify(plan, attn, null, null).Any(p => p.Detail.StartsWith("attN")));
        }

        [TestMethod] public void SymmetryAndSurfaceAttachmentAreChecked()
        {
            var live = Live(); live.Children("PART").ElementAt(1).AddValue("sym", "fuelTankSmall_100000");
            Assert.IsTrue(PlanVerifier.Verify(plan, live, null, null).Any(p => p.Detail.StartsWith("sym")));
            var srf = Live(); srf.Children("PART").ElementAt(2).AddValue("srfN", "srfAttach,mk1pod.v2_100000");
            Assert.IsTrue(PlanVerifier.Verify(plan, srf, null, null).Any(p => p.Detail.StartsWith("srfN")));
        }

        [TestMethod] public void PositionsAreComparedRootRelativeSoASpawnOffsetNeverMatters()
        {
            var live = Live();
            foreach (var part in live.Children("PART").ToList()) { var v = part.First("pos").Split(','); var y = double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture) + 37.5; part.Entries[part.Entries.FindIndex(e => e.IsValue && e.Key == "pos")] = new Pure.ConfigEntry("pos", v[0] + "," + y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + v[2]); }
            Assert.AreEqual(0, PlanVerifier.Verify(plan, live, null, null).Count);
        }

        [TestMethod] public void AMisplacedPartIsAGeometryMismatchBeyondOneCentimetre()
        {
            var pos = plan.Parts[2].Position;
            string Moved(double dx) { return string.Join(",", new[] { pos.X + dx, pos.Y, pos.Z }.Select(d => d.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
            Assert.AreEqual(0, PlanVerifier.Verify(plan, Edit(Live(), 2, "pos", Moved(0.005)), null, null).Count, "5 mm is within tolerance");
            var problems = PlanVerifier.Verify(plan, Edit(Live(), 2, "pos", Moved(0.02)), null, null);
            Assert.AreEqual("geometry_mismatch_after_load", problems[0].Code); StringAssert.Contains(problems[0].Detail, "position");
        }

        [TestMethod] public void ARotatedPartIsAGeometryMismatch()
        {
            var problems = PlanVerifier.Verify(plan, Edit(Live(), 1, "rot", "0,0.7071068,0,0.7071068"), null, null);
            Assert.IsTrue(problems.Any(p => p.Code == "geometry_mismatch_after_load" && p.Detail.StartsWith("rotation")), Codes(problems));
        }

        [TestMethod] public void TheStackNodeGapAndConnectivityAreChecked()
        {
            Assert.AreEqual(0, PlanVerifier.Verify(plan, Live(), 0.004, true).Count);
            var gap = PlanVerifier.Verify(plan, Live(), 0.05, true); Assert.AreEqual("geometry_mismatch_after_load", gap[0].Code); StringAssert.Contains(gap[0].Detail, "stack_node_gap");
            var split = PlanVerifier.Verify(plan, Live(), 0.0, false); Assert.AreEqual("structure_mismatch_after_load", split[0].Code); StringAssert.Contains(split[0].Detail, "parts_not_connected");
        }

        [TestMethod] public void ProblemsAreBounded()
        {
            var live = Live();
            for (var i = 0; i < 3; i++) foreach (var key in new[] { "istg", "dstg", "sidx", "sqor", "sepI", "attm" }) Edit(live, i, key, "77");
            Assert.IsTrue(PlanVerifier.Verify(plan, live, null, null).Count <= 32);
        }
    }

    [TestClass]
    public class GraphJsonAndPlannerTests
    {
        private static string Err(string json) { string error; var graph = GraphJson.Parse(json, out error); Assert.IsNull(graph, json); Assert.IsNotNull(error); return error; }

        [TestMethod] public void AValidGraphBinds()
        {
            string error; var g = GraphJson.Parse(OperationRig.TwoStageGraph, out error);
            Assert.IsNull(error); Assert.AreEqual("Probe One", g.Name); Assert.AreEqual("VAB", g.Facility); Assert.AreEqual("pod", g.Root); Assert.AreEqual(3, g.Parts.Count);
            Assert.AreEqual("bottom", g.Parts[1].ParentNode); Assert.IsNull(g.Parts[0].Parent);
        }

        [TestMethod] public void SurfaceSymmetryStageAndConfigurationBind()
        {
            string error; var g = GraphJson.Parse("{\"name\":\"n\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"surface\":{\"heightOffset\":1.5,\"angleDegrees\":90},\"symmetry\":4,\"stage\":2,\"configuration\":[\"x\"]}]}", out error);
            Assert.IsNull(error); Assert.AreEqual(1.5, g.Parts[0].Surface.HeightOffset); Assert.AreEqual(90, g.Parts[0].Surface.AngleDegrees); Assert.AreEqual(4, g.Parts[0].Symmetry); Assert.AreEqual(2, g.Parts[0].Stage); CollectionAssert.AreEqual(new[] { "x" }, g.Parts[0].Configuration);
        }

        [TestMethod] public void StrictnessMatchesTheHost()
        {
            Err(""); Err("   "); Err("[]"); Err("{"); Err("null");
            StringAssert.Contains(Err("{\"name\":\"a\",// c\n\"parts\":[]}"), "comment");
            StringAssert.Contains(Err("{\"name\":\"a\",\"name\":\"b\"}"), "not valid graph JSON");
            StringAssert.Contains(Err("{\"Name\":\"a\"}"), "unknown member");
            StringAssert.Contains(Err("{\"name\":5,\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}"), "name must be a string");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"symmetry\":2.5}]}"), "integer");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"symmetry\":\"2\"}]}"), "integer");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":99999999999}]}"), "out of range");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":123456789012345678901234567890}]}"), "out of range");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"surface\":{\"heightOffset\":\"x\"}}]}"), "number");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"bogus\":1}]}"), "unknown member");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[null]}"), "null");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[]}"), "1..");
            StringAssert.Contains(Err("{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"configuration\":[1]}]}"), "string");
            StringAssert.Contains(Err("{\"name\":\"" + new string('x', ConstructionLimits.MaxGraphBytes) + "\"}"), "at most");
        }

        [TestMethod] public void TooManyPartsAreRefused()
        {
            var parts = string.Join(",", Enumerable.Range(0, ConstructionLimits.MaxGraphParts + 1).Select(i => "{\"id\":\"p" + i + "\",\"part\":\"x\"}"));
            StringAssert.Contains(Err("{\"name\":\"a\",\"facility\":\"VAB\",\"root\":\"p0\",\"parts\":[" + parts + "]}"), "1..250");
        }

        [TestMethod] public void DeeplyNestedJsonIsRefused()
        {
            Err(new string('[', 40) + new string(']', 40));
        }

        [TestMethod] public void ThePlanHashIsStableAndDependsOnTheCatalog()
        {
            var rig = new OperationRig(lease: false);
            var one = ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, rig.Port.Header, rig.Port.ReadUi(), () => 1, "VAB");
            var two = ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, new EditorHeader("1.12.5", "other"), new EditorUi("x", "y", "Squad/Flags/falcon"), () => 77, "VAB");
            Assert.IsTrue(one.Ok && two.Ok);
            Assert.AreEqual(one.PlanHash, two.PlanHash, "persistent ids, _modVersions and the flag are not part of the plan hash");
            Assert.AreEqual(64, one.PlanHash.Length); Assert.AreEqual(one.CatalogHash, two.CatalogHash);
            Assert.AreEqual(Pure.PlanHasher.Hash(one.Graph, one.Plan, one.Catalog.Hash()), one.PlanHash);
            rig.Reader.Parts["fuelTankSmall"].StackNodes[1].Position[1] -= 0.01;
            var three = ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, rig.Port.Header, rig.Port.ReadUi(), () => 1, "VAB");
            Assert.AreNotEqual(one.PlanHash, three.PlanHash); Assert.AreNotEqual(one.CatalogHash, three.CatalogHash);
        }

        [TestMethod] public void TheRenderedTextCarriesTheLiveHeaderFacts()
        {
            var rig = new OperationRig(lease: false);
            var plan = ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, new EditorHeader("1.12.9", "A=1;B=2"), new EditorUi("n", "d", "Squad/Flags/falcon"), () => 1000, "VAB");
            var node = Pure.ConfigText.Parse(plan.CraftText);
            Assert.AreEqual("A=1;B=2", node.First("_modVersions")); Assert.AreEqual("1.12.9", node.First("version")); Assert.AreEqual("Squad/Flags/falcon", node.First("missionFlag")); Assert.AreEqual("Probe One", node.First("ship"));
            Assert.AreEqual(3, plan.PartCount);
            Assert.AreEqual(Pure.StructuralCraftValidator.Validate(node, plan.Catalog).Count, 0);
        }

        [TestMethod] public void EveryRefusalCarriesAReasonAndIssues()
        {
            var rig = new OperationRig(lease: false);
            var unknown = ApplyPlanner.Prepare(OperationRig.TwoStageGraph.Replace("fuelTankSmall", "nope"), rig.Reader, rig.Port.Header, rig.Port.ReadUi(), () => 1, "VAB");
            Assert.AreEqual("invalid_graph", unknown.Reason); Assert.IsTrue(unknown.Issues.Any(i => i.Code == "unknown_part"));
            var noHeader = ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, null, rig.Port.ReadUi(), () => 1, "VAB");
            Assert.AreEqual("mod_versions_unavailable", noHeader.Reason);
            var facility = ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, rig.Port.Header, rig.Port.ReadUi(), () => 1, "SPH");
            Assert.AreEqual("facility_mismatch", facility.Reason);
            var bad = ApplyPlanner.Prepare("{", rig.Reader, rig.Port.Header, rig.Port.ReadUi(), () => 1, "VAB");
            Assert.AreEqual("invalid_argument", bad.Reason);
        }

        [TestMethod] public void ASandboxCatalogWithoutResearchAllowsPartsAndAnUnreadableOneRefusesThem()
        {
            var rig = new OperationRig(lease: false);
            rig.Reader.ResearchAvailable = false; rig.Reader.SandboxMode = true;
            foreach (var part in rig.Reader.Parts.Values) { part.TechAvailable = null; part.ModelPurchased = null; }
            Assert.IsTrue(ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, rig.Port.Header, rig.Port.ReadUi(), () => 1, "VAB").Ok);
            rig.Reader.SandboxMode = false;
            var locked = ApplyPlanner.Prepare(OperationRig.TwoStageGraph, rig.Reader, rig.Port.Header, rig.Port.ReadUi(), () => 1, "VAB");
            Assert.AreEqual("part_locked", locked.Reason);
        }
    }

    [TestClass]
    public class DiskOperationFilesTests
    {
        private string dir;
        private readonly DiskOperationFiles files = new DiskOperationFiles();

        [TestInitialize] public void Setup() { dir = Path.Combine(Path.GetTempPath(), "kc-disk-" + Guid.NewGuid().ToString("N")); }
        [TestCleanup] public void Cleanup() { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } }

        [TestMethod] public void WritesAtomicallyCreatingDirectoriesAndLeavesNoTemporaryFiles()
        {
            var path = Path.Combine(dir, "a", "b", "file.craft");
            files.WriteAtomic(path, Encoding.UTF8.GetBytes("one")); Assert.AreEqual("one", File.ReadAllText(path));
            files.WriteAtomic(path, Encoding.UTF8.GetBytes("two")); Assert.AreEqual("two", File.ReadAllText(path));
            Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)).Length, "no .tmp left behind");
        }

        [TestMethod] public void ReadDeleteCopyAndListBehave()
        {
            var a = Path.Combine(dir, "x", "a.png"); var b = Path.Combine(dir, "x", "b.txt"); var copy = Path.Combine(dir, "y", "c.png");
            files.WriteAtomic(a, new byte[] { 1, 2, 3 }); files.WriteAtomic(b, new byte[] { 4 });
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, files.ReadAllBytes(a));
            files.Copy(a, copy, true); Assert.IsTrue(files.Exists(copy));
            Assert.ThrowsException<IOException>(() => files.Copy(a, copy, false));
            var listed = files.List(Path.Combine(dir, "x"), ".png"); Assert.AreEqual(1, listed.Count); Assert.AreEqual("a.png", listed[0].Name); Assert.AreEqual(3, listed[0].Length);
            Assert.AreEqual(2, files.List(Path.Combine(dir, "x"), null).Count);
            Assert.AreEqual(0, files.List(Path.Combine(dir, "missing"), ".png").Count);
            files.Delete(a); Assert.IsFalse(files.Exists(a)); files.Delete(a);
            Assert.ThrowsException<FileNotFoundException>(() => files.ReadAllBytes(a));
        }

        [TestMethod] public void ReparsePointDetectionIsFalseForPlainAndMissingPaths()
        {
            var a = Path.Combine(dir, "a.txt"); files.WriteAtomic(a, new byte[] { 1 });
            Assert.IsFalse(files.IsReparsePoint(a)); Assert.IsFalse(files.IsReparsePoint(dir)); Assert.IsFalse(files.IsReparsePoint(Path.Combine(dir, "nope")));
        }

        [TestMethod] public void ShaOfBytesAndTextAgree()
        {
            Assert.AreEqual(OperationHash.Sha256Hex("abc"), OperationHash.Sha256Hex(Encoding.UTF8.GetBytes("abc")));
            Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", OperationHash.Sha256Hex("abc"));
        }
    }

    [TestClass]
    public class EditorReflectionWriteTests
    {
        [TestMethod] public void ReadsAllGuardFieldsInOneGo()
        {
            var fields = new EditorReflection(typeof(FakeEditor)).TryReadSaveFields(new FakeEditor());
            Assert.AreEqual("Probe", fields.Name); Assert.AreEqual("Probe", fields.Sanitized); Assert.AreEqual(4, fields.UndoLevel); Assert.AreEqual(4, fields.UndoIndexAtLastSave);
            Assert.IsNull(new EditorReflection(typeof(BareEditor)).TryReadSaveFields(new BareEditor()));
            Assert.IsNull(new EditorReflection(typeof(FakeEditor)).TryReadSaveFields(null));
        }

        [TestMethod] public void WritesBothSaveNamesAndTheUndoIndex()
        {
            var editor = new FakeEditor(); var reflection = new EditorReflection(typeof(FakeEditor));
            Assert.IsTrue(reflection.TryWriteSavedName(editor, EditorObservationService.GuardSentinel, EditorObservationService.GuardSentinel));
            Assert.IsTrue(reflection.TryWriteUndoIndex(editor, -1));
            var fields = reflection.TryReadSaveFields(editor);
            Assert.AreEqual(EditorObservationService.GuardSentinel, fields.Name); Assert.AreEqual(EditorObservationService.GuardSentinel, fields.Sanitized); Assert.AreEqual(-1, fields.UndoIndexAtLastSave);
            string name; Assert.IsTrue(reflection.TryReadLastSavedName(editor, out name)); Assert.AreEqual(EditorObservationService.GuardSentinel, name);
            Assert.AreEqual(0, editor.SetterCalls, "a writable sanitised field is set directly");
            editor.Edit(); editor.Edit(); Assert.IsTrue(reflection.TryMarkSaved(editor));
            fields = reflection.TryReadSaveFields(editor); Assert.AreEqual(6, fields.UndoLevel); Assert.AreEqual(6, fields.UndoIndexAtLastSave);
        }

        [TestMethod] public void AReadonlySanitisedFieldIsSetThroughTheStockSetter()
        {
            var editor = new ReadonlyWithSetter(); var reflection = new EditorReflection(typeof(ReadonlyWithSetter));
            Assert.IsTrue(reflection.Capabilities.SaveOverwriteGuard);
            Assert.IsTrue(reflection.TryWriteSavedName(editor, "New name", "New name")); Assert.AreEqual(1, editor.SetterCalls);
            Assert.AreEqual("New name", reflection.TryReadSaveFields(editor).Name);
        }

        [TestMethod] public void WithoutTheGuardEveryWriteIsRefusedAndNothingChanges()
        {
            var bare = new EditorReflection(typeof(BareEditor)); var editor = new BareEditor();
            Assert.IsFalse(bare.TryWriteSavedName(editor, "a", "a")); Assert.IsFalse(bare.TryWriteUndoIndex(editor, 1)); Assert.IsFalse(bare.TryMarkSaved(editor));
            var readonlyNoSetter = new EditorReflection(typeof(NoSetterReadonlySanitized));
            Assert.IsFalse(readonlyNoSetter.TryWriteSavedName(new NoSetterReadonlySanitized(), "a", "a"));
            Assert.IsFalse(new EditorReflection(typeof(FakeEditor)).TryWriteSavedName(null, "a", "a"));
            Assert.IsFalse(new EditorReflection(typeof(FakeEditor)).TryWriteUndoIndex(null, 1));
        }
    }

    [TestClass]
    public class ProjectionDiffTests
    {
        [TestMethod] public void NamesThePartNodeAndKeyThatChanged()
        {
            var registry = Pure.RoundtripVolatileKeys.Default();
            var a = Pure.CraftFingerprint.Project(Pure.ConfigText.Parse(EditorFake.CraftText(moduleValue: "10")), registry, null, null, null);
            var b = Pure.CraftFingerprint.Project(Pure.ConfigText.Parse(EditorFake.CraftText(moduleValue: "11")), registry, null, null, null);
            var keys = new List<string>(); ProjectionDiff.Collect(a, b, keys, 16);
            Assert.AreEqual(1, keys.Count); StringAssert.Contains(keys[0], "mk1pod.v2"); StringAssert.Contains(keys[0], "MODULE#1"); StringAssert.Contains(keys[0], "value");
        }

        [TestMethod] public void IdenticalProjectionsHaveNoDifferencesAndTheListIsBoundedAndDistinct()
        {
            var a = Pure.CraftFingerprint.Project(Pure.ConfigText.Parse(EditorFake.CraftText()), Pure.RoundtripVolatileKeys.Default(), null, null, null);
            var keys = new List<string>(); ProjectionDiff.Collect(a, a, keys, 16); Assert.AreEqual(0, keys.Count);
            var b = Pure.CraftFingerprint.Project(Pure.ConfigText.Parse(EditorFake.CraftText(podStage: "3", tankStage: "4", moduleValue: "99")), Pure.RoundtripVolatileKeys.Default(), null, null, null);
            ProjectionDiff.Collect(a, b, keys, 2); Assert.AreEqual(2, keys.Count);
            ProjectionDiff.Collect(a, b, keys, 2); Assert.AreEqual(2, keys.Count, "bounded");
            var more = new List<string>(); ProjectionDiff.Collect(a, b, more, 16); ProjectionDiff.Collect(a, b, more, 16); Assert.AreEqual(more.Count, more.Distinct().Count());
        }

        [TestMethod] public void UiChangesAreReportedToo()
        {
            var a = Pure.CraftFingerprint.Project(Pure.ConfigText.Parse(EditorFake.CraftText()), Pure.RoundtripVolatileKeys.Default(), "A", "", "f");
            var b = Pure.CraftFingerprint.Project(Pure.ConfigText.Parse(EditorFake.CraftText()), Pure.RoundtripVolatileKeys.Default(), "B", "", "f");
            var keys = new List<string>(); ProjectionDiff.Collect(a, b, keys, 16); CollectionAssert.Contains(keys, "ui");
        }
    }

    [TestClass]
    public class SuspensionPruneTests
    {
        [TestMethod] public void SeenRecordsOlderThanThirtyDaysArePrunedEvenForTheHighestGeneration()
        {
            long now = 1000; var utc = AuthorityHelpers.Utc0; var store = new MemorySuspensionStore();
            var authority = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, store, () => utc);
            authority.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind(), AuthorityHelpers.ValidStatus());
            authority.ProvisionGrant(AuthorityHelpers.Grant(1, "grant", 1000));
            Assert.AreEqual(1, store.Load().Count(s => s.Reason == "seen"));
            utc = AuthorityHelpers.Utc0.AddDays(29); authority.PruneSuspensions(); Assert.AreEqual(1, store.Load().Count(s => s.Reason == "seen"), "not old enough");
            utc = AuthorityHelpers.Utc0.AddDays(31); authority.PruneSuspensions(); Assert.AreEqual(0, store.Load().Count(s => s.Reason == "seen"), "older than 30 days");
            Assert.AreEqual(1L, authority.HighestGeneration("grant"), "the in-memory guard stays for this session");
            var restarted = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, store, () => utc);
            restarted.NoteGeneration("grant", 1); Assert.AreEqual(1L, restarted.HighestGeneration("grant"));
        }

        [TestMethod] public void AStopEntryOfTheHighestGenerationIsKeptButOldSeenRecordsOfOtherGrantsGo()
        {
            long now = 1000; var utc = AuthorityHelpers.Utc0; var store = new MemorySuspensionStore();
            var authority = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, store, () => utc);
            authority.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind(), AuthorityHelpers.ValidStatus());
            authority.ProvisionGrant(AuthorityHelpers.Grant(1, "grant", 1000)); authority.Stop(); authority.NoteGeneration("other", 5);
            utc = AuthorityHelpers.Utc0.AddDays(40); authority.PruneSuspensions();
            var left = store.Load();
            Assert.AreEqual(1, left.Count(s => s.Reason == "stop")); Assert.AreEqual(0, left.Count(s => s.Reason == "seen"));
        }
    }
}
