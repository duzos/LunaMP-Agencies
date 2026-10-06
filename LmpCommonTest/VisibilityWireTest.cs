using System;
using System.IO;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace LmpCommonTest
{
    [TestClass]
    public class VisibilityWireTest
    {
        private static NetIncomingMessage Incoming(NetClient peer, NetOutgoingMessage output, int? bits = null)
        {
            output.Position = 0;
            var input = peer.CreateIncomingMessage(NetIncomingMessageType.Data, output.ReadBytes(output.LengthBytes));
            input.LengthBits = bits ?? output.LengthBits;
            return input;
        }
        [TestMethod]
        public void SnapshotRoundtripPreservesDirectedRulesAndTruncationClearsPooledState()
        {
            var peer = new NetClient(new NetPeerConfiguration("visibility-wire"));
            var factory = new ServerMessageFactory();
            var endpoint = new VisibilityEndpoint { VesselId = Guid.NewGuid(), OwnerAgencyId = Guid.NewGuid(), OwnershipRevision = 19 };
            var targetAgency = Guid.NewGuid();
            var source = factory.CreateNewMessageData<AgencyVisibilitySnapshotMsgData>();
            source.Ready = true; source.Revision = 25; source.Endpoints = new[] { endpoint };
            source.AgencyGrants = new[] { new VisibilityAgencyGrant { OwnerAgencyId = endpoint.OwnerAgencyId, TargetAgencyId = targetAgency } };
            source.CraftOverrides = new[] { new VisibilityCraftOverride { Source = endpoint, TargetAgencyId = targetAgency, Rule = VisibilityOverride.Deny } };
            var bytes = peer.CreateMessage(); source.Serialize(bytes);
            Assert.IsTrue(source.GetMessageSize() >= bytes.LengthBytes);
            var result = factory.CreateNewMessageData<AgencyVisibilitySnapshotMsgData>(); result.Deserialize(Incoming(peer, bytes));
            Assert.IsTrue(result.Ready); Assert.AreEqual(25L, result.Revision);
            Assert.AreEqual(targetAgency, result.AgencyGrants[0].TargetAgencyId);
            Assert.AreEqual(19L, result.CraftOverrides[0].Source.OwnershipRevision);
            Assert.AreEqual(VisibilityOverride.Deny, result.CraftOverrides[0].Rule);
            Assert.ThrowsException<EndOfStreamException>(() => result.Deserialize(Incoming(peer, bytes, bytes.LengthBits - 1)));
            Assert.IsFalse(result.Ready); Assert.AreEqual(0, result.Endpoints.Length); Assert.AreEqual(0, result.CraftOverrides.Length);
        }
        [TestMethod]
        public void CommandAndResultRoundtripPreserveOwnershipRevisionAndRequest()
        {
            var peer = new NetClient(new NetPeerConfiguration("visibility-command"));
            var factory = new ClientMessageFactory();
            var source = factory.CreateNewMessageData<AgencyVisibilityCommandMsgData>();
            source.RequestId = Guid.NewGuid(); source.VesselId = Guid.NewGuid(); source.TargetAgencyId = Guid.NewGuid();
            source.Operation = VisibilityOperation.SetCraftOverride; source.ExpectedOwnershipRevision = 123; source.Rule = VisibilityOverride.Allow;
            var bytes = peer.CreateMessage(); source.Serialize(bytes);
            Assert.IsTrue(source.GetMessageSize() >= bytes.LengthBytes);
            var result = factory.CreateNewMessageData<AgencyVisibilityCommandMsgData>(); result.Deserialize(Incoming(peer, bytes));
            Assert.AreEqual(source.RequestId, result.RequestId); Assert.AreEqual(source.TargetAgencyId, result.TargetAgencyId);
            Assert.AreEqual(123L, result.ExpectedOwnershipRevision); Assert.AreEqual(VisibilityOverride.Allow, result.Rule);
            var server = new ServerMessageFactory(); var reply = server.CreateNewMessageData<AgencyVisibilityResultMsgData>();
            reply.RequestId = source.RequestId; reply.Reason = "Saved"; reply.Success = true;
            bytes = peer.CreateMessage(); reply.Serialize(bytes);
            var received = server.CreateNewMessageData<AgencyVisibilityResultMsgData>(); received.Deserialize(Incoming(peer, bytes));
            Assert.AreEqual(source.RequestId, received.RequestId); Assert.IsTrue(received.Success); Assert.AreEqual("Saved", received.Reason);
        }
        [TestMethod]
        public void DuplicateAndOversizedRulesRejectedBeforeWriting()
        {
            var peer = new NetClient(new NetPeerConfiguration("visibility-invalid"));
            var source = new ServerMessageFactory().CreateNewMessageData<AgencyVisibilitySnapshotMsgData>();
            source.AgencyGrants = new VisibilityAgencyGrant[VisibilityLimits.MaxAgencyGrants + 1];
            Assert.ThrowsException<InvalidDataException>(() => source.Serialize(peer.CreateMessage()));
            var grant = new VisibilityAgencyGrant { OwnerAgencyId = Guid.NewGuid(), TargetAgencyId = Guid.NewGuid() };
            source.AgencyGrants = new[] { grant, grant };
            Assert.ThrowsException<InvalidDataException>(() => source.Serialize(peer.CreateMessage()));
        }
    }
}
