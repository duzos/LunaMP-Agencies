using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Data.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencySettingsSerializationTest
    {
        [TestMethod]
        public void LegacySettingsWithoutAgencyTail_ResetReusedFlagsToFalse()
        {
            var factory = new ServerMessageFactory();
            var peer = new NetClient(new NetPeerConfiguration("SettingsTests"));
            var settings = factory.CreateNewMessageData<SettingsReplyMsgData>();
            settings.ConsoleIdentifier = "Legacy server";
            settings.PrintMotdInChat = true;
            var outgoing = peer.CreateMessage(settings.GetMessageSize());
            settings.Serialize(outgoing);
            var incoming = peer.CreateIncomingMessage(NetIncomingMessageType.Data, outgoing.ReadBytes(outgoing.LengthBytes));
            incoming.LengthBits = outgoing.LengthBits - 5;
            var parsed = factory.CreateNewMessageData<SettingsReplyMsgData>();
            parsed.AgencyExperimentsPerAgency = true;
            parsed.AgencyKerbalsPerAgency = true;
            parsed.AgencyScansatPerAgency = true;
            parsed.AgencyContractsPoolPerAgency = true;
            parsed.AgencyCommNetPerAgency = true;
            parsed.Deserialize(incoming);
            Assert.AreEqual("Legacy server", parsed.ConsoleIdentifier);
            Assert.IsTrue(parsed.PrintMotdInChat);
            Assert.IsFalse(parsed.AgencyExperimentsPerAgency);
            Assert.IsFalse(parsed.AgencyKerbalsPerAgency);
            Assert.IsFalse(parsed.AgencyScansatPerAgency);
            Assert.IsFalse(parsed.AgencyContractsPoolPerAgency);
            Assert.IsFalse(parsed.AgencyCommNetPerAgency);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SettingsRoundTrip_PreservesAgencyFlagsAndPrecedingFields(bool enabled)
        {
            var factory = new ServerMessageFactory();
            var peer = new NetClient(new NetPeerConfiguration("SettingsTests"));
            var settings = factory.CreateNewMessageData<SettingsReplyMsgData>();
            settings.ConsoleIdentifier = "Test server";
            settings.StartingFunds = 12345;
            settings.PrintMotdInChat = true;
            settings.AgencyExperimentsPerAgency = enabled;
            settings.AgencyKerbalsPerAgency = !enabled;
            settings.AgencyScansatPerAgency = enabled;
            settings.AgencyContractsPoolPerAgency = !enabled;
            settings.AgencyCommNetPerAgency = enabled;
            var outgoing = peer.CreateMessage(settings.GetMessageSize());
            settings.Serialize(outgoing);
            Assert.IsTrue(settings.GetMessageSize() >= outgoing.LengthBytes);
            var incoming = peer.CreateIncomingMessage(NetIncomingMessageType.Data, outgoing.ReadBytes(outgoing.LengthBytes));
            incoming.LengthBits = outgoing.LengthBits;
            var parsed = factory.CreateNewMessageData<SettingsReplyMsgData>();
            parsed.Deserialize(incoming);
            Assert.AreEqual("Test server", parsed.ConsoleIdentifier);
            Assert.AreEqual(12345f, parsed.StartingFunds);
            Assert.IsTrue(parsed.PrintMotdInChat);
            Assert.AreEqual(enabled, parsed.AgencyExperimentsPerAgency);
            Assert.AreEqual(!enabled, parsed.AgencyKerbalsPerAgency);
            Assert.AreEqual(enabled, parsed.AgencyScansatPerAgency);
            Assert.AreEqual(!enabled, parsed.AgencyContractsPoolPerAgency);
            Assert.AreEqual(enabled, parsed.AgencyCommNetPerAgency);
        }
    }
}
