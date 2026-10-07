using System;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    /// <summary>The guard owns the fly-by-wire callback only while a lease is held, and undoes it on Stop, revoke and destroy.</summary>
    [TestClass]
    public class FlightControlGuardTests
    {
        private FakeFlight port;
        private bool lease;
        private FlightControlGuard guard;

        [TestInitialize]
        public void Setup() { port = new FakeFlight(); lease = true; guard = new FlightControlGuard(port, () => lease); }

        [TestMethod]
        public void EngageWithoutALeaseInstallsNothingAndWritesNothing()
        {
            lease = false;
            Assert.IsFalse(guard.Engage(0.5f));
            Assert.IsFalse(port.Attached); Assert.AreEqual(0, port.Calls.Count);
        }

        [TestMethod]
        public void EngageInstallsTheCallbackOnceAndTheTickAppliesTheThrottle()
        {
            Assert.IsTrue(guard.Engage(0.4f)); Assert.IsTrue(guard.Engage(0.7f));
            Assert.AreEqual(1, port.Count("attach"));
            port.VesselTick();
            Assert.AreEqual(0.7, port.Snap.Controls.Throttle, 1e-6);
            Assert.IsTrue(guard.Engaged);
        }

        [TestMethod]
        public void OutOfRangeThrottleIsRefusedWithoutTouchingTheVessel()
        {
            Assert.IsFalse(guard.Engage(1.2f)); Assert.IsFalse(guard.Engage(-0.1f)); Assert.IsFalse(guard.Engage(float.NaN));
            Assert.AreEqual(0, port.Calls.Count);
        }

        [TestMethod]
        public void LeaseEndedFromAnyThreadMakesTheNextTickZeroThrottleAndRemoveTheCallback()
        {
            guard.Engage(0.9f);
            var thread = new System.Threading.Thread(() => guard.OnLeaseEnded("stop")); thread.Start(); thread.Join();
            port.VesselTick();
            Assert.AreEqual(0.0, port.Snap.Controls.Throttle, 1e-6); Assert.IsFalse(port.Attached); Assert.IsFalse(guard.Engaged);
            Assert.AreEqual("stop", guard.LastRelease);
        }

        [TestMethod]
        public void StopCutsTheThrottleBeforeItRemovesTheCallback()
        {
            guard.Engage(0.9f); port.Calls.Clear();
            guard.Release("stop");
            Assert.AreEqual("throttle:0|detach", string.Join("|", port.Calls));
        }

        [TestMethod]
        public void AnAgentReleaseKeepsTheThrottleItSet()
        {
            guard.Engage(0.6f); guard.Release("released");
            Assert.AreEqual(0.6, port.Snap.Controls.Throttle, 1e-6); Assert.IsFalse(port.Attached);
        }

        [TestMethod]
        public void AHumanTakeoverLeavesTheHumansControlsAlone()
        {
            guard.Engage(0.6f); guard.Release("human_takeover");
            CollectionAssert.DoesNotContain(port.Calls, "throttle:0"); Assert.IsFalse(port.Attached);
        }

        [DataTestMethod]
        [DataRow("lease_expired")] [DataRow("watchdog")] [DataRow("context_changed")] [DataRow("grant_replaced")] [DataRow("binding_changed")] [DataRow("fault")] [DataRow("destroyed")]
        public void EveryOtherEndZeroesTheThrottle(string reason)
        {
            guard.Engage(0.8f); guard.Release(reason);
            Assert.AreEqual(0.0, port.Snap.Controls.Throttle, 1e-6); Assert.IsFalse(port.Attached);
        }

        [TestMethod]
        public void UpdateReleasesWhenTheLeaseIsGoneEvenWithoutAnyEventOrVesselTick()
        {
            guard.Engage(0.8f); lease = false;
            guard.Update();
            Assert.AreEqual(0.0, port.Snap.Controls.Throttle, 1e-6); Assert.IsFalse(port.Attached);
        }

        [TestMethod]
        public void ReleaseIsIdempotentAndDoesNothingWhenNothingIsHeld()
        {
            guard.Release("stop"); Assert.AreEqual(0, port.Calls.Count);
            guard.Engage(0.5f); guard.Release("stop"); port.Calls.Clear();
            guard.Release("stop"); guard.Update(); Assert.AreEqual(0, port.Calls.Count);
        }

        [TestMethod]
        public void AStalePendingRevocationReleasesTheOldHoldBeforeANewLeaseEngages()
        {
            guard.Engage(0.5f); guard.OnLeaseEnded("lease_expired");
            Assert.IsTrue(guard.Engage(0.3f));
            Assert.AreEqual(2, port.Count("attach")); Assert.AreEqual(1, port.Count("detach"));
            port.VesselTick(); Assert.AreEqual(0.3, port.Snap.Controls.Throttle, 1e-6);
        }

        [TestMethod]
        public void ARaisedWarpIsDroppedByANeutralisingReleaseButNotByAnAgentRelease()
        {
            guard.NoteWarp(true); port.Snap.Warp.CurrentIndex = 3; guard.Release("released");
            Assert.AreEqual(0, port.Count("cancelwarp"));
            guard.NoteWarp(true); guard.Release("stop");
            Assert.AreEqual(1, port.Count("cancelwarp")); Assert.AreEqual(0, port.Snap.Warp.CurrentIndex);
        }

        [TestMethod]
        public void AFailingDetachStillLeavesTheThrottleCut()
        {
            var failing = new ThrowingDetach();
            var g = new FlightControlGuard(failing, () => true);
            g.Engage(0.5f); g.Release("stop");
            Assert.AreEqual(0f, failing.Written, 1e-6); Assert.IsFalse(g.Engaged);
        }

        [TestMethod]
        public void ADetachedCallbackNeverReasserts()
        {
            guard.Engage(0.5f); guard.Release("stop");
            port.Snap.Controls.Throttle = 0.2f;
            port.VesselTick();
            Assert.AreEqual(0.2, port.Snap.Controls.Throttle, 1e-6);
        }

        [TestMethod]
        public void AttachFailureEngagesNothing()
        {
            port.AttachFails = true;
            Assert.IsFalse(guard.Engage(0.5f)); Assert.IsFalse(guard.Engaged); Assert.AreEqual(0, port.Count("throttle"));
        }

        private sealed class ThrowingDetach : IFlightInputPort
        {
            public float Written = -1;
            public bool Attach(Func<float?> tick) { return true; }
            public void Detach() { throw new InvalidOperationException("vessel destroyed"); }
            public void WriteThrottle(float value) { Written = value; }
            public void CancelWarp() { }
        }

        // ---- the guard against the real authority: every way a lease can end ----

        [TestMethod]
        public void StopOnTheAuthorityEndsTheHoldOnTheNextFrameAndStopNowSemanticsAreImmediate()
        {
            var rig = new FlightRig();
            Assert.AreEqual(JobStatuses.Completed, rig.SetControls("req-throttle-1", "throttle", 0.8).Status);
            Assert.IsTrue(rig.Guard.Engaged);
            rig.Authority.Stop(); rig.Guard.Release("stop"); // the addon does exactly this in the same frame
            Assert.AreEqual(0.0, rig.Flight.Snap.Controls.Throttle, 1e-6); Assert.IsFalse(rig.Flight.Attached);
        }

        [TestMethod]
        public void StopWithoutTheImmediateCallStillEndsAtTheNextFrame()
        {
            var rig = new FlightRig();
            rig.SetControls("req-throttle-1", "throttle", 0.8);
            rig.Authority.Stop(); rig.Frame();
            Assert.AreEqual(0.0, rig.Flight.Snap.Controls.Throttle, 1e-6); Assert.IsFalse(rig.Flight.Attached);
        }

        [TestMethod]
        public void AVesselSwitchRevokesTheLeaseAndCutsTheThrottle()
        {
            var rig = new FlightRig();
            rig.SetControls("req-throttle-1", "throttle", 0.8);
            rig.Flight.Snap.VesselId = "22222222-2222-2222-2222-222222222222";
            rig.Frame();
            Assert.IsFalse(rig.Authority.LeaseHeld);
            Assert.AreEqual(0.0, rig.Flight.Snap.Controls.Throttle, 1e-6); Assert.IsFalse(rig.Flight.Attached);
        }

        [TestMethod]
        public void ASceneChangeRevokesTheLeaseAndCutsTheThrottle()
        {
            var rig = new FlightRig();
            rig.SetControls("req-throttle-1", "throttle", 0.5);
            rig.Context.Epoch = "epoch2"; rig.Frame();
            Assert.IsFalse(rig.Authority.LeaseHeld); Assert.IsFalse(rig.Flight.Attached);
            Assert.AreEqual(0.0, rig.Flight.Snap.Controls.Throttle, 1e-6);
        }

        [TestMethod]
        public void ALostHeartbeatWatchdogRevokesTheLeaseAndCutsTheThrottle()
        {
            var rig = new FlightRig();
            rig.SetControls("req-throttle-1", "throttle", 0.5);
            rig.Heartbeats = false; rig.Frame(2500); rig.Frame(16);
            Assert.IsFalse(rig.Authority.LeaseHeld); Assert.IsFalse(rig.Flight.Attached);
            Assert.AreEqual(0.0, rig.Flight.Snap.Controls.Throttle, 1e-6);
        }

        [TestMethod]
        public void ReleasingTheLeaseKeepsTheCommandedThrottleButRemovesTheHook()
        {
            var rig = new FlightRig();
            rig.SetControls("req-throttle-1", "throttle", 0.5);
            Assert.IsTrue(rig.Authority.ReleaseLease(rig.Lease)); rig.Frame();
            Assert.IsFalse(rig.Flight.Attached); Assert.AreEqual(0.5, rig.Flight.Snap.Controls.Throttle, 1e-6);
        }

        [TestMethod]
        public void AHumanHoldingAControlTakesTheLeaseOverAfterTheDebounceAndKeepsTheirThrottle()
        {
            var rig = new FlightRig();
            rig.SetControls("req-throttle-1", "throttle", 0.5);
            rig.Flight.Pressing = true; rig.Frame(); rig.Frame();
            Assert.IsTrue(rig.Authority.LeaseHeld, "two frames are inside the debounce");
            rig.Frame();
            Assert.IsFalse(rig.Authority.LeaseHeld);
            Assert.AreEqual(ControlReasons.AuthorityRevoked, rig.Authority.LeaseFailureReason(rig.Lease));
            Assert.IsFalse(rig.Flight.Attached); Assert.AreEqual(0.5, rig.Flight.Snap.Controls.Throttle, 1e-6);
            Assert.IsTrue(rig.Authority.Status().CooldownSeconds > 0);
        }

        [TestMethod]
        public void ABriefTouchBelowTheDebounceIsNotATakeover()
        {
            var rig = new FlightRig();
            rig.Flight.Pressing = true; rig.Frame(); rig.Flight.Pressing = false; rig.Frame(); rig.Flight.Pressing = true; rig.Frame(); rig.Frame();
            Assert.IsTrue(rig.Authority.LeaseHeld);
        }

        [TestMethod]
        public void NoTakeoverWatchWithoutALease()
        {
            var rig = new FlightRig(lease: false);
            rig.Flight.Pressing = true; rig.Frame(); rig.Frame(); rig.Frame(); rig.Frame();
            Assert.AreEqual(0, rig.Authority.Status().CooldownSeconds);
        }
    }
}
