using System.Threading;
using LmpClient.Systems.AgenciesUpdate;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class MainThreadQueueTest
    {
        [TestMethod]
        public void Enqueue_FromWorkerThread_DoesNotRunUntilDrained_ThenRunsOnDrainingThread()
        {
            var queue = new MainThreadQueue();
            var ran = 0;
            var ranOn = -1;

            var worker = new Thread(() => queue.Enqueue(() => { ran++; ranOn = Thread.CurrentThread.ManagedThreadId; }));
            worker.Start();
            worker.Join();

            Assert.AreEqual(0, ran, "enqueueing must not run the work on the enqueuing thread");

            Assert.AreEqual(1, queue.Drain());
            Assert.AreEqual(1, ran);
            Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, ranOn, "work runs on the draining thread");
            Assert.AreEqual(0, queue.Drain(), "drained work is not run twice");
        }

        [TestMethod]
        public void Drain_ThrowingItemDoesNotStopTheRest()
        {
            var queue = new MainThreadQueue();
            var ran = 0;
            queue.Enqueue(() => throw new System.InvalidOperationException("boom"));
            queue.Enqueue(() => ran++);

            Assert.AreEqual(2, queue.Drain());
            Assert.AreEqual(1, ran);
        }
    }
}
