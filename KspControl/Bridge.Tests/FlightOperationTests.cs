using System;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class FlightOperationTests
    {
        private FlightRig rig;
        private FakeFlight flight;

        [TestInitialize]
        public void Setup() { rig = new FlightRig(); flight = rig.Flight; }

        private static void AssertRefused(BridgeResponse response, string reason)
        {
            Assert.AreEqual(JobStatuses.Failed, response.Status, response.Data.ToString());
            Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.IsTrue((bool)response.Data["notDispatched"]); Assert.IsFalse((bool)response.Data["dispatched"]);
        }

        // ---------------------------------------------------------------- set_controls

        [TestMethod]
        public void ThrottleIsHeldThroughTheGuardAndReadBack()
        {
            var r = rig.SetControls("req-set-0001", "throttle", 0.5);
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            Assert.IsTrue((bool)r.Data["dispatched"]); Assert.IsTrue(rig.Guard.Engaged);
            Assert.AreEqual(0.5, (double)r.Data["observed"]["throttleFraction"], 1e-6);
            Assert.IsTrue((bool)r.Data["observed"]["flyByWireHeld"]);
            CollectionAssert.AreEqual(new[] { "throttle=0.5" }, r.Data["applied"].Select(t => (string)t).ToArray());
            flight.VesselTick(); Assert.AreEqual(0.5, flight.Snap.Controls.Throttle, 1e-6);
        }

        [TestMethod]
        public void FullAndCutAreJustThrottleOneAndZero()
        {
            Assert.AreEqual(JobStatuses.Completed, rig.SetControls("req-full-0001", "throttle", 1.0).Status);
            Assert.AreEqual(1.0, flight.Snap.Controls.Throttle, 1e-6);
            Assert.AreEqual(JobStatuses.Completed, rig.SetControls("req-cut-00001", "throttle", 0.0).Status);
            Assert.AreEqual(0.0, flight.Snap.Controls.Throttle, 1e-6);
        }

        [TestMethod]
        public void ThrottleDeltaAddsToTheCommandedThrottleAndClamps()
        {
            rig.SetControls("req-base-0001", "throttle", 0.5);
            var up = rig.SetControls("req-up-000001", "throttleDelta", 0.25);
            Assert.AreEqual(0.75, flight.Snap.Controls.Throttle, 1e-6, up.Data.ToString());
            rig.SetControls("req-up-000002", "throttleDelta", 0.5);
            Assert.AreEqual(1.0, flight.Snap.Controls.Throttle, 1e-6);
            rig.SetControls("req-down-0001", "throttleDelta", -1.0);
            Assert.AreEqual(0.0, flight.Snap.Controls.Throttle, 1e-6);
        }

        [TestMethod]
        public void DesiredStateSettersAreUsedAndOnlyForStatesThatChange()
        {
            flight.Groups["Light"] = true;
            var r = rig.SetControls("req-state-001", "sas", true, "gear", true, "lights", true, "brakes", false);
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            CollectionAssert.AreEqual(new[] { "set:SAS=True", "set:Gear=True" }, flight.Calls.Where(c => c.StartsWith("set:")).ToArray());
            Assert.AreEqual(0, flight.Count("toggle"));
            Assert.IsTrue((bool)r.Data["observed"]["sas"]); Assert.IsTrue((bool)r.Data["observed"]["gear"]);
            Assert.IsTrue(r.Data["applied"].Select(t => (string)t).Contains("Light=true (already)"));
        }

        [TestMethod]
        public void AllStatesAlreadyMatchingIsACompletedNoOpThatReachesNoCallback()
        {
            var r = rig.SetControls("req-noop-0001", "sas", false);
            Assert.AreEqual(JobStatuses.Completed, r.Status); Assert.IsTrue((bool)r.Data["noop"]); Assert.IsFalse((bool)r.Data["dispatched"]);
            Assert.AreEqual(0, flight.Calls.Count);
        }

        [TestMethod]
        public void ASetterThatDoesNotTakeEffectIsIndeterminateAndNotRetried()
        {
            flight.GroupSetterIgnored = true;
            var r = rig.SetControls("req-ignore-01", "gear", true);
            Assert.AreEqual(JobStatuses.Indeterminate, r.Status); Assert.AreEqual(FlightReasons.NotConfirmed, r.ReasonCode);
            Assert.IsTrue((bool)r.Data["dispatched"]); Assert.AreEqual(1, flight.Count("set:Gear"));
            StringAssert.Contains((string)r.Data["reconcile"], "flight_state");
        }

        [TestMethod]
        public void AThrottleWriteThatIsNotObservedIsIndeterminate()
        {
            flight.ThrottleWriteIgnored = true;
            var r = rig.SetControls("req-nowrite-01", "throttle", 0.9);
            Assert.AreEqual(JobStatuses.Indeterminate, r.Status); Assert.AreEqual(FlightReasons.NotConfirmed, r.ReasonCode);
        }

        [TestMethod]
        public void TheThrottleStepRunsBeforeTheGroupSteps()
        {
            rig.SetControls("req-order-001", "gear", true, "throttle", 0.0, "sas", true);
            var order = flight.Calls.Where(c => c.StartsWith("throttle:") || c.StartsWith("set:")).ToArray();
            Assert.AreEqual("throttle:0", order[0]);
        }

        [TestMethod]
        public void AGroupBoundToAnUnknownModActionIsReportedAndTheRequestStillCompletes()
        {
            flight.Bindings["Gear"].Add(FakeFlight.Act("ModuleLandingGear", "1"));
            flight.Bindings["Gear"].Add(FakeFlight.Act("SomeModBomb", "2", "Drop"));
            var r = rig.SetControls("req-unknown-01", "throttle", 1.0, "gear", true);
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            Assert.AreEqual("2/SomeModBomb.Drop", (string)r.Data["unclassified"][0]);
            CollectionAssert.Contains(r.Data["consequential"].Select(x => (string)x).ToArray(), "unclassified:2/SomeModBomb.Drop");
            Assert.IsTrue(flight.Calls.Count > 0, "the callbacks ran");
        }

        [TestMethod]
        public void ConsequentialModulesAreAllowedAndReported()
        {
            flight.Bindings["Light"].Add(FakeFlight.Act("ModuleLight", "1")); flight.Bindings["Light"].Add(FakeFlight.Act("ModuleDecouple", "3", "Decouple"));
            var r = rig.SetControls("req-conseq-001", "lights", true);
            Assert.AreEqual(JobStatuses.Completed, r.Status);
            CollectionAssert.AreEqual(new[] { "decouple:3/ModuleDecouple.Decouple" }, r.Data["consequential"].Select(t => (string)t).ToArray());
        }

        [TestMethod]
        public void ArgumentChecksAreStrictAndNeverReachTheVessel()
        {
            foreach (var args in new[]
            {
                FlightRig.Args("req-arg-00001"),
                FlightRig.Args("req-arg-00002", "throttle", 1.5),
                FlightRig.Args("req-arg-00003", "throttle", -0.1),
                FlightRig.Args("req-arg-00004", "throttleDelta", 2),
                FlightRig.Args("req-arg-00005", "throttle", 0.5, "throttleDelta", 0.1),
                FlightRig.Args("req-arg-00006", "sas", "yes"),
                FlightRig.Args("req-arg-00007", "throttle", "0.5"),
                FlightRig.Args("req-arg-00008", "throttle", 0.5, "extra", 1),
                FlightRig.Args("bad id", "throttle", 0.5),
            })
            {
                var r = rig.Send(FlightOperations.SetControls, args);
                Assert.AreEqual(ControlReasons.InvalidArgument, r.ReasonCode, args.ToString());
            }
            Assert.AreEqual(0, flight.Calls.Count);
        }

        // ---------------------------------------------------------------- lease, grant, vessel gates

        [TestMethod]
        public void NoLeaseAndAMalformedLeaseAreRefusedBeforeAnything()
        {
            AssertRefused(rig.Send(FlightOperations.SetControls, FlightRig.Args("req-nolease-1", "throttle", 0.1), withLease: false), ControlReasons.LeaseRequired);
            AssertRefused(rig.Send(FlightOperations.SetControls, FlightRig.Args("req-badlease1", "throttle", 0.1), "zz"), ControlReasons.LeaseInvalid);
            AssertRefused(rig.Send(FlightOperations.SetControls, FlightRig.Args("req-wronglease", "throttle", 0.1), new string('a', 32)), ControlReasons.LeaseInvalid);
            Assert.AreEqual(0, flight.Calls.Count);
        }

        [TestMethod]
        public void ARevokedLeaseIsRefusedWithTheAuthoritysReason()
        {
            rig.Authority.HumanTakeover();
            AssertRefused(rig.SetControls("req-revoked-01", "throttle", 0.1), ControlReasons.AuthorityRevoked);
            Assert.AreEqual(0, flight.Calls.Count);
        }

        [TestMethod]
        public void AnEditorOnlyGrantCannotBindALeaseToAVessel()
        {
            var editorOnly = new FlightRig(lease: false, grant: false, custom: AuthorityHelpers.Grant(1, "grant", 1000, facilities: new[] { "VAB" }));
            var error = Assert.ThrowsException<InvalidOperationException>(() => editorOnly.Authority.AcquireLease(300000, "flight probe"));
            Assert.AreEqual(ControlReasons.FacilityMismatch, error.Message);
        }

        [TestMethod]
        public void AFlightGrantThatDropsTheFamilyPermissionIsDeniedAtAdmission()
        {
            var noPermission = new FlightRig(custom: new TrustedExecutionGrant("g", 1, AuthorityHelpers.Bind(), AuthorityHelpers.Utc0.AddHours(1000),
                new[] { new EffectPermission("editor.replace_craft", "editor:VAB") }, new[] { FlightEffects.EntityWildcard }));
            AssertRefused(noPermission.SetControls("req-denied-001", "throttle", 0.1), OperationReasons.GrantOperationDenied);
            Assert.AreEqual(0, noPermission.Flight.Calls.Count);
        }

        [TestMethod]
        public void OutsideTheFlightSceneAndWithoutAnOwnedControllableVesselNothingRuns()
        {
            flight.InFlightValue = false;
            AssertRefused(rig.SetControls("req-scene-0001", "throttle", 0.1), FlightReasons.FlightUnavailable);
            flight.InFlightValue = true; flight.Snap.Owned = false;
            AssertRefused(rig.SetControls("req-owner-0001", "throttle", 0.1), FlightReasons.VesselNotOwned);
            flight.Snap.Owned = true; flight.Snap.Controllable = false;
            AssertRefused(rig.SetControls("req-ctrl-00001", "throttle", 0.1), FlightReasons.VesselNotControllable);
            Assert.AreEqual(0, flight.Calls.Count);
        }

        [TestMethod]
        public void TheLeaseIsBoundToTheVesselItWasAcquiredFor()
        {
            flight.Snap.VesselId = "33333333-3333-3333-3333-333333333333"; // switched, and no frame has pumped the context yet
            var r = rig.SetControls("req-switch-001", "throttle", 0.1);
            Assert.AreEqual(JobStatuses.Failed, r.Status); Assert.IsTrue((bool)r.Data["notDispatched"]);
            Assert.AreEqual(0, flight.Calls.Count);
        }

        [TestMethod]
        public void AuthorityLostBetweenAdmissionAndTheCallbackStopsBeforeTheCallback()
        {
            flight.OnRead = n => { if (n == 2) rig.Authority.Stop(); }; // read 1 is the gate, read 2 is the revalidation before the callback
            var r = rig.SetControls("req-midstop-01", "gear", true);
            Assert.AreEqual(JobStatuses.Failed, r.Status, r.Data.ToString()); Assert.IsFalse((bool)r.Data["dispatched"]);
            Assert.AreEqual(ControlReasons.AuthorityRevoked, r.ReasonCode);
            Assert.AreEqual(0, flight.Count("set:"));
        }

        [TestMethod]
        public void BindingsThatChangeAfterAdmissionAreStaleAndNothingIsInvoked()
        {
            flight.Bindings["Custom01"].Add(FakeFlight.Act("ModuleLight", "1"));
            flight.OnRead = n => { if (n == 2) flight.Bindings["Custom01"].Add(FakeFlight.Act("ModuleDecouple", "9", "Decouple")); };
            var r = rig.Group("req-stale-0001", "Custom01", true);
            Assert.AreEqual(JobStatuses.Failed, r.Status, r.Data.ToString()); Assert.AreEqual("effects_changed", r.ReasonCode);
            Assert.AreEqual(0, flight.Calls.Count);
        }

        // ---------------------------------------------------------------- stage

        [TestMethod]
        public void StageActivatesWhenTheExpectedStageMatchesAndConfirmsTheAdvance()
        {
            flight.StageParts[2] = new System.Collections.Generic.List<FlightPartAction>
            {
                FakeFlight.Act("ModuleDecouple", "10", "Decouple", true), FakeFlight.Act("ModuleEnginesFX", "11", "Activate", true), FakeFlight.Act("ModuleCommand", "12")
            };
            var r = rig.Stage("req-stage-0001", 3);
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            Assert.AreEqual(1, flight.Count("stage")); Assert.AreEqual(2, flight.Snap.Controls.CurrentStage);
            Assert.AreEqual(3, (int)r.Data["stageBefore"]); Assert.AreEqual(2, (int)r.Data["stageAfter"]);
            CollectionAssert.AreEquivalent(new[] { "decouple:10/ModuleDecouple.Decouple", "engine:11/ModuleEnginesFX.Activate" }, r.Data["consequential"].Select(t => (string)t).ToArray());
        }

        [TestMethod]
        public void AStageMismatchIsRefusedWithoutStaging()
        {
            var r = rig.Stage("req-mismatch-01", 1);
            AssertRefused(r, FlightReasons.StageMismatch);
            Assert.AreEqual(3, (int)r.Data["currentStage"]); Assert.AreEqual(1, (int)r.Data["expectedStage"]);
            Assert.AreEqual(0, flight.Count("stage"));
        }

        [TestMethod]
        public void AnUnknownModuleThatActsOnStagingIsReportedAndAnInertOneIsNot()
        {
            flight.StageParts[2] = new System.Collections.Generic.List<FlightPartAction> { FakeFlight.Act("ModuleInertMod", "5", null, false) };
            Assert.AreEqual(JobStatuses.Completed, rig.Stage("req-inert-0001", 3).Status);
            flight.Snap.Controls.CurrentStage = 3;
            flight.StageParts[2] = new System.Collections.Generic.List<FlightPartAction> { FakeFlight.Act("ModuleNukeMod", "6", null, true) };
            var r = rig.Stage("req-nuke-00001", 3);
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            CollectionAssert.Contains(r.Data["unclassified"].Select(x => (string)x).ToArray(), "6/ModuleNukeMod");
            Assert.AreEqual(2, flight.Count("stage"), "both stages ran");
        }

        [TestMethod]
        public void LockedStagingIsRefused()
        {
            flight.Locked = true;
            AssertRefused(rig.Stage("req-locked-0001", 3), FlightReasons.StagingLocked);
            Assert.AreEqual(0, flight.Count("stage"));
        }

        [TestMethod]
        public void AStageThatDoesNotAdvanceIsIndeterminateAndTheRequestIdStaysUsable()
        {
            flight.StageIgnored = true;
            var r = rig.Stage("req-noadv-0001", 3);
            Assert.AreEqual(JobStatuses.Indeterminate, r.Status); Assert.AreEqual(FlightReasons.NotConfirmed, r.ReasonCode);
            var again = rig.Stage("req-noadv-0001", 3); // same id, same arguments: the remembered answer, no second activation
            Assert.IsTrue((bool)again.Data["replayed"]); Assert.AreEqual(1, flight.Count("stage"));
        }

        [TestMethod]
        public void TheStageAboutToFireIsClassifiedNotTheOneAlreadyActivated()
        {
            // CurrentStage is 3 (last activated): ActivateNextStage fires the parts of stage 2.
            flight.StageParts[3] = new System.Collections.Generic.List<FlightPartAction> { FakeFlight.Act("ModuleNukeMod", "1", null, true) };
            flight.StageParts[2] = new System.Collections.Generic.List<FlightPartAction> { FakeFlight.Act("ModuleDecouple", "2", "Decouple", true) };
            var r = rig.Stage("req-next-00001", 3);
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            CollectionAssert.AreEqual(new[] { "decouple:2/ModuleDecouple.Decouple" }, r.Data["consequential"].Select(t => (string)t).ToArray());
        }

        [TestMethod]
        public void AnUnknownModuleOnTheNextStageIsReportedAndTheStageRuns()
        {
            flight.StageParts[2] = new System.Collections.Generic.List<FlightPartAction> { FakeFlight.Act("ModuleNukeMod", "6", null, true) };
            var r = rig.Stage("req-next-00002", 3);
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            CollectionAssert.Contains(r.Data["unclassified"].Select(x => (string)x).ToArray(), "6/ModuleNukeMod");
            Assert.AreEqual(1, flight.Count("stage"));
        }

        [TestMethod]
        public void StagingAlsoReportsUnknownModulesOnTheStageActionGroupBindings()
        {
            flight.StageParts[2] = new System.Collections.Generic.List<FlightPartAction>();
            flight.Bindings["Stage"].Add(FakeFlight.Act("ModuleMysteryMod", "8", "Fire"));
            var first = rig.Stage("req-grpstage-01", 3);
            Assert.AreEqual(JobStatuses.Completed, first.Status, first.Data.ToString());
            CollectionAssert.Contains(first.Data["unclassified"].Select(x => (string)x).ToArray(), "8/ModuleMysteryMod.Fire");
            Assert.AreEqual(1, flight.Count("stage"));
            flight.Snap.Controls.CurrentStage = 3;
            flight.Bindings["Stage"].Clear(); flight.Bindings["Stage"].Add(FakeFlight.Act("ModuleParachute", "9", "Deploy"));
            var ok = rig.Stage("req-grpstage-02", 3);
            Assert.AreEqual(JobStatuses.Completed, ok.Status);
            CollectionAssert.AreEqual(new[] { "chute:9/ModuleParachute.Deploy" }, ok.Data["consequential"].Select(t => (string)t).ToArray());
        }

        [TestMethod]
        public void StageBindingsThatChangeAfterAdmissionAreStale()
        {
            flight.OnRead = n => { if (n == 2) flight.Bindings["Stage"].Add(FakeFlight.Act("ModuleDecouple", "9", "Decouple")); };
            var r = rig.Stage("req-stale-stage1", 3);
            Assert.AreEqual("effects_changed", r.ReasonCode); Assert.AreEqual(0, flight.Count("stage"));
        }

        [TestMethod]
        public void WhenNothingHasBeenLeftToActivateTheStageIsRefused()
        {
            flight.Snap.Controls.CurrentStage = 0;
            AssertRefused(rig.Stage("req-nostage-001", 0), FlightReasons.NoStageToActivate);
            Assert.AreEqual(0, flight.Count("stage"));
        }

        [TestMethod]
        public void RaisingTheThrottleDuringWarpIsRefusedButCuttingItIsNot()
        {
            flight.Snap.Warp.CurrentIndex = 3; flight.Snap.Warp.CurrentRate = 50;
            AssertRefused(rig.SetControls("req-wthr-00001", "throttle", 0.5), FlightReasons.WarpThrottleActive);
            AssertRefused(rig.SetControls("req-wthr-00002", "throttleDelta", 0.2), FlightReasons.WarpThrottleActive);
            Assert.AreEqual(0, flight.Calls.Count);
            Assert.AreEqual(JobStatuses.Completed, rig.SetControls("req-wthr-00003", "throttle", 0.0).Status);
            Assert.AreEqual(JobStatuses.Completed, rig.SetControls("req-wthr-00004", "gear", true).Status);
        }

        [TestMethod]
        public void AWarpOnlyReleaseNeverWritesAThrottle()
        {
            rig.Warp("req-wonly-0001", 2);
            rig.Authority.Stop(); rig.Frame();
            CollectionAssert.DoesNotContain(flight.Calls, "throttle:0");
            Assert.AreEqual(1, flight.Count("cancelwarp"));
        }

        // ---------------------------------------------------------------- action groups

        [TestMethod]
        public void ACustomGroupWithADesiredStateReconcilesInsteadOfBlindlyToggling()
        {
            flight.Bindings["Custom02"].Add(FakeFlight.Act("ModuleLight", "1"));
            var on = rig.Group("req-grp-00001", "Custom02", true);
            Assert.AreEqual(JobStatuses.Completed, on.Status, on.Data.ToString()); Assert.AreEqual(1, flight.Count("set:Custom02=True"));
            var same = rig.Group("req-grp-00002", "Custom02", true);
            Assert.AreEqual(JobStatuses.Completed, same.Status); Assert.IsTrue((bool)same.Data["noop"]);
            Assert.AreEqual(1, flight.Count("set:Custom02"), "already on: no second callback");
        }

        [TestMethod]
        public void WithoutADesiredStateTheGroupIsToggledOnceAndTheChangeConfirmed()
        {
            var r = rig.Group("req-tog-00001", "Brakes");
            Assert.AreEqual(JobStatuses.Completed, r.Status); Assert.AreEqual(1, flight.Count("toggle:Brakes"));
            Assert.IsFalse((bool)r.Data["stateBefore"]); Assert.IsTrue((bool)r.Data["stateAfter"]);
        }

        [TestMethod]
        public void AnUnknownBoundActionIsReportedAndTheGroupStillRuns()
        {
            flight.Bindings["Custom03"].Add(FakeFlight.Act("ModuleAnimateGeneric", "1")); flight.Bindings["Custom03"].Add(FakeFlight.Act("ModuleMysteryWeapon", "2", "Fire"));
            var r = rig.Group("req-mixed-0001", "Custom03", true);
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            Assert.AreEqual("2/ModuleMysteryWeapon.Fire", (string)r.Data["unclassified"][0]);
            Assert.IsTrue(flight.Calls.Count > 0);
        }

        [TestMethod]
        public void AnUnknownGroupNameIsInvalid()
        {
            var r = rig.Send(FlightOperations.ActionGroup, FlightRig.Args("req-name-0001", "group", "Custom11"));
            Assert.AreEqual(ControlReasons.InvalidArgument, r.ReasonCode);
            Assert.AreEqual(ControlReasons.InvalidArgument, rig.Send(FlightOperations.ActionGroup, FlightRig.Args("req-name-0002", "group", "Abort")).ReasonCode);
            Assert.AreEqual(0, flight.Calls.Count);
        }

        [TestMethod]
        public void AToggleThatDoesNotChangeTheStateIsIndeterminate()
        {
            flight.GroupSetterIgnored = true;
            var r = rig.Group("req-tog-00002", "Custom05");
            Assert.AreEqual(JobStatuses.Indeterminate, r.Status);
        }

        // ---------------------------------------------------------------- abort

        [TestMethod]
        public void AbortFiresTheGroupAndConfirmsItAndDoesNotTouchTheThrottle()
        {
            rig.SetControls("req-thr-000001", "throttle", 0.6);
            flight.Bindings["Abort"].Add(FakeFlight.Act("ModuleEnginesFX", "40", "Activate"));
            var r = rig.Abort("req-abort-0001");
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString()); Assert.AreEqual(1, flight.Count("abort"));
            Assert.AreEqual(0.6, flight.Snap.Controls.Throttle, 1e-6);
            CollectionAssert.AreEqual(new[] { "engine:40/ModuleEnginesFX.Activate" }, r.Data["consequential"].Select(t => (string)t).ToArray());
        }

        [TestMethod]
        public void AbortWithAnUnclassifiedBoundActionIsReportedAndStillFires()
        {
            flight.Bindings["Abort"].Add(FakeFlight.Act("ModuleSelfDestruct", "7", "Boom"));
            var r = rig.Abort("req-abort-0002");
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            Assert.AreEqual("7/ModuleSelfDestruct.Boom", (string)r.Data["unclassified"][0]);
            Assert.AreEqual(1, flight.Count("abort"));
        }

        [TestMethod]
        public void AnUnconfirmedAbortIsIndeterminate()
        {
            flight.AbortIgnored = true;
            Assert.AreEqual(JobStatuses.Indeterminate, rig.Abort("req-abort-0003").Status);
        }

        // ---------------------------------------------------------------- warp

        [TestMethod]
        public void WarpWithinTheCapsGoesThroughTheStockSetter()
        {
            var r = rig.Warp("req-warp-0001", 5); // 1000x is exactly the cap
            Assert.AreEqual(JobStatuses.Completed, r.Status, r.Data.ToString());
            Assert.AreEqual(1, flight.Count("warp:5")); Assert.AreEqual(5, flight.Snap.Warp.CurrentIndex);
            Assert.AreEqual(1000.0, (double)r.Data["observed"]["warpEffectiveRate"], 1e-6);
        }

        [TestMethod]
        public void WarpAboveTheEffectiveRateCapIsRefusedWithoutCallingTheSetter()
        {
            var r = rig.Warp("req-warp-0002", 6); // 10000x
            AssertRefused(r, FlightReasons.WarpAboveCap);
            Assert.AreEqual(0, flight.Calls.Count);
            Assert.AreEqual(1000.0, (double)r.Data["capEffectiveRate"], 1e-6);
        }

        [TestMethod]
        public void PhysicsWarpIsCappedAtRealTime()
        {
            flight.Snap.Warp.Mode = "physics"; flight.Snap.Warp.Rates = new float[] { 1, 2, 3, 4 };
            AssertRefused(rig.Warp("req-warp-0003", 1), FlightReasons.WarpAboveCap);
            Assert.AreEqual(JobStatuses.Completed, rig.Warp("req-warp-0004", 0).Status);
        }

        [TestMethod]
        public void TheAltitudeLimitAndAnIndexOutsideTheTableAreRefused()
        {
            flight.Snap.Warp.AltitudeLimitIndex = 3;
            AssertRefused(rig.Warp("req-warp-0005", 4), FlightReasons.WarpAboveCap);
            flight.Snap.Warp.Rates = new float[] { 1, 5, 10 }; flight.Snap.Warp.AltitudeLimitIndex = -1;
            AssertRefused(rig.Warp("req-warp-0006", 3), ControlReasons.InvalidArgument);
            Assert.AreEqual(0, flight.Calls.Count);
        }

        [TestMethod]
        public void WarpingUpWhileThrustingIsRefusedButWarpingDownIsAlwaysAllowed()
        {
            rig.SetControls("req-thr-000002", "throttle", 0.5);
            AssertRefused(rig.Warp("req-warp-0007", 2), FlightReasons.WarpThrottleActive);
            rig.SetControls("req-thr-000003", "throttle", 0.0);
            Assert.AreEqual(JobStatuses.Completed, rig.Warp("req-warp-0008", 2).Status);
            Assert.AreEqual(JobStatuses.Completed, rig.Warp("req-warp-0009", 0).Status);
        }

        [TestMethod]
        public void AWarpTheGameRefusesIsAKnownFailureWithTheRateUnchanged()
        {
            flight.WarpDenied = true;
            var r = rig.Warp("req-warp-0010", 3);
            Assert.AreEqual(JobStatuses.Failed, r.Status); Assert.AreEqual(FlightReasons.WarpDenied, r.ReasonCode);
            Assert.IsTrue((bool)r.Data["dispatched"]); Assert.AreEqual(0, flight.Snap.Warp.CurrentIndex);
            StringAssert.Contains((string)r.Data["detail"], "LunaMP");
        }

        [TestMethod]
        public void StopReturnsABridgeRaisedWarpToRealTime()
        {
            rig.Warp("req-warp-0011", 4);
            Assert.AreEqual(4, flight.Snap.Warp.CurrentIndex);
            rig.Authority.Stop(); rig.Frame();
            Assert.AreEqual(0, flight.Snap.Warp.CurrentIndex); Assert.AreEqual(1, flight.Count("cancelwarp"));
        }

        [TestMethod]
        public void RequestingTheCurrentWarpIndexIsACompletedNoOp()
        {
            var r = rig.Warp("req-warp-0012", 0);
            Assert.AreEqual(JobStatuses.Completed, r.Status); Assert.IsTrue((bool)r.Data["noop"]); Assert.AreEqual(0, flight.Calls.Count);
        }

        // ---------------------------------------------------------------- idempotency

        [TestMethod]
        public void ARetriedRequestReturnsTheSameAnswerWithoutActingTwice()
        {
            var first = rig.Group("req-idem-0001", "Gear");
            var second = rig.Group("req-idem-0001", "Gear");
            Assert.AreEqual(1, flight.Count("toggle:Gear"), "a toggle must never flip back on a retry");
            Assert.AreEqual(first.Status, second.Status); Assert.IsTrue((bool)second.Data["replayed"]);
            Assert.IsTrue(flight.Groups["Gear"]);
        }

        [TestMethod]
        public void TheSameIdWithDifferentArgumentsIsAConflict()
        {
            rig.Group("req-idem-0002", "Gear");
            var r = rig.Group("req-idem-0002", "Light");
            AssertRefused(r, OperationReasons.RequestIdConflict);
            Assert.AreEqual(0, flight.Count("toggle:Light"));
        }

        [TestMethod]
        public void ARefusalDoesNotReserveTheRequestId()
        {
            AssertRefused(rig.Stage("req-retry-0001", 1), FlightReasons.StageMismatch);
            Assert.AreEqual(JobStatuses.Completed, rig.Stage("req-retry-0001", 3).Status, "a refusal never reached a callback, so the id is evaluated afresh");
        }

        [TestMethod]
        public void TheRememberedResultsAreBounded()
        {
            for (var i = 0; i < 80; i++) rig.Group("req-bound-" + i.ToString("D4"), "Custom01", i % 2 == 0);
            // The oldest results are forgotten: the first id is evaluated as a new request (a no-op or a toggle), not replayed.
            var old = rig.Group("req-bound-0000", "Custom01", true);
            Assert.IsFalse(old.Data["replayed"] != null && (bool)old.Data["replayed"]);
        }

        [TestMethod]
        public void NoOperationOutsideTheFlightSetIsServed()
        {
            var r = rig.Send("flight.teleport", FlightRig.Args("req-nothing-01"));
            Assert.AreEqual(ControlReasons.InvalidArgument, r.ReasonCode);
        }
    }
}
