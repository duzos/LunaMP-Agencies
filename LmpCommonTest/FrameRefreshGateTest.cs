using LmpClient.Windows.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    /// <summary>The linked production FrameRefreshGate behind StockUi and the Designs rows (plan 40 review MED2).</summary>
    [TestClass]
    public class FrameRefreshGateTest
    {
        [TestMethod]
        public void RefreshesOnlyOnLayout_AtMostOncePerFrame()
        {
            var gate = new FrameRefreshGate(0.5f);
            Assert.IsFalse(gate.TryBegin(1, false, 0f), "Never outside a Layout event.");
            Assert.IsTrue(gate.TryBegin(1, true, 0f), "The first Layout of a frame rebuilds.");
            Assert.IsFalse(gate.TryBegin(1, true, 10f), "A second Layout in the same frame (another window or the Repaint's Layout) sees the same data.");
            gate.Invalidate();
            Assert.IsFalse(gate.TryBegin(1, true, 10f), "Even invalidated, never twice in one frame.");
            Assert.IsTrue(gate.TryBegin(2, true, 10f), "The invalidation applies on the next frame.");
        }

        [TestMethod]
        public void RefreshesWhenTheIntervalPassedOrInvalidated()
        {
            var gate = new FrameRefreshGate(0.5f);
            Assert.IsTrue(gate.TryBegin(1, true, 0f));
            Assert.IsFalse(gate.TryBegin(2, true, 0.4f), "Not due yet.");
            Assert.IsTrue(gate.TryBegin(3, true, 0.5f), "Due after the interval.");
            gate.Invalidate();
            Assert.IsTrue(gate.TryBegin(4, true, 0.6f), "Invalidated: due at once.");
            Assert.IsFalse(gate.TryBegin(5, true, 0.7f), "The invalidation was consumed.");
        }
    }
}
