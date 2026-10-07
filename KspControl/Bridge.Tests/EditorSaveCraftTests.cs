using System;
using System.Linq;
using System.Text;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>editor_save_craft on the whole fake operation layer: admission, the single save step, ledger, bookkeeping and refusals.</summary>
    [TestClass]
    public class EditorSaveCraftTests
    {
        private OperationRig rig;

        [TestInitialize] public void Setup() { rig = new OperationRig(); }

        /// <summary>A rig whose editor was set up by <paramref name="setup"/> before the lease was taken, so the setup is not a human takeover.</summary>
        private void Fresh(Action<OperationRig> setup, string saveFolder = "TestSave")
        {
            rig = new OperationRig(lease: false, saveFolder: saveFolder);
            setup(rig); rig.Run(20); rig.AcquireLease();
        }

        /// <summary>An edit made while no operation runs is a human edit: it takes the lease, so the cooldown passes and the lease is taken again.</summary>
        private void Mutate(Action edit) { edit(); rig.Run(70, 500); rig.AcquireLease(); }

        private OperationJob SaveAndRun(string name = "Probe Saved", string requestId = "save-00001", string replace = null)
        {
            var response = rig.Save(name, requestId, replace);
            Assert.AreEqual("running", response.Status, response.ReasonCode + " " + response.Data);
            return rig.RunToEnd();
        }

        private BridgeResponse Refused(BridgeResponse response, string reason)
        {
            Assert.AreEqual("failed", response.Status, response.Data.ToString());
            Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.AreEqual(true, (bool)response.Data["notDispatched"]);
            Assert.IsFalse(rig.Runner.Busy);
            Assert.AreEqual(0, rig.Files.Writes.Count(w => w.EndsWith(".craft", StringComparison.OrdinalIgnoreCase)), "no ship file was written");
            return response;
        }

        private string Hash(string name) { return OperationHash.Sha256Hex(rig.Files.ReadAllBytes(rig.ShipFile(name))); }

        // ---- the main path ----

        [TestMethod] public void ASaveWritesOneFileInTheShipsFolderAndCompletes()
        {
            var job = SaveAndRun();
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            var path = rig.ShipFile("Probe Saved");
            Assert.AreEqual(path, job.SavedPath);
            Assert.IsTrue(rig.Files.Exists(path));
            StringAssert.EndsWith(path, "Ships" + System.IO.Path.DirectorySeparatorChar + "VAB" + System.IO.Path.DirectorySeparatorChar + "Probe Saved.craft");
            Assert.AreEqual(Hash("Probe Saved"), job.SavedSha256);
            Assert.AreEqual(1, rig.Files.Writes.Count(w => w == path));
            Assert.IsTrue(job.Dispatched);
            Assert.AreEqual(0, rig.Port.LoadCalls, "a save never loads anything");
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId), "unlocked");
            CollectionAssert.Contains(job.EffectsApplied, "craft_saved");
            Assert.IsNull(job.Snapshot, "a save does not change the editor, so there is nothing to snapshot");
        }

        [TestMethod] public void TheEnvelopeNamesTheOperationAndCarriesTheSaveFacts()
        {
            var job = SaveAndRun();
            var e = job.ToEnvelope();
            Assert.AreEqual("save_craft", (string)e["operation"]);
            Assert.AreEqual(job.SavedPath, (string)e["savedPath"]); Assert.AreEqual(job.SavedSha256, (string)e["sha256"]);
            Assert.AreEqual(true, (bool)e["craftIdentifiersValid"]);
            Assert.AreEqual("recorded", (string)e["ledger"]); Assert.AreEqual("synced", (string)e["saveBookkeeping"]);
            Assert.AreEqual(true, (bool)e["comparison"]["equal"]); Assert.AreEqual(0, (int)e["comparison"]["totalDifferences"]);
            Assert.IsFalse((bool)e["notDispatched"]);
            Assert.IsNotNull((string)e["editorRevision"], "the new token is set at the terminal state");
            Assert.IsTrue(e["declaredOutputs"].Any(d => (string)d["class"] == "ships" && (string)d["action"] == "created" && (string)d["path"] == job.SavedPath));
            Assert.AreEqual("save_craft", (string)rig.Service.LastOperation()["operation"]);
        }

        [TestMethod] public void TheHeaderCarriesTheUiNameDescriptionAndFlagNotTheStaleShipName()
        {
            Fresh(r => { r.Port.Name = "Renamed In UI"; r.Port.Description = "typed but not synced"; r.Port.Flag = "Custom/Flag"; });
            SaveAndRun();
            var saved = Pure.ConfigText.Parse(rig.Files.Text(rig.ShipFile("Probe Saved")));
            Assert.AreEqual("Renamed In UI", saved.First("ship")); Assert.AreEqual("typed but not synced", saved.First("description")); Assert.AreEqual("Custom/Flag", saved.First("missionFlag"));
            Assert.AreEqual("Renamed In UI", rig.Port.Name, "the UI field is left alone");
        }

        [TestMethod] public void TheSavedFileHoldsEveryPartAndReloadsThroughTheComparator()
        {
            SaveAndRun();
            var saved = Pure.ConfigText.Parse(rig.Files.Text(rig.ShipFile("Probe Saved")));
            var live = Pure.ConfigText.Parse(rig.Port.Craft);
            Assert.AreEqual(3, saved.Children("PART").Count());
            var comparison = Pure.CraftComparator.Compare(SnapshotStore.WithUiHeader(live, rig.Port.ReadUi()), saved);
            Assert.IsTrue(comparison.Equal);
            string problem; Assert.IsTrue(CraftIdentifierRule.Check(saved, out problem), problem);
        }

        [TestMethod] public void NothingOutsideTheShipsFolderAndTheWorkspaceIsWritten()
        {
            SaveAndRun();
            var ships = System.IO.Path.GetFullPath(System.IO.Path.Combine(rig.KspRoot, "saves", "TestSave", "Ships", "VAB")) + System.IO.Path.DirectorySeparatorChar;
            var workspace = System.IO.Path.GetFullPath(System.IO.Path.Combine(rig.KspRoot, "KspControlData", "TestSave")) + System.IO.Path.DirectorySeparatorChar;
            foreach (var write in rig.Files.Writes.Select(System.IO.Path.GetFullPath))
                Assert.IsTrue(write.StartsWith(ships, StringComparison.OrdinalIgnoreCase) || write.StartsWith(workspace, StringComparison.OrdinalIgnoreCase), write);
            Assert.AreEqual(0, rig.Files.Writes.Count(w => w.IndexOf("SPH", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        // ---- the save-name rule (R3-section 8) ----

        [TestMethod] public void AfterASaveTheSaveNameFieldsNameTheFileAndTheCraftCountsAsSaved()
        {
            rig.Port.LastSaved = "\u0001kspcontrol-unsaved"; rig.Port.SaveState = new SaveFields("\u0001kspcontrol-unsaved", "\u0001kspcontrol-unsaved", 7, -1); rig.Port.UnsavedValue = true;
            SaveAndRun("Probe Saved");
            Assert.AreEqual("Probe Saved", rig.Port.LastSaved);
            Assert.AreEqual("Probe Saved", rig.Port.SaveState.Name); Assert.AreEqual("Probe Saved", rig.Port.SaveState.Sanitized);
            Assert.AreEqual(rig.Port.SaveState.UndoLevel, rig.Port.SaveState.UndoIndexAtLastSave);
            Assert.AreEqual(false, rig.Port.UnsavedValue);
        }

        [TestMethod] public void WithoutTheGuardTheSaveStillLandsAndSaysTheFieldsWereNotSynced()
        {
            Fresh(r => r.Port.Caps = new EditorCapabilities());
            var job = SaveAndRun();
            Assert.AreEqual("completed", job.Status);
            Assert.AreEqual("unavailable", job.BookkeepingResult);
            Assert.AreEqual(0, rig.Port.GuardWrites);
        }

        [TestMethod] public void AFailedFieldWriteIsReportedButTheSavedFileStands()
        {
            Fresh(r => r.Port.GuardWritesFail = true);
            var job = SaveAndRun();
            Assert.AreEqual("completed", job.Status);
            Assert.AreEqual("partial", job.BookkeepingResult, "the marker was set, the names were not");
            Fresh(r => { r.Port.GuardWritesFail = true; r.Port.UnsavedMarkerFails = true; });
            Assert.AreEqual("write_failed", SaveAndRun().BookkeepingResult);
        }

        [TestMethod] public void ASaveDoesNotTouchTheGeneratedCraftBookkeepingOfOtherJobs()
        {
            var apply = rig.Apply(); Assert.AreEqual("running", apply.Status); var applyJob = rig.RunToEnd();
            Assert.AreEqual("completed", applyJob.Status, applyJob.ReasonCode);
            Assert.IsNotNull(rig.Jobs.LastOperationFingerprint);
            var before = rig.Jobs.LastOperationFingerprint;
            var job = SaveAndRun("Saved After Apply");
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.AreEqual(before, rig.Jobs.LastOperationFingerprint, "the save does not change who made the craft");
        }

        // ---- overwrite rules ----

        [TestMethod] public void AHumanFileIsNeverOverwritten()
        {
            var path = rig.ShipFile("Probe Saved"); rig.Files.Put(path, "ship = Human\r\n"); var before = rig.Files.Writes.Count;
            Refused(rig.Save(), "file_exists");
            Assert.AreEqual("ship = Human\r\n", rig.Files.Text(path));
            Assert.AreEqual(before, rig.Files.Writes.Count);
        }

        [TestMethod] public void AHumanFileIsNotReplacedEvenWithItsRealHash()
        {
            var path = rig.ShipFile("Probe Saved"); rig.Files.Put(path, "ship = Human\r\n");
            var hash = OperationHash.Sha256Hex(rig.Files.ReadAllBytes(path));
            Refused(rig.Save(replace: hash), "file_not_kspcontrol_owned");
            Assert.AreEqual("ship = Human\r\n", rig.Files.Text(path));
        }

        [TestMethod] public void ACaseVariantOfAHumanFileCountsAsTheSameFile()
        {
            rig.Files.Put(rig.ShipFile("probe saved"), "ship = Human\r\n");
            Assert.IsTrue(rig.Files.Exists(rig.ShipFile("probe saved")));
            // The fake file system is case-insensitive like Windows, so a differently cased request hits the same path.
            Refused(rig.Save("PROBE SAVED"), "file_exists");
        }

        [TestMethod] public void ALedgerOwnedFileIsReplacedOnlyWithItsCurrentHash()
        {
            var first = SaveAndRun(); var firstHash = first.SavedSha256;
            Mutate(() => rig.Port.Name = "Second Version");
            Refused2(rig.Save(requestId: "save-00002"), "file_exists");
            Refused2(rig.Save(requestId: "save-00003", replace: new string('0', 64)), "file_changed");
            var again = rig.Save(requestId: "save-00004", replace: firstHash);
            Assert.AreEqual("running", again.Status, again.ReasonCode + " " + again.Data);
            var job = rig.RunToEnd();
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.AreEqual(firstHash, job.ReplacedSha256); Assert.AreNotEqual(firstHash, job.SavedSha256);
            Assert.AreEqual(job.SavedSha256, Hash("Probe Saved"));
            StringAssert.Contains(rig.Files.Text(rig.ShipFile("Probe Saved")), "Second Version");
            Assert.IsTrue(job.ToEnvelope()["declaredOutputs"].Any(d => (string)d["action"] == "replaced"));
            // The ledger now carries the new hash, so a third replace needs the new one.
            Refused2(rig.Save(requestId: "save-00005", replace: firstHash), "file_changed");
        }

        private void Refused2(BridgeResponse response, string reason)
        {
            Assert.AreEqual("failed", response.Status, response.Data.ToString()); Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.AreEqual(true, (bool)response.Data["notDispatched"]); Assert.IsFalse(rig.Runner.Busy);
        }

        [TestMethod] public void AnOwnedFileEditedByAHumanSinceIsNoLongerReplaceable()
        {
            SaveAndRun();
            var path = rig.ShipFile("Probe Saved"); rig.Files.Put(path, rig.Files.Text(path) + "// edited\r\n");
            Refused2(rig.Save(requestId: "save-00002", replace: Hash("Probe Saved")), "file_changed");
        }

        [TestMethod] public void AReplaceHashForAMissingFileIsRefused()
        {
            Refused(rig.Save(replace: new string('a', 64)), "file_changed");
        }

        [TestMethod] public void ACorruptLedgerRefusesEverySave()
        {
            rig.Files.Put(rig.Paths.LedgerFile, "{ not json");
            Refused(rig.Save(), "ledger_unavailable");
            Assert.AreEqual("{ not json", rig.Files.Text(rig.Paths.LedgerFile), "a corrupt ledger is never overwritten");
        }

        [TestMethod] public void ALedgerThatCannotBeWrittenLeavesTheFileUnownedButSaved()
        {
            rig.Files.FailWrite = p => p.EndsWith("ledger.json");
            var job = SaveAndRun();
            Assert.AreEqual("completed", job.Status);
            Assert.AreEqual("write_failed", job.LedgerResult);
            Refused2(rig.Save(requestId: "save-00002", replace: job.SavedSha256), "file_not_kspcontrol_owned");
        }

        [TestMethod] public void LedgerEntriesForDeletedFilesArePrunedAtTheNextSave()
        {
            SaveAndRun("First One");
            rig.Files.Delete(rig.ShipFile("First One"));
            SaveAndRun("Second One", "save-00002");
            var ledger = CraftLedger.Load(rig.Files, rig.Paths.LedgerFile);
            Assert.IsNull(ledger.Find("VAB", "First One.craft")); Assert.IsNotNull(ledger.Find("VAB", "Second One.craft"));
        }

        // ---- admission refusals ----

        [DataTestMethod]
        [DataRow("")] [DataRow("x.craft")] [DataRow("../evil")] [DataRow("a/b")] [DataRow("..")] [DataRow(".hidden")] [DataRow("con")] [DataRow("name:stream")]
        public void BadFileNamesAreInvalidArguments(string name)
        {
            var response = rig.Service.Handle(rig.SaveRequest(name));
            Assert.AreEqual("failed", response.Status); Assert.AreEqual("invalid_argument", response.ReasonCode, name);
            Assert.AreEqual(0, rig.Files.Writes.Count(w => w.EndsWith(".craft", StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod] public void ANameOfSixtyFiveCharactersIsRefusedAndSixtyFourIsFine()
        {
            Assert.AreEqual("invalid_argument", rig.Service.Handle(rig.SaveRequest(new string('a', 65))).ReasonCode);
            Assert.AreEqual("running", rig.Service.Handle(rig.SaveRequest(new string('a', 64))).Status);
        }

        [DataTestMethod]
        [DataRow("short")] [DataRow("UPPERCASEHASH0000000000000000000000000000000000000000000000000000")] [DataRow("zz")]
        public void ABadReplaceHashIsAnInvalidArgument(string hash)
        {
            var request = rig.SaveRequest(replace: hash);
            Assert.AreEqual("invalid_argument", rig.Service.Handle(request).ReasonCode);
        }

        [TestMethod] public void ANonStringReplaceHashIsAnInvalidArgument()
        {
            var request = rig.SaveRequest(); request.Arguments["replaceExpectedSha256"] = 5;
            Assert.AreEqual("invalid_argument", rig.Service.Handle(request).ReasonCode);
            request = rig.SaveRequest(requestId: "save-00002"); request.Arguments["replaceExpectedSha256"] = JValue.CreateNull();
            Assert.AreEqual("running", rig.Service.Handle(request).Status, "an explicit null means no replace");
        }

        [TestMethod] public void WithoutALeaseOrWithABadOneTheSaveIsRefused()
        {
            var request = rig.SaveRequest(); request.LeaseId = null;
            Assert.AreEqual("lease_required", rig.Service.Handle(request).ReasonCode);
            request = rig.SaveRequest(requestId: "save-00002"); request.LeaseId = "short";
            Assert.AreEqual("lease_invalid", rig.Service.Handle(request).ReasonCode);
        }

        [TestMethod] public void AGrantWithoutCraftWriteDeniesTheSaveButStillAllowsAnApply()
        {
            rig = new OperationRig(operations: new[] { OperationEffects.ReplaceCraft });
            Refused(rig.Save(), "grant_operation_denied");
            rig = new OperationRig(operations: new[] { OperationEffects.WriteCraft });
            Assert.AreEqual("running", rig.Save().Status);
            rig = new OperationRig(operations: new[] { OperationEffects.WriteCraft });
            Assert.AreEqual("grant_operation_denied", rig.Service.Handle(rig.ApplyRequest()).ReasonCode, "craft.write does not grant the editor effects");
        }

        [TestMethod] public void AStaleRevisionIsRefusedBeforeAnythingIsWritten()
        {
            var token = rig.Token();
            rig.Port.Craft = EditorFake.CraftText(podStage: "3");
            var response = rig.Service.Handle(rig.SaveRequest(token: token));
            Refused(response, "stale_revision");
        }

        [TestMethod] public void AnEmptyEditorHasNothingToSave()
        {
            Fresh(r => { r.Port.Parts = 0; r.Port.Craft = ""; r.Port.Ship = new object(); r.Port.Fsm = "st_podSelect"; });
            Refused(rig.Save(), "craft_empty");
        }

        [TestMethod] public void ABusyEditorOrARunningJobRefusesTheSave()
        {
            Fresh(r => r.Port.Selected = true);
            var response = rig.Save();
            Assert.AreEqual("editor_busy", response.ReasonCode);
        }

        [TestMethod] public void ALinkedShipsFolderIsAPathRefusal()
        {
            var ships = System.IO.Path.GetFullPath(System.IO.Path.Combine(rig.KspRoot, "saves", "TestSave", "Ships", "VAB"));
            rig.Files.Reparse.Add(ships);
            var response = rig.Save();
            Refused(response, "path_outside_save");
            StringAssert.Contains((string)response.Data["detail"], "reparse_point");
        }

        [TestMethod] public void ASaveFolderNamedControlHasNoLedgerAndSoRefusesTheSave()
        {
            rig = new OperationRig(saveFolder: "control");
            Refused(rig.Save(), "path_outside_save");
        }

        [TestMethod] public void ACraftWithBadIdentifiersIsRefusedBeforeTheWrite()
        {
            Fresh(r => r.Port.Craft = r.Port.Craft.Replace("fuelTankSmall_100001", "fuelTankSmall_100000").Replace("liquidEngine_100002", "liquidEngine_100000"));
            var response = rig.Save();
            Assert.AreEqual("running", response.Status);
            var job = rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("craft_identifiers_invalid", job.ReasonCode);
            Assert.IsFalse(job.Dispatched); Assert.AreEqual(false, job.CraftIdentifiersValid);
            Assert.IsFalse(rig.Files.Exists(rig.ShipFile("Probe Saved")));
        }

        [TestMethod] public void AnOversizeCraftIsRefused()
        {
            Fresh(r => r.Port.Description = new string('d', EditorOperationRunner.MaxSaveBytes + 10));
            var big = rig.Save(); Assert.AreEqual("running", big.Status);
            var bigJob = rig.RunToEnd();
            Assert.AreEqual("craft_too_large", bigJob.ReasonCode); Assert.IsFalse(bigJob.Dispatched);
        }

        // ---- idempotence ----

        [TestMethod] public void TheSameRequestReturnsTheSameJobAndADifferentOneConflicts()
        {
            var first = rig.Save(); Assert.AreEqual("running", first.Status);
            var job = rig.RunToEnd();
            var same = rig.Service.Handle(rig.SaveRequest(token: null, requestId: "save-00001"));
            Assert.AreEqual("completed", same.Status, same.ReasonCode + same.Data);
            Assert.AreEqual(job.SavedSha256, (string)same.Data["sha256"]);
            Assert.AreEqual(1, rig.Files.Writes.Count(w => w == rig.ShipFile("Probe Saved")), "no second write");
            var other = rig.Service.Handle(rig.SaveRequest("Another Name", "save-00001"));
            Assert.AreEqual("request_id_conflict", other.ReasonCode);
        }

        [TestMethod] public void OperationStatusAnswersForASaveJob()
        {
            rig.Save();
            var running = rig.Service.Handle(rig.StatusRequest("save-00001"));
            Assert.AreEqual("running", running.Status); Assert.AreEqual("save_craft", (string)running.Data["operation"]);
            rig.RunToEnd();
            var done = rig.Service.Handle(rig.StatusRequest("save-00001"));
            Assert.AreEqual("completed", done.Status); Assert.IsNull(done.ReasonCode);
        }

        // ---- failure paths ----

        [TestMethod] public void AWriteFailureLeavesNothingBehindAndIsNotDispatched()
        {
            rig.Files.FailWrite = p => p.EndsWith("Probe Saved.craft");
            var job = SaveAndRun();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("write_failed", job.ReasonCode); Assert.IsFalse(job.Dispatched);
            Assert.IsTrue((bool)job.ToEnvelope()["notDispatched"]);
            Assert.IsFalse(rig.Files.Exists(rig.ShipFile("Probe Saved")));
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
            Assert.IsFalse(rig.Files.Exists(rig.Paths.LedgerFile));
        }

        [TestMethod] public void AFileThatAppearsBetweenAdmissionAndTheWriteIsNeverOverwritten()
        {
            rig.Save();
            rig.Files.Put(rig.ShipFile("Probe Saved"), "ship = Appeared\r\n");
            var job = rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("file_exists", job.ReasonCode); Assert.IsFalse(job.Dispatched);
            Assert.AreEqual("ship = Appeared\r\n", rig.Files.Text(rig.ShipFile("Probe Saved")));
        }

        [TestMethod] public void ACorruptedWriteIsDetectedAndTheNewFileRemoved()
        {
            var target = rig.ShipFile("Probe Saved");
            rig.Files.CorruptWrite = p => p == target;
            var job = SaveAndRun();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("save_verify_failed", job.ReasonCode, job.Detail);
            Assert.IsTrue(job.Dispatched, "the file did exist for a moment");
            Assert.IsFalse(rig.Files.Exists(target), "the new file was removed");
            Assert.IsFalse(rig.Files.Exists(rig.Paths.LedgerFile), "nothing is owned");
            Assert.IsNull(job.SavedPath);
            Assert.IsTrue(job.ToEnvelope()["declaredOutputs"].Any(d => (string)d["action"] == "deleted"));
        }

        [TestMethod] public void ACorruptedReplaceRestoresThePreviousBytes()
        {
            var first = SaveAndRun(); var target = rig.ShipFile("Probe Saved"); var before = rig.Files.Text(target);
            Mutate(() => rig.Port.Name = "Changed");
            var writes = 0;
            rig.Files.CorruptWrite = p => p == target && writes++ == 0;
            rig.Save(requestId: "save-00002", replace: first.SavedSha256);
            var job = rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("save_verify_failed", job.ReasonCode);
            Assert.AreEqual(before, rig.Files.Text(target), "the previous bytes are back");
            Assert.AreEqual(first.SavedSha256, CraftLedger.Load(rig.Files, rig.Paths.LedgerFile).Find("VAB", "Probe Saved.craft").Sha256, "the ledger still describes the file");
            Assert.IsTrue(job.ToEnvelope()["declaredOutputs"].Any(d => (string)d["action"] == "restored"));
        }

        [TestMethod] public void AFailedRevertIsIndeterminate()
        {
            var target = rig.ShipFile("Probe Saved");
            rig.Files.CorruptWrite = p => p == target; rig.Files.FailDelete = p => p == target;
            var job = SaveAndRun();
            Assert.AreEqual("indeterminate", job.Status);
            Assert.IsTrue(job.ToEnvelope()["declaredOutputs"].Any(d => ((string)d["detail"]).Contains("revert failed")));
        }

        [TestMethod] public void APartsCheckFailureIsAVerifyFailureToo()
        {
            rig.Port.AllPartsFoundResult = false;
            var job = SaveAndRun();
            Assert.AreEqual("save_verify_failed", job.ReasonCode); StringAssert.Contains(job.Detail, "parts_not_found");
            Assert.IsFalse(rig.Files.Exists(rig.ShipFile("Probe Saved")));
        }

        [TestMethod] public void HumanInputInsideTheLockEndsTheSaveBeforeAnyWrite()
        {
            rig.Save();
            rig.Frame(); // locked, about to run the save step
            rig.Tracker.OnEvent(EditorEventKind.PartPicked);
            var job = rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("human_input_during_operation", job.ReasonCode);
            Assert.IsFalse(job.Dispatched); Assert.IsFalse(rig.Files.Exists(rig.ShipFile("Probe Saved")));
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
        }

        [TestMethod] public void LeavingTheEditorBeforeTheWriteEndsTheSave()
        {
            rig.Save();
            rig.Port.InEditorValue = false;
            var job = rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.IsFalse(job.Dispatched);
            Assert.IsFalse(rig.Files.Exists(rig.ShipFile("Probe Saved")));
        }

        [TestMethod] public void ARevokedLeaseBeforeTheWriteEndsTheSaveWithNothingWritten()
        {
            rig.Save();
            rig.Authority.ReleaseLease(rig.Lease);
            var job = rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.IsFalse(job.Dispatched);
            Assert.IsFalse(rig.Files.Exists(rig.ShipFile("Probe Saved")));
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
        }

        [TestMethod] public void AnAbortBeforeTheStepFailsWithoutWriting()
        {
            rig.Save();
            rig.Runner.Abort();
            Assert.AreEqual("failed", rig.Runner.Current.Status); Assert.AreEqual("bridge_stopped", rig.Runner.Current.ReasonCode);
            Assert.IsFalse(rig.Files.Exists(rig.ShipFile("Probe Saved")));
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
        }

        // ---- sidecars (R3-section 9) ----

        [TestMethod] public void ASidecarAppearingBesideTheSaveIsReportedNeverAFailure()
        {
            var target = rig.ShipFile("Probe Saved");
            rig.Files.OnWrite = p => { if (p == target) rig.Files.Put(target + ".original", "x"); };
            rig.Files.Put(rig.ShipFile("Human") + ".loadmeta", "old");
            var job = SaveAndRun();
            Assert.AreEqual("completed", job.Status);
            var outputs = job.ToEnvelope()["declaredOutputs"];
            Assert.IsTrue(outputs.Any(d => (string)d["path"] == target + ".original" && (string)d["action"] == "reported"));
            Assert.IsFalse(outputs.Any(d => ((string)d["path"]).EndsWith("Human.craft.loadmeta") || ((string)d["path"]).EndsWith(".loadmeta")), "an untouched sidecar is not reported");
        }

        [TestMethod] public void AChangedLoadmetaIsReportedToo()
        {
            var meta = rig.ShipFile("Human") + ".loadmeta"; rig.Files.Put(meta, "old");
            var target = rig.ShipFile("Probe Saved");
            rig.Files.OnWrite = p => { if (p == target) rig.Files.Touch(meta); };
            var job = SaveAndRun();
            Assert.AreEqual("completed", job.Status);
            Assert.IsTrue(job.ToEnvelope()["declaredOutputs"].Any(d => (string)d["path"] == meta && ((string)d["detail"]).Contains("changed_beside_save")));
        }

        // ---- the human save afterwards ----

        [TestMethod] public void ASecondSaveOfADifferentNameLeavesTheFirstFileAlone()
        {
            SaveAndRun("First One"); var firstHash = Hash("First One");
            SaveAndRun("Second One", "save-00002");
            Assert.AreEqual(firstHash, Hash("First One"));
            Assert.AreEqual("Second One", rig.Port.LastSaved);
        }
    }
}
