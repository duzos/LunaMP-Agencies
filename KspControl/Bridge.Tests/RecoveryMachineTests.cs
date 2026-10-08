using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>The recovery phase machine on its own, against the fake MechJeb and vessel ports and a hand-driven clock.</summary>
    [TestClass]
    public class RecoveryMachineTests
    {
        private long now;
        private FakeMechJebPort mechjeb;
        private FakeRecoveryPort vessel;
        private List<string> effects;
        private RecoveryMachine machine;
        private readonly object user = new object();

        [TestInitialize] public void Setup() { now = 0; mechjeb = new FakeMechJebPort(); vessel = FakeRecoveryPort.Capsule("vessel-1"); Make(); }

        private void Make(RecoveryRequest request = null)
        {
            effects = new List<string>();
            machine = new RecoveryMachine("vessel-1", user, request ?? new RecoveryRequest(), mechjeb, vessel, () => now, new RecoveryOptions(), effects);
        }

        private RecoveryOutcome Step(long milliseconds = 100) { now += milliseconds; return machine.Step(vessel.Read("vessel-1")); }
        private void Steps(int count, long milliseconds = 100) { for (var i = 0; i < count; i++) Assert.AreEqual(RecoveryVerdict.Continue, Step(milliseconds).Verdict, machine.Phase); }

        private void Burn()
        {
            Step(); Assert.AreEqual(RecoveryMachine.DeorbitAlign, machine.Phase);
            mechjeb.AttitudeAngle = 3; Step();
            Assert.AreEqual(RecoveryMachine.DeorbitBurn, machine.Phase);
        }

        // ---- the whole recovery ----

        [TestMethod] public void FromOrbitTheCrewIsBroughtHomeThroughEveryPhase()
        {
            Step();
            Assert.AreEqual(RecoveryMachine.DeorbitAlign, machine.Phase);
            CollectionAssert.Contains(mechjeb.Calls, "hold:orbit_retrograde");
            Assert.AreEqual(0, vessel.Count("throttle:"), "no thrust before the vessel points retrograde");

            mechjeb.AttitudeAngle = 4.9; Step();
            Assert.AreEqual(RecoveryMachine.DeorbitBurn, machine.Phase); Assert.AreEqual(1f, vessel.Throttle);
            vessel.Fly(80000, 50000); Step(); Assert.AreEqual(1f, vessel.Throttle);
            vessel.Fly(80000, 34000); Step(); Assert.AreEqual(0.25f, vessel.Throttle, "the burn tapers near the target");
            vessel.Fly(80000, 29500); Step();
            Assert.AreEqual(RecoveryMachine.Separation, machine.Phase); Assert.AreEqual(0f, vessel.Throttle);
            CollectionAssert.Contains(vessel.Calls, "throttle_release");

            Step();
            CollectionAssert.Contains(vessel.Calls, "stage:1", "the decoupler stage fires: it has no crew, chute or heat shield and propulsion is attached");
            Steps(20);
            Assert.AreEqual(1, vessel.Count("stage:"), "the chute stage is never staged");
            Assert.AreEqual("only_recovery_parts_remain", machine.SeparationResult);
            Assert.AreEqual(RecoveryMachine.Reentry, machine.Phase, "within 25 km of the interface surface retrograde is held");
            Assert.AreEqual("surface_retrograde", mechjeb.AttitudeHeld);

            vessel.Fly(74000, 29500); Step();
            Assert.AreEqual(RecoveryMachine.Reentry, machine.Phase); Assert.AreEqual(0, vessel.Count("arm:"), "above the atmosphere top nothing is armed");
            vessel.Fly(69000, 29500, 2300); Step();
            Assert.AreEqual(1, vessel.Count("arm:2@0"), "below the atmosphere top the chute is armed with automateSafeDeploy 0: stock holds it until SAFE");
            Assert.AreEqual("ACTIVE", vessel.Chute().State);
            Step(); CollectionAssert.Contains(effects, "chute_armed:2");
            vessel.Fly(40000, 29500, 2100); Steps(3);
            vessel.Fly(19000, 29500, 400); Step();
            Assert.AreEqual(RecoveryMachine.Descent, machine.Phase); Assert.IsNull(mechjeb.AttitudeHeld, "the capsule weathervanes on its own below 20 km");
            vessel.Fly(9500, 29500, 250); Step();
            Assert.AreEqual(1, vessel.Count("arm:"), "an armed chute is armed once");
            vessel.Chute().State = "DEPLOYED"; vessel.Fly(300, 29500, 6.8); Step();
            vessel.Fly(0, 29500, 0.4, "SPLASHED");
            var outcome = Step();

            Assert.AreEqual(RecoveryVerdict.Completed, outcome.Verdict, outcome.Detail);
            Assert.AreEqual(6.8, machine.ImpactSpeed.Value, 1e-9, "the speed of the last frame before touchdown");
            StringAssert.Contains(outcome.Detail, "splashed at 6.8 m/s with 1 of 1 crew alive");
            var d = machine.Describe();
            Assert.AreEqual("landed", (string)d["phase"]); Assert.AreEqual(6.8, (double)d["impactSpeedMetersPerSecond"]); Assert.AreEqual(1, (int)d["crewAlive"]);
            Assert.AreEqual("DEPLOYED", (string)d["chutes"][0]["state"]); Assert.AreEqual(true, (bool)d["chutes"][0]["armedByBridge"]);
            Assert.AreEqual("below_atmosphere_top", (string)d["chutes"][0]["armReason"]);
            Assert.AreEqual(69000, (double)d["chutes"][0]["armedAtHeightMeters"]);
            Assert.AreEqual(1, (int)d["chutesOpen"]); Assert.AreEqual(1, ((JArray)d["stagesFired"]).Count);
            Assert.AreEqual(1, (int)d["stagesFired"][0]["stage"]);
        }

        [TestMethod] public void APeriapsisAlreadyUnderTheTargetSkipsTheBurn()
        {
            vessel.Fly(60000, -200000, 1800);
            Step();
            Assert.IsTrue(machine.DeorbitSkipped); Assert.AreEqual(RecoveryMachine.Separation, machine.Phase);
            Steps(30);
            Assert.AreEqual(0, vessel.Count("throttle:"), "never any thrust");
            Assert.AreEqual(RecoveryMachine.Reentry, machine.Phase);
        }

        [TestMethod] public void AFreeReturnWithThePeriapsisAboveTheTargetSkipsTheBurnAndArmsTheChutes()
        {
            vessel.Fly(400000, 35000, 2500); // a Mun free return: 35 km periapsis, sub-orbital, above the default 30 km target
            Assert.AreEqual("SUB_ORBITAL", vessel.Reading.Situation);
            Step();
            Assert.IsTrue(machine.DeorbitSkipped); Assert.AreEqual(RecoveryMachine.Separation, machine.Phase);
            Steps(30);
            Assert.AreEqual(0, vessel.Count("throttle:"), "never any thrust");
            Assert.AreEqual(1, vessel.Count("stage:1"), "the service stage is still dropped");
            Assert.AreEqual(RecoveryMachine.Coast, machine.Phase);
            vessel.Fly(69000, 35000, 3100); Step();
            Assert.AreEqual(RecoveryMachine.Reentry, machine.Phase);
            Assert.AreEqual(1, vessel.Count("arm:2@0"));
        }

        [TestMethod] public void ALateStartBelowTheAtmosphereTopSkipsTheBurnEvenOnAnOrbitingReading()
        {
            vessel.Fly(65000, 45000, 2300); vessel.Reading.Orbiting = true; vessel.Reading.Situation = "ORBITING";
            Step();
            Assert.IsTrue(machine.DeorbitSkipped); Assert.AreEqual(RecoveryMachine.Separation, machine.Phase);
            Steps(30);
            Assert.AreEqual(0, vessel.Count("throttle:"));
            Assert.AreEqual(1, vessel.Count("arm:2@0"));
        }

        [TestMethod] public void BecomingSubOrbitalBeforeTheBurnGoesStraightToSeparation()
        {
            Step(); Assert.AreEqual(RecoveryMachine.DeorbitAlign, machine.Phase);
            vessel.Fly(80000, 40000);
            Step();
            Assert.AreEqual(RecoveryMachine.Separation, machine.Phase); Assert.IsTrue(machine.DeorbitSkipped);
            Assert.AreEqual(0, vessel.Count("throttle:"));
        }

        [TestMethod] public void AStartedBurnKeepsGoingToTheTargetWhenThePeriapsisCrossesTheAtmosphereTop()
        {
            Burn();
            vessel.Fly(80000, 50000); Step();
            Assert.AreEqual("SUB_ORBITAL", vessel.Reading.Situation);
            Assert.AreEqual(RecoveryMachine.DeorbitBurn, machine.Phase); Assert.AreEqual(1f, vessel.Throttle);
            vessel.Fly(80000, 29000); Step();
            Assert.AreEqual(RecoveryMachine.Separation, machine.Phase); Assert.IsFalse(machine.DeorbitSkipped);
        }

        [TestMethod] public void EnteringTheAtmosphereDuringTheBurnStopsItAndContinues()
        {
            Burn();
            vessel.Fly(69000, 50000); Step();
            Assert.AreEqual(RecoveryMachine.Separation, machine.Phase); Assert.AreEqual(0f, vessel.Throttle);
            Assert.IsTrue(machine.Warnings.Any(w => w.StartsWith("deorbit_short")));
        }

        [TestMethod] public void BurnAtApoapsisWaitsAlignedUntilJustBeforeTheApoapsis()
        {
            Make(new RecoveryRequest { BurnAtApoapsis = true });
            mechjeb.AttitudeAngle = 2;
            Step(); Step();
            Assert.AreEqual(RecoveryMachine.DeorbitWait, machine.Phase); Assert.AreEqual(0, vessel.Count("throttle:"));
            vessel.Reading.TimeToApoapsis = 25; Step(); Assert.AreEqual(RecoveryMachine.DeorbitWait, machine.Phase);
            vessel.Reading.TimeToApoapsis = 19; Step();
            Assert.AreEqual(RecoveryMachine.DeorbitBurn, machine.Phase); Assert.AreEqual(1f, vessel.Throttle);
        }

        // ---- deorbit failures ----

        [TestMethod] public void NotAligningInTimeFailsWithoutEverThrottling()
        {
            Step();
            var outcome = Step(3 * 60 * 1000 + 1);
            Assert.AreEqual(RecoveryVerdict.Failed, outcome.Verdict); Assert.AreEqual(AutopilotReasons.AttitudeNotReached, outcome.Reason);
            machine.Release();
            Assert.AreEqual(0, vessel.Count("throttle")); CollectionAssert.Contains(mechjeb.Calls, "release_attitude");
        }

        [TestMethod] public void NotRealigningInTimeWithThePeriapsisInsideTheAtmosphereContinues()
        {
            Burn();
            vessel.Fly(80000, 50000); Step();
            mechjeb.AttitudeAngle = 25; Step();
            Assert.AreEqual(RecoveryMachine.DeorbitAlign, machine.Phase);
            var outcome = Step(3 * 60 * 1000 + 1);
            Assert.AreEqual(RecoveryVerdict.Continue, outcome.Verdict, outcome.Detail);
            Assert.AreEqual(RecoveryMachine.Separation, machine.Phase); Assert.AreEqual(0f, vessel.Throttle);
            Assert.IsTrue(machine.Warnings.Any(w => w.StartsWith("deorbit_short: MechJeb did not bring")));
        }

        [TestMethod] public void ABurnTimeoutContinuesInsideTheAtmosphereAndFailsAboveIt()
        {
            foreach (var inside in new[] { true, false })
            {
                Setup(); Burn();
                var pe = inside ? 60000.0 : 78000.0;
                RecoveryOutcome outcome = null;
                for (var i = 0; i < 200 && (outcome == null || outcome.Verdict == RecoveryVerdict.Continue) && machine.Phase == RecoveryMachine.DeorbitBurn; i++)
                {
                    pe -= 10; vessel.Fly(80000, pe); outcome = Step(5000);
                }
                if (inside)
                {
                    Assert.AreEqual(RecoveryVerdict.Continue, outcome.Verdict, outcome.Detail);
                    Assert.AreEqual(RecoveryMachine.Separation, machine.Phase);
                    Assert.IsTrue(machine.Warnings.Any(w => w.StartsWith("deorbit_short: the deorbit burn took too long")));
                }
                else { Assert.AreEqual(RecoveryVerdict.Failed, outcome.Verdict); Assert.AreEqual(AutopilotReasons.Timeout, outcome.Reason); }
                Assert.AreEqual(0f, vessel.Throttle);
            }
        }

        [TestMethod] public void ABurnThatStopsLoweringThePeriapsisAboveTheAtmosphereFailsAndCutsTheThrottle()
        {
            Burn();
            vessel.Fly(80000, 75000); Step();
            var outcome = Step(10001);
            Assert.AreEqual(RecoveryVerdict.Failed, outcome.Verdict); Assert.AreEqual(AutopilotReasons.DeorbitFailed, outcome.Reason);
            StringAssert.Contains(outcome.Detail, "75000 m"); Assert.AreEqual(0f, vessel.Throttle); CollectionAssert.Contains(vessel.Calls, "throttle_release");
        }

        [TestMethod] public void ABurnThatRunsDryInsideTheAtmosphereContinuesWithAWarning()
        {
            Burn();
            vessel.Fly(80000, 50000); Step();
            Assert.AreEqual(RecoveryVerdict.Continue, Step(10001).Verdict);
            Assert.AreEqual(RecoveryMachine.Separation, machine.Phase); Assert.AreEqual(0f, vessel.Throttle);
            Assert.IsTrue(machine.Warnings.Any(w => w.StartsWith("deorbit_short")));
        }

        [TestMethod] public void DriftingOffRetrogradePausesTheBurnUntilAlignedAgain()
        {
            Burn();
            mechjeb.AttitudeAngle = 25; Step();
            Assert.AreEqual(RecoveryMachine.DeorbitAlign, machine.Phase); Assert.AreEqual(0f, vessel.Throttle);
            mechjeb.AttitudeAngle = 4; Step();
            Assert.AreEqual(RecoveryMachine.DeorbitBurn, machine.Phase); Assert.AreEqual(1f, vessel.Throttle);
        }

        [TestMethod] public void TimeWarpCutsTheThrottleAndHoldsTheBurn()
        {
            Burn();
            vessel.Reading.WarpIndex = 2; Step();
            Assert.AreEqual(0f, vessel.Throttle); Assert.AreEqual(RecoveryMachine.DeorbitAlign, machine.Phase);
            Steps(10);
            Assert.AreEqual(1, vessel.Count("throttle:1"), "nothing burns while warping");
        }

        [TestMethod] public void ARefusedThrottleFailsTheJob()
        {
            vessel.ThrottleRefused = true;
            Step(); mechjeb.AttitudeAngle = 1;
            var outcome = Step();
            Assert.AreEqual(RecoveryVerdict.Failed, outcome.Verdict); Assert.AreEqual(AutopilotReasons.ThrottleUnavailable, outcome.Reason);
        }

        // ---- takeover ----

        [TestMethod] public void RemovingOurAttitudeHoldIsATakeoverAndTheBurnStops()
        {
            Burn();
            mechjeb.AttitudeOwn = false;
            var outcome = Step();
            Assert.AreEqual(RecoveryVerdict.Takeover, outcome.Verdict); Assert.AreEqual(OperationReasons.HumanInputDuringOperation, outcome.Reason);
            Assert.AreEqual(0f, vessel.Throttle);
        }

        [TestMethod] public void AnotherAttitudeUserIsATakeover()
        {
            Step(); mechjeb.AttitudeOthers = 1;
            Assert.AreEqual(RecoveryVerdict.Takeover, Step().Verdict);
        }

        // ---- separation topology ----

        [TestMethod] public void SeparationNeverFiresAStageWithCrewChuteOrHeatShield()
        {
            vessel.Fly(75000, 20000);
            foreach (var flag in new[] { "crew", "chute", "shield" })
            {
                Setup(); vessel.Fly(75000, 20000);
                var decoupler = vessel.Reading.Parts.First(p => p.PartId == "4");
                if (flag == "crew") decoupler.Command = true; else if (flag == "chute") decoupler.Parachute = true; else decoupler.HeatShield = true;
                Steps(5);
                Assert.AreEqual(0, vessel.Count("stage:"), flag);
                Assert.IsTrue(machine.Warnings.Any(w => w.StartsWith("separation_incomplete")), flag);
                Assert.AreEqual(RecoveryMachine.Reentry, machine.Phase, flag);
            }
        }

        [TestMethod] public void AStageWhoseDecouplerWouldDropAChuteShieldOrCrewIsNotFired()
        {
            foreach (var flag in new[] { "chute", "shield", "crew" })
            {
                Setup(); vessel.Fly(75000, 20000);
                // Hung below the decoupler: firing it would take this part away with the service stage.
                var below = new RecoveryPart { PartId = "9", ParentId = "5", Name = "x", Parachute = flag == "chute", HeatShield = flag == "shield", Crewed = flag == "crew" };
                vessel.Reading.Parts.Add(below);
                Steps(5);
                Assert.AreEqual(0, vessel.Count("stage:"), flag);
                StringAssert.StartsWith(machine.SeparationResult, "stage_would_drop_", flag);
                StringAssert.EndsWith(machine.SeparationResult, ":9", flag);
            }
        }

        [TestMethod] public void AStageThatWouldIgniteAnAttachedEngineIsNotFired()
        {
            vessel.Fly(75000, 20000);
            // A radial booster on the pod, staged with the decoupler: it stays on the capsule and would light up.
            vessel.Reading.Parts.Add(new RecoveryPart { PartId = "9", ParentId = "1", Name = "sepMotor1", Propulsion = true, Engine = true, StagingOn = true, InverseStage = 1 });
            Steps(5);
            Assert.AreEqual(0, vessel.Count("stage:"));
            Assert.AreEqual(RecoverySeparation.AttachedEngine, machine.SeparationResult);
            Assert.IsTrue(machine.Warnings.Any(w => w.StartsWith("separation_skipped_engine")));
        }

        [TestMethod] public void AnEngineDroppedWithTheStageDoesNotBlockIt()
        {
            vessel.Fly(75000, 20000);
            vessel.Reading.Parts.Add(new RecoveryPart { PartId = "9", ParentId = "5", Name = "sepMotor1", Propulsion = true, Engine = true, StagingOn = true, InverseStage = 1 });
            Step();
            Assert.AreEqual(1, vessel.Count("stage:1"));
        }

        [TestMethod] public void ARootPartBelowTheDecouplerSkipsSeparation()
        {
            vessel.Reading.RootIsCommand = false; vessel.Fly(75000, 20000);
            Steps(5);
            Assert.AreEqual(0, vessel.Count("stage:"));
            Assert.AreEqual("root_part_is_not_the_command_part", machine.SeparationResult);
        }

        [TestMethod] public void AStageThatDoesNotAdvanceStopsSeparationWithAWarning()
        {
            vessel.StageIgnored = true; vessel.Fly(75000, 20000);
            Steps(30);
            Assert.AreEqual(1, vessel.Count("stage:"));
            Assert.IsTrue(machine.Warnings.Any(w => w.StartsWith("separation_stalled")));
        }

        [TestMethod] public void SeveralServiceStagesAreDroppedInTurn()
        {
            vessel.Reading.CurrentStage = 3;
            vessel.Reading.Parts.First(p => p.PartId == "6").InverseStage = 3; // lit with the booster, so stage 2 only decouples
            vessel.Reading.Parts.Add(new RecoveryPart { PartId = "7", ParentId = "6", Name = "Decoupler.1", Separator = true, StagingOn = true, InverseStage = 2 });
            vessel.Reading.Parts.Add(new RecoveryPart { PartId = "8", ParentId = "7", Name = "booster", Propulsion = true, Engine = true, StagingOn = true, InverseStage = 3 });
            vessel.Drops[2] = new List<string> { "7", "8" };
            vessel.Fly(75000, 20000);
            Steps(40);
            CollectionAssert.AreEqual(new[] { "stage:2", "stage:1" }, vessel.Calls.Where(c => c.StartsWith("stage:")).ToArray(), machine.SeparationResult);
        }

        // ---- parachutes ----

        [TestMethod] public void ASafeChuteIsArmedInsideTheAtmosphere()
        {
            vessel.Fly(30000, -100000, 600); vessel.Chute().Safety = "SAFE";
            Steps(25);
            Assert.AreEqual(1, vessel.Count("arm:2@0"));
            Assert.AreEqual("safe", (string)machine.Describe()["chutes"][0]["armReason"]);
        }

        [TestMethod] public void EveryNormalArmForcesTheChuteToWaitForSafe()
        {
            vessel.Chute().AutomateSafeDeploy = 2; // "Immediate": it would open at once even hypersonic
            vessel.Fly(60000, -100000, 2200); Steps(25);
            Assert.AreEqual(1, vessel.Count("arm:2@0"));
            Assert.AreEqual(0, vessel.Chute().AutomateSafeDeploy, "stock now opens it only when SAFE");
        }

        [TestMethod] public void AboveTheAtmosphereTopNothingIsArmed()
        {
            vessel.Fly(71000, -100000, 2300); vessel.Chute().Safety = "SAFE";
            Steps(25);
            Assert.AreEqual(0, vessel.Count("arm:"));
        }

        [TestMethod] public void BelowTwoKilometresEveryStowedChuteIsLetOpenWhateverItsSafety()
        {
            vessel.Fly(1900, -100000, 300); Steps(3);
            Assert.AreEqual(1, vessel.Count("arm:2@2"));
            Assert.AreEqual("last_resort", (string)machine.Describe()["chutes"][0]["armReason"]);
        }

        [TestMethod] public void BelowTwoKilometresAnArmedChuteStillWaitingIsLetOpenOnce()
        {
            vessel.Fly(5000, -100000, 300); Steps(25);
            Assert.AreEqual(1, vessel.Count("arm:2@0")); Assert.AreEqual("ACTIVE", vessel.Chute().State);
            vessel.Fly(1900, -100000, 280); Steps(30);
            Assert.AreEqual(1, vessel.Count("arm:2@2")); Assert.AreEqual(2, vessel.Chute().AutomateSafeDeploy);
            vessel.Chute().State = "SEMIDEPLOYED"; Steps(30);
            Assert.AreEqual(2, vessel.Count("arm:"));
        }

        [TestMethod] public void AChuteArmedByAPersonIsAlsoLetOpenBelowTwoKilometres()
        {
            vessel.Chute().State = "ACTIVE";
            vessel.Fly(1500, -100000, 250); Steps(3);
            Assert.AreEqual(1, vessel.Count("arm:2@2"));
        }

        [TestMethod] public void TheArmAltitudeNoLongerDelaysArming()
        {
            Make(new RecoveryRequest { ArmAltitudeMeters = 4000 });
            vessel.Fly(60000, -100000, 2000); Steps(25);
            Assert.AreEqual(1, vessel.Count("arm:2@0"));
        }

        [TestMethod] public void AShieldedChuteIsRetriedThenReportedAndTheLastResortKeepsTrying()
        {
            vessel.Shielded.Add("2"); vessel.Fly(5000, -100000, 300);
            Steps(100);
            Assert.AreEqual(3, vessel.Count("arm:2"));
            Assert.IsTrue(machine.Warnings.Any(w => w.StartsWith("chute_not_arming: part 2")));
            vessel.Fly(1900, -100000, 300); Steps(100);
            Assert.IsTrue(vessel.Count("arm:2@2") >= 4, "the last resort ignores the attempt limit: " + vessel.Count("arm:2@2"));
        }

        [TestMethod] public void DuringTheDeorbitBurnChutesAreLeftAlone()
        {
            vessel.Chute().Safety = "SAFE";
            Burn(); Steps(5);
            Assert.AreEqual(0, vessel.Count("arm:"));
        }

        // ---- touchdown ----

        [TestMethod] public void LosingCrewFailsTheRecovery()
        {
            vessel.Fly(500, -100000, 40); Step();
            vessel.Reading.CrewCount = 0; vessel.Fly(0, -100000, 0, "LANDED");
            var outcome = Step();
            Assert.AreEqual(RecoveryVerdict.Failed, outcome.Verdict); Assert.AreEqual(AutopilotReasons.CrewLost, outcome.Reason);
            Assert.AreEqual(40, machine.ImpactSpeed.Value, 1e-9);
        }

        [TestMethod] public void TheMachineGivesUpAfterTheOverallTimeout()
        {
            vessel.Fly(60000, -100000, 1000); Step();
            var outcome = Step(2 * 60 * 60 * 1000L + 1);
            Assert.AreEqual(AutopilotReasons.Timeout, outcome.Reason);
        }

        [TestMethod] public void ReleaseInTheAirArmsEveryStowedChuteToWaitForSafe()
        {
            vessel.Reading.Chutes.Add(new RecoveryChute { PartId = "7", Name = "parachuteSingle", State = "STOWED", Safety = "UNSAFE", AutomateSafeDeploy = 1 });
            vessel.Fly(90000, 20000, 2400); Step(); // sub-orbital, above the atmosphere top: nothing armed yet
            Assert.AreEqual(0, vessel.Count("arm:"));
            machine.Release();
            Assert.AreEqual(1, vessel.Count("arm:2@0")); Assert.AreEqual(1, vessel.Count("arm:7@0"));
            Assert.AreEqual(0, vessel.Chute("7").AutomateSafeDeploy);
            CollectionAssert.Contains(effects, "chutes_armed_on_release:2");
            machine.Release();
            Assert.AreEqual(2, vessel.Count("arm:"), "an armed chute is not armed again");
        }

        [TestMethod] public void ReleaseInAStableOrbitOnTheGroundOrWithTheVesselGoneLeavesTheChutes()
        {
            Step(); machine.Release();
            Assert.AreEqual(0, vessel.Count("arm:"), "a vessel in orbit keeps its chutes stowed");
            Setup(); vessel.Fly(500, -100000, 40); Step(); vessel.Fly(0, -100000, 0, "LANDED"); Step(); machine.Release();
            Assert.AreEqual(0, vessel.Count("arm:2@0"), "after touchdown nothing is armed");
            Setup(); vessel.Fly(90000, 20000); Step(); vessel.Gone = true; machine.Release();
            Assert.AreEqual(0, vessel.Count("arm:"));
        }

        [TestMethod] public void ReleaseIsIdempotentAndOnlyUndoesWhatWasTaken()
        {
            machine.Release();
            Assert.AreEqual(0, vessel.Count("throttle_release")); Assert.AreEqual(0, mechjeb.Calls.Count(c => c == "release_attitude"));
            Burn();
            machine.Release(); machine.Release();
            Assert.AreEqual(1, vessel.Count("throttle_release")); Assert.AreEqual(1, mechjeb.Calls.Count(c => c == "release_attitude"));
        }
    }
}
