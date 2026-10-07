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
    public class EditorOperationRunnerTests
    {
        private OperationRig rig;

        [TestInitialize] public void Setup() { rig = new OperationRig(); }

        private static string Reason(BridgeResponse response) { return response.ReasonCode; }

        private OperationJob Start(string graph = OperationRig.TwoStageGraph, string requestId = "apply-0001")
        {
            var response = rig.Apply(graph, requestId);
            Assert.AreEqual("running", response.Status, response.ReasonCode + " " + response.Data);
            return rig.Runner.Current;
        }

        private string StagingPath(string requestId = "apply-0001") { return rig.Paths.StagingPath(requestId).FullPath; }

        // ---- the main path ----

        [TestMethod] public void AnApplyRunsEveryPhaseAndCompletes()
        {
            var job = Start();
            var phases = new List<string>();
            for (var i = 0; i < 4000 && !job.Terminal; i++) { rig.Frame(50); var name = OperationJob.PhaseName(job.Phase); if (phases.Count == 0 || phases[phases.Count - 1] != name) phases.Add(name); }
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            CollectionAssert.AreEqual(new[] { "snapshot", "staging", "dispatch", "settle", "verify", "post_unlock_grace", "thumbnail_settle", "finalizing", "done" }, phases.Skip(0).ToList().Where(p => p != "locking").ToList());
            Assert.AreEqual(1, rig.Port.LoadCalls);
            Assert.AreEqual(StagingPath(), rig.Port.LoadedPaths[0]);
            Assert.AreEqual(3, rig.Port.Parts);
            Assert.AreEqual("Probe One", rig.Port.Name);
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId), "unlocked");
            Assert.IsTrue(job.Dispatched);
            Assert.IsNotNull(job.Snapshot, "the editor was not empty, so a snapshot was taken");
            Assert.IsFalse(rig.Files.Exists(StagingPath()), "staging is removed at the terminal state");
            Assert.IsTrue(rig.Files.Exists(job.Snapshot.CraftPath), "the snapshot stays");
        }

        [TestMethod] public void TheEnvelopeCarriesTheDocumentedFields()
        {
            var job = Start(); rig.RunToEnd();
            var envelope = job.ToEnvelope();
            foreach (var key in new[] { "operation", "phase", "notDispatched", "editorRevision", "previousEditorRevision", "snapshotId", "effects", "restore", "declaredOutputs", "cleanedArtifacts", "observed", "planHash" })
                Assert.IsNotNull(envelope[key], key);
            Assert.AreEqual("apply_craft", (string)envelope["operation"]);
            Assert.AreEqual(false, (bool)envelope["notDispatched"]);
            Assert.AreEqual("done", (string)envelope["phase"]);
            Assert.AreEqual(job.Snapshot.SnapshotId, (string)envelope["snapshotId"]);
            CollectionAssert.IsSubsetOf(new[] { "snapshot_taken", "craft_load_dispatched", "craft_replaced" }, ((JArray)envelope["effects"]).Select(t => (string)t).ToList());
        }

        [TestMethod] public void ApplyingToAnEmptyEditorTakesNoSnapshot()
        {
            rig = new OperationRig(lease: false);
            rig.Port.Parts = 0; rig.Port.Craft = ""; rig.Port.Ship = new object(); rig.Port.Fsm = "st_podSelect"; rig.Run(20); rig.AcquireLease();
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.IsNull(job.Snapshot); Assert.AreEqual(JTokenType.Null, job.ToEnvelope()["snapshotId"].Type);
            Assert.AreEqual(0, rig.Files.Data.Keys.Count(k => k.Contains("recovery")));
        }

        [TestMethod] public void EditorRevisionIsOnlyReportedAfterTheGraceWindow()
        {
            var job = Start();
            var seenRunning = false;
            for (var i = 0; i < 4000 && !job.Terminal; i++)
            {
                rig.Frame(50);
                if (job.Phase == OperationPhase.Grace || job.Phase == OperationPhase.Thumbnails) { seenRunning = true; Assert.AreEqual(JTokenType.Null, job.ToEnvelope()["editorRevision"].Type); Assert.AreEqual("running", job.Status); }
            }
            Assert.IsTrue(seenRunning);
            var token = (string)job.ToEnvelope()["editorRevision"];
            Assert.AreEqual(TokenCheck.Fresh, rig.Tracker.CheckToken(token, "epoch1"), "the recorded revision is the new baseline");
            Assert.AreEqual(rig.Token(), token);
        }

        [TestMethod] public void OurOwnLoadEventsAreAttributedToTheOperationAndNeverTakeOver()
        {
            var job = Start(); var revision = rig.Tracker.EditRevision;
            rig.RunToEnd();
            Assert.AreEqual("completed", job.Status);
            Assert.IsTrue(rig.Authority.LeaseHeld, "no takeover");
            Assert.IsTrue(job.ObservedEvents >= 4, "restart, setbackup, modified, started and load were all raised by the load");
            Assert.IsTrue(rig.Tracker.EditRevision > revision);
            Assert.IsFalse(job.TakeoverDuringGrace);
        }

        [TestMethod] public void TheTicketMustBeRebasedAfterTheLoadBeforeAnotherDispatchCheckPasses()
        {
            var job = Start();
            while (job.Phase != OperationPhase.Verify) rig.Frame(50);
            // The runner rebased at the end of settle, so a dispatch check passes now ...
            rig.Authority.ValidateForDispatch(job.Ticket, rig.Context.CurrentContext(), rig.Context.CurrentBinding(), job.Effects);
            // ... but a second load's worth of events (revision moves) without a rebase would not.
            rig.Tracker.OnEvent(EditorEventKind.ShipModified);
            rig.Port.Ship = new object(); rig.Tracker.RefreshGeneration();
            ExpectAuthorityError("stale_context", () => rig.Authority.ValidateForDispatch(job.Ticket, rig.Context.CurrentContext(), rig.Context.CurrentBinding(), job.Effects));
            rig.Authority.RebaseUnderOperation(job.Ticket, job.RequestId, rig.Tracker.EditRevision);
            rig.Authority.ValidateForDispatch(job.Ticket, rig.Context.CurrentContext(), rig.Context.CurrentBinding(), job.Effects);
        }

        // ---- thumbnails, inside the lock ----

        [TestMethod] public void TheDelayedThumbnailIsHousekeptBeforeTheLocksAreReleased()
        {
            var human = rig.Thumb("Probe One"); rig.Files.Put(human, "human original"); var other = rig.Thumb("Somebody Else"); rig.Files.Put(other, "theirs");
            rig.Port.OnLoad = (f, p) =>
            {
                rig.Files.Put(rig.Thumb("kc-apply-0001"), "generated from the staging file");
                rig.Files.Put(rig.Thumb("Probe One"), "generated from the ship name");
                rig.Files.Put(rig.Thumb("Fresh Unrelated"), "new");
            };
            var job = Start(); var lockedWhenCleaned = false;
            for (var i = 0; i < 4000 && job.Phase != OperationPhase.Finalize && !job.Terminal; i++) rig.Frame(50);
            lockedWhenCleaned = rig.Port.Locks.Contains(EditorIdle.OperationLockId) && !rig.Files.Exists(rig.Thumb("kc-apply-0001"));
            Assert.IsTrue(lockedWhenCleaned, "thumbnail settle runs inside the lock, before the unlock");
            rig.RunToEnd();
            Assert.AreEqual("completed", job.Status);
            Assert.AreEqual("human original", rig.Files.Text(human), "the human's ship-named thumbnail is restored");
            Assert.AreEqual("theirs", rig.Files.Text(other)); Assert.IsTrue(rig.Files.Exists(rig.Thumb("Fresh Unrelated")), "unrelated thumbnails are reported, not deleted");
            Assert.AreEqual("settled", (string)job.ToEnvelope()["observed"]["thumbnail"]);
            var outputs = (JArray)job.ToEnvelope()["declaredOutputs"];
            Assert.IsTrue(outputs.Any(o => (string)o["class"] == "thumbnail" && (string)o["action"] == "deleted"));
            Assert.IsTrue(outputs.Any(o => (string)o["class"] == "thumbnail" && (string)o["action"] == "restored"));
            Assert.IsTrue(outputs.Any(o => (string)o["class"] == "thumbnail" && (string)o["action"] == "reported"));
            Assert.IsTrue(((JArray)job.ToEnvelope()["cleanedArtifacts"]).Count >= 3, "staging file plus the two thumbnail actions");
        }

        [TestMethod] public void AThumbnailThatNeverAppearsIsClassifiedAsCacheAfterTenSeconds()
        {
            var job = Start(); var thumbStart = 0L;
            for (var i = 0; i < 4000 && !job.Terminal; i++) { rig.Frame(100); if (job.Phase == OperationPhase.Thumbnails && thumbStart == 0) thumbStart = rig.Clock.Milliseconds; }
            Assert.AreEqual("completed", job.Status); Assert.AreEqual("cache_unobserved", job.ThumbnailResult);
            Assert.IsTrue(rig.Clock.Milliseconds - thumbStart >= 9900, "waited the full ten seconds");
        }

        [TestMethod] public void EveryThumbnailActionIsDeclaredEvenWhenTheJobFails()
        {
            rig.Files.Put(rig.Thumb("Probe One"), "human");
            var first = true;
            rig.Port.OnLoad = (f, p) => { rig.Files.Put(rig.Thumb(first ? "kc-apply-0001" : "kc-snap-snap000001"), "gen"); rig.Files.Put(rig.Thumb("Probe One"), "gen"); if (first) { first = false; f.Mode = EditorFake.LoadMode.Normal; } };
            rig.Port.Mode = EditorFake.LoadMode.EmptyAfterLoad;
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("load_failed", job.ReasonCode); Assert.AreEqual("restored", job.Restore.Result);
            Assert.AreEqual("human", rig.Files.Text(rig.Thumb("Probe One")));
            Assert.IsFalse(rig.Files.Exists(rig.Thumb("kc-apply-0001"))); Assert.IsFalse(rig.Files.Exists(rig.Thumb("kc-snap-snap000001")), "the recovery-named thumbnail goes too");
        }

        // ---- the overwrite guard ----

        [TestMethod] public void AfterAnApplyKspTreatsTheCraftAsUnsavedUnderASentinelName()
        {
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("completed", job.Status); Assert.AreEqual("sentinel_and_unsaved_marker", job.OverwriteGuard);
            Assert.AreEqual(EditorObservationService.GuardSentinel, rig.Port.LastSaved); Assert.AreEqual(EditorObservationService.GuardSentinel, rig.Port.SaveState.Sanitized);
            Assert.AreEqual(-1, rig.Port.SaveState.UndoIndexAtLastSave); Assert.AreEqual(true, rig.Port.UnsavedValue);
            var state = rig.Observation.State().Data;
            Assert.AreEqual("kspcontrol_unsaved_sentinel", (string)state["lastSavedName"]); Assert.AreEqual(true, (bool)state["unsaved"]);
        }

        [TestMethod] public void WithoutTheGuardTheApplyStillCompletesAndSaysItIsUnavailable()
        {
            rig = new OperationRig(lease: false);
            rig.Port.Parts = 0; rig.Port.Craft = ""; rig.Port.Ship = new object(); rig.Port.Fsm = "st_podSelect"; rig.Port.Caps.SaveOverwriteGuard = false; rig.Run(20); rig.AcquireLease();
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("completed", job.Status); Assert.AreEqual("unavailable", job.OverwriteGuard); Assert.AreEqual(0, rig.Port.GuardWrites);
        }

        [TestMethod] public void AGuardWriteThatFailsFailsTheApplyAndRestores()
        {
            rig.Port.GuardWritesFail = true; rig.Port.OnLoad = (f, p) => { if (p.Contains("kc-snap-")) f.GuardWritesFail = false; };
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("save_overwrite_guard_unavailable", job.ReasonCode); Assert.AreEqual("write_failed", job.OverwriteGuard);
            Assert.AreEqual("restored", job.Restore.Result);
        }

        // ---- paths and the workspace ----

        [TestMethod] public void EveryWriteStaysInTheWorkspaceAndNothingTouchesTheShipsFolderOrTheControlDirectory()
        {
            rig.Files.Put(rig.Thumb("Probe One"), "human");
            rig.Port.OnLoad = (f, p) => rig.Files.Put(rig.Thumb("kc-apply-0001"), "x");
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("completed", job.Status);
            var workspace = System.IO.Path.Combine(rig.KspRoot, "KspControlData", "TestSave");
            foreach (var path in rig.Files.Writes) Assert.IsTrue(path.StartsWith(workspace, StringComparison.OrdinalIgnoreCase), "write outside the workspace: " + path);
            foreach (var path in rig.Files.Deletes.Concat(rig.Files.Copies))
                Assert.IsFalse(path.Contains(System.IO.Path.DirectorySeparatorChar + "Ships" + System.IO.Path.DirectorySeparatorChar) || path.Contains(System.IO.Path.Combine("KspControlData", "control")), path);
            Assert.AreEqual(0, rig.Files.Data.Keys.Count(k => k.Contains(System.IO.Path.Combine("KspControlData", "control"))));
        }

        [TestMethod] public void ASaveNamedControlNeverGetsAWorkspaceInsideTheControlDirectory()
        {
            rig = new OperationRig(saveFolder: "control");
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("snapshot_unverified", job.ReasonCode); StringAssert.Contains(job.Detail, "path_outside_save");
            Assert.AreEqual(0, rig.Files.Writes.Count); Assert.AreEqual(0, rig.Port.LoadCalls);
            var restore = rig.Service.Handle(rig.RestoreRequest("snap000001", requestId: "restore-0001"));
            Assert.AreEqual("failed", restore.Status);
        }

        // ---- between admission and the load ----

        [TestMethod] public void ALaunchThatAppearsAfterAdmissionStopsTheJobBeforeAnythingChanges()
        {
            Start(); rig.Port.Locks.Add(EditorIdle.LaunchLockId);
            var job = rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("editor_busy", job.ReasonCode); Assert.AreEqual(0, rig.Port.LoadCalls); Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
            Assert.AreEqual(0, rig.Files.Writes.Count);
        }

        [TestMethod] public void ATakeoverBetweenAdmissionAndTheLoadIsNotDispatched()
        {
            var job = Start(); rig.Authority.HumanTakeover();
            rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("authority_revoked", job.ReasonCode);
            Assert.AreEqual(0, rig.Port.LoadCalls, "no load call after ValidateForDispatch failed"); Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
        }

        [TestMethod] public void TheOperationWindowIsReportedWhileTheJobRuns()
        {
            var job = Start();
            while (job.Phase != OperationPhase.Grace) rig.Frame(50);
            Assert.AreEqual("grace", (string)rig.Observation.State().Data["operationWindow"]);
            rig.RunToEnd();
            Assert.AreEqual("none", (string)rig.Observation.State().Data["operationWindow"]);
        }

        [TestMethod] public void APortThatThrowsMidwayStillEndsInATerminalStateWithTheLocksReleased()
        {
            var job = Start();
            while (job.Phase != OperationPhase.Verify) rig.Frame(50);
            rig.Port.CaptureThrows = true; // the native save fails during verify
            rig.RunToEnd();
            Assert.IsTrue(job.Terminal); Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId)); Assert.IsFalse(rig.Runner.Busy);
        }

        private static void ExpectAuthorityError(string code, Action action)
        {
            try { action(); Assert.Fail("expected " + code); }
            catch (InvalidOperationException error) { Assert.AreEqual(code, error.Message); }
        }
    }
}
