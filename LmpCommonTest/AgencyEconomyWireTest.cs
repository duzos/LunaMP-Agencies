using System;
using System.IO;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyEconomyWireTest
    {
        private static NetIncomingMessage Incoming(NetClient peer, NetOutgoingMessage output, int? bits = null)
        {
            output.Position = 0;
            var input = peer.CreateIncomingMessage(NetIncomingMessageType.Data, output.ReadBytes(output.LengthBytes));
            input.LengthBits = bits ?? output.LengthBits;
            return input;
        }

        [TestMethod]
        public void TradeOfferAndImmutableDeliveryRoundtrip()
        {
            var peer = new NetClient(new NetPeerConfiguration("trade-wire"));
            var factory = new ServerMessageFactory();
            var source = factory.CreateNewMessageData<AgencyEconomySnapshotMsgData>();
            var id = Guid.NewGuid();
            source.Snapshot = new EconomySnapshot { Ready = true,
                Offers = new[] { new TradeOffer { OfferId = id, Revision = 17, Status = TradeOfferStatus.Open,
                    SellerFunds = 12, BuyerScience = 3, BlueprintName = "Lander" } },
                Entitlements = new[] { new TradeEntitlement { EntitlementId = Guid.NewGuid(), Fingerprint = "fingerprint",
                    BlueprintData = new byte[] { 1, 2, 3 }, BlueprintHash = "hash", Delivered = true } } };
            var output = peer.CreateMessage(); source.Serialize(output);
            Assert.IsTrue(source.GetMessageSize() >= output.LengthBytes);
            var target = factory.CreateNewMessageData<AgencyEconomySnapshotMsgData>(); target.Deserialize(Incoming(peer, output));
            Assert.AreEqual(id, target.Snapshot.Offers[0].OfferId);
            Assert.AreEqual(17L, target.Snapshot.Offers[0].Revision);
            Assert.AreEqual(3d, target.Snapshot.Offers[0].BuyerScience);
            Assert.IsTrue(target.Snapshot.Entitlements[0].Delivered);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, target.Snapshot.Entitlements[0].BlueprintData);
            Assert.ThrowsException<EndOfStreamException>(() => target.Deserialize(Incoming(peer, output, output.LengthBits - 1)));
            Assert.IsFalse(target.Snapshot.Ready);
            Assert.AreEqual(0, target.Snapshot.Offers.Length);
            Assert.AreEqual(0, target.Snapshot.Entitlements.Length);
        }

        [TestMethod]
        public void SingleLaunchTermsAndVoucherIdRoundtripAndLegacyJsonKeepsToday()
        {
            var peer = new NetClient(new NetPeerConfiguration("trade-modes-wire"));
            var snapshotSource = new ServerMessageFactory().CreateNewMessageData<AgencyEconomySnapshotMsgData>();
            var launch = Guid.NewGuid();
            snapshotSource.Snapshot = new EconomySnapshot { Ready = true,
                Offers = new[] { new TradeOffer { OfferId = Guid.NewGuid(), DesignMode = TradeDesignMode.SingleLaunch, PrepaidLaunchFunds = 123.5, LaunchMultiplier = 2 } },
                Entitlements = new[] { new TradeEntitlement { EntitlementId = Guid.NewGuid(), Kind = TradeEntitlementKind.SingleLaunch, PrepaidFunds = 123.5, LaunchMultiplier = 2, LaunchId = launch, Redeemed = true } } };
            var output = peer.CreateMessage(); snapshotSource.Serialize(output);
            var snapshotTarget = new ServerMessageFactory().CreateNewMessageData<AgencyEconomySnapshotMsgData>(); snapshotTarget.Deserialize(Incoming(peer, output));
            Assert.AreEqual(TradeDesignMode.SingleLaunch, snapshotTarget.Snapshot.Offers[0].DesignMode);
            Assert.AreEqual(123.5, snapshotTarget.Snapshot.Offers[0].PrepaidLaunchFunds);
            Assert.AreEqual(2d, snapshotTarget.Snapshot.Offers[0].LaunchMultiplier);
            var voucher = snapshotTarget.Snapshot.Entitlements[0];
            Assert.AreEqual(TradeEntitlementKind.SingleLaunch, voucher.Kind);
            Assert.AreEqual(123.5, voucher.PrepaidFunds);
            Assert.AreEqual(launch, voucher.LaunchId);
            Assert.IsTrue(voucher.Redeemed);

            var commandSource = new ClientMessageFactory().CreateNewMessageData<AgencyEconomyCommandMsgData>();
            var id = Guid.NewGuid();
            commandSource.Command = new EconomyCommand { RequestId = Guid.NewGuid(), Operation = EconomyOperation.PrepareLaunch, VoucherId = id,
                Trade = new TradeCommand { DesignMode = TradeDesignMode.SingleLaunch } };
            output = peer.CreateMessage(); commandSource.Serialize(output);
            var commandTarget = new ClientMessageFactory().CreateNewMessageData<AgencyEconomyCommandMsgData>(); commandTarget.Deserialize(Incoming(peer, output));
            Assert.AreEqual(id, commandTarget.Command.VoucherId);
            Assert.AreEqual(TradeDesignMode.SingleLaunch, commandTarget.Command.Trade.DesignMode);

            // An agencies.4 payload has none of the new fields: offers keep meaning tooling plus design and entitlements stay permanent.
            var legacyOffer = Newtonsoft.Json.JsonConvert.DeserializeObject<TradeOffer>("{\"OfferId\":\"" + Guid.NewGuid() + "\",\"SellerFunds\":5.0}");
            Assert.AreEqual(TradeDesignMode.ToolingAndDesign, legacyOffer.DesignMode);
            Assert.AreEqual(0d, legacyOffer.PrepaidLaunchFunds);
            var legacyEntitlement = Newtonsoft.Json.JsonConvert.DeserializeObject<TradeEntitlement>("{\"EntitlementId\":\"" + Guid.NewGuid() + "\",\"Fingerprint\":\"x\"}");
            Assert.AreEqual(TradeEntitlementKind.Permanent, legacyEntitlement.Kind);
            Assert.IsFalse(legacyEntitlement.Redeemed);
            Assert.AreEqual(Guid.Empty, legacyEntitlement.LaunchId);
            Assert.AreEqual(Guid.Empty, Newtonsoft.Json.JsonConvert.DeserializeObject<EconomyCommand>("{}").VoucherId);
        }

        [TestMethod]
        public void BoardingCommandPreservesCorrelationFinalProtoAndCargoBindings()
        {
            var peer = new NetClient(new NetPeerConfiguration("economy-command"));
            var factory = new ClientMessageFactory();
            var source = factory.CreateNewMessageData<AgencyEconomyCommandMsgData>();
            source.Command = new EconomyCommand { RequestId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Sequence = 91,
                Operation = EconomyOperation.BoardEva, ParentVesselId = Guid.NewGuid(), CrewName = "Jeb",
                VesselData = new byte[] { 7, 8, 9 }, RecoveredCargo = new[] { new ToolingCargo {
                    Name = "probe", Count = 2, UnitCost = 123, ContainerFlightId = 77, ContainerPartIndex = 1, CrewName = "Jeb" } } };
            var output = peer.CreateMessage(); source.Serialize(output);
            Assert.IsTrue(source.GetMessageSize() >= output.LengthBytes);
            var target = factory.CreateNewMessageData<AgencyEconomyCommandMsgData>(); target.Deserialize(Incoming(peer, output));
            Assert.AreEqual(source.Command.RequestId, target.Command.RequestId);
            Assert.AreEqual(source.Command.SessionId, target.Command.SessionId);
            Assert.AreEqual(91L, target.Command.Sequence);
            Assert.AreEqual(EconomyOperation.BoardEva, target.Command.Operation);
            CollectionAssert.AreEqual(source.Command.VesselData, target.Command.VesselData);
            Assert.AreEqual(77u, target.Command.RecoveredCargo[0].ContainerFlightId);
            Assert.AreEqual("Jeb", target.Command.RecoveredCargo[0].CrewName);
        }

        [TestMethod]
        public void TruncatedSnapshotClearsPreviouslyReadyPooledState()
        {
            var peer = new NetClient(new NetPeerConfiguration("economy-snapshot"));
            var factory = new ServerMessageFactory();
            var source = factory.CreateNewMessageData<AgencyEconomySnapshotMsgData>();
            source.Snapshot = new EconomySnapshot { Ready = true, Funds = 123, LastSequence = 91,
                Vessels = new[] { new PaidVesselRecord { VesselId = Guid.NewGuid(), Parts = new[] {
                    new PaidPart { FlightId = 77, Multiplier = .1, MaximumRefund = 12 } } } } };
            var output = peer.CreateMessage(); source.Serialize(output);
            var target = factory.CreateNewMessageData<AgencyEconomySnapshotMsgData>(); target.Deserialize(Incoming(peer, output));
            Assert.IsTrue(target.Snapshot.Ready);
            Assert.AreEqual(.1, target.Snapshot.Vessels[0].Parts[0].Multiplier);
            Assert.ThrowsException<EndOfStreamException>(() => target.Deserialize(Incoming(peer, output, output.LengthBits - 1)));
            Assert.IsFalse(target.Snapshot.Ready);
            Assert.AreEqual(0, target.Snapshot.Vessels.Length);
            var oversized = peer.CreateMessage(); oversized.Write(AgencyEconomyWire.MaximumPayloadBytes + 1);
            Assert.ThrowsException<InvalidDataException>(() => AgencyEconomyWire.Read<EconomySnapshot>(Incoming(peer, oversized)));
        }

        [TestMethod]
        public void LegacyProtoClearsPaidLaunchAndCargoTailOnReuse()
        {
            var peer = new NetClient(new NetPeerConfiguration("economy-proto"));
            var factory = new ClientMessageFactory();
            var source = factory.CreateNewMessageData<VesselProtoMsgData>();
            source.VesselId = Guid.NewGuid(); source.Data = new byte[] { 1, 2, 3 }; source.NumBytes = 3;
            source.EconomyLaunchId = Guid.NewGuid(); source.EconomyLaunchToken = Guid.NewGuid();
            source.EconomyManifestIndices = new[] { 1, 0 };
            source.EconomySplitOperationId = Guid.NewGuid();
            source.TradeEntitlementId = Guid.NewGuid();
            source.EconomySplitParentData = new byte[] { 4, 5, 6 };
            source.EconomyCargo = new[] { new ToolingCargo { Name = "probe", Count = 1, ContainerFlightId = 77 } };
            var output = peer.CreateMessage(); source.Serialize(output);
            var target = factory.CreateNewMessageData<VesselProtoMsgData>(); target.Deserialize(Incoming(peer, output));
            Assert.AreEqual(source.EconomyLaunchToken, target.EconomyLaunchToken);
            Assert.AreEqual(source.EconomySplitOperationId, target.EconomySplitOperationId);
            Assert.AreEqual(source.TradeEntitlementId, target.TradeEntitlementId);
            CollectionAssert.AreEqual(source.EconomySplitParentData, target.EconomySplitParentData);
            CollectionAssert.AreEqual(new[] { 1, 0 }, target.EconomyManifestIndices);
            Assert.AreEqual(77u, target.EconomyCargo[0].ContainerFlightId);
            var legacy = factory.CreateNewMessageData<VesselProtoMsgData>();
            legacy.Data = new byte[] { 1, 2, 3 }; legacy.NumBytes = 3;
            output = peer.CreateMessage(); legacy.Serialize(output); target.Deserialize(Incoming(peer, output));
            Assert.AreEqual(Guid.Empty, target.EconomyLaunchId);
            Assert.AreEqual(Guid.Empty, target.EconomyLaunchToken);
            Assert.AreEqual(Guid.Empty, target.EconomySplitOperationId);
            Assert.AreEqual(Guid.Empty, target.TradeEntitlementId);
            Assert.AreEqual(0, target.EconomySplitParentData.Length);
            Assert.AreEqual(0, target.EconomyManifestIndices.Length);
            Assert.IsNull(target.EconomyCargo);
        }
    }
}
