using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyMessageSerializationTest
    {
        private static readonly ClientMessageFactory Factory = new ClientMessageFactory();
        private static readonly NetClient Client = new NetClient(new NetPeerConfiguration("TESTS"));

        private static AgencyBaseMsgData RoundTripAgencyMsg(AgencyBaseMsgData msgData)
        {
            var msg = Factory.CreateNew<AgencyCliMsg>(msgData);
            var outgoing = Client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(outgoing);

            var data = outgoing.ReadBytes(outgoing.LengthBytes);
            var incoming = Client.CreateIncomingMessage(NetIncomingMessageType.Data, data);
            incoming.LengthBytes = outgoing.LengthBytes;

            msg.Recycle();

            var deserialized = Factory.Deserialize(incoming, Environment.TickCount);
            return (AgencyBaseMsgData)deserialized.Data;
        }

        [TestMethod]
        public void CreateMsg_RoundTrip()
        {
            var msgData = Factory.CreateNewMessageData<AgencyCreateMsgData>();
            msgData.Name = "Kerbin Dynamics";

            var parsed = (AgencyCreateMsgData)RoundTripAgencyMsg(msgData);
            Assert.AreEqual("Kerbin Dynamics", parsed.Name);
        }

        [TestMethod]
        public void RenameMsg_RoundTrip()
        {
            var id = Guid.NewGuid();
            var msgData = Factory.CreateNewMessageData<AgencyRenameMsgData>();
            msgData.AgencyId = id;
            msgData.NewName = "New Name";

            var parsed = (AgencyRenameMsgData)RoundTripAgencyMsg(msgData);
            Assert.AreEqual(id, parsed.AgencyId);
            Assert.AreEqual("New Name", parsed.NewName);
        }

        [TestMethod]
        public void JoinRequestMsg_RoundTrip()
        {
            var id = Guid.NewGuid();
            var msgData = Factory.CreateNewMessageData<AgencyJoinRequestMsgData>();
            msgData.AgencyId = id;

            var parsed = (AgencyJoinRequestMsgData)RoundTripAgencyMsg(msgData);
            Assert.AreEqual(id, parsed.AgencyId);
        }

        [TestMethod]
        public void AdminOpMsg_RoundTrip()
        {
            var id = Guid.NewGuid();
            var msgData = Factory.CreateNewMessageData<AgencyAdminOpMsgData>();
            msgData.AdminPassword = "s3cret";
            msgData.Op = AgencyAdminOp.SetFunds;
            msgData.TargetAgencyId = id;
            msgData.StringArg = "ignored";
            msgData.NumericArg = 123456.78;

            var parsed = (AgencyAdminOpMsgData)RoundTripAgencyMsg(msgData);
            Assert.AreEqual(AgencyAdminOp.SetFunds, parsed.Op);
            Assert.AreEqual(id, parsed.TargetAgencyId);
            Assert.AreEqual(123456.78, parsed.NumericArg, 1e-6);
        }

        [TestMethod]
        public void ChatMsgData_RoundTrips_AgencyChannel()
        {
            var msgData = Factory.CreateNewMessageData<ChatMsgData>();
            msgData.From = "Jeb";
            msgData.Text = "team rollout";
            msgData.Relay = true;
            msgData.Channel = ChatChannel.Agency;
            msgData.AgencyId = Guid.NewGuid();

            var msg = Factory.CreateNew<ChatCliMsg>(msgData);
            var outgoing = Client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(outgoing);
            var data = outgoing.ReadBytes(outgoing.LengthBytes);
            var incoming = Client.CreateIncomingMessage(NetIncomingMessageType.Data, data);
            incoming.LengthBytes = outgoing.LengthBytes;
            msg.Recycle();

            var deserialized = (ChatMsgData)Factory.Deserialize(incoming, Environment.TickCount).Data;
            Assert.AreEqual(ChatChannel.Agency, deserialized.Channel);
            Assert.AreEqual(msgData.AgencyId, deserialized.AgencyId);
            Assert.AreEqual("team rollout", deserialized.Text);
        }

        [TestMethod]
        public void ContractInfo_RoundTrips_OwningAgencyId()
        {
            var info = new ContractInfo { ContractGuid = Guid.NewGuid(), OwningAgencyId = Guid.NewGuid() };
            var outgoing = Client.CreateMessage();
            info.Serialize(outgoing);
            var data = outgoing.ReadBytes(outgoing.LengthBytes);
            var incoming = Client.CreateIncomingMessage(NetIncomingMessageType.Data, data);
            incoming.LengthBytes = outgoing.LengthBytes;

            var parsed = new ContractInfo();
            parsed.Deserialize(incoming);
            Assert.AreEqual(info.ContractGuid, parsed.ContractGuid);
            Assert.AreEqual(info.OwningAgencyId, parsed.OwningAgencyId);
        }
    }
}
