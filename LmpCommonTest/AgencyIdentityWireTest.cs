using System;
using System.IO;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Handshake;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyIdentityWireTest
    {
        private static NetIncomingMessage Incoming(NetClient peer, NetOutgoingMessage output, int? bits = null)
        {
            output.Position = 0; var input = peer.CreateIncomingMessage(NetIncomingMessageType.Data, output.ReadBytes(output.LengthBytes));
            input.LengthBits = bits ?? output.LengthBits; return input;
        }
        [TestMethod]
        public void CapabilityTailRoundTripsAndPooledLegacyReadsResetBothDirections()
        {
            var peer = new NetClient(new NetPeerConfiguration("identity-handshake")); var cf = new ClientMessageFactory(); var sf = new ServerMessageFactory();
            var request = cf.CreateNewMessageData<HandshakeRequestMsgData>();
            request.PlayerName = "Jeb"; request.UniqueIdentifier = "id"; request.KspVersion = "1.12"; request.AgenciesBuild = 3; request.AgencyIdentityProtocol = 1;
            var output = peer.CreateMessage(); request.Serialize(output); var parsed = cf.CreateNewMessageData<HandshakeRequestMsgData>();
            parsed.Deserialize(Incoming(peer, output)); Assert.AreEqual(1, parsed.AgencyIdentityProtocol);
            parsed.Deserialize(Incoming(peer, output, output.LengthBits - 32)); Assert.AreEqual(0, parsed.AgencyIdentityProtocol); Assert.AreEqual(3, parsed.AgenciesBuild);
            var reply = sf.CreateNewMessageData<HandshakeReplyMsgData>(); reply.Reason = "ok"; reply.ModFileData = ""; reply.ServerAgenciesBuild = 3; reply.AgencyIdentityProtocol = 1;
            output = peer.CreateMessage(); reply.Serialize(output); var received = sf.CreateNewMessageData<HandshakeReplyMsgData>();
            received.Deserialize(Incoming(peer, output)); Assert.AreEqual(1, received.AgencyIdentityProtocol);
            received.Deserialize(Incoming(peer, output, output.LengthBits - 32)); Assert.AreEqual(0, received.AgencyIdentityProtocol); Assert.AreEqual(3, received.ServerAgenciesBuild);
        }
        [TestMethod]
        public void IdentitySnapshotRoundTripsUnalignedRecords()
        {
            var peer = new NetClient(new NetPeerConfiguration("identity-wire")); var factory = new ServerMessageFactory();
            var source = factory.CreateNewMessageData<AgencyIdentitySnapshotMsgData>();
            source.Identities = new[] { new AgencyIdentityInfo { AgencyId = Guid.NewGuid(), Revision = 7, HasColour = true, Red = 255, Blue = 3 }, new AgencyIdentityInfo { AgencyId = Guid.NewGuid(), Revision = 0, FlagUrl = "Custom/Flags/foo" } };
            var output = peer.CreateMessage(); source.Serialize(output); var target = factory.CreateNewMessageData<AgencyIdentitySnapshotMsgData>(); target.Deserialize(Incoming(peer, output));
            Assert.AreEqual(2, target.Identities.Length); Assert.AreEqual(source.Identities[1].AgencyId, target.Identities[1].AgencyId);
            Assert.AreEqual((byte)255, target.Identities[0].Red); Assert.AreEqual("Custom/Flags/foo", target.Identities[1].FlagUrl);
            Assert.IsTrue(source.GetMessageSize() >= output.LengthBytes);
        }
        [TestMethod]
        public void BoundedReadsRejectHugeCountAndTextBeforeAllocation()
        {
            var peer = new NetClient(new NetPeerConfiguration("identity-bounds")); var factory = new ServerMessageFactory();
            var output = peer.CreateMessage(); output.Write(0L); output.Write((ushort)0); output.Write((ushort)0); output.Write((ushort)0); output.Write(int.MaxValue);
            var target = factory.CreateNewMessageData<AgencyIdentitySnapshotMsgData>();
            Assert.ThrowsException<InvalidDataException>(() => target.Deserialize(Incoming(peer, output)));
            Assert.AreEqual(0, target.Identities.Length);
            output = peer.CreateMessage(); output.Write(int.MaxValue);
            Assert.ThrowsException<InvalidDataException>(() => AgencyIdentityWire.ReadText(Incoming(peer, output), 256));
        }
        [TestMethod]
        public void ModFlagUrlsAreSafeAndBadOnesAreRejected()
        {
            foreach (var url in new[] { "FlagPack/Flags/United_Kingdom", "FlagPack/Flags/Zimbabwe-2", "Squad/Flags/B612_Foundation_flag", "SomeMod/Textures/Flags/a1", "Mod/FlagsOrganization/x",
                         // Real FlagPack / PlusFlags / stock agency names the stock flag browser lists.
                         "FlagPack/Flags/my flag", "FlagPack/Flags/Kerbin flag (blue)", "FlagPack/Flags/Czech Rep.", "FlagPack/Flags/Krikler7's UK flag (fixed for Wales)",
                         "FlagPack/Flags/Imperial Aquila from Warhammer 40,000", "PlusFlags/Flags/Space & Upper Atmosphere Research Commission of Pakistan",
                         "PlusFlags/Flags/National Space Agency of the Republic of Kazakhstan.pgn", "Squad/Agencies/R&D", "JSIA Vision/Flags/JSIA" })
                Assert.IsTrue(AgencyIdentityDefaults.IsSafeFlagUrl(url), url);
            foreach (var url in new[] { "", null, "FlagPack/../Flags/x", "..", "FlagPack/Flags/x.png", "FlagPack/Flags/x.DDS", "FlagPack/Flags/\u00e9", @"C:\Flags\x", "/FlagPack/Flags/x", "FlagPack/Flags/", @"FlagPack\Flags\x",
                         "FlagPack/Flags/trailing ", " FlagPack/Flags/x", "FlagPack/ /x", "FlagPack/ Flags/x", "FlagPack/./x", "A//B", "A/B=C", "A/{B}", "A/B$C", "A/B\tC", "A/B|C", "A/B?" })
                Assert.IsFalse(AgencyIdentityDefaults.IsSafeFlagUrl(url), url);
        }
        [TestMethod]
        public void OnlyServerUploadNamesAreUploadable()
        {
            Assert.IsTrue(AgencyIdentityDefaults.IsUploadableFlagName("Custom/Flags/flag_one-2"));
            foreach (var url in new[] { "FlagPack/Flags/Kerbin flag (blue)", "Squad/Agencies/R&D", "FlagPack/Flags/Czech Rep.", "../x", "", null })
                Assert.IsFalse(AgencyIdentityDefaults.IsUploadableFlagName(url), url);
        }
        [TestMethod]
        public void UnsafeFlagPathsAreRejected()
        {
            foreach (var path in new[] { "../flag", "C:/flag", "/flag", "A//B", "A/B.png", "A\\B", "A/\nB", new string('x', 257) })
                Assert.IsFalse(AgencyIdentityDefaults.IsSafeFlagUrl(path), path);
            Assert.IsTrue(AgencyIdentityDefaults.IsSafeFlagUrl("Custom/Flags/flag_one-2"));
        }
    }
}
