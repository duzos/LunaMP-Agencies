using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>flight.autopilot_recover through the real execution authority, the runner and the service.</summary>
    [TestClass]
    public class RecoveryJobTests
    {
        private AutopilotRig rig;
        [TestInitialize] public void Setup() { rig = new AutopilotRig(); rig.Flight.Telemetry.Situation = "ORBITING"; rig.Flight.Telemetry.Orbiting = true; }

        private BridgeResponse Refused(BridgeResponse response, string reason)
        {
            Assert.AreEqual("failed", response.Status, response.Data.ToString());
            Assert.AreEqual(reason, response.ReasonCode, response.Data.ToString());
            Assert.AreEqual(true, (bool)response.Data["notDispatched"]);
            Assert.IsFalse(rig.Runner.Busy);
            Assert.AreEqual(0, rig.MechJeb.Calls.Count, string.Join(",", rig.MechJeb.Calls));
            Assert.AreEqual(0, rig.Recovery.Calls.Count, string.Join(",", rig.Recovery.Calls));
            return response;
        }

        private AutopilotJob Start(JObject extra = null)
        {
            var response = rig.Service.Handle(rig.Recover(extra: extra));
            Assert.AreEqual("running", response.Status, response.Data.ToString());
            return rig.Runner.Current;
        }

        // ---- admission ----

        [TestMethod] public void ArgumentsAreBoundedAndTyped()
        {
            foreach (var extra in new[]
            {
                new JObject { ["targetPeriapsisMeters"] = -50001 }, new JObject { ["targetPeriapsisMeters"] = 60001 }, new JObject { ["targetPeriapsisMeters"] = 30000.5 },
                new JObject { ["armAltitudeMeters"] = 999 }, new JObject { ["armAltitudeMeters"] = 30001 }, new JObject { ["burnAt"] = "perigee" }, new JObject { ["burnAt"] = 1 }
            })
                Refused(rig.Service.Handle(rig.Recover(extra: extra)), "invalid_argument");
        }

        [TestMethod] public void TheLeaseAndTheFlightFamilyAreRequired()
        {
            Refused(rig.Service.Handle(rig.Recover(lease: null)), "lease_required");
            rig = new AutopilotRig(operations: new[] { FlightEffects.Family });
            Refused(rig.Service.Handle(rig.Recover()), "grant_operation_denied");
        }

        [TestMethod] public void AVesselWithoutAParachuteIsRefused()
        {
            rig.Recovery.Reading.Chutes.Clear();
            Refused(rig.Service.Handle(rig.Recover()), AutopilotReasons.NoParachute);
            rig.Recovery.Reading.Chutes.Add(new RecoveryChute { PartId = "2", State = "CUT", Safety = "NONE" });
            Refused(rig.Service.Handle(rig.Recover(requestId: "recover-0002")), AutopilotReasons.NoParachute);
        }

        [TestMethod] public void ALandedVesselOrAnAirlessBodyIsNotApplicable()
        {
            rig.Recovery.Reading.Situation = "SPLASHED";
            Refused(rig.Service.Handle(rig.Recover()), "not_applicable");
            rig.Recovery.Reading.Situation = "ORBITING"; rig.Recovery.Reading.HasAtmosphere = false; rig.Recovery.Reading.Body = "Mun";
            var response = Refused(rig.Service.Handle(rig.Recover(requestId: "recover-0002")), "not_applicable");
            StringAssert.Contains((string)response.Data["detail"], "Mun has no atmosphere");
        }

        [TestMethod] public void TheTargetMustBeUnderTheAtmosphereTop()
        {
            rig.Recovery.Reading.AtmosphereTopMeters = 50000; // a thin-atmosphere stand-in
            Refused(rig.Service.Handle(rig.Recover(extra: new JObject { ["targetPeriapsisMeters"] = 50000 })), "invalid_argument");
            Assert.AreEqual("running", rig.Service.Handle(rig.Recover(requestId: "recover-0002", extra: new JObject { ["targetPeriapsisMeters"] = 49999 })).Status);
        }

        [TestMethod] public void TheAttitudeControllerMustBeResolvedAndNoOtherControllerEngaged()
        {
            rig.MechJeb.Caps.Modules["attitudeControl"] = false;
            Refused(rig.Service.Handle(rig.Recover()), "mechjeb_module_unavailable");
            rig.MechJeb.Caps.Modules["attitudeControl"] = true; rig.MechJeb.Competitors.Add("mechjeb.attitude");
            Refused(rig.Service.Handle(rig.Recover(requestId: "recover-0002")), "competing_controller");
        }

        [TestMethod] public void TheEnvelopeCarriesTheRequestAndATopologyPreview()
        {
            rig.Recovery.Reading.Parts.RemoveAll(p => p.HeatShield);
            var job = Start(new JObject { ["burnAt"] = "apoapsis", ["armAltitudeMeters"] = 8000 });
            var envelope = job.ToEnvelope();
            Assert.AreEqual("autopilot_recover", (string)envelope["operation"]);
            Assert.AreEqual(30000, (double)envelope["request"]["targetPeriapsisMeters"]); Assert.AreEqual("apoapsis", (string)envelope["request"]["burnAt"]);
            Assert.AreEqual(8000, (double)envelope["request"]["armAltitudeMeters"]);
            var preview = envelope["preview"];
            Assert.AreEqual(true, (bool)preview["deorbitBurnNeeded"]);
            CollectionAssert.AreEqual(new[] { 1 }, ((JArray)preview["separationCandidates"]).Select(t => (int)t).ToArray());
            StringAssert.StartsWith((string)preview["separationStopsAt"], "next_stage_has_parachute:0");
            Assert.IsTrue(((JArray)preview["warnings"]).Any(w => ((string)w).StartsWith("no_heat_shield")));
            Assert.AreEqual(0, rig.MechJeb.Calls.Count, "admission touches nothing: the machine takes the controls on its first frame");
        }

        [TestMethod] public void ARetryWithTheSameIdReturnsTheSameJobAndADifferentRequestConflicts()
        {
            Start();
            Assert.AreEqual("running", rig.Service.Handle(rig.Recover()).Status);
            Assert.AreEqual("request_id_conflict", rig.Service.Handle(rig.Recover(extra: new JObject { ["targetPeriapsisMeters"] = 20000 })).ReasonCode);
        }

        // ---- running ----

        [TestMethod] public void AFullRecoveryCompletesAndReportsTheTouchdown()
        {
            var job = Start();
            rig.Frame();
            Assert.AreEqual("deorbit_align", job.Phase);
            rig.MechJeb.AttitudeAngle = 2; rig.Frame();
            Assert.AreEqual("deorbit_burn", job.Phase); Assert.AreEqual(1f, rig.Recovery.Throttle);
            rig.Recovery.Fly(80000, 25000); rig.Frame();
            rig.Run(20, 100);
            Assert.AreEqual(1, rig.Recovery.Count("stage:1"));
            rig.Recovery.Fly(60000, 25000, 2200); rig.Frame();
            Assert.AreEqual("reentry", job.Phase);
            var envelope = job.ToEnvelope();
            Assert.AreEqual("surface_retrograde", (string)envelope["recovery"]["attitude"]["held"]);
            Assert.AreEqual(60000, (double)envelope["recovery"]["telemetry"]["altitudeMeters"]);
            Assert.AreEqual("ACTIVE", (string)envelope["recovery"]["chutes"][0]["state"], "armed below the atmosphere top; stock opens it only when SAFE");
            rig.Recovery.Fly(8000, 25000, 230); rig.Frame();
            Assert.AreEqual("descent", job.Phase); Assert.AreEqual("ACTIVE", rig.Recovery.Chute().State);
            rig.Recovery.Fly(10, 25000, 7.2); rig.Frame();
            rig.Recovery.Fly(0, 25000, 0, "LANDED"); rig.Frame();

            Assert.AreEqual("completed", job.Status, job.Detail);
            Assert.AreEqual(0, rig.Flight.CutCalls, "no burn was under way at touchdown");
            envelope = job.ToEnvelope();
            Assert.AreEqual(7.2, (double)envelope["recovery"]["impactSpeedMetersPerSecond"]);
            Assert.AreEqual(1, (int)envelope["recovery"]["crewAlive"]);
            Assert.AreEqual("landed", (string)envelope["recovery"]["phase"]);
            var applied = ((JArray)envelope["effects"]).Select(t => (string)t).ToList();
            CollectionAssert.IsSubsetOf(new[] { "attitude_hold:orbit_retrograde", "throttle_engaged", "throttle_cut", "deorbit_burn_complete", "stage_fired:1", "attitude_hold:surface_retrograde", "attitude_released", "chute_armed:2", "released" }, applied);
            Assert.IsTrue(rig.MechJeb.Vessels.All(v => v == "vessel-1"), "every MechJeb call named the job's vessel");
            Assert.IsNull(rig.MechJeb.AttitudeHeld, "MechJeb is never left engaged");
        }

        [TestMethod] public void StopMidBurnCutsTheThrottleAndReleasesTheAttitude()
        {
            var job = Start();
            rig.Frame(); rig.MechJeb.AttitudeAngle = 1; rig.Frame();
            Assert.AreEqual(1f, rig.Recovery.Throttle);
            rig.Runner.Abort(AutopilotReasons.StoppedByRequest);
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual("stopped", job.ReasonCode);
            Assert.AreEqual(0f, rig.Recovery.Throttle); CollectionAssert.Contains(rig.Recovery.Calls, "throttle_release");
            Assert.IsNull(rig.MechJeb.AttitudeHeld); CollectionAssert.Contains(rig.MechJeb.Calls, "release_attitude");
        }

        [TestMethod] public void ALostLeaseEndsTheJobAndReleasesEverything()
        {
            var job = Start();
            rig.Frame(); rig.MechJeb.AttitudeAngle = 1; rig.Frame();
            rig.Authority.ReleaseLease(rig.Lease); rig.Lease = null;
            rig.Frame();
            Assert.AreEqual("cancelled", job.Status);
            Assert.AreEqual(0f, rig.Recovery.Throttle); Assert.IsNull(rig.MechJeb.AttitudeHeld);
        }

        [TestMethod] public void HumanInputIsATakeoverThatCutsTheThrottle()
        {
            var job = Start();
            rig.Frame(); rig.MechJeb.AttitudeAngle = 1; rig.Frame();
            rig.Flight.Human = true; rig.Frame(); rig.Frame();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual(OperationReasons.HumanInputDuringOperation, job.ReasonCode);
            Assert.AreEqual(0f, rig.Recovery.Throttle); Assert.IsNull(rig.MechJeb.AttitudeHeld);
            Assert.AreEqual(1, rig.Flight.CutCalls, "mid-burn the stock throttle is cut too, whatever the guard did first");
            Assert.IsFalse(rig.Authority.LeaseHeld, "a takeover revokes the lease");
        }

        [TestMethod] public void SwitchingSmartAssOffIsATakeover()
        {
            var job = Start();
            rig.Frame(); rig.MechJeb.AttitudeOwn = false; rig.Frame();
            Assert.AreEqual("cancelled", job.Status); Assert.AreEqual(OperationReasons.HumanInputDuringOperation, job.ReasonCode);
            Assert.IsFalse(rig.Authority.LeaseHeld);
        }

        [TestMethod] public void AnotherControllerEngagingIsATakeover()
        {
            var job = Start();
            rig.Frame(); rig.MechJeb.Competitors.Add("mechjeb.landing");
            rig.Run(6);
            Assert.AreEqual("cancelled", job.Status); StringAssert.Contains(job.Detail, "mechjeb.landing");
            Assert.IsTrue(rig.MechJeb.WindowFlags.All(f => !f), "the ascent window is never ours in a recovery");
        }

        [TestMethod] public void ADestroyedCapsuleIsReportedAsLost()
        {
            var job = Start();
            rig.Recovery.Fly(30000, -100000, 1500); rig.Frame();
            rig.Recovery.Gone = true; rig.Flight.Telemetry.VesselId = "debris-2";
            rig.Frame();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual(AutopilotReasons.VesselLost, job.ReasonCode);
            StringAssert.Contains(job.Detail, "30000 m");
        }

        [TestMethod] public void AVesselSwitchWhileTheCapsuleStillExistsIsVesselChanged()
        {
            var job = Start();
            rig.Frame();
            rig.Flight.Telemetry.VesselId = "vessel-2";
            rig.Frame();
            Assert.AreEqual(AutopilotReasons.VesselChanged, job.ReasonCode);
            Assert.IsNull(rig.MechJeb.AttitudeHeld);
        }

        [TestMethod] public void AFailedAttitudeEngageFailsTheJobCleanly()
        {
            rig.MechJeb.HoldFails = () => new MechJebException("mechjeb_module_unavailable", "attitudeTo missing");
            var job = Start();
            rig.Frame();
            Assert.AreEqual("failed", job.Status); Assert.AreEqual("mechjeb_module_unavailable", job.ReasonCode);
            Assert.AreEqual(0, rig.Recovery.Count("throttle:"));
        }

        // ---- every ending in the air leaves the chutes armed ----

        /// <summary>Sub-orbital at 90 km (periapsis 20 km), past separation and holding surface retrograde, chute still stowed (above the atmosphere top).</summary>
        private AutopilotJob FallingAboveTheInterface()
        {
            var job = Start();
            rig.Recovery.Fly(90000, 20000, 2400);
            rig.Run(200);
            Assert.AreEqual("reentry", job.Phase);
            Assert.AreEqual(0, rig.Recovery.Count("arm:"));
            return job;
        }

        private void ChutesArmedOnRelease(AutopilotJob job, string status, bool attitudeReleased = true)
        {
            Assert.AreEqual(status, job.Status, job.Detail);
            Assert.AreEqual(1, rig.Recovery.Count("arm:2@0"), string.Join(",", rig.Recovery.Calls));
            Assert.AreEqual("ACTIVE", rig.Recovery.Chute().State); Assert.AreEqual(0, rig.Recovery.Chute().AutomateSafeDeploy);
            CollectionAssert.Contains(job.EffectsApplied, "chutes_armed_on_release:1");
            if (attitudeReleased) Assert.IsNull(rig.MechJeb.AttitudeHeld);
        }

        [TestMethod] public void StopInTheAirArmsTheChutes()
        {
            var job = FallingAboveTheInterface();
            rig.Runner.Abort(AutopilotReasons.StoppedByRequest);
            ChutesArmedOnRelease(job, "cancelled");
        }

        [TestMethod] public void TakeoverKeysInTheAirArmTheChutes()
        {
            var job = FallingAboveTheInterface();
            rig.Flight.Human = true; rig.Run(3);
            ChutesArmedOnRelease(job, "cancelled");
            Assert.AreEqual(OperationReasons.HumanInputDuringOperation, job.ReasonCode);
        }

        [TestMethod] public void ALeaseExpiringInTheAirArmsTheChutes()
        {
            var job = FallingAboveTheInterface();
            rig.Heartbeats = false; rig.Run(400);
            ChutesArmedOnRelease(job, "cancelled");
        }

        [TestMethod] public void SmartAssSwitchedOffInTheAirArmsTheChutes()
        {
            var job = FallingAboveTheInterface();
            rig.MechJeb.AttitudeOwn = false; rig.Frame();
            ChutesArmedOnRelease(job, "cancelled", attitudeReleased: false); // the hold is already gone; nothing of ours to release
        }

        [TestMethod] public void ACompetingControllerInTheAirArmsTheChutes()
        {
            var job = FallingAboveTheInterface();
            rig.MechJeb.Competitors.Add("mechjeb.landing"); rig.Run(6);
            ChutesArmedOnRelease(job, "cancelled");
        }

        [TestMethod] public void AMechJebExceptionInTheAirArmsTheChutes()
        {
            var job = FallingAboveTheInterface();
            rig.MechJeb.ReadAttitudeFails = () => new MechJebException("mechjeb_module_unavailable", "reflection failed");
            rig.Frame();
            ChutesArmedOnRelease(job, "failed");
        }

        [TestMethod] public void TheOverallTimeoutInTheAirArmsTheChutes()
        {
            rig.Options.Recovery.TimeoutMs = 5000;
            var job = FallingAboveTheInterface();
            rig.Run(400);
            ChutesArmedOnRelease(job, "failed");
            Assert.AreEqual(AutopilotReasons.Timeout, job.ReasonCode);
        }

        [TestMethod] public void StopInAStableOrbitLeavesTheChutesStowed()
        {
            var job = Start();
            rig.Frame();
            rig.Runner.Abort(AutopilotReasons.StoppedByRequest);
            Assert.AreEqual("cancelled", job.Status);
            Assert.AreEqual(0, rig.Recovery.Count("arm:")); Assert.AreEqual("STOWED", rig.Recovery.Chute().State);
        }

        [TestMethod] public void AnotherAutopilotJobIsRefusedWhileTheRecoveryRuns()
        {
            Start();
            var response = rig.Service.Handle(rig.Ascent());
            Assert.AreEqual(AutopilotReasons.AutopilotBusy, response.ReasonCode);
        }
    }
}
