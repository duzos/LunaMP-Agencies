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
    [TestClass]
    public class EditorOperationAdmissionTests
    {
        private OperationRig rig;

        [TestInitialize] public void Setup() { rig = new OperationRig(); }

        private BridgeResponse Refused(BridgeResponse response, string reason, int loads = 0)
        {
            Assert.AreEqual("failed", response.Status, response.Data.ToString());
            Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.AreEqual(true, (bool)response.Data["notDispatched"], "an admission refusal is a guaranteed no-effect failure");
            Assert.IsFalse(rig.Runner.Busy, "nothing was started");
            Assert.AreEqual(loads, rig.Port.LoadCalls);
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
            return response;
        }

        // ---- lease and authority ----

        [TestMethod] public void AMutationWithoutALeaseIsLeaseRequired()
        {
            var request = rig.ApplyRequest(); request.LeaseId = null;
            Refused(rig.Service.Handle(request), "lease_required");
        }

        [TestMethod] public void AMalformedOrUnknownLeaseIsLeaseInvalid()
        {
            var bad = rig.ApplyRequest(); bad.LeaseId = "short";
            Refused(rig.Service.Handle(bad), "lease_invalid");
            var unknown = rig.ApplyRequest(requestId: "apply-0002"); unknown.LeaseId = new string('a', 32);
            Refused(rig.Service.Handle(unknown), "lease_invalid");
        }

        [TestMethod] public void ARevokedLeaseReportsWhyAndAnExpiredOneSaysExpired()
        {
            var request = rig.ApplyRequest();
            rig.Authority.ReleaseLease(rig.Lease);
            Refused(rig.Service.Handle(request), "lease_invalid"); // released: the id is simply not a lease any more
            rig = new OperationRig(); var second = rig.ApplyRequest();
            rig.Authority.HumanTakeover();
            Refused(rig.Service.Handle(second), "authority_revoked");
            rig = new OperationRig(); rig.Heartbeats = false; var third = rig.ApplyRequest();
            rig.Run(30, 100);
            Refused(rig.Service.Handle(third), "lease_expired");
        }

        [TestMethod] public void AGrantWithoutTheOperationFamilyDeniesIt()
        {
            rig = new OperationRig(operations: new[] { OperationEffects.RestoreSnapshot });
            Refused(rig.Service.Handle(rig.ApplyRequest()), "grant_operation_denied");
            rig = new OperationRig(operations: new[] { OperationEffects.ReplaceCraft });
            Refused(rig.Service.Handle(rig.RestoreRequest("snap000001")), "grant_operation_denied");
        }

        // ---- staleness ----

        [TestMethod] public void AnEditWithNoEventAfterTheTokenIsStaleAndTakesOver()
        {
            var request = rig.ApplyRequest();
            rig.Port.Craft = CraftEdit.SetPartKey(rig.Port.Craft, 0, "probeValue", "1"); // a PAW slider: no event, no frame yet
            var response = Refused(rig.Service.Handle(request), "stale_revision");
            Assert.IsFalse(rig.Authority.LeaseHeld, "the observation noticed the change and took over");
            var next = rig.ApplyRequest(requestId: "apply-0002", token: rig.Token(), planHash: rig.PlanHash(OperationRig.TwoStageGraph));
            Refused(rig.Service.Handle(next), "authority_revoked");
            Assert.IsTrue(rig.Authority.Status().CooldownSeconds > 0);
            Assert.IsNotNull(response);
        }

        [TestMethod] public void AnOlderTokenAfterAnotherChangeIsStale()
        {
            var request = rig.ApplyRequest();
            rig.Tracker.OnEvent(EditorEventKind.PartPlaced); rig.Frame(16); // a human attach
            Refused(rig.Service.Handle(request), "authority_revoked");
        }

        [TestMethod] public void ATokenFromAnotherWorldIsStale()
        {
            var token = rig.Token();
            rig.Context.Epoch = "epoch2"; rig.Frame(16);
            var response = rig.Service.Handle(rig.ApplyRequest(token: token));
            Assert.AreEqual("failed", response.Status);
            Assert.IsTrue(response.ReasonCode == "authority_revoked" || response.ReasonCode == "stale_revision" || response.ReasonCode == "lease_invalid", response.ReasonCode);
            Assert.AreEqual(0, rig.Port.LoadCalls);
        }

        [TestMethod] public void ATokenThatIsNotOneIsAnInvalidArgument()
        {
            Refused(rig.Service.Handle(rig.ApplyRequest(token: "not-a-token")), "invalid_argument");
        }

        // ---- busy and availability ----

        [TestMethod] public void ABusyEditorIsRefusedWithTheReasons()
        {
            rig.Port.Fsm = "st_place";
            var response = Refused(rig.Service.Handle(rig.ApplyRequest()), "editor_busy");
            CollectionAssert.Contains(((JArray)response.Data["busy"]).Select(t => (string)t).ToList(), "fsm_not_idle");
            rig.Port.Fsm = "st_idle"; rig.Port.Locks.Add("SaveConfirmationDialog");
            response = Refused(rig.Service.Handle(rig.ApplyRequest(requestId: "apply-0002")), "editor_busy");
            CollectionAssert.Contains(((JArray)response.Data["busy"]).Select(t => (string)t).ToList(), "modal_lock:SaveConfirmationDialog");
            rig.Port.Locks.Clear(); rig.Port.Locks.Add(EditorIdle.LaunchLockId);
            response = Refused(rig.Service.Handle(rig.ApplyRequest(requestId: "apply-0003")), "editor_busy");
            CollectionAssert.Contains(((JArray)response.Data["busy"]).Select(t => (string)t).ToList(), "launch_pending");
        }

        [TestMethod] public void AnOutOfEditorBridgeIsUnavailable()
        {
            var request = rig.ApplyRequest();
            rig.Port.InEditorValue = false;
            Refused(rig.Service.Handle(request), "editor_unavailable");
        }

        [TestMethod] public void AnOperationInProgressBlocksAnotherAtAdmission()
        {
            Assert.AreEqual("running", rig.Apply().Status);
            var second = rig.Service.Handle(rig.ApplyRequest(requestId: "apply-0002", token: "x"));
            Assert.AreEqual("failed", second.Status);
            Assert.AreEqual("editor_busy", second.ReasonCode);
            CollectionAssert.Contains(((JArray)second.Data["busy"]).Select(t => (string)t).ToList(), "operation_running");
        }

        [TestMethod] public void TwoRequestsInTheSameFrameCannotBothBeAdmitted()
        {
            var first = rig.ApplyRequest(requestId: "apply-0001"); var second = rig.ApplyRequest(requestId: "apply-0002");
            Assert.AreEqual("running", rig.Service.Handle(first).Status);
            Assert.AreEqual("editor_busy", rig.Service.Handle(second).ReasonCode, "the runner is busy the moment the first is admitted");
        }

        // ---- the unsaved-craft policy ----

        [TestMethod] public void UnsavedHumanWorkIsRefusedUnderThePolicyRefuse()
        {
            rig = new OperationRig(policy: "refuse");
            rig.Port.UnsavedValue = true;
            Refused(rig.Service.Handle(rig.ApplyRequest()), "unsaved_human_craft");
        }

        [TestMethod] public void UnknownUnsavedStateCountsAsUnsaved()
        {
            rig = new OperationRig(policy: "refuse");
            rig.Port.UnsavedValue = null;
            Refused(rig.Service.Handle(rig.ApplyRequest()), "unsaved_human_craft");
        }

        [TestMethod] public void SavedWorkMayBeReplacedUnderRefuse()
        {
            rig = new OperationRig(policy: "refuse");
            Assert.AreEqual("running", rig.Apply().Status);
        }

        [TestMethod] public void UnsavedWorkIsReplacedUnderSnapshotThenReplace()
        {
            rig.Port.UnsavedValue = true;
            Assert.AreEqual("running", rig.Apply().Status);
            var job = rig.RunToEnd(); Assert.AreEqual("completed", job.Status); Assert.AreEqual("true", job.Snapshot.WasUnsaved);
        }

        [TestMethod] public void SnapshotThenReplaceWithoutTheGuardIsRefused()
        {
            rig.Port.UnsavedValue = true; rig.Port.Caps.SaveOverwriteGuard = false; rig.Port.Caps.SetLastSanitizedSaveName = false;
            Refused(rig.Service.Handle(rig.ApplyRequest()), "save_overwrite_guard_unavailable");
        }

        [TestMethod] public void ANameCollisionWithoutTheGuardIsRefused()
        {
            rig.Port.Caps.SaveOverwriteGuard = false;
            rig.Files.Put(rig.Paths.ResolveNewShip("VAB", "Probe One").FullPath, "ship = Probe One");
            Refused(rig.Service.Handle(rig.ApplyRequest()), "name_collision_unguarded");
            // With the guard available, the same collision is fine: KSP prompts before a human overwrite.
            rig = new OperationRig(); rig.Files.Put(rig.Paths.ResolveNewShip("VAB", "Probe One").FullPath, "ship = Probe One");
            Assert.AreEqual("running", rig.Apply().Status);
        }

        [TestMethod] public void ACraftWeGeneratedAndNobodyTouchedIsNotHumanWork()
        {
            rig = new OperationRig(policy: "refuse");
            Assert.AreEqual("running", rig.Apply().Status); rig.RunToEnd();
            Assert.IsTrue(rig.Port.Unsaved == true, "an applied craft is flagged unsaved so a human Save prompts");
            var second = rig.Service.Handle(rig.ApplyRequest(requestId: "apply-0002"));
            Assert.AreEqual("running", second.Status, "iterating on our own craft is allowed under refuse: " + second.ReasonCode);
        }

        [TestMethod] public void ACraftAHumanChangedAfterOurApplyIsHumanWork()
        {
            rig = new OperationRig(policy: "refuse");
            rig.Apply(); rig.RunToEnd();
            rig.Port.Craft = CraftEdit.SetPartKey(rig.Port.Craft, 0, "probeValue", "7"); rig.Run(70, 500); // takeover, cooldown passes
            rig.Authority.Heartbeat(rig.Lease ?? "");
            rig.AcquireLease();
            Refused(rig.Service.Handle(rig.ApplyRequest(requestId: "apply-0002")), "unsaved_human_craft", 1);
        }

        // ---- the plan ----

        [TestMethod] public void APlanThatNoLongerHashesTheSameIsPlanChanged()
        {
            var response = rig.Service.Handle(rig.ApplyRequest(planHash: new string('a', 64)));
            Refused(response, "plan_changed");
            Assert.AreEqual(rig.PlanHash(OperationRig.TwoStageGraph), (string)response.Data["planHash"]);
        }

        [TestMethod] public void ChangedCatalogDataMakesThePlanChanged()
        {
            var request = rig.ApplyRequest();
            rig.Reader.Parts["fuelTankSmall"].StackNodes[0].Position[1] += 0.01;
            Refused(rig.Service.Handle(request), "plan_changed");
        }

        [TestMethod] public void ALockedPartIsRefused()
        {
            var request = rig.ApplyRequest();
            rig.Reader.Parts["liquidEngine.v2"].TechAvailable = false;
            var response = Refused(rig.Service.Handle(request), "part_locked");
            Assert.IsTrue(((JArray)response.Data["issues"]).Any(i => (string)i["code"] == "part_locked" && (string)i["partId"] == "engine"));
        }

        [TestMethod] public void AnUnknownPartIsAnInvalidGraphWithIssues()
        {
            var graph = OperationRig.TwoStageGraph.Replace("liquidEngine.v2", "noSuchPart");
            var request = new BridgeRequest { RequestId = "w", Operation = EditorOperations.ApplyCraft, LeaseId = rig.Lease, Arguments = new JObject
            { ["requestId"] = "apply-0001", ["graph"] = graph, ["expectedPlanHash"] = new string('a', 64), ["expectedRevision"] = rig.Token() } };
            var response = Refused(rig.Service.Handle(request), "invalid_graph");
            Assert.IsTrue(((JArray)response.Data["issues"]).Any(i => (string)i["code"] == "unknown_part"));
        }

        [TestMethod] public void AnUnverifiedPartIsNotRefusedForConstructionSupport()
        {
            rig.Reader.Parts["fuelTankLarge"] = new CatalogPartSource
            {
                Name = "fuelTankLarge", KspCategory = "Propulsion", Buildable = true, TechAvailable = true, ModelPurchased = true, ModuleNames = new string[0], ResourceNames = new[] { "LiquidFuel" },
                StackNodes = new[] { new CatalogNodeSource { Id = "top", Position = new[] { 0.0, 1.0, 0.0 }, Orientation = new[] { 0.0, 1.0, 0.0 }, Size = 1 }, new CatalogNodeSource { Id = "bottom", Position = new[] { 0.0, -1.0, 0.0 }, Orientation = new[] { 0.0, -1.0, 0.0 }, Size = 1 } },
                AttachRules = new CatalogAttachRulesSource { Stack = true, AllowStack = true }
            };
            var graph = OperationRig.TwoStageGraph.Replace("fuelTankSmall", "fuelTankLarge");
            var request = new BridgeRequest { RequestId = "w", Operation = EditorOperations.ApplyCraft, LeaseId = rig.Lease, Arguments = new JObject
            { ["requestId"] = "apply-0001", ["graph"] = graph, ["expectedPlanHash"] = new string('a', 64), ["expectedRevision"] = rig.Token() } };
            Refused(rig.Service.Handle(request), "plan_changed");
        }

        [TestMethod] public void AnUnsupportedConfigurationIsRefused()
        {
            var graph = OperationRig.TwoStageGraph.Replace("\"id\":\"pod\",\"part\":\"mk1pod.v2\"", "\"id\":\"pod\",\"part\":\"mk1pod.v2\",\"configuration\":[\"x\"]");
            var request = new BridgeRequest { RequestId = "w", Operation = EditorOperations.ApplyCraft, LeaseId = rig.Lease, Arguments = new JObject
            { ["requestId"] = "apply-0001", ["graph"] = graph, ["expectedPlanHash"] = new string('a', 64), ["expectedRevision"] = rig.Token() } };
            Refused(rig.Service.Handle(request), "unsupported_configuration");
        }

        [TestMethod] public void AGraphForTheOtherFacilityIsAFacilityMismatch()
        {
            var graph = OperationRig.TwoStageGraph.Replace("\"VAB\"", "\"SPH\"");
            var request = new BridgeRequest { RequestId = "w", Operation = EditorOperations.ApplyCraft, LeaseId = rig.Lease, Arguments = new JObject
            { ["requestId"] = "apply-0001", ["graph"] = graph, ["expectedPlanHash"] = new string('a', 64), ["expectedRevision"] = rig.Token() } };
            Refused(rig.Service.Handle(request), "facility_mismatch");
        }

        [TestMethod] public void WithoutALiveModVersionsHeaderTheApplyIsRefused()
        {
            var request = rig.ApplyRequest();
            rig.Port.Header = new EditorHeader("1.12.5", null);
            Refused(rig.Service.Handle(request), "mod_versions_unavailable");
            rig.Port.Header = null;
            Refused(rig.Service.Handle(rig.ApplyRequest(requestId: "apply-0002", planHash: new string('b', 64))), "mod_versions_unavailable");
        }

        [TestMethod] public void TheRenderedCraftCarriesTheLiveModVersionsAndPersistentIds()
        {
            rig.Port.Header = new EditorHeader("1.12.5", "Mod=1.2.3;Other=9");
            var job = rig.Runner; rig.Apply(); var running = rig.Runner.Current;
            rig.RunToEnd();
            var staged = running.Plan.CraftText;
            var node = Pure.ConfigText.Parse(staged);
            Assert.AreEqual("Mod=1.2.3;Other=9", node.First("_modVersions"));
            Assert.AreEqual("1.12.5", node.First("version"));
            Assert.AreEqual(3, node.Children("PART").Count());
            Assert.IsTrue(node.Children("PART").All(p => p.First("persistentId") != "0"));
            Assert.IsNotNull(job);
        }

        // ---- arguments ----

        [DataTestMethod]
        [DataRow("requestId", "short")] [DataRow("requestId", "has space in it")] [DataRow("expectedPlanHash", "ABC")] [DataRow("expectedPlanHash", "")]
        [DataRow("graph", "")] [DataRow("expectedRevision", "")]
        public void BadArgumentsAreInvalidArguments(string name, string value)
        {
            var request = rig.ApplyRequest(); request.Arguments[name] = value;
            Refused(rig.Service.Handle(request), "invalid_argument");
        }

        [TestMethod] public void AMissingArgumentIsAnInvalidArgument()
        {
            var request = rig.ApplyRequest(); request.Arguments.Remove("graph");
            Refused(rig.Service.Handle(request), "invalid_argument");
            var typed = rig.ApplyRequest(requestId: "apply-0002"); typed.Arguments["expectedRevision"] = 5;
            Refused(rig.Service.Handle(typed), "invalid_argument");
        }

        [TestMethod] public void AnOversizeGraphIsAnInvalidArgument()
        {
            var request = rig.ApplyRequest(); request.Arguments["graph"] = "{\"name\":\"" + new string('x', ConstructionLimits.MaxGraphBytes) + "\"}";
            Refused(rig.Service.Handle(request), "invalid_argument");
        }

        [TestMethod] public void MalformedGraphJsonIsAnInvalidArgument()
        {
            var request = rig.ApplyRequest(); request.Arguments["graph"] = "{\"name\":";
            Refused(rig.Service.Handle(request), "invalid_argument");
            request.Arguments["graph"] = "{\"name\":\"a\",\"name\":\"b\"}";
            Refused(rig.Service.Handle(request), "invalid_argument");
        }

        // ---- dedupe ----

        [TestMethod] public void TheSameRequestIsIdempotentAndStartsNoSecondJob()
        {
            var request = rig.ApplyRequest();
            var first = rig.Service.Handle(request); var job = rig.Runner.Current;
            var again = rig.Service.Handle(request);
            Assert.AreEqual("running", first.Status); Assert.AreEqual("running", again.Status);
            Assert.AreSame(job, rig.Runner.Current);
            rig.RunToEnd();
            var done = rig.Service.Handle(request);
            Assert.AreEqual("completed", done.Status, "a finished job answers the same request with its result");
            Assert.AreEqual(1, rig.Port.LoadCalls);
        }

        [TestMethod] public void ADifferentRequestWithTheSameIdIsAConflict()
        {
            rig.Apply(); rig.RunToEnd();
            var other = rig.ApplyRequest(graph: OperationRig.TwoStageGraph.Replace("Probe One", "Probe Two"), requestId: "apply-0001");
            var response = rig.Service.Handle(other);
            Assert.AreEqual("failed", response.Status); Assert.AreEqual("request_id_conflict", response.ReasonCode);
            Assert.AreEqual(1, rig.Port.LoadCalls);
        }

        [TestMethod] public void ARefusedRequestDoesNotConsumeItsId()
        {
            var request = rig.ApplyRequest(token: rig.Token());
            rig.Port.Fsm = "st_place"; Assert.AreEqual("editor_busy", rig.Service.Handle(request).ReasonCode);
            rig.Port.Fsm = "st_idle";
            Assert.AreEqual("running", rig.Service.Handle(request).Status, "the same request succeeds once the editor is idle");
        }

        // ---- editor.operation_status ----

        [TestMethod] public void StatusReportsRunningThenTheResultAndUnknownJobsClearly()
        {
            rig.Apply();
            var running = rig.Service.Handle(rig.StatusRequest("apply-0001"));
            Assert.AreEqual("running", running.Status); Assert.AreEqual("locking", (string)running.Data["phase"]);
            rig.RunToEnd();
            var done = rig.Service.Handle(rig.StatusRequest("apply-0001"));
            Assert.AreEqual("completed", done.Status); Assert.AreEqual("done", (string)done.Data["phase"]);
            var unknown = rig.Service.Handle(rig.StatusRequest("never-seen-1"));
            Assert.AreEqual("failed", unknown.Status); Assert.AreEqual("job_unknown", unknown.ReasonCode);
            var bad = rig.Service.Handle(rig.StatusRequest("x"));
            Assert.AreEqual("invalid_argument", bad.ReasonCode);
        }

        [TestMethod] public void AFailedJobReportsItsReasonOnTheResponse()
        {
            rig.Files.CorruptWrite = p => p.Contains("kc-snap-") && p.EndsWith(".craft");
            rig.Apply(); rig.RunToEnd();
            var response = rig.Service.Handle(rig.StatusRequest("apply-0001"));
            Assert.AreEqual("failed", response.Status); Assert.AreEqual("snapshot_unverified", response.ReasonCode);
            Assert.AreEqual(true, (bool)response.Data["notDispatched"]);
        }

        // ---- restore ----

        [TestMethod] public void ASnapshotCanBeRestoredByAnotherJob()
        {
            var original = rig.Port.Craft;
            rig.Apply(); var apply = rig.RunToEnd();
            Assert.AreEqual("completed", apply.Status);
            var originalFp = SnapshotStore.Fingerprint(Pure.ConfigText.Parse(original), new EditorUi("Probe", "", "Squad/Flags/default"));
            var response = rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId));
            Assert.AreEqual("running", response.Status, response.ReasonCode + response.Data);
            var job = rig.RunToEnd();
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.AreEqual("restore_snapshot", (string)job.ToEnvelope()["operation"]);
            Assert.AreEqual("restored", job.Restore.Result);
            Assert.AreEqual(originalFp, SnapshotStore.Fingerprint(Pure.ConfigText.Parse(rig.Port.Craft), rig.Port.ReadUi()));
            Assert.AreEqual("Probe", rig.Port.Name); Assert.AreEqual("Probe", rig.Port.LastSaved);
            Assert.AreEqual(false, rig.Port.UnsavedValue, "the snapshot was of a saved craft");
            Assert.AreEqual(apply.Snapshot.CraftPath, rig.Port.LoadedPaths.Last());
        }

        [TestMethod] public void RestoringAnUnsavedSnapshotMarksTheCraftUnsavedAgain()
        {
            rig.Port.UnsavedValue = true; rig.Apply(); var apply = rig.RunToEnd();
            rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId));
            var job = rig.RunToEnd();
            Assert.AreEqual("completed", job.Status); Assert.AreEqual(true, rig.Port.UnsavedValue);
            Assert.AreEqual("private_field", job.Restore.UnsavedMarkerRestored); Assert.AreEqual(-1, rig.Port.SaveState.UndoIndexAtLastSave);
        }

        [TestMethod] public void WithoutTheGuardAnUnsavedRestoreFallsBackToSetBackup()
        {
            rig.Port.UnsavedValue = true; rig.Apply(); var apply = rig.RunToEnd();
            rig.Port.Caps.SaveOverwriteGuard = false; rig.Port.Caps.UnsavedMarker = false; rig.Port.Caps.UndoLevel = false; rig.Port.Caps.UndoIndexAtLastSave = false;
            rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId));
            var job = rig.RunToEnd();
            Assert.AreEqual("completed", job.Status, job.ReasonCode);
            Assert.AreEqual("set_backup", job.Restore.UnsavedMarkerRestored); Assert.AreEqual("false", job.Restore.SaveFieldsRestored); Assert.AreEqual(1, rig.Port.SetBackupCalls);
        }

        [TestMethod] public void RestoreRefusesUnknownCorruptAndOtherFacilitySnapshots()
        {
            rig.Apply(); var apply = rig.RunToEnd();
            Refused2(rig.Service.Handle(rig.RestoreRequest("nosuchsnap1")), "snapshot_not_found");
            rig.Files.Put(apply.Snapshot.CraftPath, "tampered");
            Refused2(rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId, requestId: "restore-0002")), "snapshot_unverified");
        }

        [TestMethod] public void RestoreRefusesASnapshotFromTheOtherFacility()
        {
            rig.Apply(); var apply = rig.RunToEnd();
            var metaPath = rig.Paths.RecoveryMetaPath(apply.Snapshot.SnapshotId).FullPath;
            var meta = JObject.Parse(rig.Files.Text(metaPath)); meta["facility"] = "SPH";
            rig.Files.Put(metaPath, meta.ToString());
            Refused2(rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId, requestId: "restore-0003")), "facility_mismatch");
        }

        private OperationJob ApplyThenHumanEdit(string policy)
        {
            rig = new OperationRig(policy: policy);
            rig.Apply(); var apply = rig.RunToEnd();
            rig.Port.Craft = CraftEdit.SetPartKey(rig.Port.Craft, 0, "probeValue", "7"); rig.Run(70, 500); // takeover, cooldown passes
            rig.AcquireLease();
            return apply;
        }

        [TestMethod] public void RestoreOverUnsavedHumanWorkIsRefusedUnderRefuseBeforeDispatch()
        {
            var apply = ApplyThenHumanEdit("refuse");
            Refused(rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId)), "unsaved_human_craft", 1);
        }

        [TestMethod] public void RestoreOverUnsavedHumanWorkNeedsTheGuardUnderSnapshotThenReplace()
        {
            var apply = ApplyThenHumanEdit("snapshot_then_replace");
            rig.Port.Caps.SaveOverwriteGuard = false;
            Refused(rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId)), "save_overwrite_guard_unavailable", 1);
        }

        [TestMethod] public void RestoreOverOurOwnUntouchedCraftIsAllowedUnderRefuse()
        {
            rig = new OperationRig(policy: "refuse");
            rig.Apply(); var apply = rig.RunToEnd();
            Assert.AreEqual("running", rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId)).Status);
        }

        [TestMethod] public void RestoreOverNonEmptyWorkSnapshotsItFirst()
        {
            var apply = ApplyThenHumanEdit("snapshot_then_replace");
            var humanCraft = rig.Port.Craft;
            Assert.AreEqual("running", rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId)).Status);
            var job = rig.RunToEnd();
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.IsNotNull(job.Snapshot, "a restore over existing work takes its own snapshot");
            Assert.AreNotEqual(apply.Snapshot.SnapshotId, job.Snapshot.SnapshotId);
            Assert.AreEqual("true", job.Snapshot.WasUnsaved);
            Assert.IsTrue(job.EffectsApplied.Contains("snapshot_taken"));
            Assert.AreEqual(2, ((JArray)rig.Observation.State().Data["recentSnapshots"]).Count);
            Assert.AreEqual(apply.Snapshot.CraftPath, rig.Port.LoadedPaths.Last(), "the requested snapshot stays the load target");
            Assert.AreEqual("restored", job.Restore.Result);
        }

        [TestMethod] public void ARestoreOverAnEmptyEditorTakesNoSnapshot()
        {
            rig.Apply(); var apply = rig.RunToEnd();
            rig.Port.Parts = 0; rig.Port.Craft = ""; rig.Port.Ship = new object(); rig.Port.Fsm = "st_podSelect"; rig.Port.UnsavedValue = false;
            rig.Run(70, 500); rig.AcquireLease();
            rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId));
            var job = rig.RunToEnd();
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.IsNull(job.Snapshot);
        }

        [TestMethod] public void AFailedRestoreGoesBackToTheSnapshotItTookFirst()
        {
            var apply = ApplyThenHumanEdit("snapshot_then_replace");
            var humanCraft = rig.Port.Craft;
            var humanFingerprint = SnapshotStore.Fingerprint(Pure.ConfigText.Parse(humanCraft), rig.Port.ReadUi());
            rig.Port.Mode = EditorFake.LoadMode.EmptyAfterLoad;
            rig.Port.OnLoad = (f, p) => f.Mode = EditorFake.LoadMode.Normal; // the target loads empty, the recovery load works
            rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId));
            var job = rig.RunToEnd();
            Assert.AreEqual("failed", job.Status);
            Assert.IsNotNull(job.Snapshot);
            Assert.AreEqual(job.Snapshot.CraftPath, rig.Port.LoadedPaths.Last(), "the recovery load is the snapshot taken before the restore");
            Assert.AreEqual(humanFingerprint, SnapshotStore.Fingerprint(Pure.ConfigText.Parse(rig.Port.Craft), rig.Port.ReadUi()), "the human's craft is back");
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
        }

        private void Refused2(BridgeResponse response, string reason)
        {
            Assert.AreEqual("failed", response.Status); Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.IsFalse(rig.Runner.Busy);
        }

        [TestMethod] public void RestoreNeedsItsOwnGrantFamily()
        {
            rig = new OperationRig(operations: new[] { OperationEffects.ReplaceCraft });
            rig.Apply(); var apply = rig.RunToEnd();
            Assert.AreEqual("grant_operation_denied", rig.Service.Handle(rig.RestoreRequest(apply.Snapshot.SnapshotId)).ReasonCode);
        }

        // ---- editor.state extras ----

        [TestMethod] public void EditorStateListsRecentSnapshotsAndTheLastOperation()
        {
            Assert.AreEqual(0, ((JArray)rig.Observation.State().Data["recentSnapshots"]).Count);
            rig.Apply(); var running = rig.Observation.State().Data;
            Assert.AreEqual("operation_running", ((JArray)running["busy"]).Select(t => (string)t).Single());
            var job = rig.RunToEnd();
            var state = rig.Observation.State().Data;
            var snapshots = (JArray)state["recentSnapshots"];
            Assert.AreEqual(1, snapshots.Count); Assert.AreEqual(job.Snapshot.SnapshotId, (string)snapshots[0]["id"]); Assert.AreEqual("Probe", (string)snapshots[0]["shipName"]);
            Assert.AreEqual(3, (int)snapshots[0]["partCount"]);
            Assert.AreEqual("apply-0001", (string)state["lastOperation"]["requestId"]); Assert.AreEqual("completed", (string)state["lastOperation"]["status"]);
        }
    }
}
