using LmpClient.Harmony;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class UiFaultPolicyTest
    {
        [TestMethod]
        public void LogsFirstOccurrencePerKeyThenOnlyCounts()
        {
            var throttle = new UiFaultLogThrottle(60);
            Assert.IsTrue(throttle.Record("SoftMask.UpdateMaskParameters", 0));
            Assert.IsFalse(throttle.Record("SoftMask.UpdateMaskParameters", 0.1));
            Assert.IsTrue(throttle.Record("SoftMask.DestroyMaterials", 0.2));
            Assert.IsFalse(throttle.Record("SoftMask.UpdateMaskParameters", 0.3));
            Assert.AreEqual(2, throttle.TotalSuppressed);
        }

        [TestMethod]
        public void SummarisesAtMostOncePerIntervalAndOnlyWhenSomethingWasSuppressed()
        {
            var throttle = new UiFaultLogThrottle(60);
            Assert.IsNull(throttle.TakeSummary(1000), "nothing recorded yet");

            throttle.Record("B", 0);
            Assert.IsNull(throttle.TakeSummary(120), "first occurrence is logged in full, nothing pending");

            throttle.Record("B", 1);
            throttle.Record("B", 2);
            throttle.Record("A", 3);
            throttle.Record("A", 4);
            Assert.IsNull(throttle.TakeSummary(59), "interval counts from the first full log");
            Assert.AreEqual("A x1, B x2", throttle.TakeSummary(60));
            Assert.IsNull(throttle.TakeSummary(61), "pending cleared");

            throttle.Record("A", 70);
            Assert.IsNull(throttle.TakeSummary(119), "throttled until 60 s after the previous summary");
            Assert.AreEqual("A x1", throttle.TakeSummary(120));
        }

        [TestMethod]
        public void EscalatesSoftRepairThenBoundedRestartsThenDisable()
        {
            var policy = new UiFaultRepairPolicy(graceSeconds: 1, healthyAfterSeconds: 10, maxRestarts: 2);
            Assert.AreEqual(UiFaultAction.SoftRepair, policy.OnFailure(7, 0));
            Assert.AreEqual(UiFaultAction.None, policy.OnFailure(7, 0.01), "same-frame failures do not escalate");
            Assert.AreEqual(UiFaultAction.None, policy.OnFailure(7, 0.9));
            Assert.AreEqual(UiFaultAction.Restart, policy.OnFailure(7, 1.0));
            Assert.AreEqual(UiFaultAction.None, policy.OnFailure(7, 1.5));
            Assert.AreEqual(UiFaultAction.Restart, policy.OnFailure(7, 2.0));
            Assert.AreEqual(UiFaultAction.Disable, policy.OnFailure(7, 3.0));
            Assert.AreEqual(UiFaultAction.None, policy.OnFailure(7, 100), "disabled instances stay disabled");
            Assert.AreEqual(1, policy.DisabledCount);

            Assert.AreEqual(UiFaultAction.SoftRepair, policy.OnFailure(8, 3.0), "instances are tracked independently");
        }

        [TestMethod]
        public void QuietInstanceIsTreatedAsRecovered()
        {
            var policy = new UiFaultRepairPolicy(graceSeconds: 1, healthyAfterSeconds: 10, maxRestarts: 1);
            Assert.AreEqual(UiFaultAction.SoftRepair, policy.OnFailure(1, 0));
            Assert.AreEqual(UiFaultAction.Restart, policy.OnFailure(1, 2));
            Assert.AreEqual(UiFaultAction.SoftRepair, policy.OnFailure(1, 30), "10 s without failures resets escalation");
            Assert.AreEqual(UiFaultAction.Restart, policy.OnFailure(1, 32));
            Assert.AreEqual(UiFaultAction.Disable, policy.OnFailure(1, 34));

            policy.Forget(1);
            Assert.AreEqual(0, policy.DisabledCount);
            Assert.AreEqual(UiFaultAction.SoftRepair, policy.OnFailure(1, 35));
        }
    }
}
