using System;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    internal sealed class TrackerRig
    {
        public readonly FakeClock Clock = new FakeClock();
        public readonly EditorFake Port;
        public readonly FakeSink Sink = new FakeSink();
        public readonly EditorRevisionTracker Tracker;

        public TrackerRig(bool lease = false)
        {
            Port = new EditorFake(Clock);
            Sink.LeaseHeld = lease;
            Tracker = new EditorRevisionTracker(Port, Sink, () => Clock.Milliseconds, () => Clock.CostMilliseconds);
            // Adopt the baseline: the first sight of a ship is adoption, never a takeover.
            Advance(0); Advance(300); Advance(300);
        }

        public void Advance(long milliseconds) { Clock.Milliseconds += milliseconds; Tracker.Update(); }
        public long Revision => Tracker.EditRevision;
        public void Edit(string craft) { Port.Craft = craft; }
        /// <summary>A new lease: the tracker re-baselines silently on the next frame, so edits made after this call are the interesting ones.</summary>
        public void Relock() { Sink.LeaseHeld = true; Advance(16); }
    }

    [TestClass]
    public class EditorRevisionTrackerTests
    {
        // ---- baseline ----

        [TestMethod] public void FirstSightOfTheShipAdoptsTheBaselineWithoutTakeover()
        {
            var rig = new TrackerRig(lease: true);
            Assert.AreEqual(0, rig.Sink.Takeovers);
            Assert.AreEqual(1, rig.Tracker.Generation);
            Assert.IsTrue(rig.Tracker.Observe(false).FingerprintOk);
        }

        [TestMethod] public void IdleEditorKeepsRevisionFingerprintAndGenerationOverTenCalls()
        {
            var rig = new TrackerRig(lease: true);
            var first = rig.Tracker.Observe(true);
            for (var i = 0; i < 10; i++) { rig.Advance(1000); var again = rig.Tracker.Observe(true); Assert.AreEqual(first.Fingerprint, again.Fingerprint); Assert.AreEqual(first.EditRevision, again.EditRevision); Assert.AreEqual(first.Generation, again.Generation); }
            Assert.AreEqual(0, rig.Sink.Takeovers);
        }

        [TestMethod] public void FingerprintMatchesTheSharedEditorModelDefinition()
        {
            var rig = new TrackerRig();
            var expected = Pure.CraftFingerprint.Compute(Pure.ConfigText.Parse(rig.Port.Craft), Pure.RoundtripVolatileKeys.Default(), "Probe", "", "Squad/Flags/default");
            Assert.AreEqual(expected, rig.Tracker.Observe(true).Fingerprint);
        }

        [TestMethod] public void VolatileRegistryKeysNeverChangeTheFingerprintOrCauseTakeover()
        {
            var rig = new TrackerRig(lease: true);
            var before = rig.Tracker.Observe(true).Fingerprint;
            rig.Edit(EditorFake.CraftText(cryoTime: "99999")); // ModuleCryoTank.LastUpdateTime is registered volatile
            rig.Advance(1000); rig.Advance(1000);
            Assert.AreEqual(before, rig.Tracker.Observe(true).Fingerprint);
            Assert.AreEqual(0, rig.Sink.Takeovers);
        }

        // ---- the R3-§7 table: outside an operation ----

        [TestMethod] public void BareModifiedEventWithUnchangedFingerprintDoesNothing()
        {
            var rig = new TrackerRig(lease: true); var revision = rig.Revision; var captures = rig.Port.Captures;
            rig.Tracker.OnEvent(EditorEventKind.ShipModified); rig.Tracker.OnEvent(EditorEventKind.PartEvent); rig.Tracker.OnEvent(EditorEventKind.VariantApplied);
            rig.Advance(300);
            Assert.AreEqual(revision, rig.Revision);
            Assert.AreEqual(0, rig.Sink.Takeovers);
            Assert.IsTrue(rig.Port.Captures > captures, "the fingerprint was rechecked and found unchanged");
        }

        [TestMethod] public void ChangedFingerprintWithoutLeaseBumpsTheRevisionButNeverTakesOver()
        {
            var rig = new TrackerRig(lease: false); var revision = rig.Revision;
            rig.Edit(EditorFake.CraftText(moduleValue: "11")); rig.Tracker.OnEvent(EditorEventKind.ShipModified);
            rig.Advance(300);
            Assert.AreEqual(revision, rig.Revision, "no lease: settled lazily, not polled");
            rig.Tracker.Observe(false);
            Assert.AreEqual(revision + 1, rig.Revision);
            Assert.AreEqual(0, rig.Sink.Takeovers);
        }

        [TestMethod] public void WithoutLeaseADirtyEditorIsNotSavedEveryQuarterSecond()
        {
            var rig = new TrackerRig(lease: false); var captures = rig.Port.Captures;
            rig.Tracker.OnEvent(EditorEventKind.ShipModified);
            for (var i = 0; i < 8; i++) rig.Advance(250);
            Assert.AreEqual(captures, rig.Port.Captures);
        }

        [TestMethod] public void EventlessEditBeforeTheLeaseIsNotATakeoverWhenTheLeaseAppears()
        {
            var rig = new TrackerRig(lease: false);
            rig.Edit(EditorFake.CraftText(tankStage: "4")); rig.Port.Name = "Edited early"; // no events, no lease
            rig.Advance(2000);
            rig.Sink.LeaseHeld = true; // inline AcquireLease on a worker thread
            rig.Advance(16); rig.Advance(1000); rig.Advance(1000);
            Assert.AreEqual(0, rig.Sink.Takeovers);
            rig.Edit(EditorFake.CraftText(tankStage: "5")); rig.Advance(1000);
            Assert.AreEqual(1, rig.Sink.Takeovers, "an edit after the lease still takes over");
        }

        [TestMethod] public void ChangedFingerprintWithLeaseTakesOver()
        {
            var rig = new TrackerRig(lease: true); var revision = rig.Revision;
            rig.Edit(EditorFake.CraftText(moduleValue: "11")); rig.Tracker.OnEvent(EditorEventKind.ShipModified);
            rig.Advance(300);
            Assert.AreEqual(revision + 1, rig.Revision);
            Assert.AreEqual(1, rig.Sink.Takeovers);
        }

        [TestMethod] public void LeaseHeldPollsTheFingerprintEverySecondEvenWithoutAnyEvent()
        {
            var rig = new TrackerRig(lease: true);
            rig.Edit(EditorFake.CraftText(tankStage: "2")); // a stage drag fires no event
            rig.Advance(300);
            Assert.AreEqual(0, rig.Sink.Takeovers, "not due yet");
            rig.Advance(300);
            Assert.AreEqual(1, rig.Sink.Takeovers);
        }

        [TestMethod] public void WithoutLeaseAnUnannouncedChangeIsFoundOnTheNextForcedObservation()
        {
            var rig = new TrackerRig(lease: false); var revision = rig.Revision;
            rig.Edit(EditorFake.CraftText(tankStage: "2"));
            rig.Advance(5000);
            Assert.AreEqual(revision, rig.Revision, "no events and no lease: nothing polls");
            var observed = rig.Tracker.Observe(true);
            Assert.AreEqual(revision + 1, observed.EditRevision);
            Assert.AreEqual(0, rig.Sink.Takeovers);
        }

        [TestMethod] public void UiNameChangeWithLeaseTakesOverAndWithoutLeaseOnlyBumps()
        {
            var rig = new TrackerRig(lease: true);
            rig.Port.Name = "Renamed"; rig.Advance(16);
            Assert.AreEqual(1, rig.Sink.Takeovers);
            var quiet = new TrackerRig(lease: false); var revision = quiet.Revision;
            quiet.Port.Description = "new text"; quiet.Advance(16);
            Assert.AreEqual(revision + 1, quiet.Revision); Assert.AreEqual(0, quiet.Sink.Takeovers);
        }

        [TestMethod] public void UiChangeIsReflectedInTheFingerprintWithoutASecondBump()
        {
            var rig = new TrackerRig(lease: false);
            var before = rig.Tracker.Observe(true).Fingerprint; var revision = rig.Revision;
            rig.Port.Flag = "Squad/Flags/other"; rig.Advance(16);
            Assert.AreEqual(revision + 1, rig.Revision);
            var after = rig.Tracker.Observe(true);
            Assert.AreNotEqual(before, after.Fingerprint);
            Assert.AreEqual(revision + 1, after.EditRevision, "the forced recapture adopts the already counted UI change");
        }

        [TestMethod] public void GenerationChangeWithLeaseTakesOverAndWithoutLeaseOnlyBumps()
        {
            var rig = new TrackerRig(lease: true); var generation = rig.Tracker.Generation;
            rig.Port.Ship = new object(); rig.Advance(16);
            Assert.AreEqual(generation + 1, rig.Tracker.Generation); Assert.AreEqual(1, rig.Sink.Takeovers);
            var quiet = new TrackerRig(lease: false); var revision = quiet.Revision;
            quiet.Port.Ship = new object(); quiet.Advance(16);
            Assert.AreEqual(revision + 1, quiet.Revision); Assert.AreEqual(0, quiet.Sink.Takeovers);
        }

        [TestMethod] public void PartPickedBumpsAndTakesOverWithLeaseAndOnlyBumpsWithout()
        {
            var rig = new TrackerRig(lease: true); var revision = rig.Revision;
            rig.Tracker.OnEvent(EditorEventKind.PartPicked);
            Assert.AreEqual(revision + 1, rig.Revision); Assert.AreEqual(1, rig.Sink.Takeovers);
            var quiet = new TrackerRig(lease: false); revision = quiet.Revision;
            quiet.Tracker.OnEvent(EditorEventKind.PartPicked);
            Assert.AreEqual(revision + 1, quiet.Revision); Assert.AreEqual(0, quiet.Sink.Takeovers);
        }

        [DataTestMethod]
        [DataRow("PartPlaced")] [DataRow("PartDeleted")] [DataRow("PodPicked")] [DataRow("PodDeleted")] [DataRow("Undo")] [DataRow("Redo")] [DataRow("Load")]
        public void EveryHumanInputEventTakesOverWithLease(string name)
        {
            var kind = (EditorEventKind)Enum.Parse(typeof(EditorEventKind), name);
            Assert.IsTrue(EditorRevisionTracker.IsHumanInput(kind));
            var rig = new TrackerRig(lease: true); var revision = rig.Revision;
            rig.Tracker.OnEvent(kind);
            Assert.AreEqual(revision + 1, rig.Revision); Assert.AreEqual(1, rig.Sink.Takeovers);
        }

        [DataTestMethod]
        [DataRow("ShipModified")] [DataRow("PartEvent")] [DataRow("VariantApplied")] [DataRow("ShipCrewModified")] [DataRow("SetBackup")] [DataRow("Restart")] [DataRow("Started")] [DataRow("RestoreState")] [DataRow("PawShown")]
        public void BareAndGenerationEventsAreNotHumanInput(string name)
        { Assert.IsFalse(EditorRevisionTracker.IsHumanInput((EditorEventKind)Enum.Parse(typeof(EditorEventKind), name))); }

        [TestMethod] public void SelectedPartObservedTakesOverOnceAndRearmsWhenReleased()
        {
            var rig = new TrackerRig(lease: true); var revision = rig.Revision;
            rig.Port.Selected = true; rig.Advance(16); rig.Advance(16); rig.Advance(16);
            Assert.AreEqual(1, rig.Sink.Takeovers); Assert.AreEqual(revision + 1, rig.Revision, "edge triggered, not per frame");
            rig.Port.Selected = false; rig.Advance(16); rig.Relock(); rig.Port.Selected = true; rig.Advance(16);
            Assert.AreEqual(2, rig.Sink.Takeovers);
        }

        [TestMethod] public void RestartStartedAndRestoreWithANewShipAreGenerationChanges()
        {
            var rig = new TrackerRig(lease: false); var generation = rig.Tracker.Generation;
            rig.Port.Ship = new object(); rig.Tracker.OnEvent(EditorEventKind.Restart);
            Assert.AreEqual(generation + 1, rig.Tracker.Generation, "settled in the event, not a frame later");
        }

        [TestMethod] public void PawEventsAreIgnoredOutsideAnOperation()
        {
            var rig = new TrackerRig(lease: true); var revision = rig.Revision;
            rig.Tracker.OnEvent(EditorEventKind.PawShown);
            Assert.AreEqual(revision, rig.Revision); Assert.AreEqual(0, rig.Sink.Takeovers);
        }

        [TestMethod] public void LeavingTheEditorResetsAndReturningAdoptsWithoutTakeover()
        {
            var rig = new TrackerRig(lease: true);
            rig.Port.InEditorValue = false; rig.Advance(16);
            rig.Port.InEditorValue = true; rig.Port.Ship = new object(); rig.Advance(16); rig.Advance(300);
            Assert.AreEqual(0, rig.Sink.Takeovers);
            Assert.IsTrue(rig.Tracker.Observe(false).FingerprintOk);
        }

        [TestMethod] public void EventsRaisedByOurOwnCaptureAreIgnoredAndCannotLoop()
        {
            var rig = new TrackerRig(lease: true);
            rig.Port.DuringCapture = () => { rig.Tracker.OnEvent(EditorEventKind.ShipModified); rig.Tracker.OnEvent(EditorEventKind.PartPicked); };
            var revision = rig.Revision; var captures = rig.Port.Captures;
            rig.Advance(1000); rig.Advance(1000);
            Assert.AreEqual(revision, rig.Revision); Assert.AreEqual(0, rig.Sink.Takeovers);
            Assert.AreEqual(captures + 2, rig.Port.Captures, "one capture per poll: self events did not mark the editor dirty");
            Assert.IsTrue(rig.Tracker.SelfEvents >= 4);
        }

        [TestMethod] public void CaptureFailureReportsFingerprintUnavailableAndRecovers()
        {
            var rig = new TrackerRig(lease: true); var revision = rig.Revision;
            rig.Port.CaptureThrows = true; rig.Advance(1000); rig.Advance(1000);
            Assert.AreEqual(revision, rig.Revision); Assert.AreEqual(0, rig.Sink.Takeovers);
            Assert.AreEqual("InvalidOperationException", rig.Tracker.LastError);
            var failed = rig.Tracker.Observe(true);
            Assert.IsFalse(failed.FingerprintOk, "a failed forced check must not report the old fingerprint");
            Assert.IsNull(rig.Tracker.Token("epoch1", failed));
            var staleToken = rig.Tracker.Token("epoch1", rig.Tracker.Observe(false));
            Assert.IsNull(staleToken);
            rig.Port.CaptureThrows = false; rig.Advance(1000);
            Assert.IsNull(rig.Tracker.LastError);
            Assert.IsTrue(rig.Tracker.Observe(false).FingerprintOk, "dirty was kept, so the next poll recovers");
        }

        // ---- polling mode ----

        [TestMethod] public void CheapFingerprintKeepsFullModeAndCostAboveTwentyMillisecondsSelectsDirtyTracked()
        {
            var cheap = new TrackerRig(lease: true); cheap.Port.CaptureCost = 5;
            cheap.Advance(1000); Assert.AreEqual(FingerprintMode.Full, cheap.Tracker.Mode);
            var heavy = new TrackerRig(lease: true); heavy.Port.CaptureCost = 30;
            heavy.Tracker.Observe(true);
            Assert.AreEqual(FingerprintMode.DirtyTracked, heavy.Tracker.Mode); Assert.AreEqual(30.0, heavy.Tracker.LastPollCostMilliseconds, 1e-9);
        }

        [TestMethod] public void DirtyTrackedModeNoticesStageReorderActionGroupAndRenameWithoutEvents()
        {
            var rig = new TrackerRig(lease: true); rig.Port.CaptureCost = 30; rig.Tracker.Observe(true);
            Assert.AreEqual(FingerprintMode.DirtyTracked, rig.Tracker.Mode);
            var captures = rig.Port.Captures;
            rig.Advance(1000); rig.Advance(1000);
            Assert.AreEqual(captures, rig.Port.Captures, "an unchanged cheap hash does not trigger a full save");
            // stage reorder: the cheap hash changes together with the craft
            rig.Edit(EditorFake.CraftText(tankStage: "3")); rig.Port.Cheap = "c-stage";
            rig.Advance(1000);
            Assert.AreEqual(1, rig.Sink.Takeovers); Assert.IsTrue(rig.Port.Captures > captures);
            // action group change
            rig.Relock(); rig.Edit(EditorFake.CraftText(tankStage: "3", moduleValue: "77")); rig.Port.Cheap = "c-group";
            rig.Advance(1000); Assert.AreEqual(2, rig.Sink.Takeovers);
            // rename: caught per frame by the UI check, no save needed
            rig.Relock(); var before = rig.Port.Captures; rig.Port.Name = "Other"; rig.Advance(16);
            Assert.AreEqual(3, rig.Sink.Takeovers); Assert.AreEqual(before, rig.Port.Captures);
        }

        [TestMethod] public void DirtyTrackedModeStillRefreshesTheFullFingerprintEveryTenSeconds()
        {
            var rig = new TrackerRig(lease: true); rig.Port.CaptureCost = 30; rig.Tracker.Observe(true);
            rig.Edit(EditorFake.CraftText(moduleValue: "123")); // cheap hash unchanged: invisible to the cheap layer
            for (var i = 0; i < 9; i++) rig.Advance(1000);
            Assert.AreEqual(0, rig.Sink.Takeovers);
            rig.Advance(1500);
            Assert.AreEqual(1, rig.Sink.Takeovers);
        }

        // ---- admission token ----

        [TestMethod] public void TokenIsFreshThenStaleAfterAChangeAndInvalidWhenForged()
        {
            var rig = new TrackerRig();
            var token = rig.Tracker.Token("epoch1", rig.Tracker.Observe(true));
            Assert.AreEqual(TokenCheck.Fresh, rig.Tracker.CheckToken(token, "epoch1"));
            Assert.AreEqual(TokenCheck.Stale, rig.Tracker.CheckToken(token, "epoch2"), "another world epoch");
            rig.Edit(EditorFake.CraftText(moduleValue: "12"));
            Assert.AreEqual(TokenCheck.Stale, rig.Tracker.CheckToken(token, "epoch1"));
            Assert.AreEqual(TokenCheck.Invalid, rig.Tracker.CheckToken("not-a-token", "epoch1"));
            Assert.AreEqual(TokenCheck.Invalid, rig.Tracker.CheckToken(null, "epoch1"));
        }

        [TestMethod] public void AnEditAndItsRevertCountedByEventsStillStalesTheToken()
        {
            var rig = new TrackerRig(); var token = rig.Tracker.Token("epoch1", rig.Tracker.Observe(true));
            var original = rig.Port.Craft;
            rig.Edit(EditorFake.CraftText(moduleValue: "12")); rig.Tracker.OnEvent(EditorEventKind.PartPlaced);
            rig.Edit(original); rig.Tracker.OnEvent(EditorEventKind.PartDeleted); // human-input events count the edit and its revert even though the fingerprint ends up equal
            Assert.AreEqual(TokenCheck.Stale, rig.Tracker.CheckToken(token, "epoch1"));
        }

        [TestMethod] public void ForcedCheckFailureAfterABaselineMakesTheTokenUnavailableNotFresh()
        {
            var rig = new TrackerRig(); var token = rig.Tracker.Token("epoch1", rig.Tracker.Observe(true));
            rig.Port.CaptureThrows = true;
            Assert.AreEqual(TokenCheck.Unavailable, rig.Tracker.CheckToken(token, "epoch1"));
        }

        [TestMethod] public void TransientNullShipKeepsIdentityAndAReplacementShipIsAGenerationChange()
        {
            var rig = new TrackerRig(lease: true); var generation = rig.Tracker.Generation;
            var original = rig.Port.Ship;
            rig.Port.ShipNull = true; rig.Advance(16); rig.Advance(16);
            Assert.AreEqual(generation, rig.Tracker.Generation); Assert.AreEqual(0, rig.Sink.Takeovers);
            rig.Port.ShipNull = false; rig.Advance(16);
            Assert.AreEqual(generation, rig.Tracker.Generation, "same ship came back");
            rig.Port.ShipNull = true; rig.Advance(16); rig.Port.Ship = new object(); rig.Port.ShipNull = false; rig.Advance(16);
            Assert.AreEqual(generation + 1, rig.Tracker.Generation); Assert.AreEqual(1, rig.Sink.Takeovers);
        }

        [TestMethod] public void TransientNullShipDoesNotEndAnOperationWindow()
        {
            var rig = new TrackerRig(lease: true); rig.Tracker.BeginOperation();
            rig.Port.ShipNull = true; rig.Advance(16);
            Assert.AreEqual(OperationWindow.Locked, rig.Tracker.Window);
        }

        [TestMethod] public void TokenIsUnavailableWhenTheFingerprintCannotBeComputed()
        {
            var rig = new TrackerRig(); var token = rig.Tracker.Token("epoch1", rig.Tracker.Observe(true));
            rig.Port.Ship = new object(); // new generation: no baseline until a capture succeeds
            rig.Port.CaptureThrows = true;
            Assert.AreEqual(TokenCheck.Unavailable, rig.Tracker.CheckToken(token, "epoch1"));
            Assert.IsNull(rig.Tracker.Token("epoch1", rig.Tracker.Observe(true)));
        }

        // ---- operation windows ----

        [TestMethod] public void EventsInsideDispatchBelongToTheOperationIncludingTheTrailingLoadEvent()
        {
            var rig = new TrackerRig(lease: true);
            rig.Tracker.BeginOperation(); rig.Tracker.BeginDispatch();
            foreach (var kind in new[] { EditorEventKind.Restart, EditorEventKind.SetBackup, EditorEventKind.ShipModified, EditorEventKind.Started, EditorEventKind.Load, EditorEventKind.PartPicked })
                rig.Tracker.OnEvent(kind);
            rig.Port.Ship = new object();
            rig.Tracker.EndDispatch();
            Assert.AreEqual(0, rig.Sink.Takeovers);
            Assert.AreEqual(6, rig.Tracker.Report.ObservedEvents);
            Assert.IsFalse(rig.Tracker.Report.HumanInputDuringOperation);
        }

        [TestMethod] public void GenerationChangeAfterDispatchInsideTheLockIsAttributedToTheOperation()
        {
            var rig = new TrackerRig(lease: true); var generation = rig.Tracker.Generation;
            rig.Tracker.BeginOperation(); rig.Tracker.BeginDispatch(); rig.Port.Ship = new object(); rig.Tracker.EndDispatch();
            rig.Advance(16);
            Assert.AreEqual(generation + 1, rig.Tracker.Generation);
            Assert.AreEqual(0, rig.Sink.Takeovers);
        }

        [TestMethod] public void HumanInputInsideTheLockOutsideDispatchIsAnomalousAndTakesOver()
        {
            var rig = new TrackerRig(lease: true);
            rig.Tracker.BeginOperation();
            rig.Tracker.OnEvent(EditorEventKind.ShipModified);
            Assert.AreEqual(0, rig.Sink.Takeovers); Assert.IsFalse(rig.Tracker.Report.HumanInputDuringOperation);
            rig.Tracker.OnEvent(EditorEventKind.PartPicked);
            Assert.AreEqual(1, rig.Sink.Takeovers); Assert.IsTrue(rig.Tracker.Report.HumanInputDuringOperation);
        }

        [TestMethod] public void SelectedPartAndPawInsideTheLockAreHumanInput()
        {
            var rig = new TrackerRig(lease: true); rig.Tracker.BeginOperation();
            rig.Port.Selected = true; rig.Advance(16);
            Assert.IsTrue(rig.Tracker.Report.HumanInputDuringOperation); Assert.AreEqual(1, rig.Sink.Takeovers);
            var paw = new TrackerRig(lease: true); paw.Tracker.BeginOperation(); paw.Tracker.OnEvent(EditorEventKind.PawShown);
            Assert.IsTrue(paw.Tracker.Report.HumanInputDuringOperation); Assert.AreEqual(1, paw.Sink.Takeovers);
        }

        [TestMethod] public void NoFingerprintWorkHappensWhileTheOperationLockIsHeldBeforeGrace()
        {
            var rig = new TrackerRig(lease: true); rig.Tracker.BeginOperation(); var captures = rig.Port.Captures;
            rig.Edit(EditorFake.CraftText(moduleValue: "55")); rig.Advance(2000); rig.Advance(2000);
            Assert.AreEqual(captures, rig.Port.Captures); Assert.AreEqual(0, rig.Sink.Takeovers);
        }

        [TestMethod] public void BareFingerprintChangeDuringGraceIsRebasedNotTakeover()
        {
            var rig = new TrackerRig(lease: true);
            rig.Tracker.BeginOperation(); rig.Tracker.BeginGrace(); var revision = rig.Revision;
            rig.Edit(EditorFake.CraftText(moduleValue: "late-init")); // late module initialisation
            rig.Advance(600);
            Assert.AreEqual(0, rig.Sink.Takeovers); Assert.IsFalse(rig.Tracker.Report.TakeoverDuringGrace);
            Assert.AreEqual(1, rig.Tracker.Report.GraceFingerprintChanges); Assert.AreEqual(revision + 1, rig.Revision);
            Assert.AreEqual(rig.Clock.Milliseconds, rig.Tracker.Report.LastGraceChangeAt);
        }

        [TestMethod] public void GraceExtendsWhileChangingThenIsStable()
        {
            var rig = new TrackerRig(lease: true); rig.Tracker.BeginOperation(); rig.Tracker.BeginGrace();
            for (var i = 0; i < 10; i++) { rig.Edit(EditorFake.CraftText(moduleValue: "step" + i)); rig.Advance(500); }
            Assert.AreEqual(10, rig.Tracker.Report.GraceFingerprintChanges); Assert.AreEqual(0, rig.Sink.Takeovers);
            var last = rig.Tracker.Report.LastGraceChangeAt;
            for (var i = 0; i < 4; i++) rig.Advance(500);
            Assert.AreEqual(last, rig.Tracker.Report.LastGraceChangeAt, "four equal polls: the runner may end grace");
            Assert.AreEqual(10, rig.Tracker.Report.GraceFingerprintChanges);
        }

        [TestMethod] public void UiGenerationPawAndHumanInputDuringGraceAreTakeover()
        {
            foreach (var scenario in new Action<TrackerRig>[]
            {
                r => { r.Port.Name = "Renamed"; r.Advance(16); },
                r => { r.Port.Ship = new object(); r.Advance(16); },
                r => r.Tracker.OnEvent(EditorEventKind.PawShown),
                r => r.Tracker.OnEvent(EditorEventKind.Undo),
                r => { r.Port.Selected = true; r.Advance(16); }
            })
            {
                var rig = new TrackerRig(lease: true); rig.Tracker.BeginOperation(); rig.Tracker.BeginGrace();
                scenario(rig);
                Assert.IsTrue(rig.Tracker.Report.TakeoverDuringGrace); Assert.AreEqual(1, rig.Sink.Takeovers);
            }
        }

        [TestMethod] public void EndOperationAdoptsTheFinalStateAsTheBaselineAndReturnsTheRevision()
        {
            var rig = new TrackerRig(lease: true);
            rig.Tracker.BeginOperation(); rig.Tracker.BeginDispatch();
            rig.Edit(EditorFake.CraftText(moduleValue: "loaded")); rig.Port.Ship = new object(); rig.Port.Name = "Generated";
            rig.Tracker.EndDispatch(); rig.Tracker.BeginGrace();
            var revision = rig.Tracker.EndOperation();
            Assert.AreEqual(revision, rig.Revision); Assert.AreEqual(OperationWindow.None, rig.Tracker.Window);
            rig.Advance(1000); rig.Advance(1000);
            Assert.AreEqual(revision, rig.Revision, "the end state is the new baseline");
            Assert.AreEqual(0, rig.Sink.Takeovers);
            rig.Edit(EditorFake.CraftText(moduleValue: "human")); rig.Advance(1000);
            Assert.AreEqual(1, rig.Sink.Takeovers);
        }

        [TestMethod] public void SceneChangeMidOperationResetsTheWindow()
        {
            var rig = new TrackerRig(lease: true); rig.Tracker.BeginOperation(); rig.Tracker.BeginDispatch();
            rig.Port.InEditorValue = false; rig.Advance(16);
            Assert.AreEqual(OperationWindow.None, rig.Tracker.Window);
        }

        // ---- the real authority ----

        [TestMethod] public void HumanEditRevokesTheRealLeaseAndStartsTheCooldown()
        {
            long now = 1000; var utc = AuthorityHelpers.Utc0;
            var authority = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, new MemorySuspensionStore(), () => utc);
            authority.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind(), AuthorityHelpers.ValidStatus());
            authority.ProvisionGrant(AuthorityHelpers.Grant());
            var lease = authority.AcquireLease(30000, "test purpose");
            var clock = new FakeClock(); var port = new EditorFake(clock);
            var tracker = new EditorRevisionTracker(port, new AuthorityTakeoverSink(authority), () => now, () => clock.CostMilliseconds);
            tracker.Update(); now += 300; tracker.Update(); now += 300; tracker.Update();
            Assert.IsTrue(authority.LeaseHeld); Assert.IsTrue(authority.Heartbeat(lease));
            tracker.OnEvent(EditorEventKind.PartPicked);
            Assert.IsFalse(authority.LeaseHeld); Assert.IsFalse(authority.Heartbeat(lease));
            var refusal = Assert.ThrowsException<InvalidOperationException>(() => authority.AcquireLease(30000, "again"));
            Assert.AreEqual(ControlReasons.HumanActivityCooldown, refusal.Message);
        }

        [TestMethod] public void PumpRunsTheTrackerBeforePublishingTheContextSoTheLeaseContextCarriesTheRevision()
        {
            long now = 1000; var utc = AuthorityHelpers.Utc0;
            var authority = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, new MemorySuspensionStore(), () => utc);
            var clock = new FakeClock(); var port = new EditorFake(clock);
            var tracker = new EditorRevisionTracker(port, new AuthorityTakeoverSink(authority), () => now, () => clock.CostMilliseconds);
            var source = new TrackerSource(tracker);
            var pump = new ControlPump(authority, null, source, tracker);
            pump.Update(); now += 300; pump.Update();
            authority.ProvisionGrant(AuthorityHelpers.Grant());
            var lease = authority.AcquireLease(30000, "revision wiring");
            var effects = new[] { new ClassifiedEffect("editor.replace_craft", "editor:VAB", 1) };
            var ticket = authority.Admit(lease, tracker.EditRevision, effects);
            authority.ValidateForDispatch(ticket, source.CurrentContext(), effects);
            // A human edit: the tracker bumps the revision and revokes the lease within the same pump step.
            tracker.OnEvent(EditorEventKind.PartPlaced);
            now += 100; pump.Update();
            Assert.AreEqual(tracker.EditRevision, authority.PublishedRevision);
            Assert.IsFalse(authority.LeaseHeld);
        }

        private sealed class TrackerSource : IEditorContextSource
        {
            private readonly EditorRevisionTracker tracker;
            public TrackerSource(EditorRevisionTracker tracker) { this.tracker = tracker; }
            public LeaseContext CurrentContext() { return new LeaseContext("epoch1", "editor:VAB", tracker.EditRevision, true); }
            public GrantBinding CurrentBinding() { return AuthorityHelpers.Bind(); }
        }
    }
}
