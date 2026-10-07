using System;
using LmpClient.Harmony;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class CommNetDenialLogGateTest
    {
        private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

        [TestMethod]
        public void FirstDenial_Logs_ThenSuppressedWithinInterval()
        {
            var gate = new CommNetDenialLogGate(60000);
            Assert.IsTrue(gate.ShouldLog(A, B, 0));
            Assert.IsFalse(gate.ShouldLog(A, B, 1000));
            Assert.IsFalse(gate.ShouldLog(A, B, 59999));
        }

        [TestMethod]
        public void SamePairLogsAgainAfterInterval()
        {
            var gate = new CommNetDenialLogGate(60000);
            Assert.IsTrue(gate.ShouldLog(A, B, 0));
            Assert.IsTrue(gate.ShouldLog(A, B, 60000));
            Assert.IsFalse(gate.ShouldLog(A, B, 60001));
        }

        [TestMethod]
        public void PairIsUnordered_AndDistinctPairsAreIndependent()
        {
            var gate = new CommNetDenialLogGate(60000);
            Assert.IsTrue(gate.ShouldLog(A, B, 0));
            Assert.IsFalse(gate.ShouldLog(B, A, 10));
            Assert.IsTrue(gate.ShouldLog(A, C, 10));
        }
    }
}
