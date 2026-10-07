using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Handshake;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgenciesHandshakeWireTest
    {
        private static NetIncomingMessage Incoming(NetClient peer, NetOutgoingMessage output, int? bits = null)
        {
            output.Position = 0;
            var input = peer.CreateIncomingMessage(NetIncomingMessageType.Data, output.ReadBytes(output.LengthBytes));
            input.LengthBits = bits ?? output.LengthBits;
            return input;
        }

        private static HandshakeRequestMsgData Request(ClientMessageFactory factory, int build)
        {
            var data = factory.CreateNewMessageData<HandshakeRequestMsgData>();
            data.PlayerName = "Jeb";
            data.UniqueIdentifier = "unique-id";
            data.KspVersion = "1.12.5";
            data.AgenciesBuild = build;
            return data;
        }

        private static HandshakeReplyMsgData Reply(ServerMessageFactory factory, HandshakeReply response, int build)
        {
            var data = factory.CreateNewMessageData<HandshakeReplyMsgData>();
            data.Response = response;
            data.Reason = "why";
            data.ModControl = true;
            data.ServerStartTime = 123456789L;
            data.ModFileData = "mod-file";
            data.ServerAgenciesBuild = build;
            return data;
        }

        [TestMethod]
        public void MismatchReplyValueIsAppendedAfterTheExistingOnes()
        {
            Assert.AreEqual(0, (int)HandshakeReply.HandshookSuccessfully);
            Assert.AreEqual(1, (int)HandshakeReply.PlayerBanned);
            Assert.AreEqual(2, (int)HandshakeReply.ServerFull);
            Assert.AreEqual(3, (int)HandshakeReply.InvalidPlayername);
            Assert.AreEqual(4, (int)HandshakeReply.AgenciesBuildMismatch);
        }

        [TestMethod]
        public void RequestRoundTripsTheAgenciesBuild()
        {
            var peer = new NetClient(new NetPeerConfiguration("handshake-request-wire"));
            var factory = new ClientMessageFactory();
            var source = Request(factory, 7);
            var output = peer.CreateMessage();
            source.Serialize(output);

            var parsed = factory.CreateNewMessageData<HandshakeRequestMsgData>();
            parsed.Deserialize(Incoming(peer, output));

            Assert.AreEqual(7, parsed.AgenciesBuild);
            Assert.AreEqual("Jeb", parsed.PlayerName);
            Assert.AreEqual("unique-id", parsed.UniqueIdentifier);
            Assert.AreEqual("1.12.5", parsed.KspVersion);
            Assert.IsTrue(source.GetMessageSize() >= output.LengthBytes, "the declared size must cover the new field");
        }

        [TestMethod]
        public void PooledRequestReadingAnOldFormatPacketResetsTheBuildToZero()
        {
            var peer = new NetClient(new NetPeerConfiguration("handshake-request-legacy"));
            var factory = new ClientMessageFactory();
            var source = Request(factory, 2);
            var current = peer.CreateMessage();
            source.Serialize(current);

            // The old format is exactly the new one without the trailing int.
            var legacyBits = current.LengthBits - 32;

            var pooled = factory.CreateNewMessageData<HandshakeRequestMsgData>();
            pooled.Deserialize(Incoming(peer, current));
            Assert.AreEqual(2, pooled.AgenciesBuild);

            pooled.Deserialize(Incoming(peer, current, legacyBits));
            Assert.AreEqual(0, pooled.AgenciesBuild, "a recycled instance must not keep the previous build");
            Assert.AreEqual("Jeb", pooled.PlayerName);
            Assert.AreEqual("1.12.5", pooled.KspVersion);
        }

        [TestMethod]
        public void RequestWithAPartialBuildTailReadsZeroInsteadOfThrowing()
        {
            var peer = new NetClient(new NetPeerConfiguration("handshake-request-partial"));
            var factory = new ClientMessageFactory();
            var source = Request(factory, 9);
            var output = peer.CreateMessage();
            source.Serialize(output);

            var parsed = factory.CreateNewMessageData<HandshakeRequestMsgData>();
            parsed.Deserialize(Incoming(peer, output));
            Assert.AreEqual(9, parsed.AgenciesBuild);

            parsed.Deserialize(Incoming(peer, output, output.LengthBits - 16));
            Assert.AreEqual(0, parsed.AgenciesBuild);
        }

        [TestMethod]
        public void ReplyRoundTripsTheServerAgenciesBuildAndMismatchResponse()
        {
            var peer = new NetClient(new NetPeerConfiguration("handshake-reply-wire"));
            var factory = new ServerMessageFactory();
            var source = Reply(factory, HandshakeReply.AgenciesBuildMismatch, 3);
            var output = peer.CreateMessage();
            source.Serialize(output);

            var parsed = factory.CreateNewMessageData<HandshakeReplyMsgData>();
            parsed.Deserialize(Incoming(peer, output));

            Assert.AreEqual(HandshakeReply.AgenciesBuildMismatch, parsed.Response);
            Assert.AreEqual("why", parsed.Reason);
            Assert.IsTrue(parsed.ModControl);
            Assert.AreEqual(123456789L, parsed.ServerStartTime);
            Assert.AreEqual("mod-file", parsed.ModFileData);
            Assert.AreEqual(3, parsed.ServerAgenciesBuild);
            Assert.IsTrue(source.GetMessageSize() >= output.LengthBytes, "the declared size must cover the new field");
        }

        [TestMethod]
        public void PooledReplyReadingAnOldFormatPacketResetsTheServerBuildToZero()
        {
            var peer = new NetClient(new NetPeerConfiguration("handshake-reply-legacy"));
            var factory = new ServerMessageFactory();
            var source = Reply(factory, HandshakeReply.HandshookSuccessfully, AgenciesBuild.Number);
            var current = peer.CreateMessage();
            source.Serialize(current);
            var legacyBits = current.LengthBits - 32;

            var pooled = factory.CreateNewMessageData<HandshakeReplyMsgData>();
            pooled.Deserialize(Incoming(peer, current));
            Assert.AreEqual(AgenciesBuild.Number, pooled.ServerAgenciesBuild);

            pooled.Deserialize(Incoming(peer, current, legacyBits));
            Assert.AreEqual(0, pooled.ServerAgenciesBuild, "a recycled instance must not keep the previous server build");
            Assert.AreEqual("mod-file", pooled.ModFileData);
            Assert.AreEqual(HandshakeReply.HandshookSuccessfully, pooled.Response);
        }

        [TestMethod]
        public void ReplyWithAPartialServerBuildTailReadsZeroInsteadOfThrowing()
        {
            var peer = new NetClient(new NetPeerConfiguration("handshake-reply-partial"));
            var factory = new ServerMessageFactory();
            var source = Reply(factory, HandshakeReply.HandshookSuccessfully, 5);
            var output = peer.CreateMessage();
            source.Serialize(output);

            var parsed = factory.CreateNewMessageData<HandshakeReplyMsgData>();
            parsed.Deserialize(Incoming(peer, output));
            Assert.AreEqual(5, parsed.ServerAgenciesBuild);

            parsed.Deserialize(Incoming(peer, output, output.LengthBits - 16));
            Assert.AreEqual(0, parsed.ServerAgenciesBuild);
        }
    }
}
