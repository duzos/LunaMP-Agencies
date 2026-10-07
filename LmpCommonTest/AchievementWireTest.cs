using System;
using System.IO;
using System.Linq;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.ShareProgress;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace LmpCommonTest
{
    [TestClass]
    public class AchievementWireTest
    {
        private static NetIncomingMessage Incoming(NetClient peer, NetOutgoingMessage output)
        {
            output.Position = 0;
            var input = peer.CreateIncomingMessage(NetIncomingMessageType.Data, output.ReadBytes(output.LengthBytes));
            input.LengthBits = output.LengthBits; return input;
        }
        [TestMethod]
        public void MultipleAgenciesKeepUnicodeDetailsBoundariesAndSize()
        {
            var peer = new NetClient(new NetPeerConfiguration("firsts")); var factory = new ServerMessageFactory();
            var message = factory.CreateNewMessageData<AgencySyncAllMsgData>();
            message.Agencies = Enumerable.Range(0, 3).Select(i => new AgencyInfo { Id = Guid.NewGuid(), Name = "\u754c" + i, Funds = 123 + i, FirstAchievements = new[] {
                new FirstAchievement { Key = "Orbit:Mun", UtcTicks = 1234, Details = new AchievementDetails { UniversalTime = 42, VesselName = new string('\u754c', 256), CrewNames = new[] { "Val", "\u754c" } } },
                new FirstAchievement { Key = "Legacy", UtcTicks = 4567 }
            } }).ToArray();
            var output = peer.CreateMessage(); message.Serialize(output); Assert.IsTrue(message.GetMessageSize() >= output.LengthBytes);
            var read = factory.CreateNewMessageData<AgencySyncAllMsgData>(); read.Deserialize(Incoming(peer, output));
            Assert.AreEqual(3, read.Agencies.Length); Assert.AreEqual(125d, read.Agencies[2].Funds);
            Assert.AreEqual("\u754c", read.Agencies[1].FirstAchievements[0].Details.CrewNames[1]);
            Assert.IsNull(read.Agencies[1].FirstAchievements[1].Details);
            var upsert = factory.CreateNewMessageData<AgencyUpsertMsgData>(); upsert.Agency = message.Agencies[0];
            output = peer.CreateMessage(); upsert.Serialize(output); Assert.IsTrue(upsert.GetMessageSize() >= output.LengthBytes);
        }
        [TestMethod]
        public void PooledMessageReuseClearsDetailsAndPreservesKnownUncrewed()
        {
            var peer = new NetClient(new NetPeerConfiguration("first-message")); var factory = new ClientMessageFactory();
            var message = factory.CreateNewMessageData<ShareProgressAchievementsMsgData>(); message.Id = "Mun";
            message.Details = new AchievementDetails { UniversalTime = 0, CrewNames = Array.Empty<string>() };
            var read = factory.CreateNewMessageData<ShareProgressAchievementsMsgData>();
            var output = peer.CreateMessage(); message.Serialize(output); Assert.IsTrue(message.GetMessageSize() >= output.LengthBytes);
            read.Deserialize(Incoming(peer, output)); Assert.AreEqual(0, read.Details.CrewNames.Length);
            message.Details = new AchievementDetails { UniversalTime = 123, CrewNames = new[] { "Val" }, CrewTruncated = true };
            output = peer.CreateMessage(); message.Serialize(output); read.Deserialize(Incoming(peer, output));
            Assert.IsTrue(read.Details.CrewTruncated);
            Assert.AreEqual(123d, read.Details.UniversalTime.Value);
            Assert.AreEqual(Guid.Empty, read.Details.VesselId);
            message.Details = null; output = peer.CreateMessage(); message.Serialize(output);
            read.Deserialize(Incoming(peer, output)); Assert.IsNull(read.Details);
        }
        [TestMethod]
        public void RejectsUnboundedAndInvalidDetails()
        {
            var peer = new NetClient(new NetPeerConfiguration("invalid-first"));
            Assert.ThrowsException<InvalidDataException>(() => AchievementWire.WriteDetails(peer.CreateMessage(), new AchievementDetails { UniversalTime = double.NaN }));
            Assert.ThrowsException<InvalidDataException>(() => AchievementWire.WriteDetails(peer.CreateMessage(), new AchievementDetails { CrewNames = new string[AchievementWire.MaxCrew + 1] }));
            Assert.ThrowsException<InvalidDataException>(() => AchievementWire.WriteDetails(peer.CreateMessage(), new AchievementDetails { VesselName = new string('x', AchievementWire.MaxName + 1) }));
            var output = peer.CreateMessage(); output.Write(true); output.Write(false); output.Write(Guid.Empty.ToByteArray()); output.Write(""); output.Write(AchievementWire.MaxCrew + 1);
            Assert.ThrowsException<InvalidDataException>(() => AchievementWire.ReadDetails(Incoming(peer, output)));
            Assert.AreEqual("Recover From Orbit / Mun", AchievementWire.ReadableName("RecoverFromOrbit:Mun")); Assert.AreEqual("Mun", AchievementWire.ReadableName("Mun"));
        }
    }
}
