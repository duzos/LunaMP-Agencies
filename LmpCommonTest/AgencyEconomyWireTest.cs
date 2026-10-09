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
            [TestMethod]
        public void DesignStockFieldsRoundtrip()
        {
            var peer = new NetClient(new NetPeerConfiguration("stock-wire"));
            var lot = new DesignStockLot { LotId = Guid.NewGuid(), Fingerprint = "fp", Units = 7, PrepaidPerUnit = 12.5, LaunchMultiplier = .07,
                BuilderAgencyId = Guid.NewGuid(), SourceAgencyId = Guid.NewGuid(), FundsBuilt = true, CreatedUtcTicks = 42 };
            var snapshotSource = new ServerMessageFactory().CreateNewMessageData<AgencyEconomySnapshotMsgData>();
            snapshotSource.Snapshot = new EconomySnapshot { Ready = true,
                Designs = new[] { new ToolingDesign { Fingerprint = "fp", Name = "Mun Flyer", Manifest = new ToolingManifest() } },
                Stock = new[] { lot },
                DesignBlueprints = new[] { new ToolingBlueprintInfo { Fingerprint = "fp", Name = "Mun Flyer", Editor = "SPH", Hash = "abc", Bytes = 1234 } },
                Offers = new[] { new TradeOffer { OfferId = Guid.NewGuid(), DesignMode = TradeDesignMode.Stock, StockUnits = 3, StockPrepaidTotal = 37.5 } },
                Entitlements = new[] { new TradeEntitlement { EntitlementId = Guid.NewGuid(), Kind = TradeEntitlementKind.StockDesign, Fingerprint = "fp" } } };
            snapshotSource.Snapshot.StockHeldByFingerprint["fp"] = 9;
            var output = peer.CreateMessage(); snapshotSource.Serialize(output);
            Assert.IsTrue(snapshotSource.GetMessageSize() >= output.LengthBytes);
            var snapshot = new ServerMessageFactory().CreateNewMessageData<AgencyEconomySnapshotMsgData>();
            snapshot.Deserialize(Incoming(peer, output));
            var read = snapshot.Snapshot;
            Assert.AreEqual("Mun Flyer", read.Designs[0].Name);
            Assert.AreEqual(lot.LotId, read.Stock[0].LotId); Assert.AreEqual("fp", read.Stock[0].Fingerprint); Assert.AreEqual(7, read.Stock[0].Units);
            Assert.AreEqual(12.5, read.Stock[0].PrepaidPerUnit); Assert.AreEqual(.07, read.Stock[0].LaunchMultiplier);
            Assert.AreEqual(lot.BuilderAgencyId, read.Stock[0].BuilderAgencyId); Assert.AreEqual(lot.SourceAgencyId, read.Stock[0].SourceAgencyId);
            Assert.IsTrue(read.Stock[0].FundsBuilt); Assert.AreEqual(42L, read.Stock[0].CreatedUtcTicks);
            Assert.AreEqual("SPH", read.DesignBlueprints[0].Editor); Assert.AreEqual("abc", read.DesignBlueprints[0].Hash); Assert.AreEqual(1234, read.DesignBlueprints[0].Bytes);
            Assert.AreEqual(TradeDesignMode.Stock, read.Offers[0].DesignMode); Assert.AreEqual(3, read.Offers[0].StockUnits); Assert.AreEqual(37.5, read.Offers[0].StockPrepaidTotal);
            Assert.AreEqual(TradeEntitlementKind.StockDesign, read.Entitlements[0].Kind);
            Assert.AreEqual(9, read.StockHeldByFingerprint["fp"]);

            var commandSource = new ClientMessageFactory().CreateNewMessageData<AgencyEconomyCommandMsgData>();
            var lotId = Guid.NewGuid();
            commandSource.Command = new EconomyCommand { RequestId = Guid.NewGuid(), Operation = EconomyOperation.BuildStock, StockLotId = lotId, StockFingerprint = "fp", StockUnits = 10,
                ExpectedCharge = 650.25, DesignName = "Mun Flyer", BlueprintData = new byte[] { 4, 5, 6 }, BlueprintEditor = "VAB",
                Trade = new TradeCommand { DesignMode = TradeDesignMode.Stock, StockUnits = 4 } };
            output = peer.CreateMessage(); commandSource.Serialize(output);
            var command = new ClientMessageFactory().CreateNewMessageData<AgencyEconomyCommandMsgData>(); command.Deserialize(Incoming(peer, output));
            Assert.AreEqual(EconomyOperation.BuildStock, command.Command.Operation);
            Assert.AreEqual(lotId, command.Command.StockLotId); Assert.AreEqual("fp", command.Command.StockFingerprint); Assert.AreEqual(10, command.Command.StockUnits);
            Assert.AreEqual(650.25, command.Command.ExpectedCharge); Assert.AreEqual("Mun Flyer", command.Command.DesignName);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, command.Command.BlueprintData); Assert.AreEqual("VAB", command.Command.BlueprintEditor);
            Assert.AreEqual(TradeDesignMode.Stock, command.Command.Trade.DesignMode); Assert.AreEqual(4, command.Command.Trade.StockUnits);

            var resultSource = new ServerMessageFactory().CreateNewMessageData<AgencyEconomyResultMsgData>();
            resultSource.Result = new EconomyResult { RequestId = Guid.NewGuid(), Operation = EconomyOperation.FetchBlueprint, Success = true,
                BlueprintData = new byte[] { 9, 8 }, BlueprintEditor = "SPH", BlueprintName = "Mun Flyer", BlueprintHash = "hash" };
            output = peer.CreateMessage(); resultSource.Serialize(output);
            var result = new ServerMessageFactory().CreateNewMessageData<AgencyEconomyResultMsgData>(); result.Deserialize(Incoming(peer, output));
            Assert.AreEqual(EconomyOperation.FetchBlueprint, result.Result.Operation);
            CollectionAssert.AreEqual(new byte[] { 9, 8 }, result.Result.BlueprintData);
            Assert.AreEqual("SPH", result.Result.BlueprintEditor); Assert.AreEqual("Mun Flyer", result.Result.BlueprintName); Assert.AreEqual("hash", result.Result.BlueprintHash);
        }

        [TestMethod]
        public void NewEconomyOperationsAreAppendedAfterTheTradeRange()
        {
            Assert.AreEqual(15, (int)EconomyOperation.TradeDelivered, "Existing operation byte values must not move.");
            Assert.AreEqual(16, (int)EconomyOperation.BuildStock);
            Assert.AreEqual(17, (int)EconomyOperation.FetchBlueprint);
            Assert.AreEqual(2, (int)TradeDesignMode.Stock);
            Assert.AreEqual(2, (int)TradeEntitlementKind.StockDesign);
        }

        [TestMethod]
        public void LegacyJsonWithoutStockFieldsReadsDefaults()
        {
            var legacy = Newtonsoft.Json.JsonConvert.DeserializeObject<EconomySnapshot>("{\"Ready\":true,\"Funds\":5.0}");
            Assert.AreEqual(0, legacy.Stock.Length);
            Assert.AreEqual(0, legacy.DesignBlueprints.Length);
            Assert.IsNotNull(legacy.StockHeldByFingerprint);
            Assert.AreEqual(0, legacy.StockHeldByFingerprint.Count);
            var command = Newtonsoft.Json.JsonConvert.DeserializeObject<EconomyCommand>("{}");
            Assert.AreEqual(Guid.Empty, command.StockLotId); Assert.IsNull(command.StockFingerprint); Assert.AreEqual(0, command.StockUnits);
            Assert.AreEqual(0d, command.ExpectedCharge); Assert.IsNull(command.DesignName); Assert.AreEqual(0, command.BlueprintData.Length); Assert.IsNull(command.BlueprintEditor);
            var result = Newtonsoft.Json.JsonConvert.DeserializeObject<EconomyResult>("{}");
            Assert.AreEqual(0, result.BlueprintData.Length); Assert.IsNull(result.BlueprintHash);
            var offer = Newtonsoft.Json.JsonConvert.DeserializeObject<TradeOffer>("{}");
            Assert.AreEqual(0, offer.StockUnits); Assert.AreEqual(0d, offer.StockPrepaidTotal);
            Assert.AreEqual(0, Newtonsoft.Json.JsonConvert.DeserializeObject<TradeCommand>("{}").StockUnits);
            Assert.IsNull(Newtonsoft.Json.JsonConvert.DeserializeObject<ToolingDesign>("{\"Fingerprint\":\"x\"}").Name);
        }

        [TestMethod]
        public void StockHeldMapIsNeverNullAndIsCappedOnRead()
        {
            var peer = new NetClient(new NetPeerConfiguration("stock-held-wire"));
            var explicitNull = Newtonsoft.Json.JsonConvert.DeserializeObject<EconomySnapshot>("{\"StockHeldByFingerprint\":null}");
            Assert.IsNotNull(explicitNull.StockHeldByFingerprint);
            var source = new ServerMessageFactory().CreateNewMessageData<AgencyEconomySnapshotMsgData>();
            source.Snapshot = new EconomySnapshot { Ready = true };
            for (var i = 0; i < EconomySnapshot.MaxStockHeldEntries + 5; i++) source.Snapshot.StockHeldByFingerprint[i.ToString("D5")] = i;
            var output = peer.CreateMessage(); source.Serialize(output);
            var target = new ServerMessageFactory().CreateNewMessageData<AgencyEconomySnapshotMsgData>(); target.Deserialize(Incoming(peer, output));
            Assert.AreEqual(EconomySnapshot.MaxStockHeldEntries, target.Snapshot.StockHeldByFingerprint.Count);
            Assert.AreEqual(0, target.Snapshot.StockHeldByFingerprint["00000"]);
        }
    }
}
