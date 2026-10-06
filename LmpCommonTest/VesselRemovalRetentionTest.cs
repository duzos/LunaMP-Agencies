using System;
using System.Collections.Generic;
using LmpClient.Systems.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass, DoNotParallelize]
    public class VesselRemovalRetentionTest
    {
        private readonly ClientMessageFactory factory = new ClientMessageFactory();
        private readonly Queue<IMessageBase> outgoing = new Queue<IMessageBase>();
        [TestInitialize] public void Reset() { VesselPublicationGuard.ResetSession(); outgoing.Clear(); }
        private void Retain(Guid id, bool permanent = false)
        {
            Assert.IsTrue(VesselPublicationGuard.RetainRemoval(id, permanent, permanent ? "Destroyed" : "Removed", 10, out var overflow));
            Assert.IsFalse(overflow);
        }
        private void Pump() => VesselPublicationGuard.PumpRemovals(
            () => factory.CreateNew<VesselCliMsg>(factory.CreateNewMessageData<VesselRemoveMsgData>()),
            (message, epoch) => { VesselPublicationGuard.Stamp(message, epoch); outgoing.Enqueue(message); });
        private static bool Send(IMessageBase message)
        {
            using (VesselPublicationGuard.EnterSend(message, out var allowed)) return allowed;
        }
        [TestMethod]
        public void RemovalsWaitForTheEntireSplitChainAndCoalescePermanentFlag()
        {
            var parent = Guid.NewGuid(); var child = Guid.NewGuid();
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            Assert.IsTrue(VesselPublicationGuard.Begin(first, parent, child));
            Retain(parent); Retain(parent, true); Pump(); Assert.AreEqual(0, outgoing.Count);
            Assert.IsTrue(VesselPublicationGuard.AdvanceSplit(first, second, child, Guid.NewGuid()));
            VesselPublicationGuard.Complete(first); Pump(); Assert.AreEqual(0, outgoing.Count);
            VesselPublicationGuard.Complete(second); Pump(); Assert.AreEqual(1, outgoing.Count);
            var message = outgoing.Dequeue(); Assert.IsTrue(Send(message));
            Assert.IsTrue(((VesselRemoveMsgData)message.Data).AddToKillList);
            Assert.AreEqual(parent, ((VesselRemoveMsgData)message.Data).VesselId);
            Pump(); Assert.AreEqual(0, outgoing.Count);
        }
        [TestMethod]
        public void SplitStartingAfterEnqueueRetainsRemovalUntilRetry()
        {
            Retain(Guid.NewGuid()); Pump(); var stale = outgoing.Dequeue();
            var operation = Guid.NewGuid(); Assert.IsTrue(VesselPublicationGuard.Begin(operation, Guid.NewGuid(), Guid.NewGuid()));
            Assert.IsFalse(Send(stale)); Pump(); Assert.AreEqual(0, outgoing.Count);
            VesselPublicationGuard.Complete(operation); Pump(); Assert.AreEqual(1, outgoing.Count);
            Assert.IsTrue(Send(outgoing.Dequeue())); Pump(); Assert.AreEqual(0, outgoing.Count);
        }
        [TestMethod]
        public void CompletedSplitBetweenEnqueueAndSendDoesNotLoseRemoval()
        {
            Retain(Guid.NewGuid()); Pump(); var message = outgoing.Dequeue();
            var operation = Guid.NewGuid(); Assert.IsTrue(VesselPublicationGuard.Begin(operation, Guid.NewGuid(), Guid.NewGuid()));
            VesselPublicationGuard.Complete(operation); Assert.IsTrue(Send(message));
            Pump(); Assert.AreEqual(0, outgoing.Count);
        }
        [TestMethod]
        public void InFlightCoalescingUpdatesPermanentFlagAtActualSend()
        {
            var vessel = Guid.NewGuid(); Retain(vessel); Pump(); Retain(vessel, true); Pump();
            Assert.AreEqual(1, outgoing.Count); var message = outgoing.Dequeue(); Assert.IsTrue(Send(message));
            Assert.IsTrue(((VesselRemoveMsgData)message.Data).AddToKillList);
            Assert.AreEqual("Destroyed", ((VesselRemoveMsgData)message.Data).Reason);
        }
        [TestMethod]
        public void ResetDiscardsRetainedAndInFlightMessages()
        {
            var vessel = Guid.NewGuid(); Retain(vessel); Pump(); var previous = outgoing.Dequeue();
            VesselPublicationGuard.ResetSession(); Retain(vessel, true); Pump();
            Assert.IsFalse(Send(previous)); Assert.IsTrue(Send(outgoing.Dequeue()));
            Pump(); Assert.AreEqual(0, outgoing.Count);
        }
        [TestMethod]
        public void RejectionAndNonSplitTransactionsNeverPublishRemovals()
        {
            Retain(Guid.NewGuid()); Pump(); var message = outgoing.Dequeue();
            VesselPublicationGuard.Reject(); Assert.IsFalse(Send(message)); Pump(); Assert.AreEqual(0, outgoing.Count);
            Assert.IsFalse(VesselPublicationGuard.RetainRemoval(Guid.NewGuid(), true, "Destroyed", 10, out var overflow));
            Assert.IsFalse(overflow);
        }
        [TestMethod]
        public void OverflowIsExplicitAndDoesNotEvictExistingRecords()
        {
            for (var i = 0; i < VesselPublicationGuard.MaxRetainedRemovals; i++) Retain(Guid.NewGuid());
            Assert.IsFalse(VesselPublicationGuard.RetainRemoval(Guid.NewGuid(), true, "Destroyed", 10, out var overflow));
            Assert.IsTrue(overflow); Pump(); Assert.AreEqual(VesselPublicationGuard.MaxRetainedRemovals, outgoing.Count);
        }
    }
}
