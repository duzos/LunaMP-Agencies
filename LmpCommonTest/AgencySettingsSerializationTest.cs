using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Data.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencySettingsSerializationTest
    {
        // Locate the first agency flag by changing only that bit. This keeps historical
        // fixtures stable when more independent flags are appended in later features.
        private static int AgencyTailStart(NetClient peer, SettingsReplyMsgData settings, NetOutgoingMessage original)
        {
            settings.AgencyExperimentsPerAgency = !settings.AgencyExperimentsPerAgency;
            var changed = peer.CreateMessage(); settings.Serialize(changed);
            settings.AgencyExperimentsPerAgency = !settings.AgencyExperimentsPerAgency;
            original.Position = changed.Position = 0;
            for (var bit = 0; bit < original.LengthBits; bit++)
            {
                if (original.ReadBoolean() == changed.ReadBoolean()) continue;
                original.Position = 0;
                return bit;
            }
            throw new global::System.InvalidOperationException("Agency flag was not serialized.");
        }

        [DataTestMethod]
        [DataRow(0)]
        [DataRow(2)]
        public void LegacySettingsWithoutAgencyTail_ResetReusedFlagsToFalse(int partialTailBits)
        {
            var factory = new ServerMessageFactory();
            var peer = new NetClient(new NetPeerConfiguration("SettingsTests"));
            var settings = factory.CreateNewMessageData<SettingsReplyMsgData>();
            settings.ConsoleIdentifier = "Legacy server";
            settings.PrintMotdInChat = true;
            settings.AgencyExperimentsPerAgency = true;
            settings.AgencyKerbalsPerAgency = true;
            var outgoing = peer.CreateMessage(settings.GetMessageSize());
            settings.Serialize(outgoing);
            var agencyTailStart = AgencyTailStart(peer, settings, outgoing);
            var incoming = peer.CreateIncomingMessage(NetIncomingMessageType.Data, outgoing.ReadBytes(outgoing.LengthBytes));
            incoming.LengthBits = agencyTailStart + partialTailBits;
            var parsed = factory.CreateNewMessageData<SettingsReplyMsgData>();
            parsed.AgencyExperimentsPerAgency = true;
            parsed.AgencyKerbalsPerAgency = true;
            parsed.AgencyScansatPerAgency = true;
            parsed.AgencyContractsPoolPerAgency = true;
            parsed.AgencyCommNetPerAgency = true;
            parsed.AgencyLaunchSitesPerAgency = true;
            parsed.AgencyVesselOwnership = true;
            parsed.Deserialize(incoming);
            Assert.AreEqual("Legacy server", parsed.ConsoleIdentifier);
            Assert.IsTrue(parsed.PrintMotdInChat);
            Assert.IsFalse(parsed.AgencyExperimentsPerAgency);
            Assert.IsFalse(parsed.AgencyKerbalsPerAgency);
            Assert.IsFalse(parsed.AgencyScansatPerAgency);
            Assert.IsFalse(parsed.AgencyContractsPoolPerAgency);
            Assert.IsFalse(parsed.AgencyCommNetPerAgency);
            Assert.IsFalse(parsed.AgencyLaunchSitesPerAgency);
            Assert.IsFalse(parsed.AgencyVesselOwnership);
        }

        [TestMethod]
        public void PreviousFiveFlagTailRetainsItsValuesWhenNewFlagIsAbsent()
        {
            var factory = new ServerMessageFactory();
            var peer = new NetClient(new NetPeerConfiguration("PreviousSettings"));
            var source = factory.CreateNewMessageData<SettingsReplyMsgData>();
            source.AgencyExperimentsPerAgency = source.AgencyKerbalsPerAgency = source.AgencyScansatPerAgency = source.AgencyContractsPoolPerAgency = source.AgencyCommNetPerAgency = true;
            source.AgencyLaunchSitesPerAgency = true;
            var outgoing = peer.CreateMessage(source.GetMessageSize());
            source.Serialize(outgoing);
            var agencyTailStart = AgencyTailStart(peer, source, outgoing);
            var incoming = peer.CreateIncomingMessage(NetIncomingMessageType.Data, outgoing.ReadBytes(outgoing.LengthBytes));
            incoming.LengthBits = agencyTailStart + 5;
            var parsed = factory.CreateNewMessageData<SettingsReplyMsgData>();
            parsed.AgencyLaunchSitesPerAgency = true;
            parsed.AgencyVesselOwnership = true;
            parsed.Deserialize(incoming);
            Assert.IsTrue(parsed.AgencyExperimentsPerAgency && parsed.AgencyKerbalsPerAgency && parsed.AgencyScansatPerAgency && parsed.AgencyContractsPoolPerAgency && parsed.AgencyCommNetPerAgency);
            Assert.IsFalse(parsed.AgencyLaunchSitesPerAgency);
            Assert.IsFalse(parsed.AgencyVesselOwnership);
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
            settings.AgencyLaunchSitesPerAgency = !enabled;
            settings.AgencyVesselOwnership = enabled;
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
            Assert.AreEqual(!enabled, parsed.AgencyLaunchSitesPerAgency);
            Assert.AreEqual(enabled, parsed.AgencyVesselOwnership);
        }
    }
}
