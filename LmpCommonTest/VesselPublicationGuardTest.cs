using System;
using LmpClient.Systems.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass, DoNotParallelize]
    public class VesselPublicationGuardTest
    {
        [TestInitialize] public void Reset() => VesselPublicationGuard.ResetSession();
        private static IMessageBase Message(Guid? operation = null)
        {
            var factory = new ClientMessageFactory();
            if (operation.HasValue)
            {
                var data = factory.CreateNewMessageData<VesselCoupleMsgData>(); data.OperationId = operation.Value;
                return factory.CreateNew<VesselCliMsg>(data);
            }
            return factory.CreateNew<VesselCliMsg>(factory.CreateNewMessageData<VesselProtoMsgData>());
        }
        private static bool CanSend(IMessageBase message, long epoch)
        {
            VesselPublicationGuard.Stamp(message, epoch);
            using (VesselPublicationGuard.EnterSend(message, out var allowed)) return allowed;
        }
        [TestMethod]
        public void PendingAllowsOnlyItsOwnCouple()
        {
            var operation = Guid.NewGuid(); Assert.IsTrue(VesselPublicationGuard.Begin(operation));
            var epoch = VesselPublicationGuard.CaptureEpoch();
            Assert.IsFalse(CanSend(Message(), epoch));
            Assert.IsFalse(CanSend(Message(Guid.NewGuid()), epoch));
            Assert.IsTrue(CanSend(Message(operation), epoch));
        }
        [TestMethod]
        public void SplitQueueAdvancesWithoutPublishingIntermediateOrdinaryUpdates()
        {
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            var parent = Guid.NewGuid(); var child = Guid.NewGuid(); var nextChild = Guid.NewGuid();
            var factory = new ClientMessageFactory();
            Func<Guid, Guid, IMessageBase> proto = (id, operation) => {
                var data = factory.CreateNewMessageData<VesselProtoMsgData>();
                data.VesselId = id; data.EconomySplitOperationId = operation;
                return factory.CreateNew<VesselCliMsg>(data);
            };
            Assert.IsTrue(VesselPublicationGuard.Begin(first, parent, child));
            var epoch = VesselPublicationGuard.CaptureEpoch();
            Assert.IsTrue(CanSend(proto(child, first), epoch));
            Assert.IsFalse(CanSend(proto(nextChild, second), epoch));
            Assert.IsTrue(VesselPublicationGuard.AdvanceSplit(first, second, parent, nextChild));
            Assert.IsTrue(VesselPublicationGuard.Pending);
            VesselPublicationGuard.Complete(first);
            Assert.IsTrue(VesselPublicationGuard.Pending);
            Assert.IsFalse(CanSend(Message(), VesselPublicationGuard.CaptureEpoch()));
            Assert.IsFalse(CanSend(proto(child, first), epoch));
            Assert.IsTrue(CanSend(proto(nextChild, second), VesselPublicationGuard.CaptureEpoch()));
            VesselPublicationGuard.Complete(second);
            Assert.IsTrue(CanSend(Message(), VesselPublicationGuard.CaptureEpoch()));
        }

        [TestMethod]
        public void AckNeverReleasesOldSerializationJobs()
        {
            var before = VesselPublicationGuard.CaptureEpoch();
            var operation = Guid.NewGuid(); VesselPublicationGuard.Begin(operation);
            var during = VesselPublicationGuard.CaptureEpoch();
            VesselPublicationGuard.Complete(operation);
            Assert.IsFalse(CanSend(Message(), before)); Assert.IsFalse(CanSend(Message(), during));
            Assert.IsTrue(CanSend(Message(), VesselPublicationGuard.CaptureEpoch()));
        }
        [TestMethod]
        public void RejectStaysBlockedAndReconnectInvalidatesOldJobs()
        {
            var operation = Guid.NewGuid(); VesselPublicationGuard.Begin(operation); VesselPublicationGuard.Reject();
            var rejectedEpoch = VesselPublicationGuard.CaptureEpoch();
            Assert.IsFalse(CanSend(Message(operation), rejectedEpoch));
            VesselPublicationGuard.Complete(operation);
            Assert.IsTrue(VesselPublicationGuard.Pending);
            VesselPublicationGuard.ResetSession();
            Assert.IsFalse(CanSend(Message(), rejectedEpoch));
            Assert.IsTrue(CanSend(Message(), VesselPublicationGuard.CaptureEpoch()));
        }
    }
}
