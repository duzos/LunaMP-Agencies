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
    internal static class CraftEdit
    {
        public static string SetPartKey(string text, int partIndex, string key, string value)
        {
            var root = Pure.ConfigText.Parse(text);
            var part = root.Children("PART").ElementAt(partIndex);
            for (var i = 0; i < part.Entries.Count; i++)
                if (part.Entries[i].IsValue && part.Entries[i].Key == key) { part.Entries[i] = new Pure.ConfigEntry(key, value); return Pure.ConfigText.Print(root); }
            part.AddValue(key, value);
            return Pure.ConfigText.Print(root);
        }
    }

    [TestClass]
    public class EditorOperationFailureTests
    {
        private OperationRig rig;
        private string originalCraft;
        private Pure.ConfigNode OriginalNode { get { return Pure.ConfigText.Parse(originalCraft); } }

        [TestInitialize] public void Setup() { rig = new OperationRig(); originalCraft = rig.Port.Craft; }

        private OperationJob Start(string graph = OperationRig.TwoStageGraph, string requestId = "apply-0001")
        {
            var response = rig.Apply(graph, requestId);
            Assert.AreEqual("running", response.Status, response.ReasonCode + " " + response.Data);
            return rig.Runner.Current;
        }

        private string OriginalFingerprint() { return SnapshotStore.Fingerprint(OriginalNode, new EditorUi("Probe", "", "Squad/Flags/default")); }
        private string LiveFingerprint() { return SnapshotStore.Fingerprint(Pure.ConfigText.Parse(rig.Port.Craft), rig.Port.ReadUi()); }

        private void AssertUnlockedAndClean(OperationJob job)
        {
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId), "locks are always released");
            Assert.IsFalse(rig.Runner.Busy);
            Assert.AreEqual(OperationPhase.Done, job.Phase);
        }

        // ---- snapshot ----

        [TestMethod] public void ASnapshotThatDoesNotHashBackIsRefusedAndNothingIsLoaded()
        {
            rig.Files.CorruptWrite = p => p.Contains("kc-snap-") && p.EndsWith(".craft");
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("snapshot_unverified", job.ReasonCode);
            Assert.AreEqual(0, rig.Port.LoadCalls, "no load call");
            Assert.IsFalse(job.Dispatched); Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
            Assert.AreEqual(0, rig.Files.Data.Keys.Count(k => k.Contains("recovery")), "the unverified snapshot is removed");
            Assert.AreEqual(originalCraft, rig.Port.Craft, "the editor was never touched");
            AssertUnlockedAndClean(job);
            Assert.IsTrue(rig.Authority.LeaseHeld, "a refusal is not a takeover");
        }

        [TestMethod] public void ASnapshotThatCannotBeWrittenIsRefused()
        {
            rig.Files.FailWrite = p => p.Contains("recovery");
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("snapshot_unverified", job.ReasonCode); Assert.AreEqual(0, rig.Port.LoadCalls);
        }

        [TestMethod] public void ASnapshotWhosePartCountDisagreesWithTheEditorIsRefused()
        {
            rig.Port.Parts = 4; // the editor claims more parts than a native save holds
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("snapshot_unverified", job.ReasonCode); StringAssert.Contains(job.Detail, "part_count_mismatch"); Assert.AreEqual(0, rig.Port.LoadCalls);
        }

        [TestMethod] public void ASnapshotOfPartsTheGameCannotFindIsRefused()
        {
            rig.Port.KnownParts = new HashSet<string> { "mk1pod.v2" };
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("snapshot_unverified", job.ReasonCode); StringAssert.Contains(job.Detail, "parts_not_found"); Assert.AreEqual(0, rig.Port.LoadCalls);
        }

        // ---- a failed load empties the editor, so the snapshot comes back ----

        [DataTestMethod] [DataRow(false)] [DataRow(true)]
        public void ALoadThatEmptiesTheEditorIsRestoredFromTheSnapshot(bool wasUnsaved)
        {
            rig.Port.UnsavedValue = wasUnsaved; rig.Run(5);
            var expected = OriginalFingerprint();
            rig.Port.Mode = EditorFake.LoadMode.EmptyAfterLoad; rig.Port.OnLoad = (f, p) => f.Mode = EditorFake.LoadMode.Normal;
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("load_failed", job.ReasonCode);
            Assert.IsTrue(job.Dispatched, "notDispatched is false once the load call ran");
            Assert.AreEqual(false, (bool)job.ToEnvelope()["notDispatched"]);
            Assert.IsTrue(job.Restore.Attempted); Assert.AreEqual("restored", job.Restore.Result);
            Assert.AreEqual(2, rig.Port.LoadCalls); Assert.AreEqual(job.Snapshot.CraftPath, rig.Port.LoadedPaths[1]);
            Assert.AreEqual(expected, LiveFingerprint(), "identical fingerprint after the restore");
            Assert.AreEqual("Probe", rig.Port.Name, "the UI name is written back");
            Assert.AreEqual("Probe", rig.Port.LastSaved, "vesselNameAtLastSave is written back");
            Assert.AreEqual(wasUnsaved, rig.Port.UnsavedValue, "the unsaved state is what it was");
            Assert.AreEqual("private_field", job.Restore.UnsavedMarkerRestored); Assert.AreEqual("true", job.Restore.SaveFieldsRestored);
            Assert.IsFalse(rig.Files.Exists(rig.Paths.StagingPath("apply-0001").FullPath));
            AssertUnlockedAndClean(job);
        }

        [TestMethod] public void ALoadThatThrowsIsRestoredToo()
        {
            rig.Port.Mode = EditorFake.LoadMode.Throws; rig.Port.OnLoad = null;
            var job = Start();
            // The first load throws before it can change anything; the editor still holds the original and the second load restores it.
            for (var i = 0; i < 60 && rig.Port.LoadCalls < 1; i++) rig.Frame(50);
            rig.Port.Mode = EditorFake.LoadMode.Normal; rig.RunToEnd();
            Assert.AreEqual("load_failed", job.ReasonCode); Assert.AreEqual("InvalidOperationException", job.Detail);
            Assert.AreEqual("restored", job.Restore.Result); Assert.AreEqual(OriginalFingerprint(), LiveFingerprint());
        }

        // ---- verify mismatches restore ----

        [TestMethod] public void AStructureMismatchAfterTheLoadIsRestored()
        {
            var first = true;
            rig.Port.OnLoad = (f, p) => { if (first) { first = false; f.Craft = CraftEdit.SetPartKey(f.Craft, 1, "istg", "9"); } };
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("structure_mismatch_after_load", job.ReasonCode);
            StringAssert.Contains(job.VerifyProblems[0], "istg");
            Assert.AreEqual("restored", job.Restore.Result); Assert.AreEqual(OriginalFingerprint(), LiveFingerprint());
        }

        [TestMethod] public void AGeometryMismatchAfterTheLoadIsRestored()
        {
            var first = true;
            rig.Port.OnLoad = (f, p) => { if (first) { first = false; f.Craft = CraftEdit.SetPartKey(f.Craft, 2, "pos", "0.5,13.1,0"); } };
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("geometry_mismatch_after_load", job.ReasonCode); Assert.AreEqual("restored", job.Restore.Result);
        }

        [TestMethod] public void ASettleThatNeverCompletesTimesOutAndRestores()
        {
            rig.Port.OnLoad = (f, p) => f.PartsStarted = p.Contains("kc-snap-");
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("load_timeout", job.ReasonCode); Assert.AreEqual("restored", job.Restore.Result);
            Assert.AreEqual(OriginalFingerprint(), LiveFingerprint());
        }

        [TestMethod] public void WithNothingToRestoreAFailedApplyLeavesTheEditorEmptyAndSaysSo()
        {
            rig = new OperationRig(lease: false);
            rig.Port.Parts = 0; rig.Port.Craft = ""; rig.Port.Ship = new object(); rig.Port.Fsm = "st_podSelect"; rig.Run(20); rig.AcquireLease();
            rig.Port.Mode = EditorFake.LoadMode.EmptyAfterLoad;
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("load_failed", job.ReasonCode); Assert.IsFalse(job.Restore.Attempted); Assert.AreEqual("previous_editor_empty", job.Restore.Result);
            Assert.AreEqual(1, rig.Port.LoadCalls); Assert.AreEqual(0, rig.Port.Parts);
        }

        [TestMethod] public void ARestoreThatFailsIsReportedWithTheSnapshotPath()
        {
            rig.Port.Mode = EditorFake.LoadMode.EmptyAfterLoad;
            rig.Port.OnLoad = (f, p) => { f.Mode = EditorFake.LoadMode.Normal; rig.Files.Put(rig.Jobs.Get("apply-0001").Snapshot.CraftPath, "tampered"); };
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("restore_failed", job.ReasonCode);
            Assert.IsTrue(job.Restore.Attempted); Assert.AreEqual("snapshot_hash_mismatch", job.Restore.Result);
            var envelope = job.ToEnvelope();
            Assert.AreEqual(job.Snapshot.CraftPath, (string)envelope["snapshotPath"], "the snapshot path is reported so a human can recover by hand");
            Assert.AreEqual(1, rig.Port.LoadCalls, "a tampered snapshot is never loaded");
            AssertUnlockedAndClean(job);
        }

        [TestMethod] public void ARestoreWhoseFingerprintDiffersIsAFailure()
        {
            var calls = 0;
            rig.Port.Mode = EditorFake.LoadMode.EmptyAfterLoad;
            rig.Port.OnLoad = (f, p) => { calls++; f.Mode = EditorFake.LoadMode.Normal; if (calls == 2) f.Craft = CraftEdit.SetPartKey(f.Craft, 0, "istg", "5"); };
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("restore_failed", job.ReasonCode); Assert.AreEqual("fingerprint_mismatch", job.Restore.Result);
        }

        // ---- the generated craft left behind is guarded ----

        private OperationJob StartOnEmptyEditor()
        {
            rig = new OperationRig(lease: false);
            rig.Port.Parts = 0; rig.Port.Craft = ""; rig.Port.Ship = new object(); rig.Port.Fsm = "st_podSelect"; rig.Run(20); rig.AcquireLease();
            return Start();
        }

        private void AssertGuarded(OperationJob job)
        {
            Assert.AreEqual(EditorObservationService.GuardSentinel, rig.Port.LastSaved, "a human Save over a same-named file must prompt");
            Assert.AreEqual(true, rig.Port.UnsavedValue);
            Assert.AreEqual("sentinel_and_unsaved_marker", (string)job.ToEnvelope()["observed"]["overwriteGuard"]);
        }

        [TestMethod] public void AVerifyFailureOnAnEmptyEditorGuardsTheGeneratedCraftLeftInPlace()
        {
            rig = new OperationRig(lease: false);
            rig.Port.Parts = 0; rig.Port.Craft = ""; rig.Port.Ship = new object(); rig.Port.Fsm = "st_podSelect"; rig.Run(20); rig.AcquireLease();
            rig.Port.OnLoad = (f, p) => f.Craft = CraftEdit.SetPartKey(f.Craft, 1, "istg", "9");
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("structure_mismatch_after_load", job.ReasonCode);
            Assert.AreEqual("previous_editor_empty_generated_craft_left_in_place", job.Restore.Result);
            AssertGuarded(job);
            AssertUnlockedAndClean(job);
        }

        [TestMethod] public void ACancelAfterDispatchGuardsTheGeneratedCraft()
        {
            var job = StartAndReach(OperationPhase.Grace);
            rig.Authority.Stop(); rig.RunToEnd();
            Assert.AreEqual("cancelled", job.Status);
            Assert.AreEqual(EditorObservationService.GuardSentinel, rig.Port.LastSaved);
            Assert.AreEqual(true, rig.Port.UnsavedValue);
        }

        [TestMethod] public void ARestoredPreviousCraftIsNotOverwrittenByTheGuard()
        {
            rig.Port.OnLoad = (f, p) => { if (!p.Contains("kc-snap-")) f.Craft = CraftEdit.SetPartKey(f.Craft, 1, "istg", "9"); };
            var second = Start(); rig.RunToEnd();
            Assert.AreEqual("restored", second.Restore.Result);
            Assert.AreEqual("Probe", rig.Port.LastSaved, "the restore wrote the original save name back; the guard does not touch it");
        }

        // ---- locks ----

        [TestMethod] public void ASceneChangeNeverLeaksTheEditorLock()
        {
            var job = StartAndReach(OperationPhase.Settle);
            Assert.IsTrue(rig.Port.EditorLockHeld);
            rig.Port.InEditorValue = false; rig.RunToEnd();
            Assert.IsFalse(rig.Port.EditorLockHeld, "the editor lock is removed by id even though the editor logic is gone");
            AssertUnlockedAndClean(job);
        }

        [TestMethod] public void ALockThatThrowsAfterItWasSetIsStillCleared()
        {
            rig.Port.SetLockThrowsAfterSet = true;
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual(0, rig.Port.LoadCalls);
            Assert.IsTrue(rig.Port.LockClears >= 1);
            Assert.IsFalse(rig.Port.EditorLockHeld); AssertUnlockedAndClean(job);
        }

        // ---- interrupted recovery ----

        [TestMethod] public void StopDuringRecoveryReportsTheRestoreAsInterruptedNotInProgress()
        {
            var first = true;
            rig.Port.OnLoad = (f, p) => { if (first) { first = false; f.Craft = CraftEdit.SetPartKey(f.Craft, 1, "istg", "9"); } };
            var job = StartAndReach(OperationPhase.RestoreSettle);
            rig.Authority.Stop(); rig.RunToEnd();
            Assert.AreEqual("cancelled", job.Status);
            Assert.AreEqual("interrupted_grant_suspended", job.Restore.Result);
            Assert.AreEqual("interrupted_grant_suspended", (string)job.ToEnvelope()["restore"]["result"]);
        }

        [TestMethod] public void ASceneChangeDuringRecoveryReportsTheRestoreAsInterrupted()
        {
            var first = true;
            rig.Port.OnLoad = (f, p) => { if (first) { first = false; f.Craft = CraftEdit.SetPartKey(f.Craft, 1, "istg", "9"); } };
            var job = StartAndReach(OperationPhase.RestoreSettle);
            rig.Port.InEditorValue = false; rig.RunToEnd();
            Assert.AreEqual("indeterminate", job.Status);
            Assert.AreEqual("interrupted_scene_changed", job.Restore.Result);
        }

        // ---- Stop, scene change, human input ----

        private OperationJob StartAndReach(OperationPhase phase)
        {
            var job = Start();
            for (var i = 0; i < 2000 && job.Phase != phase; i++) rig.Frame(50);
            Assert.AreEqual(phase, job.Phase);
            return job;
        }

        [TestMethod] public void StopAfterDispatchCancelsUnlocksAndDoesNotRestore()
        {
            var job = StartAndReach(OperationPhase.Settle);
            rig.Authority.Stop(); rig.RunToEnd();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual("grant_suspended", job.ReasonCode);
            Assert.AreEqual(1, rig.Port.LoadCalls, "no automatic restore");
            Assert.IsFalse(job.Restore.Attempted); Assert.AreEqual("not_attempted_cancelled", job.Restore.Result);
            Assert.IsTrue(job.Dispatched); Assert.IsNotNull(job.Snapshot, "the snapshot stays for a human");
            Assert.IsTrue(rig.Files.Exists(job.Snapshot.CraftPath));
            AssertUnlockedAndClean(job);
            Assert.IsFalse(rig.Authority.LeaseHeld);
        }

        [TestMethod] public void StopDuringGraceCancelsToo()
        {
            var job = StartAndReach(OperationPhase.Grace);
            rig.Authority.Stop(); rig.RunToEnd();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual(1, rig.Port.LoadCalls); AssertUnlockedAndClean(job);
        }

        [TestMethod] public void StopBeforeDispatchIsARefusalNotACancellation()
        {
            var job = StartAndReach(OperationPhase.Staging);
            rig.Authority.Stop(); rig.RunToEnd();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("authority_revoked", job.ReasonCode); Assert.AreEqual(0, rig.Port.LoadCalls);
            Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
        }

        [TestMethod] public void ASceneChangeMidOperationIsIndeterminate()
        {
            var job = StartAndReach(OperationPhase.Settle);
            rig.Port.InEditorValue = false; rig.RunToEnd();
            Assert.AreEqual("indeterminate", job.Status); Assert.AreEqual("scene_changed", job.ReasonCode);
            Assert.IsTrue(rig.Files.Exists(rig.Paths.StagingPath("apply-0001").FullPath), "staging is kept when the job is indeterminate");
            AssertUnlockedAndClean(job);
        }

        [TestMethod] public void HumanInputInsideTheLockIsIndeterminateAndTakesOver()
        {
            var job = StartAndReach(OperationPhase.Settle);
            rig.Tracker.OnEvent(EditorEventKind.PartPicked); rig.RunToEnd();
            Assert.AreEqual("indeterminate", job.Status); Assert.AreEqual("human_input_during_operation", job.ReasonCode);
            Assert.IsFalse(rig.Authority.LeaseHeld, "takeover revoked the lease");
            Assert.IsTrue(rig.Authority.Status().CooldownSeconds > 0);
            Assert.AreEqual(1, rig.Port.LoadCalls, "no restore after human input");
            AssertUnlockedAndClean(job);
        }

        [TestMethod] public void ASelectedPartInsideTheLockIsHumanInputToo()
        {
            var job = StartAndReach(OperationPhase.Settle);
            rig.Port.Selected = true; rig.RunToEnd();
            Assert.AreEqual("indeterminate", job.Status); Assert.AreEqual("human_input_during_operation", job.ReasonCode);
        }

        [TestMethod] public void HumanInputBeforeDispatchIsIndeterminateWithNothingLoaded()
        {
            var job = StartAndReach(OperationPhase.Staging);
            rig.Tracker.OnEvent(EditorEventKind.PartPlaced); rig.RunToEnd();
            Assert.AreEqual("indeterminate", job.Status); Assert.AreEqual(0, rig.Port.LoadCalls); Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
        }

        [TestMethod] public void ADroppedLockIsReassertedWhileTheOperationRuns()
        {
            var job = StartAndReach(OperationPhase.Settle);
            rig.Port.Locks.Remove(EditorIdle.OperationLockId); rig.RunToEnd();
            Assert.AreEqual("completed", job.Status); Assert.IsTrue(job.LocksReasserted >= 1);
        }

        [TestMethod] public void AbortUnlocksAtOnceAndReportsWhatIsKnown()
        {
            var job = StartAndReach(OperationPhase.Settle);
            rig.Runner.Abort();
            Assert.AreEqual("indeterminate", job.Status); Assert.AreEqual("bridge_stopped", job.ReasonCode); AssertUnlockedAndClean(job);
            var early = new OperationRig(); early.Apply(); early.Runner.Abort();
            Assert.AreEqual("failed", early.Runner.Current.Status); Assert.AreEqual("bridge_stopped", early.Runner.Current.ReasonCode);
        }

        // ---- grace ----

        private static void Poke(OperationRig r, int n) { r.Port.Craft = CraftEdit.SetPartKey(r.Port.Craft, 0, "probeValue", n.ToString()); }

        [TestMethod] public void GraceExtendsWhileTheFingerprintChangesThenEndsOnceStable()
        {
            var job = StartAndReach(OperationPhase.Grace);
            var started = rig.Clock.Milliseconds; var next = started + 1000; var n = 0;
            for (var i = 0; i < 4000 && !job.Terminal; i++)
            {
                rig.Frame(100);
                if (rig.Clock.Milliseconds < started + 5000 && rig.Clock.Milliseconds >= next) { Poke(rig, ++n); next += 1000; }
            }
            Assert.AreEqual("completed", job.Status, job.ReasonCode);
            Assert.IsFalse(job.TakeoverDuringGrace); Assert.IsFalse(job.SettleUnstable);
            Assert.IsTrue(job.GraceFingerprintChanges >= 3, "late changes were rebased into the operation, got " + job.GraceFingerprintChanges);
            Assert.IsTrue(rig.Authority.LeaseHeld, "a fingerprint-only change is not a takeover");
            Assert.IsTrue(rig.Clock.Milliseconds - started >= 6500, "grace lasted until two stable seconds after the last change");
        }

        [TestMethod] public void GraceThatNeverSettlesEndsAtTwentySecondsAsSettleUnstable()
        {
            var job = StartAndReach(OperationPhase.Grace);
            var started = rig.Clock.Milliseconds; var n = 0;
            for (var i = 0; i < 4000 && !job.Terminal; i++) { rig.Frame(250); Poke(rig, ++n); }
            Assert.AreEqual("completed", job.Status); Assert.IsTrue(job.SettleUnstable);
            Assert.IsFalse(job.TakeoverDuringGrace); Assert.IsTrue(rig.Authority.LeaseHeld, "not a takeover");
            Assert.IsTrue(job.UnstableKeys.Any(k => k.Contains("probeValue")), string.Join(";", job.UnstableKeys));
            var envelope = job.ToEnvelope(); Assert.AreEqual(true, (bool)envelope["settleUnstable"]); Assert.IsTrue(((JArray)envelope["unstableKeys"]).Count > 0);
        }

        [TestMethod] public void HumanInputDuringGraceCompletesWithTakeoverAndKeepsTheEffect()
        {
            var job = StartAndReach(OperationPhase.Grace);
            rig.Tracker.OnEvent(EditorEventKind.PawShown); rig.RunToEnd();
            Assert.AreEqual("completed", job.Status); Assert.IsTrue(job.TakeoverDuringGrace);
            Assert.IsFalse(rig.Authority.LeaseHeld); Assert.AreEqual(1, rig.Port.LoadCalls);
        }

        [TestMethod] public void AGenerationChangeOrUiEditDuringGraceIsATakeover()
        {
            var a = StartAndReach(OperationPhase.Grace);
            rig.Port.Ship = new object(); rig.RunToEnd();
            Assert.IsTrue(a.TakeoverDuringGrace);
            rig = new OperationRig(); originalCraft = rig.Port.Craft;
            var b = StartAndReach(OperationPhase.Grace);
            rig.Port.Name = "Renamed by hand"; rig.RunToEnd();
            Assert.IsTrue(b.TakeoverDuringGrace);
        }

        [TestMethod] public void TheLocksStayHeldThroughGraceAndAreReleasedAtTheEnd()
        {
            var job = StartAndReach(OperationPhase.Grace);
            for (var i = 0; i < 6; i++) { rig.Frame(100); Assert.IsTrue(rig.Port.Locks.Contains(EditorIdle.OperationLockId)); }
            rig.RunToEnd(); Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
        }

        [TestMethod] public void TheGraceBaselineIsTakenAfterOurOwnRestoreWritesSoTheyAreNotAHumanEdit()
        {
            rig.Port.Mode = EditorFake.LoadMode.EmptyAfterLoad; rig.Port.OnLoad = (f, p) => f.Mode = EditorFake.LoadMode.Normal;
            var job = Start(); rig.RunToEnd();
            Assert.AreEqual("restored", job.Restore.Result); Assert.IsFalse(job.TakeoverDuringGrace, "WriteUi changed the UI fields before the baseline");
            Assert.IsTrue(rig.Port.UiWrites >= 1);
        }

        // ---- the settle waits ----

        [TestMethod] public void SettleWaitsForQuietFramesStartedPartsAndAnIdleFsm()
        {
            var job = StartAndReach(OperationPhase.Settle);
            rig.Port.PartsStarted = false;
            for (var i = 0; i < 40; i++) rig.Frame(50);
            Assert.AreEqual(OperationPhase.Settle, job.Phase, "parts not started");
            rig.Port.PartsStarted = true; rig.Port.Fsm = "st_place";
            for (var i = 0; i < 40; i++) rig.Frame(50);
            Assert.AreEqual(OperationPhase.Settle, job.Phase, "fsm not idle");
            rig.Port.Fsm = "st_idle"; rig.Port.Restarting = true;
            for (var i = 0; i < 40; i++) rig.Frame(50);
            Assert.AreEqual(OperationPhase.Settle, job.Phase, "editor restarting");
            rig.Port.Restarting = false; rig.RunToEnd(); Assert.AreEqual("completed", job.Status);
        }

        [TestMethod] public void SettleKeepsWaitingWhileEventsKeepArriving()
        {
            var job = StartAndReach(OperationPhase.Settle);
            for (var i = 0; i < 30; i++) { rig.Tracker.OnEvent(EditorEventKind.ShipModified); rig.Frame(50); Assert.AreEqual(OperationPhase.Settle, job.Phase); }
            rig.RunToEnd(); Assert.AreEqual("completed", job.Status);
        }

        [TestMethod] public void SettleWaitsBoundedlyForDeltaV()
        {
            rig.Port.DeltaVDone = false;
            var job = Start(); var settleStart = 0L;
            for (var i = 0; i < 4000 && !job.Terminal; i++) { rig.Frame(100); if (job.Phase == OperationPhase.Settle && settleStart == 0) settleStart = rig.Clock.Milliseconds; }
            Assert.AreEqual("completed", job.Status); Assert.IsFalse(job.DeltaVReady);
        }
    }
}
