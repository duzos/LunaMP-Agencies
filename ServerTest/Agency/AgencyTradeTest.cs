using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class AgencyTradeTest
    {
        private sealed class Fixture : IDisposable
        {
            internal readonly AgencyEconomyTest.Fixture Economy = new AgencyEconomyTest.Fixture();
            private readonly bool oldTrade = GeneralSettings.SettingsStore.AgencyTrade;
            internal readonly ClientStructure Buyer;
            private Guid buyerSession;
            private long buyerSequence;
            internal Fixture()
            {
                GeneralSettings.SettingsStore.AgencyTrade = true;
                Buyer = (ClientStructure)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Economy.Client, null);
                typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Buyer, new ConcurrentQueue<IServerMessageBase>());
                Buyer.AgencyId = Guid.NewGuid(); Buyer.UniqueIdentifier = "trade-buyer"; Buyer.PlayerName = "TradeBuyer";
                ServerContext.Clients[new IPEndPoint(IPAddress.Loopback, 32352)] = Buyer;
                var agency = new Server.Agency.Agency { Id = Buyer.AgencyId, Name = "Buyer", OwnerUniqueId = Buyer.UniqueIdentifier, Funds = 20000, Science = 500 };
                agency.Members.Add(new Server.Agency.Agency.Member { UniqueId = Buyer.UniqueIdentifier, DisplayName = Buyer.PlayerName });
                AgencyStore.Agencies[agency.Id] = agency;
                AgencyScenarioStore.EnsureBaselineForAgency(agency.Id, agency.Funds, agency.Science, 0);
                Refresh();
            }
            internal void Refresh()
            {
                Economy.RefreshSession();
                AgencyEconomyStore.SendTo(Buyer);
                var snapshot = Buyer.SendMessageQueue.Select(m => m.Data).OfType<AgencyEconomySnapshotMsgData>().Last().Snapshot;
                buyerSession = snapshot.SessionId; buyerSequence = snapshot.LastSequence;
            }
            internal EconomyResult Buy(EconomyCommand command)
            {
                if (command.RequestId == Guid.Empty) command.RequestId = Guid.NewGuid();
                if (command.SessionId == Guid.Empty) command.SessionId = buyerSession;
                if (command.Sequence == 0) command.Sequence = ++buyerSequence;
                return AgencyEconomyStore.Execute(Buyer, command);
            }
            internal EconomyCommand Offer(bool design = false, Guid vessel = default)
            {
                var manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } } };
                if (design) Assert.IsTrue(Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }).Success);
                return new EconomyCommand { Operation = EconomyOperation.TradeCreate, Trade = new TradeCommand { OfferId = Guid.NewGuid(), BuyerAgencyId = Buyer.AgencyId, VesselId = vessel, SellerFunds = 100, SellerScience = 2, BuyerFunds = 1000, BuyerScience = 5, DesignFingerprint = design ? ToolingPolicy.Fingerprint(manifest) : null, BlueprintName = "Purchased Probe", Editor = "VAB", BlueprintData = design ? Encoding.UTF8.GetBytes("ship = Probe\ntype = VAB\nPART\n{\npart = probe_1\n}\n") : Array.Empty<byte>() } };
            }
            public void Dispose() { GeneralSettings.SettingsStore.AgencyTrade = oldTrade; Economy.Dispose(); }
        }

        [DataTestMethod]
        [DataRow("committed")]
        [DataRow("projections-written")]
        public void AtomicTradeFailureAndRestartPreserveImmutableBlueprintAndTwoSidedCurrency(string faultBoundary)
        {
            using (var fixture = new Fixture())
            {
                var vessel = Guid.NewGuid();
                VesselStoreSystem.CurrentVessels[vessel] = new Server.System.Vessel.Classes.Vessel(Proto(vessel, 51));
                AgencyVesselMap.Set(vessel, fixture.Economy.Client.AgencyId);
                Assert.IsTrue(AgencyVesselMap.Mutate(vessel, fixture.Economy.Client.AgencyId, true, VesselOwnershipOperation.AddCoOwner, fixture.Buyer.AgencyId, VesselDockingPolicy.Nobody).Success);
                Assert.IsTrue(AgencyVesselMap.Mutate(vessel, fixture.Economy.Client.AgencyId, true, VesselOwnershipOperation.SetDockingPolicy, Guid.Empty, VesselDockingPolicy.Anyone).Success);
                var offer = fixture.Offer(true, vessel);
                Assert.IsTrue(fixture.Economy.Execute(offer).Success);
                var original = offer.Trade.BlueprintData.ToArray();
                Array.Clear(offer.Trade.BlueprintData, 0, offer.Trade.BlueprintData.Length);
                var accept = new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.Trade.OfferId, ExpectedRevision = 1 } };
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "before-document") throw new System.IO.IOException("before"); };
                Assert.IsFalse(fixture.Buy(accept).Success);
                Assert.AreEqual(49000d, fixture.Economy.Snapshot.Funds);
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == faultBoundary) throw new System.IO.IOException("after"); };
                accept.Sequence = 0;
                var result = fixture.Buy(accept);
                Assert.IsTrue(result.RecoveryRequired);
                AgencyEconomyStore.PersistenceCheckpoint = null;
                AgencyVesselMap.Load(); AgencyVesselMap.RecoverJournal(); AgencyEconomyStore.Load(); fixture.Refresh();
                Assert.AreEqual(49900d, fixture.Economy.Snapshot.Funds);
                var buyer = AgencyEconomyStore.Snapshot(fixture.Buyer.AgencyId);
                Assert.AreEqual(19100d, buyer.Funds); Assert.AreEqual(497d, buyer.Science);
                Assert.AreEqual(103d, fixture.Economy.Snapshot.Science);
                CollectionAssert.AreEqual(original, buyer.Entitlements.Single(e => e.BlueprintData.Length > 0).BlueprintData);
                var title = AgencyVesselMap.Get(vessel);
                Assert.AreEqual(fixture.Buyer.AgencyId, title.OwnerAgencyId);
                Assert.AreEqual(0, title.CoOwnerAgencyIds.Length);
                Assert.AreEqual(VesselDockingPolicy.Nobody, title.DockingPolicy);
                Assert.AreEqual(1, buyer.Designs.Length);
                Assert.IsFalse(fixture.Buy(accept).Success, "A prior connection request cannot replay after restart.");
                Assert.AreEqual(19100d, AgencyEconomyStore.Snapshot(fixture.Buyer.AgencyId).Funds);
            }
        }

        private static string Proto(Guid id, params uint[] parts) => "pid = " + id.ToString("N") + "\nname = Offered\nroot = 0\n" + string.Concat(parts.Select(uid => "PART\n{\nname = probe\nuid = " + uid + "\n}\n")) + string.Concat(new[] { "ORBIT", "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }.Select(n => n + "\n{\n}\n"));

        [DataTestMethod]
        [DataRow("VAB")]
        [DataRow("SPH")]
        public void FlatCraftBlueprintIsTransferredWithoutChangingBytes(string editor)
        {
            using (var fixture = new Fixture())
            {
                var offer = fixture.Offer(true);
                // Exact installed KSP WriteNode output for a minimal node with a nested module (102 bytes for VAB).
                // ToString adds an outer wrapper instead, which is not the craft-file format.
                offer.Trade.Editor = editor;
                offer.Trade.BlueprintData = Encoding.UTF8.GetBytes("ship = Probe\r\ntype = " + editor + "\r\nPART\r\n{\r\n\tpart = probe_4292669534\r\n\tMODULE\r\n\t{\r\n\t\tname = ModuleTest\r\n\t}\r\n}\r\n");
                var created = fixture.Economy.Execute(offer);
                Assert.IsTrue(created.Success, created.Reason);
                var accepted = fixture.Buy(new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.Trade.OfferId, ExpectedRevision = 1 } });
                Assert.IsTrue(accepted.Success, accepted.Reason);
                var delivered = AgencyEconomyStore.Snapshot(fixture.Buyer.AgencyId).Entitlements.Single();
                Assert.AreEqual(editor, delivered.Editor);
                CollectionAssert.AreEqual(offer.Trade.BlueprintData, delivered.BlueprintData);
                Assert.AreEqual(offer.Trade.DesignFingerprint, delivered.Fingerprint);
            }
        }

        [DataTestMethod]
        [DataRow("wrapper")]
        [DataRow("named-wrapper")]
        [DataRow("wrong-editor")]
        [DataRow("invalid-part")]
        [DataRow("different-design")]
        [DataRow("oversized")]
        public void InvalidBlueprintCannotCreateOffer(string kind)
        {
            using (var fixture = new Fixture())
            {
                var offer = fixture.Offer(true);
                var text = Encoding.UTF8.GetString(offer.Trade.BlueprintData);
                switch (kind)
                {
                    case "wrapper": text = "\n{\n" + text + "}\n"; break;
                    case "named-wrapper": text = "SHIP\n{\n" + text + "}\n"; break;
                    case "wrong-editor": text = text.Replace("type = VAB", "type = SPH"); break;
                    case "invalid-part": text = text.Replace("probe_1", "probe_not-an-id"); break;
                    case "different-design": text = text.Replace("probe_1", "other_1"); break;
                    case "oversized": text += new string('x', TradeLimits.MaxBlueprintBytes); break;
                }
                offer.Trade.BlueprintData = Encoding.UTF8.GetBytes(text);
                var result = fixture.Economy.Execute(offer);
                Assert.IsFalse(result.Success, kind);
                Assert.AreEqual(0, fixture.Economy.Snapshot.Offers.Length);
                Assert.AreEqual(0, AgencyEconomyStore.Snapshot(fixture.Buyer.AgencyId).Entitlements.Length);
            }
        }

        [TestMethod]
        public void TradeOnlyCouplingRecoveryFlattensOwnershipJournalBeforeReadiness()
        {
            using (var fixture = new Fixture())
            {
                GeneralSettings.SettingsStore.AgencyTooling = false;
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var dominant = Guid.NewGuid(); var weak = Guid.NewGuid();
                VesselStoreSystem.CurrentVessels[dominant] = new Server.System.Vessel.Classes.Vessel(Proto(dominant, 71));
                VesselStoreSystem.CurrentVessels[weak] = new Server.System.Vessel.Classes.Vessel(Proto(weak, 72));
                AgencyVesselMap.Set(dominant, fixture.Economy.Client.AgencyId);
                AgencyVesselMap.Set(weak, fixture.Economy.Client.AgencyId);
                var merged = Proto(dominant, 71, 72);
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "committed") throw new System.IO.IOException("couple crash"); };
                Assert.ThrowsException<System.IO.IOException>(() => AgencyVesselMap.CommitCouple(Guid.NewGuid(), fixture.Economy.Client.UniqueIdentifier, dominant, weak, merged, new Server.System.Vessel.Classes.Vessel(merged), 71, 72));
                AgencyEconomyStore.PersistenceCheckpoint = null;
                AgencyVesselMap.Load(); AgencyVesselMap.RecoverJournal(); AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.IsTrue(AgencyVesselMap.Ready);
                Assert.IsFalse(AgencyVesselMap.HasPendingJournal);
                Assert.IsFalse(VesselStoreSystem.VesselExists(weak));
                Assert.IsTrue(AgencyVesselMap.IsAbsorbed(weak));
                CollectionAssert.AreEqual(new uint[] { 71, 72 }, AgencyVesselMap.PartIds(VesselStoreSystem.CurrentVessels[dominant]));
            }
        }

        [TestMethod]
        public void AggregateSnapshotLimitRejectsTradeBeforeMoneyTitleOrEntitlementChange()
        {
            using (var fixture = new Fixture())
            {
                var vessel = Guid.NewGuid();
                VesselStoreSystem.CurrentVessels[vessel] = new Server.System.Vessel.Classes.Vessel(Proto(vessel, 81));
                AgencyVesselMap.Set(vessel, fixture.Economy.Client.AgencyId);
                var offer = fixture.Offer(true, vessel);
                offer.Trade.BlueprintData = Encoding.UTF8.GetBytes("description = " + new string('x', 400 * 1024) + "\n" + Encoding.UTF8.GetString(offer.Trade.BlueprintData));
                var document = (EconomyDocument)typeof(AgencyEconomyStore).GetField("_document", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                while (AgencyEconomyWire.Size(fixture.Economy.Snapshot) < AgencyEconomyWire.MaximumPayloadBytes - 400 * 1024)
                {
                    var id = Guid.NewGuid();
                    document.Vessels[id] = new PaidVesselRecord { VesselId = id, Parts = Enumerable.Range(1, 250).Select(i => new PaidPart { FlightId = (uint)i, Name = new string('p', 200) }).ToArray() };
                }
                Assert.IsTrue(AgencyEconomyWire.Size(fixture.Economy.Snapshot) < AgencyEconomyWire.MaximumPayloadBytes - 4096);
                var created = fixture.Economy.Execute(offer);
                Assert.IsTrue(created.Success, created.Reason);
                var rejected = fixture.Buy(new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.Trade.OfferId, ExpectedRevision = 1 } });
                Assert.IsFalse(rejected.Success);
                StringAssert.Contains(rejected.Reason, "snapshot storage limit");
                Assert.AreEqual(49000d, fixture.Economy.Snapshot.Funds);
                Assert.AreEqual(20000d, AgencyEconomyStore.Snapshot(fixture.Buyer.AgencyId).Funds);
                Assert.AreEqual(fixture.Economy.Client.AgencyId, AgencyVesselMap.Get(vessel).OwnerAgencyId);
                Assert.AreEqual(0, AgencyEconomyStore.Snapshot(fixture.Buyer.AgencyId).Entitlements.Length);
            }
        }

        [TestMethod]
        public void UnauthorizedExpiredAndDeletedAgencyOffersCannotDebit()
        {
            using (var fixture = new Fixture())
            {
                var offer = fixture.Offer();
                Assert.IsTrue(fixture.Economy.Execute(offer).Success);
                Assert.IsFalse(fixture.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.Trade.OfferId, ExpectedRevision = 1 } }).Success);
                var now = DateTime.UtcNow;
                AgencyEconomyStore.UtcNow = () => now.AddDays(2);
                Assert.IsFalse(fixture.Buy(new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.Trade.OfferId, ExpectedRevision = 1 } }).Success);
                AgencyEconomyStore.UtcNow = () => now;
                AgencyStore.Agencies.TryRemove(fixture.Economy.Client.AgencyId, out _);
                Assert.IsFalse(fixture.Buy(new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.Trade.OfferId, ExpectedRevision = 1 } }).Success);
                Assert.AreEqual(20000d, AgencyEconomyStore.Snapshot(fixture.Buyer.AgencyId).Funds);
            }
        }

        [TestMethod]
        public void TradeOnlyDestroyedCraftInvalidatesOfferThroughMessageReader()
        {
            using (var fixture = new Fixture())
            {
                GeneralSettings.SettingsStore.AgencyTooling = false;
                var id = Guid.NewGuid();
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(Proto(id, 91));
                System.IO.Directory.CreateDirectory(VesselStoreSystem.VesselsPath);
                System.IO.File.WriteAllText(System.IO.Path.Combine(VesselStoreSystem.VesselsPath, id + VesselStoreSystem.VesselFileFormat), Proto(id, 91));
                AgencyVesselMap.Set(id, fixture.Economy.Client.AgencyId);
                var offer = fixture.Offer(false, id);
                Assert.IsTrue(fixture.Economy.Execute(offer).Success);
                var factory = new LmpCommon.Message.ClientMessageFactory();
                var data = factory.CreateNewMessageData<LmpCommon.Message.Data.Vessel.VesselRemoveMsgData>();
                data.VesselId = id; data.AddToKillList = true; data.Reason = "Destroyed";
                new Server.Message.VesselMsgReader().HandleMessage(fixture.Economy.Client, factory.CreateNew<LmpCommon.Message.Client.VesselCliMsg>(data));
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                Assert.IsNull(AgencyVesselMap.Get(id));
                Assert.AreEqual(TradeOfferStatus.Invalidated, fixture.Economy.Snapshot.Offers.Single().Status);
                Assert.IsFalse(fixture.Buy(new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.Trade.OfferId, ExpectedRevision = 1 } }).Success);
                Assert.AreEqual(50000d, fixture.Economy.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void EnablingToolingMigratesExistingTradeOnlyLaunchAfterRestart()
        {
            using (var fixture = new Fixture())
            {
                GeneralSettings.SettingsStore.AgencyTooling = false;
                var id = Guid.NewGuid();
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(Proto(id, 92));
                AgencyVesselMap.Set(id, fixture.Economy.Client.AgencyId);
                Assert.IsFalse(fixture.Economy.Snapshot.Vessels.Any(v => v.VesselId == id));
                GeneralSettings.SettingsStore.AgencyTooling = true;
                AgencyEconomyStore.Load();
                var part = fixture.Economy.Snapshot.Vessels.Single(v => v.VesselId == id).Parts.Single();
                Assert.AreEqual(92u, part.FlightId);
                Assert.IsTrue(part.Legacy);
                Assert.AreEqual(1d, part.Multiplier);
                Assert.IsTrue(AgencyEconomyStore.ValidatePublishedParts(id, new uint[] { 92 }));
                Assert.AreEqual(50000d, fixture.Economy.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void ChangedTitleInvalidatesOfferAndTradeOnlyDoesNotEnableTooling()
        {
            using (var fixture = new Fixture())
            {
                GeneralSettings.SettingsStore.AgencyTooling = false;
                var id = Guid.NewGuid();
                var raw = "pid = " + id.ToString("N") + "\nname = Offered\nPART\n{\nname = probe\nuid = 21\n}\n" + string.Concat(new[] { "ORBIT", "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }.Select(n => n + "\n{\n}\n"));
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(raw);
                AgencyVesselMap.Set(id, fixture.Economy.Client.AgencyId);
                var offer = fixture.Offer(false, id);
                Assert.IsTrue(fixture.Economy.Execute(offer).Success);
                AgencyVesselMap.Set(id, fixture.Buyer.AgencyId);
                Assert.AreEqual(TradeOfferStatus.Invalidated, fixture.Economy.Snapshot.Offers.Single().Status);
                Assert.IsFalse(fixture.Buy(new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.Trade.OfferId, ExpectedRevision = 1 } }).Success);
                var manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } } };
                Assert.IsFalse(fixture.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }).Success);
                Assert.IsTrue(fixture.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = -1 }).Success);
                Assert.AreEqual(49999d, fixture.Economy.Snapshot.Funds);
            }
        }
    }
}
