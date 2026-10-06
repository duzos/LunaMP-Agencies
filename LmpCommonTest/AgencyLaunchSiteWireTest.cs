using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyLaunchSiteWireTest
    {
        private static NetIncomingMessage Incoming(NetClient peer, NetOutgoingMessage outgoing, int? bits = null)
        {
            outgoing.Position = 0;
            var incoming = peer.CreateIncomingMessage(NetIncomingMessageType.Data, outgoing.ReadBytes(outgoing.LengthBytes));
            incoming.LengthBits = bits ?? outgoing.LengthBits;
            return incoming;
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SnapshotRoundTripPreservesEmptyAndFullMaps(bool populated)
        {
            var peer = new NetClient(new NetPeerConfiguration("LaunchSiteWire"));
            var factory = new ServerMessageFactory();
            var source = factory.CreateNewMessageData<AgencySyncAllMsgData>();
            source.MyAgencyId = Guid.NewGuid();
            source.Agencies = new[] { new AgencyInfo { Id = source.MyAgencyId, Name = "Agency", MemberUniqueIds = new[] { "id" }, MemberDisplayNames = new[] { "Pilot" } } };
            source.LaunchSitesSnapshotPresent = true;
            source.LaunchSitesRevision = 91;
            source.LaunchSites = populated ? new[] { new LaunchSiteAssignment { SiteId = "LaunchPad", AgencyId = source.MyAgencyId }, new LaunchSiteAssignment { SiteId = "KK = Custom Site", AgencyId = Guid.NewGuid() } } : Array.Empty<LaunchSiteAssignment>();
            var outgoing = peer.CreateMessage(source.GetMessageSize());
            source.Serialize(outgoing);
            Assert.IsTrue(source.GetMessageSize() >= outgoing.LengthBytes);
            var parsed = factory.CreateNewMessageData<AgencySyncAllMsgData>();
            parsed.Deserialize(Incoming(peer, outgoing));
            Assert.IsTrue(parsed.LaunchSitesSnapshotPresent);
            Assert.AreEqual(91L, parsed.LaunchSitesRevision);
            Assert.AreEqual(source.LaunchSites.Length, parsed.LaunchSites.Length);
            if (populated)
            {
                Assert.AreEqual(source.LaunchSites[1].SiteId, parsed.LaunchSites[1].SiteId);
                Assert.AreEqual(source.LaunchSites[1].AgencyId, parsed.LaunchSites[1].AgencyId);
            }
        }

        [TestMethod]
        public void LegacyAndTruncatedTailsResetReusedPermissions()
        {
            var peer = new NetClient(new NetPeerConfiguration("LaunchSiteLegacy"));
            var factory = new ServerMessageFactory();
            var source = factory.CreateNewMessageData<AgencySyncAllMsgData>();
            source.Agencies = Array.Empty<AgencyInfo>();
            source.LaunchSitesSnapshotPresent = true;
            source.LaunchSitesRevision = 12;
            source.LaunchSites = new[] { new LaunchSiteAssignment { SiteId = "Runway", AgencyId = Guid.NewGuid() } };
            var outgoing = peer.CreateMessage(source.GetMessageSize());
            source.Serialize(outgoing);
            var parsed = factory.CreateNewMessageData<AgencySyncAllMsgData>();
            foreach (var bits in new[] { 34 * 8, outgoing.LengthBits - 16, 34 * 8 + 1 })
            {
                parsed.LaunchSitesSnapshotPresent = true;
                parsed.LaunchSitesRevision = 999;
                parsed.LaunchSites = source.LaunchSites;
                parsed.Deserialize(Incoming(peer, outgoing, bits));
                Assert.IsFalse(parsed.LaunchSitesSnapshotPresent);
                Assert.AreEqual(0L, parsed.LaunchSitesRevision);
                Assert.AreEqual(0, parsed.LaunchSites.Length);
            }
        }

        [TestMethod]
        public void OversizedTailCountIsAbsentWithoutAllocatingAssignments()
        {
            var peer = new NetClient(new NetPeerConfiguration("LaunchSiteBounds"));
            var factory = new ServerMessageFactory();
            var source = factory.CreateNewMessageData<AgencySyncAllMsgData>();
            source.Agencies = Array.Empty<AgencyInfo>();
            source.LaunchSitesSnapshotPresent = false;
            var outgoing = peer.CreateMessage();
            source.Serialize(outgoing);
            outgoing.LengthBits--;
            outgoing.Write(true);
            outgoing.Write(1L);
            outgoing.Write(int.MaxValue);
            var parsed = factory.CreateNewMessageData<AgencySyncAllMsgData>();
            parsed.Deserialize(Incoming(peer, outgoing));
            Assert.IsFalse(parsed.LaunchSitesSnapshotPresent);
            Assert.AreEqual(0, parsed.LaunchSites.Length);
        }

        [DataTestMethod]
        [DataRow(AgencyAdminOp.AssignLaunchSite)]
        [DataRow(AgencyAdminOp.UnassignLaunchSite)]
        public void AdminOperationsPreserveCarrier(AgencyAdminOp operation)
        {
            var peer = new NetClient(new NetPeerConfiguration("LaunchSiteAdmin"));
            var factory = new ClientMessageFactory();
            var source = factory.CreateNewMessageData<AgencyAdminOpMsgData>();
            source.Op = operation;
            source.AdminPassword = "test-only";
            source.TargetAgencyId = Guid.NewGuid();
            source.StringArg = "Custom Site";
            var outgoing = peer.CreateMessage();
            source.Serialize(outgoing);
            var parsed = factory.CreateNewMessageData<AgencyAdminOpMsgData>();
            parsed.Deserialize(Incoming(peer, outgoing));
            Assert.AreEqual(operation, parsed.Op);
            Assert.AreEqual(source.TargetAgencyId, parsed.TargetAgencyId);
            Assert.AreEqual(source.StringArg, parsed.StringArg);
            Assert.AreEqual(source.AdminPassword, parsed.AdminPassword);
        }
    }
}
