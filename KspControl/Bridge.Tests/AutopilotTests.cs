using System;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class AutopilotAdmissionTests
    {
        private AutopilotRig rig;
        [TestInitialize] public void Setup() { rig = new AutopilotRig(); }

        private BridgeResponse Refused(BridgeResponse response, string reason)
        {
            Assert.AreEqual("failed", response.Status, response.Data.ToString());
            Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.AreEqual(true, (bool)response.Data["notDispatched"], "an admission refusal is a guaranteed no-effect failure");
            Assert.IsFalse(rig.Runner.Busy);
            Assert.AreEqual(0, rig.MechJeb.Calls.Count, "MechJeb was never touched: " + string.Join(",", rig.MechJeb.Calls));
            Assert.AreEqual(0, rig.Flight.PlanCalls);
            return response;
        }

        // ---- authority ----

        [TestMethod] public void AMutationWithoutALeaseIsLeaseRequired() { Refused(rig.Service.Handle(rig.Ascent(lease: null)), "lease_required"); }

        [TestMethod] public void AMalformedOrUnknownLeaseIsInvalid()
        {
            Refused(rig.Service.Handle(rig.Ascent(lease: "short")), "lease_invalid");
            Refused(rig.Service.Handle(rig.Ascent(requestId: "ascent-0002", lease: new string('a', 32))), "lease_invalid");
        }

        [TestMethod] public void AGrantWithoutTheFlightFamilyCannotLeaseTheFlightScene()
        {
            rig = new AutopilotRig(lease: false, operations: new[] { OperationEffects.ReplaceCraft });
            Assert.AreEqual("facility_mismatch", Assert.ThrowsException<InvalidOperationException>(() => rig.AcquireLease()).Message);
            Refused(rig.Service.Handle(rig.Ascent(lease: new string('a', 32))), "lease_invalid");
        }

        [TestMethod] public void ARevokedLeaseIsAuthorityRevoked()
        {
            var request = rig.Ascent();
            rig.Authority.HumanTakeover();
            Refused(rig.Service.Handle(request), "authority_revoked");
        }

        [TestMethod] public void AnExpiredLeaseIsReported()
        {
            rig.Heartbeats = false; var request = rig.Ascent();
            rig.Run(30, 100);
            Refused(rig.Service.Handle(request), "lease_expired");
        }

        [TestMethod] public void TheFlightSceneMustBeReady()
        {
            rig.Context.Ready = false; rig.Frame();
            Refused(rig.Service.Handle(rig.Ascent()), "flight_unavailable");
            rig.Context.Ready = true; rig.Frame();
            rig.Flight.InFlightValue = false;
            Refused(rig.Service.Handle(rig.Ascent(requestId: "ascent-0002")), "flight_unavailable");
        }

        // ---- arguments ----

        [TestMethod] public void ArgumentsOutOfBoundsAreInvalidAndNothingHappens()
        {
            foreach (var request in new[]
            {
                rig.Ascent(altitude: 69999), rig.Ascent(altitude: 500001), rig.Ascent(inclination: 180.5), rig.Ascent(inclination: -181),
                rig.Ascent(requestId: "short"), rig.Ascent(extra: new JObject { ["autostage"] = "yes" }), rig.Ascent(extra: new JObject { ["targetAltitudeMeters"] = 100000.5 }),
                rig.Ascent(extra: new JObject { ["inclinationDegrees"] = "0" }), rig.Ascent(extra: new JObject { ["autoWarp"] = "no" }),
                rig.Request(AutopilotOperations.ExecuteNode, new JObject { ["requestId"] = "node-0001" }),
                rig.PlanHohmann(body: ""), rig.PlanHohmann(body: "Mun; drop"), rig.PlanHohmann(body: new string('a', 65))
            })
                Refused(rig.Service.Handle(request), "invalid_argument");
        }

        [TestMethod] public void TheEdgesOfTheRangesAreAccepted()
        {
            Assert.AreEqual("running", rig.Service.Handle(rig.Ascent(altitude: 70000, inclination: -180)).Status);
            rig.Runner.Abort("stopped"); rig.Authority.ReleaseLease(rig.Lease); rig.AcquireLease();
            Assert.AreEqual("running", rig.Service.Handle(rig.Ascent(requestId: "ascent-0002", altitude: 500000, inclination: 180)).Status);
        }

        [TestMethod] public void AutoWarpTrueIsRefusedAsUnsupportedAndFalseIsAccepted()
        {
            Refused(rig.Service.Handle(rig.Ascent(extra: new JObject { ["autoWarp"] = true })), "unsupported_option");
            Assert.AreEqual("running", rig.Service.Handle(rig.Ascent(requestId: "ascent-0002", extra: new JObject { ["autoWarp"] = false })).Status);
        }

        // ---- MechJeb availability ----

        [TestMethod] public void MissingMechJebVersionCoreAndModulesAreRefusedDistinctly()
        {
            rig.MechJeb.Caps.Installed = false;
            Refused(rig.Service.Handle(rig.Ascent()), "mechjeb_unavailable");
            rig.MechJeb.Caps.Installed = true; rig.MechJeb.Caps.VersionSupported = false; rig.MechJeb.Caps.Version = "2.14.0.0";
            Refused(rig.Service.Handle(rig.Ascent(requestId: "ascent-0002")), "mechjeb_version_unsupported");
            rig.MechJeb.Caps.VersionSupported = true; rig.MechJeb.Core = false;
            Refused(rig.Service.Handle(rig.Ascent(requestId: "ascent-0003")), "mechjeb_unavailable");
            rig.MechJeb.Core = true; rig.MechJeb.Caps.Modules["ascentSettings"] = false;
            Refused(rig.Service.Handle(rig.Ascent(requestId: "ascent-0004")), "mechjeb_module_unavailable");
            rig.Flight.Telemetry.ManeuverNodes = 1; rig.MechJeb.Caps.Modules["node"] = false;
            Refused(rig.Service.Handle(rig.ExecuteNode()), "mechjeb_module_unavailable");
        }

        [TestMethod] public void AnotherEngagedControllerIsRefusedWithoutTouchingIt()
        {
            rig.MechJeb.Competitors.Add("mechjeb.landing"); rig.MechJeb.Competitors.Add("atmosphere_autopilot.Cruise");
            var response = Refused(rig.Service.Handle(rig.Ascent()), "competing_controller");
            CollectionAssert.AreEqual(new[] { "mechjeb.landing", "atmosphere_autopilot.Cruise" }, ((JArray)response.Data["competitors"]).Select(t => (string)t).ToArray());
            rig.Flight.Telemetry.ManeuverNodes = 1;
            Refused(rig.Service.Handle(rig.ExecuteNode()), "competing_controller");
            Assert.AreEqual("all", rig.MechJeb.CompetitorQueries[0], "admission scans the support modules too");
        }

        [TestMethod] public void ExecutingNeedsANodeAndAscendingNeedsToNotBeInOrbitAlready()
        {
            Refused(rig.Service.Handle(rig.ExecuteNode()), "no_maneuver_node");
            rig.Telemetry(110000, 110000, 105000, true);
            Refused(rig.Service.Handle(rig.Ascent()), "not_applicable");
        }

        // ---- idempotency and exclusivity ----

        [TestMethod] public void TheSameRequestIsIdempotentAndADifferentOneConflicts()
        {
            var first = rig.Service.Handle(rig.Ascent());
            Assert.AreEqual("running", first.Status); Assert.AreEqual(1, rig.MechJeb.Calls.Count(c => c == "engage_ascent"));
            var again = rig.Service.Handle(rig.Ascent());
            Assert.AreEqual("running", again.Status); Assert.AreEqual(1, rig.MechJeb.Calls.Count(c => c == "engage_ascent"), "the retry did not engage again");
            var changed = rig.Service.Handle(rig.Ascent(altitude: 120000));
            Assert.AreEqual("request_id_conflict", changed.ReasonCode);
        }

        [TestMethod] public void OnlyOneJobRunsAtATime()
        {
            rig.Service.Handle(rig.Ascent());
            rig.Flight.Telemetry.ManeuverNodes = 1;
            var response = rig.Service.Handle(rig.ExecuteNode());
            Assert.AreEqual("autopilot_busy", response.ReasonCode); Assert.AreEqual(true, (bool)response.Data["notDispatched"]);
            var plan = rig.Service.Handle(rig.PlanCircularize());
            Assert.AreEqual("autopilot_busy", plan.ReasonCode);
            Assert.AreEqual(0, rig.Flight.PlanCalls);
        }

        // ---- status reads ----

        [TestMethod] public void StatusOfAnUnknownJobIsJobUnknownAndOfAKnownOneItsEnvelope()
        {
            var unknown = rig.Service.Handle(new BridgeRequest { RequestId = "w", Operation = AutopilotOperations.Status, Arguments = new JObject { ["requestId"] = "ascent-0001" } });
            Assert.AreEqual("job_unknown", unknown.ReasonCode);
            rig.Service.Handle(rig.Ascent());
            var known = rig.Service.Handle(new BridgeRequest { RequestId = "w", Operation = AutopilotOperations.Status, Arguments = new JObject { ["requestId"] = "ascent-0001" } });
            Assert.AreEqual("running", known.Status); Assert.AreEqual("ascent-0001", (string)known.Data["requestId"]); Assert.AreEqual("autopilot_ascent", (string)known.Data["operation"]);
            var bad = rig.Service.Handle(new BridgeRequest { RequestId = "w", Operation = AutopilotOperations.Status, Arguments = new JObject { ["requestId"] = "x" } });
            Assert.AreEqual("invalid_argument", bad.ReasonCode);
        }

        [TestMethod] public void MechJebStatusIsReadOnlyAndNeedsNoLease()
        {
            rig = new AutopilotRig(lease: false);
            var response = rig.Service.Handle(new BridgeRequest { RequestId = "w", Operation = AutopilotOperations.MechJebStatus, Arguments = new JObject() });
            Assert.AreEqual("completed", response.Status);
            Assert.AreEqual("available", (string)response.Data["mechjeb"]); Assert.AreEqual(true, (bool)response.Data["readOnly"]); Assert.AreEqual("flight.autopilot", (string)response.Data["grantOperation"]);
            Assert.AreEqual("vessel-1", (string)response.Data["flight"]["vesselId"]); Assert.AreEqual(JTokenType.Null, response.Data["autopilotJob"].Type);
            Assert.AreEqual(0, rig.MechJeb.Calls.Count);
        }

        [TestMethod] public void AnUnknownOperationIsUnavailable()
        {
            Assert.AreEqual("operation_unavailable", rig.Service.Handle(new BridgeRequest { RequestId = "w", Operation = "flight.autopilot_fly_to_the_moon" }).ReasonCode);
        }

        // ---- plans (synchronous) ----

        [TestMethod] public void ACircularizePlanCompletesInsideAdmissionAndIsRecorded()
        {
            var response = rig.Service.Handle(rig.PlanCircularize());
            Assert.AreEqual("completed", response.Status, response.Data.ToString());
            Assert.AreEqual("stock_math", (string)response.Data["plan"]["source"]); Assert.AreEqual(88.0, (double)response.Data["plan"]["deltaVMetersPerSecond"]);
            CollectionAssert.AreEqual(new[] { "maneuver_node_created" }, ((JArray)response.Data["effects"]).Select(t => (string)t).ToArray());
            Assert.AreEqual(false, (bool)response.Data["notDispatched"]); Assert.AreEqual(1, rig.Flight.PlanCalls);
            Assert.IsFalse(rig.Runner.Busy);
            var again = rig.Service.Handle(rig.PlanCircularize());
            Assert.AreEqual("completed", again.Status); Assert.AreEqual(1, rig.Flight.PlanCalls, "a retried request does not add a second node");
            Assert.AreEqual(0, rig.MechJeb.Calls.Count, "the stock-math plans need no MechJeb");
        }

        [TestMethod] public void APlanWorksWithoutMechJebInstalled()
        {
            rig.MechJeb.Caps.Installed = false;
            Assert.AreEqual("completed", rig.Service.Handle(rig.PlanCircularize()).Status);
        }

        [TestMethod] public void AHohmannPlanPassesTheBodyAndLabelsItself()
        {
            var response = rig.Service.Handle(rig.PlanHohmann(body: "Mun"));
            Assert.AreEqual("completed", response.Status); Assert.AreEqual("Mun", rig.Flight.HohmannTarget);
            Assert.AreEqual("hohmann_phase_wait_estimate", (string)response.Data["plan"]["kind"]);
        }

        [TestMethod] public void AFailedPlanIsAFailedJobWithNoEffect()
        {
            rig.Flight.HohmannResult = PlanOutcome.Fail("target_unavailable", "Duna does not orbit Kerbin");
            var response = rig.Service.Handle(rig.PlanHohmann(body: "Duna"));
            Assert.AreEqual("failed", response.Status); Assert.AreEqual("target_unavailable", response.ReasonCode);
            Assert.AreEqual(true, (bool)response.Data["notDispatched"]); StringAssert.Contains((string)response.Data["detail"], "Duna");
        }

        [TestMethod] public void APlanWithoutAGrantFamilyOrLeaseIsRefused()
        {
            Refused(rig.Service.Handle(rig.PlanCircularize(lease: null)), "lease_required");
        }
    }

    [TestClass]
    public class AutopilotRunnerTests
    {
        private AutopilotRig rig;
        [TestInitialize] public void Setup() { rig = new AutopilotRig(); }

        private AutopilotJob StartAscent(int altitude = 100000, bool autostage = true)
        {
            var response = rig.Service.Handle(rig.Ascent(altitude: altitude, autostage: autostage));
            Assert.AreEqual("running", response.Status, response.Data.ToString());
            return rig.Runner.Current;
        }

        private AutopilotJob StartNode(bool all = false, int nodes = 1)
        {
            rig.Flight.Telemetry.ManeuverNodes = nodes; rig.Telemetry(120000, 120000, 120000, true);
            var response = rig.Service.Handle(rig.ExecuteNode(all: all));
            Assert.AreEqual("running", response.Status, response.Data.ToString());
            return rig.Runner.Current;
        }

        private void AssertReleased(bool throttleCut)
        {
            Assert.IsFalse(rig.MechJeb.AscentEnabled || rig.MechJeb.NodeEnabled, "MechJeb is disengaged");
            Assert.AreEqual(throttleCut ? 1 : 0, rig.Flight.CutCalls);
            Assert.AreEqual(throttleCut ? 1 : 0, rig.MechJeb.Calls.Count(c => c == "thrust_off"));
        }

        // ---- ascent ----

        [TestMethod] public void StartingConfiguresThenEngagesAndReportsTheEnvelope()
        {
            var job = StartAscent(120000, autostage: false);
            CollectionAssert.AreEqual(new[] { "configure:120000:0:False", "engage_ascent" }, rig.MechJeb.Calls);
            var envelope = job.ToEnvelope();
            Assert.AreEqual("autopilot_ascent", (string)envelope["operation"]); Assert.AreEqual("ascending", (string)envelope["phase"]); Assert.AreEqual(true, (bool)envelope["dispatched"]);
            Assert.AreEqual(120000, (double)envelope["configured"]["targetAltitudeMeters"]); Assert.AreEqual(false, (bool)envelope["request"]["autostage"]);
            CollectionAssert.AreEqual(new[] { "ascent_settings_written", "ascent_engaged" }, ((JArray)envelope["effects"]).Select(t => (string)t).ToArray());
            Assert.AreEqual("vessel-1", (string)envelope["vesselId"]);
        }

        [TestMethod] public void AscentProgressReportsPhaseAndTelemetryUntilOrbitThenCompletesWhenMechJebFinishes()
        {
            var job = StartAscent();
            rig.Frame(); Assert.AreEqual("launch", job.Phase);
            rig.Telemetry(20000, 40000, -500000, false); rig.Frame();
            Assert.AreEqual("ascending", job.Phase); Assert.AreEqual(40000, (double)job.ToEnvelope()["telemetry"]["apoapsisMeters"]); Assert.AreEqual("Pre-launch", job.ModuleStatus);
            rig.Telemetry(90000, 100000, 20000, false); rig.Frame();
            Assert.AreEqual("coasting_to_circularize", job.Phase);
            rig.Telemetry(100000, 100500, 71000, true); rig.Frame();
            Assert.IsFalse(job.OrbitReached, "periapsis must stay above the atmosphere for a moment first");
            rig.Frame(600); rig.Frame(600);
            Assert.IsTrue(job.OrbitReached); Assert.AreEqual("orbit_reached", job.Phase); Assert.IsFalse(job.Terminal, "MechJeb is still finishing");
            rig.MechJeb.AscentEnabled = false; rig.MechJeb.AscentOwn = false; rig.Frame();
            Assert.AreEqual("completed", job.Status); Assert.IsNull(job.ReasonCode); Assert.AreEqual(true, job.AscentFinished);
            Assert.AreEqual("done", job.Phase); Assert.IsNotNull(job.CompletedUtc);
            Assert.AreEqual(1, rig.MechJeb.Calls.Count(c => c == "disengage_ascent"), "our user is removed even after MechJeb finished");
            Assert.AreEqual(0, rig.Flight.CutCalls, "a finished ascent leaves the throttle as MechJeb set it");
        }

        [TestMethod] public void AFlickerOfPeriapsisDoesNotCountAsOrbit()
        {
            var job = StartAscent();
            rig.Telemetry(100000, 100500, 71000, true); rig.Frame(300);
            rig.Telemetry(100000, 100500, 60000, true); rig.Frame(300);
            rig.Telemetry(100000, 100500, 71000, true); rig.Frame(300); rig.Frame(300);
            Assert.IsFalse(job.OrbitReached);
            rig.Frame(800);
            Assert.IsTrue(job.OrbitReached);
        }

        [TestMethod] public void IfMechJebKeepsRefiningTheOrbitIsReportedAfterTheGraceAndMechJebIsReleased()
        {
            var job = StartAscent(); rig.Options.FinishGraceMs = 5000;
            rig.Telemetry(100000, 100500, 71000, true);
            for (var i = 0; i < 100 && !job.Terminal; i++) rig.Frame(300);
            Assert.AreEqual("completed", job.Status); Assert.AreEqual(false, job.AscentFinished); StringAssert.Contains(job.Detail, "still refining");
            AssertReleased(throttleCut: false);
            Assert.AreEqual(1, rig.MechJeb.Calls.Count(c => c == "disengage_ascent"));
        }

        [TestMethod] public void NoOrbitWithinTheTimeoutFailsAndCutsTheThrottle()
        {
            var job = StartAscent(); rig.Options.AscentTimeoutMs = 60000;
            rig.Telemetry(30000, 90000, -100000, false);
            for (var i = 0; i < 1000 && !job.Terminal; i++) rig.Frame(1000);
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("autopilot_timeout", job.ReasonCode); Assert.IsFalse(job.OrbitReached);
            AssertReleased(throttleCut: true);
        }

        [TestMethod] public void MechJebEndingWithoutAnOrbitFails()
        {
            var job = StartAscent();
            rig.Telemetry(40000, 60000, -100000, false); rig.Frame();
            rig.MechJeb.AscentEnabled = false; rig.MechJeb.AscentOwn = false; rig.Frame();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("ascent_ended_without_orbit", job.ReasonCode);
            Assert.AreEqual(true, (bool)job.ToEnvelope()["dispatched"]); Assert.AreEqual(false, (bool)job.ToEnvelope()["notDispatched"]);
            Assert.AreEqual(1, rig.Flight.CutCalls);
        }

        [TestMethod] public void AFailedConfigurationEndsTheJobWithoutEngaging()
        {
            rig.MechJeb.ConfigureFails = () => new MechJebException("engage_failed", "MechJeb did not keep the ascent settings");
            var response = rig.Service.Handle(rig.Ascent());
            Assert.AreEqual("failed", response.Status); Assert.AreEqual("engage_failed", response.ReasonCode);
            Assert.AreEqual(true, (bool)response.Data["notDispatched"], "nothing was written yet");
            CollectionAssert.AreEqual(new string[0], rig.MechJeb.Calls);
            Assert.AreEqual(0, rig.Flight.CutCalls, "the throttle was not touched for a job that never engaged");
            Assert.IsFalse(rig.Runner.Busy);
        }

        [TestMethod] public void AFailedEngageAfterConfigurationReleasesAndReportsTheEffect()
        {
            rig.MechJeb.EngageFails = () => new MechJebException("engage_failed", "the module did not enable");
            var response = rig.Service.Handle(rig.Ascent());
            Assert.AreEqual("failed", response.Status); Assert.AreEqual("engage_failed", response.ReasonCode);
            Assert.AreEqual(false, (bool)response.Data["notDispatched"]);
            CollectionAssert.Contains(((JArray)response.Data["effects"]).Select(t => (string)t).ToList(), "ascent_settings_written");
            CollectionAssert.Contains(rig.MechJeb.Calls, "disengage_ascent");
            Assert.AreEqual(1, rig.Flight.CutCalls);
        }

        // ---- Stop, revoke and loss of authority ----

        [TestMethod] public void StopDisengagesAtOnceEvenBeforeTheNextFrame()
        {
            var job = StartAscent();
            rig.Authority.Stop(); rig.Runner.Abort("stopped");
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual("stopped", job.ReasonCode);
            AssertReleased(throttleCut: true);
            rig.Frame(); Assert.AreEqual("stopped", job.ReasonCode, "a later frame changes nothing");
            Assert.AreEqual(1, rig.MechJeb.Calls.Count(c => c == "disengage_ascent"));
        }

        [TestMethod] public void StopWithoutAnExplicitAbortIsCaughtByTheNextFramesValidation()
        {
            var job = StartAscent();
            rig.Authority.Stop(); rig.Frame();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual("authority_revoked", job.ReasonCode);
            AssertReleased(throttleCut: true);
        }

        [TestMethod] public void ReleasingTheLeaseEndsTheJob()
        {
            var job = StartAscent();
            rig.Authority.ReleaseLease(rig.Lease); rig.Frame();
            Assert.AreEqual("cancelled", job.Status); AssertReleased(throttleCut: true);
        }

        [TestMethod] public void AnExpiredLeaseEndsTheJob()
        {
            var job = StartAscent();
            rig.Heartbeats = false;
            for (var i = 0; i < 50 && !job.Terminal; i++) rig.Frame(100);
            Assert.AreEqual("cancelled", job.Status); AssertReleased(throttleCut: true);
        }

        [TestMethod] public void ASceneChangeEndsTheJobWithoutNeedingMechJebAlive()
        {
            var job = StartAscent();
            rig.Context.Epoch = "epoch2"; rig.Context.Entity = "scene:SPACECENTER"; rig.Flight.InFlightValue = false;
            rig.Frame();
            Assert.AreEqual("cancelled", job.Status); Assert.IsNotNull(job.ReasonCode);
            Assert.IsFalse(rig.Authority.LeaseHeld);
            AssertReleased(throttleCut: true);
        }

        [TestMethod] public void LosingTheSceneWithTheLeaseIntactFailsAsFlightUnavailable()
        {
            var job = StartAscent();
            rig.Flight.InFlightValue = false; rig.Frame();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("flight_unavailable", job.ReasonCode);
        }

        [TestMethod] public void ASwitchOfActiveVesselEndsTheJob()
        {
            var job = StartAscent();
            rig.Flight.Telemetry.VesselId = "vessel-2"; rig.Frame();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("vessel_changed", job.ReasonCode);
            AssertReleased(throttleCut: true);
        }

        [TestMethod] public void ReleaseIsIdempotentAndATrickyReleaseFailureIsReportedNotThrown()
        {
            var job = StartAscent();
            rig.MechJeb.ReleaseThrows = true;
            rig.Runner.Abort("stopped"); rig.Runner.Abort("stopped");
            Assert.AreEqual("cancelled", job.Status);
            CollectionAssert.Contains(job.EffectsApplied, "release_failed");
            Assert.AreEqual(1, rig.Flight.CutCalls, "the throttle is cut even when the module could not be released");
        }

        [TestMethod] public void AnUnexpectedErrorWhileReadingReleasesMechJeb()
        {
            var job = StartAscent();
            rig.MechJeb.ReadFails = () => new MechJebException("mechjeb_module_unavailable", "the vessel was destroyed");
            rig.Frame();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("mechjeb_module_unavailable", job.ReasonCode);
            Assert.IsFalse(rig.MechJeb.AscentEnabled);
        }

        // ---- never fight a person ----

        [TestMethod] public void OneFrameOfHumanInputIsABounceButTwoIsATakeover()
        {
            var job = StartAscent();
            rig.Flight.Human = true; rig.Frame(); rig.Flight.Human = false; rig.Frame(); rig.Flight.Human = true; rig.Frame();
            Assert.IsFalse(job.Terminal, "non-consecutive frames reset the debounce");
            rig.Frame();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual("human_input_during_operation", job.ReasonCode);
            Assert.IsFalse(rig.Authority.LeaseHeld, "the takeover revoked the lease"); Assert.IsTrue(rig.Authority.Status().CooldownSeconds > 0);
            AssertReleased(throttleCut: true);
            // and the human's cooldown blocks the next request
            var next = rig.Service.Handle(rig.Ascent(requestId: "ascent-0002"));
            Assert.AreEqual("authority_revoked", next.ReasonCode);
        }

        [TestMethod] public void AnotherUserEnteringTheAscentModuleIsATakeover()
        {
            var job = StartAscent();
            rig.MechJeb.AscentOthers = 1; rig.Frame();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual("human_input_during_operation", job.ReasonCode); Assert.IsTrue(rig.Authority.Status().CooldownSeconds > 0);
        }

        [TestMethod] public void ACompetingControllerEngagedMidFlightIsATakeover()
        {
            var job = StartAscent();
            rig.Options.ScanEveryFrames = 2;
            rig.Frame(); Assert.IsFalse(job.Terminal);
            rig.MechJeb.Competitors.Add("atmosphere_autopilot.Fly By Wire"); rig.Frame(); rig.Frame();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual("human_input_during_operation", job.ReasonCode); StringAssert.Contains(job.Detail, "Fly By Wire");
            Assert.AreEqual("autopilots", rig.MechJeb.CompetitorQueries.Last(), "support modules are expected to be busy while MechJeb flies");
            AssertReleased(throttleCut: true);
        }

        // ---- node execution ----

        [TestMethod] public void ExecutingOneNodeEngagesAndCompletesWhenTheNodeIsConsumed()
        {
            var job = StartNode(all: false, nodes: 2);
            CollectionAssert.AreEqual(new[] { "engage_node_one" }, rig.MechJeb.Calls);
            rig.Frame(); Assert.AreEqual("warpalign", job.Phase);
            rig.MechJeb.NodeState = "BURN"; rig.Frame(); Assert.AreEqual("burn", job.Phase); Assert.AreEqual("BURN", job.ToEnvelope()["executorState"].ToString());
            rig.Flight.Telemetry.ManeuverNodes = 1; rig.MechJeb.NodeEnabled = false; rig.MechJeb.NodeOwn = false; rig.Frame();
            Assert.AreEqual("completed", job.Status, job.Detail); Assert.AreEqual(2, (int)job.ToEnvelope()["nodesAtStart"]);
            Assert.AreEqual(1, rig.MechJeb.Calls.Count(c => c == "disengage_node")); Assert.AreEqual(0, rig.Flight.CutCalls);
        }

        [TestMethod] public void ExecutingAllNodesNeedsThemAllGone()
        {
            var job = StartNode(all: true, nodes: 2);
            CollectionAssert.AreEqual(new[] { "engage_node_all" }, rig.MechJeb.Calls);
            rig.Flight.Telemetry.ManeuverNodes = 1; rig.MechJeb.NodeEnabled = false; rig.MechJeb.NodeOwn = false; rig.Frame();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("node_execution_ended_early", job.ReasonCode);
        }

        [TestMethod] public void TheExecutorStoppingWithTheNodeStillThereFails()
        {
            var job = StartNode();
            rig.MechJeb.NodeEnabled = false; rig.MechJeb.NodeOwn = false; rig.Frame();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("node_execution_ended_early", job.ReasonCode); Assert.AreEqual(1, rig.Flight.CutCalls);
        }

        [TestMethod] public void StopAbortsTheNodeExecutor()
        {
            var job = StartNode();
            rig.Authority.Stop(); rig.Runner.Abort("stopped");
            Assert.AreEqual("cancelled", job.Status);
            CollectionAssert.Contains(rig.MechJeb.Calls, "disengage_node");
            AssertReleased(throttleCut: true);
        }

        [TestMethod] public void ANodeThatNeverFinishesTimesOut()
        {
            var job = StartNode(); rig.Options.NodeTimeoutMs = 30000;
            for (var i = 0; i < 100 && !job.Terminal; i++) rig.Frame(1000);
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("autopilot_timeout", job.ReasonCode); AssertReleased(throttleCut: true);
        }

        [TestMethod] public void HumanInputDuringANodeBurnIsATakeover()
        {
            var job = StartNode();
            rig.Flight.Human = true; rig.Frame(); rig.Frame();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual("human_input_during_operation", job.ReasonCode); AssertReleased(throttleCut: true);
        }

        // ---- sequential jobs ----

        [TestMethod] public void AFinishedJobFreesTheRunnerForTheNext()
        {
            var job = StartAscent();
            rig.Runner.Abort("stopped"); rig.Authority.ReleaseLease(rig.Lease); rig.AcquireLease();
            Assert.IsFalse(rig.Runner.Busy);
            var next = rig.Service.Handle(rig.Ascent(requestId: "ascent-0002"));
            Assert.AreEqual("running", next.Status);
            Assert.AreNotSame(job, rig.Runner.Current);
        }
    }

    [TestClass]
    public class AutopilotGrantAndMathTests
    {
        private static GrantPayload Payload(params string[] operations)
        {
            return new GrantPayload
            {
                GrantId = "grant", Generation = 1, IssuedUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0), ExpiresUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0.AddHours(1)),
                Binding = new GrantBindingInfo { InstallId = "install", SaveFolder = "save", Agency = "agency" }, Operations = operations, Facilities = new[] { "VAB", "SPH" },
                UnsavedCraftPolicy = "refuse", MaxParts = 250, SpendLimitFunds = 0, Revoked = false
            };
        }

        [TestMethod] public void TheFlightFamilyIsGrantedPerSceneAndNotPerFacility()
        {
            var grant = GrantMapping.ToGrant(Payload("flight.autopilot", "editor.replace_craft"));
            Assert.IsTrue(grant.Allows(new ClassifiedEffect("flight.autopilot", "flight:vessel", 0)));
            Assert.IsFalse(grant.Allows(new ClassifiedEffect("flight.autopilot", "editor:VAB", 0)), "not a facility effect");
            Assert.IsTrue(grant.Allows(new ClassifiedEffect("editor.replace_craft", "editor:VAB", 0)));
            Assert.IsTrue(grant.AllowsEntity("scene:FLIGHT")); Assert.IsTrue(grant.AllowsEntity("editor:SPH"));
        }

        [TestMethod] public void AGrantWithoutTheFlightFamilyHasNoFlightEntityAndNoLeaseInFlight()
        {
            var grant = GrantMapping.ToGrant(Payload("editor.replace_craft"));
            Assert.IsFalse(grant.AllowsEntity("scene:FLIGHT")); Assert.IsFalse(grant.Allows(new ClassifiedEffect("flight.autopilot", "flight:vessel", 0)));
            var authority = new ExecutionAuthority(() => 1000, GrantMapping.KnownEffects, 2000, new MemorySuspensionStore(), () => AuthorityHelpers.Utc0);
            authority.UpdateContext(AuthorityHelpers.Ctx(entity: "scene:FLIGHT"), AuthorityHelpers.Bind(), AuthorityHelpers.ValidStatus());
            authority.ProvisionGrant(grant);
            Assert.AreEqual("facility_mismatch", Assert.ThrowsException<InvalidOperationException>(() => authority.AcquireLease(60000, "fly")).Message);
        }

        [TestMethod] public void CircularizingAtTheApoapsisOfASuborbitalArcMatchesVisViva()
        {
            const double mu = 3.5316e12, radius = 600000;
            var dv = ManeuverMath.CircularizeAtApoapsis(mu, radius + 100000, (radius + radius + 100000) / 2);
            Assert.AreEqual(88.1, dv, 0.5);
            Assert.AreEqual(0, ManeuverMath.CircularizeAtApoapsis(mu, 700000, 700000), 1e-9, "already circular");
            Assert.IsTrue(ManeuverMath.CircularizeAtApoapsis(mu, 1000000, 800000) > 0);
        }

        [TestMethod] public void AHohmannTransferFromLowKerbinOrbitToTheMunMatchesTheKnownNumbers()
        {
            const double mu = 3.5316e12;
            var plan = ManeuverMath.Hohmann(mu, 700000, 12000000, 0);
            Assert.AreEqual(842, plan.DepartureDeltaV, 3); Assert.AreEqual(110.7, plan.RequiredPhaseRadians * 180 / Math.PI, 0.6); Assert.AreEqual(26750, plan.TransferSeconds, 150);
            var atWindow = ManeuverMath.Hohmann(mu, 700000, 12000000, plan.RequiredPhaseRadians);
            Assert.AreEqual(0, atWindow.WaitSeconds, 1e-6, "already in the window");
            var needsLap = ManeuverMath.Hohmann(mu, 700000, 12000000, plan.RequiredPhaseRadians - 0.5);
            var w1 = Math.Sqrt(mu / Math.Pow(700000, 3)); var w2 = Math.Sqrt(mu / Math.Pow(12000000, 3));
            Assert.AreEqual((2 * Math.PI - 0.5) / (w1 - w2), needsLap.WaitSeconds, 1e-6);
            var soon = ManeuverMath.Hohmann(mu, 700000, 12000000, plan.RequiredPhaseRadians + 0.5);
            Assert.AreEqual(0.5 / (w1 - w2), soon.WaitSeconds, 1e-6);
        }

        [TestMethod] public void InvalidOrbitsAreRejected()
        {
            Assert.ThrowsException<ArgumentException>(() => ManeuverMath.Hohmann(3.5e12, 700000, 700000, 0));
            Assert.ThrowsException<ArgumentException>(() => ManeuverMath.Hohmann(0, 700000, 800000, 0));
            Assert.ThrowsException<ArgumentException>(() => ManeuverMath.CircularizeAtApoapsis(3.5e12, -1, 700000));
        }

        [TestMethod] public void ModeWrapsNegativesIntoTheCircle()
        {
            Assert.AreEqual(Math.PI * 1.5, ManeuverMath.Mod(-Math.PI / 2, 2 * Math.PI), 1e-12);
        }
    }
}
