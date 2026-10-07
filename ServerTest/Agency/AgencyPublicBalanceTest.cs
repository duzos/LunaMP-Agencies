using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LmpCommon.Agency;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class AgencyPublicBalanceTest
    {
        [TestMethod]
        public void ConcurrentBalancePublicationCannotQueueAnOlderSnapshotLast()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (var captured = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var attempted = new ManualResetEventSlim())
            {
                var agency = AgencyStore.Agencies[fixture.Client.AgencyId];
                while (fixture.Client.SendMessageQueue.TryDequeue(out _)) { }
                var previous = AgencyNetwork.UpsertSnapshotCaptured;
                Task oldPublication = null, newPublication = null;
                var captures = 0;
                var couldOvertake = false;
                try
                {
                    AgencyNetwork.UpsertSnapshotCaptured = info =>
                    {
                        if (Interlocked.Increment(ref captures) != 1) return;
                        captured.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Publication checkpoint not released.");
                    };
                    oldPublication = Task.Run(() => AgencyNetwork.BroadcastUpsert(agency));
                    Assert.IsTrue(captured.Wait(TimeSpan.FromSeconds(10)), "Older snapshot was not captured.");
                    newPublication = Task.Run(() =>
                    {
                        // This probe deterministically establishes whether a newer writer
                        // can overtake the paused capture, without relying on sleep timing.
                        couldOvertake = Monitor.TryEnter(agency.Lock);
                        if (couldOvertake) Monitor.Exit(agency.Lock);
                        attempted.Set();
                        lock (agency.Lock) { agency.Funds = 43210; }
                        AgencyNetwork.BroadcastUpsert(agency);
                    });
                    Assert.IsTrue(attempted.Wait(TimeSpan.FromSeconds(10)), "Newer writer never attempted the lock.");
                    Assert.IsFalse(couldOvertake, "Capture and enqueue must retain the agency lock.");
                    release.Set();
                    Assert.IsTrue(Task.WaitAll(new[] { oldPublication, newPublication }, TimeSpan.FromSeconds(10)));
                    var balances = fixture.Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyUpsertMsgData>().Select(m => m.Agency.Funds).ToArray();
                    CollectionAssert.AreEqual(new[] { 50000d, 43210d }, balances);
                }
                finally
                {
                    release.Set();
                    var pending = new[] { oldPublication, newPublication }.Where(t => t != null).ToArray();
                    try { Task.WaitAll(pending, TimeSpan.FromSeconds(10)); }
                    finally { AgencyNetwork.UpsertSnapshotCaptured = previous; }
                }
            }
        }

        [TestMethod]
        public void AdminAdjustmentAndTransfersRefreshOtherAgenciesWithoutPrivateSnapshots()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var other = new Server.Agency.Agency { Id = Guid.NewGuid(), Name = "Other", Funds = 7000, Science = 50 };
                AgencyStore.Agencies[other.Id] = other;
                AgencyScenarioStore.EnsureBaselineForAgency(other.Id, other.Funds, other.Science, 0);
                AgencyEconomyStore.SetBalance(other.Id, 9000, 75);
                var observed = fixture.Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyUpsertMsgData>().Last(m => m.Agency.Id == other.Id).Agency;
                Assert.AreEqual(9000d, observed.Funds); Assert.AreEqual(75f, observed.Science);
                Assert.IsTrue(fixture.Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyEconomySnapshotMsgData>().All(m => m.Snapshot.AgencyId == fixture.Client.AgencyId), "Public refresh must not send the other agency's private economy snapshot.");
                while (fixture.Client.SendMessageQueue.TryDequeue(out _)) { }
                var transferred = AgencyEconomyStore.TransferResources(other.Id, fixture.Client.AgencyId, ResourceKind.Funds, 250, "admin", true);
                Assert.IsTrue(transferred.Success, transferred.Message);
                var updates = fixture.Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyUpsertMsgData>().Select(m => m.Agency).ToArray();
                Assert.AreEqual(8750d, updates.Last(a => a.Id == other.Id).Funds);
                Assert.AreEqual(50250d, updates.Last(a => a.Id == fixture.Client.AgencyId).Funds);
                while (fixture.Client.SendMessageQueue.TryDequeue(out _)) { }
                transferred = AgencyEconomyStore.TransferResources(other.Id, fixture.Client.AgencyId, ResourceKind.Science, 5, "admin", true);
                Assert.IsTrue(transferred.Success, transferred.Message);
                updates = fixture.Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyUpsertMsgData>().Select(m => m.Agency).ToArray();
                Assert.AreEqual(70f, updates.Last(a => a.Id == other.Id).Science);
                Assert.AreEqual(105f, updates.Last(a => a.Id == fixture.Client.AgencyId).Science);
                while (fixture.Client.SendMessageQueue.TryDequeue(out _)) { }
                AgencyEconomyStore.Broadcast();
                Assert.IsFalse(fixture.Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyUpsertMsgData>().Any(), "Unchanged balances should not generate public upserts.");
            }
        }
    }
}
