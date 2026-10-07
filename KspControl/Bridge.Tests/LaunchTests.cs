using System;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class LaunchAdmissionTests
    {
        private LaunchRig rig;

        [TestInitialize] public void Setup() { rig = new LaunchRig(); }

        private void Refused(BridgeResponse response, string reason)
        {
            Assert.AreEqual("failed", response.Status, response.Data.ToString());
            Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.AreEqual(true, (bool)response.Data["notDispatched"], "an admission refusal is a guaranteed no-effect failure");
            Assert.IsFalse(rig.Runner.Busy, "nothing was started");
            Assert.AreEqual(0, rig.Port.BeginCalls, "the editor launch routine never ran");
            Assert.IsTrue(rig.Authority.LeaseHeld, "a refused request leaves the lease alone");
        }

        [TestMethod] public void ALaunchWithoutALeaseIsLeaseRequiredAndAMalformedOneLeaseInvalid()
        {
            var none = rig.Request(); none.LeaseId = null;
            Refused(rig.Service.Handle(none), "lease_required");
            var bad = rig.Request("launch-0002"); bad.LeaseId = "short";
            Refused(rig.Service.Handle(bad), "lease_invalid");
            var unknown = rig.Request("launch-0003"); unknown.LeaseId = new string('a', 32);
            Refused(rig.Service.Handle(unknown), "lease_invalid");
        }

        [TestMethod] public void AGrantWithoutTheLaunchFamilyDeniesIt()
        {
            rig = new LaunchRig(new[] { OperationEffects.ReplaceCraft });
            Refused(rig.Launch(), "grant_operation_denied");
        }

        [DataTestMethod]
        [DataRow("short", "LaunchPad", 5000L)] [DataRow("launch-0001", "", 5000L)] [DataRow("launch-0001", "Launch\u0001Pad", 5000L)]
        [DataRow("launch-0001", "LaunchPad", -1L)] [DataRow("launch-0001", "LaunchPad", 1000000001L)]
        public void OutOfBoundsArgumentsAreInvalid(string requestId, string site, long max)
        {
            var response = rig.Service.Handle(rig.Request(requestId, site, max));
            Refused(response, "invalid_argument");
        }

        [TestMethod] public void TheSpendCeilingMustBeAnInteger()
        {
            var request = rig.Request(); request.Arguments["maxSpendFunds"] = "5000";
            Refused(rig.Service.Handle(request), "invalid_argument");
            request = rig.Request("launch-0002"); request.Arguments["maxSpendFunds"] = 12.5;
            Refused(rig.Service.Handle(request), "invalid_argument");
            request = rig.Request("launch-0003"); request.Arguments.Remove("maxSpendFunds");
            Refused(rig.Service.Handle(request), "invalid_argument");
        }

        [TestMethod] public void AMissingFacadeRefuses()
        {
            rig.Port.Facade = false;
            Refused(rig.Launch(), "facade_unavailable");
        }

        [TestMethod] public void ALaunchTheAgencyMayNotMakeIsRefusedWithTheReason()
        {
            rig.Port.AllowedValue = false; rig.Port.AllowedReason = "Locked parts require the complete purchased design.";
            var response = rig.Launch();
            Refused(response, "launch_not_allowed");
            StringAssert.Contains((string)response.Data["detail"], "Locked parts");
        }

        [TestMethod] public void APendingReservationRefuses()
        {
            rig.Port.Pending = true;
            Refused(rig.Launch(), "launch_pending");
        }

        [TestMethod] public void AnUnavailableQuoteRefuses()
        {
            rig.Port.QuoteValue = new LaunchQuote { Success = false, Reason = "economy_not_ready" };
            var response = rig.Launch();
            Refused(response, "quote_unavailable");
            StringAssert.Contains((string)response.Data["detail"], "economy_not_ready");
            rig.Port.QuoteValue = new LaunchQuote { Success = true, LaunchCost = double.NaN };
            Refused(rig.Launch("launch-0002"), "quote_unavailable");
        }

        [TestMethod] public void AQuoteAboveTheSpendCeilingIsRefusedAndShown()
        {
            var response = rig.Launch(maxSpend: 999);
            Refused(response, "spend_exceeds_max");
            Assert.AreEqual(1000.0, (double)response.Data["quote"]["launchCost"]);
            Assert.AreEqual(250.0, (double)response.Data["quote"]["toolingCost"]);
            Assert.IsFalse((bool)response.Data["quote"]["alreadyTooled"]);
            Assert.AreEqual("fp-1", (string)response.Data["quote"]["fingerprint"]);
        }

        [TestMethod] public void AQuoteExactlyAtTheCeilingIsAdmitted()
        {
            var response = rig.Launch(maxSpend: 1000);
            Assert.AreEqual("running", response.Status, response.Data.ToString());
            Assert.AreEqual("begin_launch", (string)response.Data["phase"]);
            Assert.IsTrue(rig.Runner.Busy);
        }

        [TestMethod] public void ABalanceBelowTheLaunchCostRefuses()
        {
            rig.Port.FundsValue = 999.5;
            Refused(rig.Launch(), "insufficient_funds");
        }

        [TestMethod] public void AnUnconfirmedBalanceRefusesAPaidLaunchButNotAFreeOne()
        {
            rig.Port.FundsValue = null;
            Refused(rig.Launch(), "economy_not_ready");
            rig.Port.QuoteValue = new LaunchQuote { Success = true, LaunchCost = 0, AlreadyTooled = true, Fingerprint = "fp" };
            Assert.AreEqual("running", rig.Launch("launch-0002").Status);
        }

        [TestMethod] public void ADuplicateRequestAnswersFromTheSameJobAndANewIdWhileRunningIsBusy()
        {
            var first = rig.Launch(); Assert.AreEqual("running", first.Status);
            rig.Frame();
            var again = rig.Service.Handle(rig.Request());
            Assert.AreEqual("launch-0001", (string)again.Data["requestId"]);
            Assert.AreEqual(1, rig.Port.BeginCalls, "the same request never launches twice");
            var other = rig.Service.Handle(rig.Request("launch-0002"));
            Assert.AreEqual("failed", other.Status); Assert.AreEqual("editor_busy", other.ReasonCode);
            var conflict = rig.Service.Handle(rig.Request(maxSpend: 4000));
            Assert.AreEqual("request_id_conflict", conflict.ReasonCode);
        }

        [TestMethod] public void WhileALaunchRunsAnEditorMutationIsRefusedAsBusy()
        {
            var apply = rig.Rig.ApplyRequest();
            rig.Launch();
            var refused = rig.Rig.Service.Handle(apply);
            Assert.AreEqual("editor_busy", refused.ReasonCode, refused.Data.ToString());
            Assert.IsTrue(rig.Rig.Service.OperationRunning);
        }

        [TestMethod] public void StatusIsAnsweredFromTheLaunchRegistry()
        {
            Assert.IsFalse(rig.Service.Owns(rig.StatusRequest("launch-0001")));
            rig.Launch();
            Assert.IsTrue(rig.Service.Owns(rig.StatusRequest("launch-0001")));
            var status = rig.Service.Status(rig.StatusRequest("launch-0001"));
            Assert.AreEqual("running", status.Status); Assert.AreEqual("launch", (string)status.Data["operation"]);
            Assert.IsFalse(rig.Service.Owns(rig.StatusRequest("launch-9999")));
        }
    }

    [TestClass]
    public class LaunchRunnerTests
    {
        private LaunchRig rig;

        [TestInitialize] public void Setup() { rig = new LaunchRig(); rig.ReserveOnBegin(); }

        private string StartAndBegin(long max = 5000)
        {
            var response = rig.Launch(maxSpend: max);
            Assert.AreEqual("running", response.Status, response.Data.ToString());
            rig.Frame();
            return "launch-0001";
        }

        // ---------------------------------------------------------------- success

        [TestMethod] public void TheEditorLaunchRoutineRunsOnceWithTheSiteAndTheJobWaitsForTheFlightScene()
        {
            StartAndBegin();
            Assert.AreEqual(1, rig.Port.BeginCalls); Assert.AreEqual("LaunchPad", rig.Port.BeginSite);
            Assert.AreEqual("await_flight", JobEnvelope()["phase"].ToString()); Assert.IsTrue((bool)JobEnvelope()["dispatched"]);
            rig.Run(20);
            Assert.AreEqual(JobStatuses.Running, rig.Runner.Current.Status, "a returned launch call is not success");
            Assert.AreEqual(1, rig.Port.BeginCalls);
        }

        private JObject JobEnvelope() { return rig.Runner.Current.ToEnvelope(); }

        [TestMethod] public void SuccessNeedsTheFlightSceneAndANewOwnedPadVesselAndNoPendingReservation()
        {
            StartAndBegin();
            rig.EnterFlight();
            rig.Run(5);
            Assert.AreEqual(JobStatuses.Running, rig.Runner.Current.Status, "the reservation is still pending");
            rig.Port.Pending = false;
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Completed, job.Status, job.Detail);
            Assert.AreEqual("11111111-2222-3333-4444-555555555555", job.VesselId); Assert.AreEqual("Probe One", job.VesselName);
            var envelope = job.ToEnvelope();
            Assert.AreEqual(1000.0, (double)envelope["charge"]); Assert.AreEqual("tooling_result", (string)envelope["chargeSource"]);
            Assert.AreEqual(50000.0, (double)envelope["fundsBefore"]); Assert.AreEqual("epoch2", (string)envelope["epoch"]);
            Assert.AreEqual(1000.0, (double)envelope["quote"]["launchCost"]);
        }

        [TestMethod] public void AVesselThatIsNotOnThePadOrNotOursIsNotALaunch()
        {
            StartAndBegin();
            rig.EnterFlight(prelaunch: false); rig.Port.Pending = false;
            rig.Run(30);
            Assert.AreEqual(JobStatuses.Running, rig.Runner.Current.Status, "an old vessel is not the new one");
            rig.EnterFlight(); rig.Port.Owned = false;
            rig.Run(30);
            Assert.AreEqual(JobStatuses.Running, rig.Runner.Current.Status, "a vessel without our ownership record is not ours");
            rig.Port.Owned = true;
            Assert.AreEqual(JobStatuses.Completed, rig.RunToEnd().Status);
        }

        [TestMethod] public void WithoutFlightOperationsInTheGrantTheLeaseIsReleasedOnSuccess()
        {
            StartAndBegin(); rig.EnterFlight(); rig.Port.Pending = false;
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Completed, job.Status);
            Assert.IsTrue(job.LeaseReleased); Assert.IsFalse(job.LeaseContinues); Assert.IsNull(job.LeaseEntity);
            Assert.IsNull(rig.Authority.DescribeLease(rig.Lease), "the lease ended with the editor");
            Assert.IsFalse(rig.Authority.LeaseHeld);
        }

        [TestMethod] public void WithFlightOperationsInTheGrantTheLeaseMovesToTheVessel()
        {
            rig = new LaunchRig(new[] { OperationEffects.Launch, "flight.control" }); rig.ReserveOnBegin();
            StartAndBegin(); rig.EnterFlight(); rig.Port.Pending = false;
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Completed, job.Status, job.Detail);
            Assert.IsTrue(job.LeaseContinues); Assert.IsFalse(job.LeaseReleased);
            Assert.AreEqual("vessel:11111111-2222-3333-4444-555555555555", job.LeaseEntity);
            rig.Rig.Context.Entity = "vessel:11111111-2222-3333-4444-555555555555"; // what KspContextSource publishes once the vessel is active
            rig.Run(10);
            var lease = rig.Authority.DescribeLease(rig.Lease);
            Assert.IsNotNull(lease, "the lease survived the scene change");
            Assert.AreEqual("vessel:11111111-2222-3333-4444-555555555555", lease.Entity); Assert.AreEqual("epoch2", lease.Epoch);
        }

        [TestMethod] public void WithoutANewServerReservationTheChargeFallsBackToTheQuote()
        {
            rig.Port.OnBegin = () => { }; // tooling disabled: no reservation, the launch just proceeds
            StartAndBegin(); rig.EnterFlight();
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Completed, job.Status);
            Assert.AreEqual(1000.0, job.Charge.Value); Assert.AreEqual("quote_fallback", job.ChargeSource);
        }

        [TestMethod] public void ASceneChangeDoesNotRevokeTheLeaseWhileTheLaunchRuns()
        {
            StartAndBegin();
            rig.EnterFlight();
            rig.Run(5);
            Assert.IsTrue(rig.Authority.LeaseHeld, "the lease follows the scene while the launch transition is armed");
            Assert.AreEqual("scene:FLIGHT", rig.Authority.DescribeLease(rig.Lease).Entity);
        }

        // ---------------------------------------------------------------- aborts and refunds

        [TestMethod] public void AHumanTakeoverBeforeTheFlightSceneCancelsThroughTheNormalFlowAndEndsAfterTheRefund()
        {
            StartAndBegin();
            rig.Port.OnCancel = () => rig.Port.Pending = false; // the tooling cancel flow sends CancelLaunch
            rig.Authority.HumanTakeover();
            rig.Run(3);
            Assert.AreEqual(1, rig.Port.CancelCalls);
            Assert.AreEqual(JobStatuses.Running, rig.Runner.Current.Status, "the reservation is released only after the refund is confirmed");
            Assert.AreEqual("cancelling", rig.Runner.Current.ToEnvelope()["phase"].ToString());
            rig.Run(10);
            Assert.AreEqual(JobStatuses.Running, rig.Runner.Current.Status);
            rig.Port.FundsValue = 50000; // the refund shows in the confirmed balance
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Cancelled, job.Status); Assert.AreEqual("authority_revoked", job.ReasonCode);
            Assert.AreEqual(true, job.RefundConfirmed); Assert.IsTrue(job.ToEnvelope()["dispatched"].Value<bool>());
        }

        [TestMethod] public void StopCancelsTheReservationAndReportsTheSuspendedGrant()
        {
            StartAndBegin();
            rig.Port.OnCancel = () => { rig.Port.Pending = false; rig.Port.FundsValue = 50000; };
            rig.Authority.Stop();
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Cancelled, job.Status); Assert.AreEqual("grant_suspended", job.ReasonCode); Assert.AreEqual(1, rig.Port.CancelCalls);
        }

        [TestMethod] public void ARefundThatNeverShowsIsIndeterminateAndKeepsItsHold()
        {
            StartAndBegin();
            rig.Port.OnCancel = () => rig.Port.Pending = false; // the funds stay down
            rig.Authority.HumanTakeover();
            rig.Run(2);
            var job = rig.RunToEnd(milliseconds: 1000, maxFrames: 60);
            Assert.AreEqual(JobStatuses.Indeterminate, job.Status); Assert.AreEqual("refund_unconfirmed", job.ReasonCode);
        }

        [TestMethod] public void ALaunchThatCannotBeCancelledBecauseTheFlightSceneIsLoadingIsIndeterminate()
        {
            StartAndBegin();
            rig.Port.SceneName = "LOADING";
            rig.Authority.HumanTakeover();
            rig.Run(2);
            var job = rig.Runner.Current;
            Assert.AreEqual(JobStatuses.Indeterminate, job.Status); Assert.AreEqual("authority_revoked", job.ReasonCode);
            Assert.AreEqual(0, rig.Port.CancelCalls, "a loading flight scene cannot be cancelled");
        }

        [TestMethod] public void ACancelRefusedByTheToolingFlowIsIndeterminate()
        {
            StartAndBegin();
            rig.Port.CancelWorks = false;
            rig.Authority.HumanTakeover();
            rig.Run(2);
            Assert.AreEqual(JobStatuses.Indeterminate, rig.Runner.Current.Status);
        }

        [TestMethod] public void ADisconnectIsIndeterminate()
        {
            StartAndBegin();
            rig.Port.IsConnected = false;
            rig.Run(2);
            var job = rig.Runner.Current;
            Assert.AreEqual(JobStatuses.Indeterminate, job.Status); Assert.AreEqual("disconnected", job.ReasonCode);
        }

        [TestMethod] public void ADisconnectWhileCancellingIsIndeterminateToo()
        {
            StartAndBegin();
            rig.Port.OnCancel = () => rig.Port.Pending = false;
            rig.Authority.HumanTakeover(); rig.Run(2);
            rig.Port.IsConnected = false; rig.Run(2);
            Assert.AreEqual("disconnected", rig.Runner.Current.ReasonCode);
        }

        [TestMethod] public void AReservationTheServerRefusesEndsFailedAfterTheBalanceIsConfirmed()
        {
            StartAndBegin();
            rig.Port.Pending = false; rig.Port.Status = "Insufficient funds."; rig.Port.FundsValue = 50000;
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Failed, job.Status); Assert.AreEqual("launch_rejected", job.ReasonCode);
            StringAssert.Contains(job.Detail, "Insufficient funds"); Assert.AreEqual(true, job.RefundConfirmed);
            Assert.AreEqual(0, rig.Port.CancelCalls);
            Assert.IsTrue(rig.Authority.LeaseHeld, "the editor never closed, so the lease stays");
        }

        [TestMethod] public void ALaunchThatNeverStartsFailsAfterThePromptWindow()
        {
            rig.Port.OnBegin = () => { }; // a stock pre-flight prompt is waiting for a human click: no reservation, no scene change
            StartAndBegin();
            var job = rig.RunToEnd(milliseconds: 1000, maxFrames: 60);
            Assert.AreEqual(JobStatuses.Failed, job.Status); Assert.AreEqual("launch_not_started", job.ReasonCode);
            Assert.AreEqual(1, rig.Port.CloseCalls, "the prompt is closed before the job calls it a no-effect failure"); Assert.AreEqual(true, job.RefundConfirmed);
        }

        [TestMethod] public void APromptThatCannotBeConfirmedClosedKeepsTheReservation()
        {
            rig.Port.OnBegin = () => { }; rig.Port.PromptCloses = false;
            StartAndBegin();
            var job = rig.RunToEnd(milliseconds: 1000, maxFrames: 60);
            Assert.AreEqual(JobStatuses.Indeterminate, job.Status); Assert.AreEqual("launch_not_started", job.ReasonCode);
        }

        [TestMethod] public void AFacadeFailureAfterTheRoutineRanWithAReservationPendingIsFollowedNotReleased()
        {
            rig.Port.BeginResult = false; rig.Port.BeginReason = "launch_exception:NullReferenceException"; rig.Port.ActsBeforeFailing = true; // the reservation was requested before the routine threw
            rig.Launch();
            rig.Frame();
            Assert.AreEqual(JobStatuses.Running, rig.Runner.Current.Status); Assert.IsTrue(rig.Runner.Current.Dispatched);
            Assert.AreEqual("await_flight", rig.Runner.Current.ToEnvelope()["phase"].ToString());
        }

        [TestMethod] public void AFacadeExceptionWithNoReservationIsIndeterminateNotNoEffect()
        {
            rig.Port.BeginResult = false; rig.Port.BeginReason = "launch_exception:NullReferenceException";
            rig.Launch();
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Indeterminate, job.Status); Assert.IsTrue(job.Dispatched);
        }

        [TestMethod] public void ALockedLaunchButtonIsALaunchLockedNoEffectFailure()
        {
            rig.Port.BeginResult = false; rig.Port.BeginReason = "launch_locked";
            rig.Launch();
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Failed, job.Status); Assert.AreEqual("launch_locked", job.ReasonCode); Assert.IsFalse(job.Dispatched);
        }

        [TestMethod] public void AHungReservationTimesOutAndIsCancelled()
        {
            StartAndBegin();
            rig.Port.OnCancel = () => { rig.Port.Pending = false; rig.Port.FundsValue = 50000; };
            var job = rig.RunToEnd(milliseconds: 1000, maxFrames: 300);
            Assert.AreEqual(JobStatuses.Cancelled, job.Status); Assert.AreEqual("launch_timeout", job.ReasonCode);
            Assert.AreEqual(1, rig.Port.CancelCalls);
        }

        [TestMethod] public void AFlightSceneThatNeverYieldsOurVesselTimesOutIndeterminate()
        {
            StartAndBegin();
            rig.EnterFlight(); rig.Port.Pending = true; // registration never completes
            var job = rig.RunToEnd(milliseconds: 1000, maxFrames: 300);
            Assert.AreEqual(JobStatuses.Indeterminate, job.Status); Assert.AreEqual("launch_timeout", job.ReasonCode);
        }

        [TestMethod] public void LeavingTheEditorForSomewhereElseIsIndeterminate()
        {
            StartAndBegin();
            rig.Port.SceneName = "SPACECENTER";
            rig.Run(2);
            Assert.AreEqual(JobStatuses.Indeterminate, rig.Runner.Current.Status); Assert.AreEqual("scene_changed", rig.Runner.Current.ReasonCode);
        }

        // ---------------------------------------------------------------- begin failures

        [TestMethod] public void ARefusalFromTheFacadeIsANoEffectFailure()
        {
            rig.Port.BeginResult = false; rig.Port.BeginReason = "launch_site_invalid";
            rig.Launch();
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Failed, job.Status); Assert.AreEqual("launch_site_invalid", job.ReasonCode);
            var envelope = job.ToEnvelope();
            Assert.IsTrue((bool)envelope["notDispatched"]); Assert.IsFalse((bool)envelope["dispatched"]);
            Assert.IsTrue(rig.Authority.LeaseHeld);
            Assert.IsFalse(rig.Runner.Busy);
        }

        [TestMethod] public void AnUnknownFacadeRefusalIsLaunchRefused()
        {
            rig.Port.BeginResult = false; rig.Port.BeginReason = "launch_hook_unavailable";
            rig.Launch();
            Assert.AreEqual("launch_refused", rig.RunToEnd().ReasonCode);
        }

        [TestMethod] public void ARoutineThatThrowsMayHaveActedAndIsIndeterminate()
        {
            rig.Port.BeginThrows = true;
            rig.Launch();
            var job = rig.RunToEnd();
            Assert.AreEqual(JobStatuses.Indeterminate, job.Status); Assert.IsTrue(job.Dispatched);
        }

        [TestMethod] public void ARevokedLeaseBetweenAdmissionAndTheFirstStepNeverLaunches()
        {
            rig.Launch();
            rig.Authority.HumanTakeover();
            var job = rig.RunToEnd();
            Assert.AreEqual(0, rig.Port.BeginCalls);
            Assert.AreEqual(JobStatuses.Failed, job.Status); Assert.AreEqual("authority_revoked", job.ReasonCode);
            Assert.IsTrue(job.ToEnvelope()["notDispatched"].Value<bool>());
        }

        [TestMethod] public void ASecondLaunchIsPossibleOnceTheFirstEnded()
        {
            rig.Port.BeginResult = false; rig.Port.BeginReason = "launch_site_invalid";
            rig.Launch(); rig.RunToEnd();
            rig.Port.BeginResult = true;
            var again = rig.Service.Handle(rig.Request("launch-0002"));
            Assert.AreEqual("running", again.Status, again.Data.ToString());
        }
    }
}
