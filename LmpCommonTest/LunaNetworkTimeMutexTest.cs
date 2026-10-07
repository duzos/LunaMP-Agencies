using System;
using System.Threading;
using LmpCommon.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class LunaNetworkTimeMutexTest
    {
        [TestMethod]
        public void AbandonedMutexCountsAsAcquired()
        {
            var name = "LunaTimeMutexTest-" + Guid.NewGuid().ToString("N");
            var owner = new Thread(() => new Mutex(true, name).WaitOne());
            owner.Start(); owner.Join();
            using (var mutex = new Mutex(false, name))
            {
                Assert.IsTrue(LunaNetworkTime.TryWait(mutex));
                mutex.ReleaseMutex();
            }
        }
    }
}
