using System.Linq;
using KspControl.Bridge;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>MechJeb waits on the pad for the first staging ("Awaiting liftoff"); with autostage the ascent job fires it once through the flight_stage path.</summary>
    [TestClass]
    public class AscentIgnitionTests
    {
        private AutopilotRig rig;
        [TestInitialize] public void Setup() { rig = new AutopilotRig(); }

        private AutopilotJob Start(bool autostage = true, JObject extra = null)
        {
            var response = rig.Service.Handle(rig.Ascent(autostage: autostage, extra: extra));
            Assert.AreEqual("running", response.Status, response.Data.ToString());
            return rig.Runner.Current;
        }

        [TestMethod] public void OnThePadWithAutostageTheFirstStageIsFiredOnceAfterTheDelay()
        {
            rig.Staging.StageParts[1] = new System.Collections.Generic.List<FlightPartAction> { FakeFlight.Act("ModuleEnginesFX", "7", onStaging: true), FakeFlight.Act("LaunchClamp", "8", onStaging: true) };
            var job = Start();
            rig.Frame(500);
            Assert.AreEqual(0, rig.Staging.Count("stage"), "MechJeb gets a moment to take the controls first");
            Assert.IsNull(job.IgnitedByBridge);
            rig.Frame(600);
            Assert.AreEqual(1, rig.Staging.Count("stage"));
            Assert.AreEqual(true, job.IgnitedByBridge);
            rig.Run(20, 100);
            Assert.AreEqual(1, rig.Staging.Count("stage"), "never a second time");
            var envelope = job.ToEnvelope();
            Assert.AreEqual(true, (bool)envelope["ignitedByBridge"]);
            Assert.AreEqual(true, (bool)envelope["request"]["ignite"]);
            Assert.AreEqual(2, (int)envelope["ignition"]["stageBefore"]); Assert.AreEqual(1, (int)envelope["ignition"]["stageAfter"]);
            CollectionAssert.Contains(((JArray)envelope["ignition"]["consequential"]).Select(t => (string)t).ToArray(), "engine:7/ModuleEnginesFX.Toggle");
            CollectionAssert.Contains(((JArray)envelope["effects"]).Select(t => (string)t).ToArray(), "first_stage_fired_by_bridge");
        }

        [TestMethod] public void TheAwaitingLiftoffStatusAloneAlsoTriggersOnTheGround()
        {
            rig.Flight.Telemetry.Situation = "LANDED"; rig.MechJeb.AscentStatusText = "Awaiting liftoff";
            var job = Start();
            rig.Run(15, 100);
            Assert.AreEqual(true, job.IgnitedByBridge);
        }

        [TestMethod] public void UnknownModulesAreReportedNotRefused()
        {
            rig.Staging.StageParts[1] = new System.Collections.Generic.List<FlightPartAction> { FakeFlight.Act("ModuleModdedIgniter", "9", onStaging: true) };
            var job = Start();
            rig.Run(15, 100);
            Assert.AreEqual(true, job.IgnitedByBridge);
            CollectionAssert.Contains(((JArray)job.Ignition["unclassified"]).Select(t => (string)t).ToArray(), "9/ModuleModdedIgniter.Toggle");
        }

        [TestMethod] public void WithoutAutostageOrWithIgniteFalseNothingIsStaged()
        {
            var job = Start(autostage: false);
            rig.Run(20, 100);
            Assert.AreEqual(0, rig.Staging.Count("stage")); Assert.IsNull(job.IgnitedByBridge);
            rig.Runner.Abort("stopped"); rig.Authority.ReleaseLease(rig.Lease); rig.AcquireLease();
            var second = rig.Service.Handle(rig.Ascent(requestId: "ascent-0002", extra: new JObject { ["ignite"] = false }));
            Assert.AreEqual("running", second.Status, second.Data.ToString());
            rig.Run(20, 100);
            Assert.AreEqual(0, rig.Staging.Count("stage"));
            Assert.AreEqual(false, (bool)rig.Runner.Current.ToEnvelope()["request"]["ignite"]);
        }

        [TestMethod] public void AVesselAlreadyFlyingIsNeverStaged()
        {
            var job = Start();
            rig.Telemetry(5000, 20000, -500000, false);
            rig.Run(20, 100);
            Assert.AreEqual(0, rig.Staging.Count("stage")); Assert.IsNull(job.IgnitedByBridge);
        }

        [TestMethod] public void AStagingLockIsRetriedAndAStageThatDoesNotAdvanceIsReported()
        {
            rig.Staging.Locked = true;
            var job = Start();
            rig.Run(15, 100);
            Assert.AreEqual(0, rig.Staging.Count("stage")); Assert.IsNull(job.IgnitedByBridge);
            StringAssert.Contains((string)job.Ignition["detail"], "staging_locked");
            rig.Staging.Locked = false; rig.Staging.StageIgnored = true;
            rig.Frame(100);
            Assert.AreEqual(1, rig.Staging.Count("stage")); Assert.AreEqual(false, job.IgnitedByBridge);
            Assert.AreEqual("the stage number did not advance", (string)job.Ignition["detail"]);
            Assert.IsFalse(job.Terminal, "the ascent keeps running: a person can still launch");
        }

        [TestMethod] public void IgniteMustBeABooleanAndChangesTheFingerprint()
        {
            var bad = rig.Service.Handle(rig.Ascent(extra: new JObject { ["ignite"] = "yes" }));
            Assert.AreEqual("invalid_argument", bad.ReasonCode);
            Assert.AreEqual("running", rig.Service.Handle(rig.Ascent()).Status);
            Assert.AreEqual("request_id_conflict", rig.Service.Handle(rig.Ascent(extra: new JObject { ["ignite"] = false })).ReasonCode);
        }
    }
}
