using LmpCommon.Agency;
using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ServerTest.Agency
{
    /// <summary>
    /// Plan 40 trade mode three: selling design stock. Units are escrowed at create, move to the buyer at accept and return to the seller on
    /// every other close. Lots are seeded straight into the economy document so these cases do not depend on BuildStock.
    /// Cases marked "needs S1" call StockHeld / StockLotSlots / TryStoreBlueprint, whose bodies come from plan 40 slice S1.
    /// </summary>
    [TestClass, DoNotParallelize]
    public class AgencyStockTradeTest
    {
        private const string Craft = "ship = Probe\ntype = VAB\nPART\n{\npart = probe_1\n}\n";
        private static readonly ToolingManifest Probe = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } } };
        private static readonly string Fp = ToolingPolicy.Fingerprint(Probe);

        private static AgencyTradeTest.Fixture Start()
        {
            var fixture = new AgencyTradeTest.Fixture();
            AgencyEconomyTest.Fixture.UseRates(new ToolingRates(10, .1, 2, .1));
            return fixture;
        }

        private static EconomyDocument Doc() => (EconomyDocument)typeof(AgencyEconomyStore).GetField("_document", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        private static EconomyAgency Row(EconomyDocument document, Guid id)
        {
            if (document.Agencies.TryGetValue(id, out var row)) return row;
            var source = AgencyStore.Agencies[id];
            return document.Agencies[id] = new EconomyAgency { Funds = source.Funds, Science = source.Science };
        }

        private static DesignStockLot Seed(Guid agency, int units, double prepaid = 5, bool fundsBuilt = true, long ticks = 1, Guid source = default(Guid), double multiplier = .07)
        {
            var lot = new DesignStockLot { LotId = Guid.NewGuid(), Fingerprint = Fp, Units = units, PrepaidPerUnit = prepaid, LaunchMultiplier = multiplier, BuilderAgencyId = agency, SourceAgencyId = source, FundsBuilt = fundsBuilt, CreatedUtcTicks = ticks };
            Row(Doc(), agency).Stock.Add(lot);
            return lot;
        }

        private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        /// <summary>An open Stock offer written straight into the document, escrow taken from the seller's lots as TradeCreate would.</summary>
        private static StoredTradeOffer SeedOffer(Guid seller, Guid buyer, int units, Guid vessel = default(Guid), List<DesignStockLot> escrow = null)
        {
            var document = Doc();
            escrow = escrow ?? StockPolicy.Take(Row(document, seller).Stock, Fp, units).ToList();
            var blueprint = Encoding.UTF8.GetBytes(Craft);
            var offer = new TradeOffer { OfferId = Guid.NewGuid(), SellerAgencyId = seller, BuyerAgencyId = buyer, DesignMode = TradeDesignMode.Stock, DesignFingerprint = Fp, BlueprintName = "Stock Probe", Editor = "VAB", Revision = 1, ExpiresUtcTicks = AgencyEconomyStore.UtcNow().AddHours(24).Ticks, BuyerFunds = 500, StockUnits = units, StockPrepaidTotal = escrow.Sum(l => l.Units * l.PrepaidPerUnit), VesselId = vessel };
            var stored = new StoredTradeOffer { Offer = offer, SellerOwner = AgencyStore.Agencies.TryGetValue(seller, out var s) ? s.OwnerUniqueId : "gone", BuyerOwner = AgencyStore.Agencies[buyer].OwnerUniqueId, Blueprint = blueprint, BlueprintHash = Sha(blueprint), Escrow = escrow };
            if (vessel != Guid.Empty)
            {
                stored.VesselRevision = AgencyVesselMap.Get(vessel).Revision;
                stored.VesselFingerprint = ToolingPolicy.Fingerprint(new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe" } } });
            }
            document.TradeOffers[offer.OfferId] = stored;
            return stored;
        }

        private static EconomyCommand StockOffer(Guid buyer, int units, double buyerFunds = 500, string craft = Craft, string fingerprint = null)
            => new EconomyCommand { Operation = EconomyOperation.TradeCreate, Trade = new TradeCommand { OfferId = Guid.NewGuid(), BuyerAgencyId = buyer, DesignMode = TradeDesignMode.Stock, DesignFingerprint = fingerprint ?? Fp, StockUnits = units, BlueprintName = "Stock Probe", Editor = "VAB", BlueprintData = Encoding.UTF8.GetBytes(craft), BuyerFunds = buyerFunds } };

        private static EconomyCommand Decision(EconomyOperation operation, Guid offer, long revision = 1) => new EconomyCommand { Operation = operation, Trade = new TradeCommand { OfferId = offer, ExpectedRevision = revision } };

        private static int Units(Guid agency) => Doc().Agencies.TryGetValue(agency, out var row) ? row.Stock.Where(l => l.Fingerprint == Fp).Sum(l => l.Units) : 0;
        private static StoredTradeOffer Stored(Guid offer) => Doc().TradeOffers[offer];
        private static Guid Seller(AgencyTradeTest.Fixture f) => f.Economy.Client.AgencyId;
        private static Guid Buyer(AgencyTradeTest.Fixture f) => f.Buyer.AgencyId;

        // ---- Create ----

        [TestMethod] // needs S1 (StockLotSlots)
        public void CreateEscrowsUnitsOldestFirstWithFreshLotIdsAndNeverSetsTheDesign()
        {
            using (var f = Start())
            {
                var older = Seed(Seller(f), 3, prepaid: 5, ticks: 1);
                var newer = Seed(Seller(f), 4, prepaid: 6, ticks: 2);
                var offer = StockOffer(Buyer(f), 5);
                var created = f.Economy.Execute(offer);
                Assert.IsTrue(created.Success, created.Reason);
                var stored = Stored(offer.Trade.OfferId);
                Assert.IsNull(stored.Design, "Stock never takes the tooling path.");
                Assert.AreEqual(5, stored.Offer.StockUnits);
                Assert.AreEqual(3 * 5 + 2 * 6d, stored.Offer.StockPrepaidTotal, 1e-9);
                CollectionAssert.AreEqual(new[] { 3, 2 }, stored.Escrow.Select(l => l.Units).ToArray(), "Oldest lot first.");
                Assert.IsFalse(stored.Escrow.Any(l => l.LotId == older.LotId || l.LotId == newer.LotId), "Escrow rows get fresh LotIds, even for a whole lot.");
                var left = Row(Doc(), Seller(f)).Stock.Single();
                Assert.AreEqual(newer.LotId, left.LotId, "A partly taken lot keeps its LotId.");
                Assert.AreEqual(2, left.Units);
                Assert.AreEqual(0, f.Economy.Snapshot.Designs.Length, "Selling stock needs no tooling.");
                Assert.AreEqual(5, AgencyEconomyStore.Snapshot(Buyer(f)).Offers.Single().StockUnits, "The buyer sees the units on offer.");
            }
        }

        [DataTestMethod]
        [DataRow("shortfall")]
        [DataRow("zero-units")]
        [DataRow("too-many-units")]
        [DataRow("empty-fingerprint")]
        [DataRow("tooling-off")]
        [DataRow("trade-off")]
        [DataRow("blueprint-mismatch")]
        [DataRow("sandbox-built")]
        public void RefusedCreateStoresNoOfferAndLeavesStockUntouched(string kind)
        {
            using (var f = Start())
            {
                var lot = Seed(Seller(f), 4, fundsBuilt: kind != "sandbox-built");
                var offer = StockOffer(Buyer(f), kind == "shortfall" ? 5 : kind == "zero-units" ? 0 : kind == "too-many-units" ? 1000 : 1);
                if (kind == "empty-fingerprint") offer.Trade.DesignFingerprint = "";
                if (kind == "tooling-off") GeneralSettings.SettingsStore.AgencyTooling = false;
                if (kind == "trade-off") GeneralSettings.SettingsStore.AgencyTrade = false;
                if (kind == "blueprint-mismatch") offer.Trade.BlueprintData = Encoding.UTF8.GetBytes("ship = Other\ntype = VAB\nPART\n{\npart = tank_1\n}\n");
                var result = f.Economy.Execute(offer);
                Assert.IsFalse(result.Success, kind);
                if (kind == "shortfall") StringAssert.Contains(result.Reason, "only 4 units");
                if (kind == "empty-fingerprint") Assert.AreEqual("A stock offer needs a design.", result.Reason);
                if (kind == "sandbox-built") StringAssert.Contains(result.Reason, "4 of these units were built without funds");
                Assert.AreEqual(0, Doc().TradeOffers.Count);
                var row = Row(Doc(), Seller(f)).Stock.Single();
                Assert.AreEqual(lot.LotId, row.LotId);
                Assert.AreEqual(4, row.Units);
            }
        }

        [TestMethod] // needs S1 (StockLotSlots)
        public void CareerCreateTakesOnlyFundsBuiltUnits()
        {
            using (var f = Start())
            {
                Seed(Seller(f), 3, fundsBuilt: false, ticks: 1);
                Seed(Seller(f), 2, prepaid: 7, fundsBuilt: true, ticks: 2);
                var offer = StockOffer(Buyer(f), 2);
                var created = f.Economy.Execute(offer);
                Assert.IsTrue(created.Success, created.Reason);
                Assert.IsTrue(Stored(offer.Trade.OfferId).Escrow.All(l => l.FundsBuilt), "The older Sandbox-built lot is skipped.");
                Assert.AreEqual(3, Units(Seller(f)));
                Assert.IsFalse(f.Economy.Execute(StockOffer(Buyer(f), 1)).Success, "Only Sandbox-built units are left.");
            }
        }

        [TestMethod] // needs S1 (StockLotSlots)
        public void SandboxCreateMaySellSandboxBuiltUnits()
        {
            using (var f = Start())
            {
                GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
                Seed(Seller(f), 3, prepaid: 0, fundsBuilt: false);
                var offer = StockOffer(Buyer(f), 2);
                var created = f.Economy.Execute(offer);
                Assert.IsTrue(created.Success, created.Reason);
                Assert.AreEqual(1, Units(Seller(f)));
            }
        }

        // ---- Accept ----

        [TestMethod] // needs S1 (StockLotSlots, StockHeld)
        public void AcceptMovesUnitsWithTheirTermsAndAddsOneDedupedStockDesign()
        {
            using (var f = Start())
            {
                Assert.IsTrue(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = Probe, ManifestHash = ToolingPolicy.ManifestHash(Probe) }).Success);
                var sellerDesigns = f.Economy.Snapshot.Designs.Length;
                Seed(Seller(f), 5, prepaid: 5, multiplier: .07);
                var sellerFunds = f.Economy.Snapshot.Funds;
                var buyerFunds = AgencyEconomyStore.Snapshot(Buyer(f)).Funds;
                var offer = StockOffer(Buyer(f), 3, buyerFunds: 400);
                Assert.IsTrue(f.Economy.Execute(offer).Success);
                var escrowIds = Stored(offer.Trade.OfferId).Escrow.Select(l => l.LotId).ToArray();
                var accepted = f.Buy(Decision(EconomyOperation.TradeAccept, offer.Trade.OfferId));
                Assert.IsTrue(accepted.Success, accepted.Reason);

                var bought = Row(Doc(), Buyer(f)).Stock.Single();
                Assert.AreEqual(3, bought.Units);
                Assert.AreEqual(5d, bought.PrepaidPerUnit); Assert.AreEqual(.07, bought.LaunchMultiplier);
                Assert.AreEqual(Seller(f), bought.BuilderAgencyId); Assert.AreEqual(Seller(f), bought.SourceAgencyId); Assert.IsTrue(bought.FundsBuilt);
                CollectionAssert.DoesNotContain(escrowIds, bought.LotId, "Buyer rows get fresh LotIds.");
                Assert.AreEqual(2, Units(Seller(f)));
                Assert.AreEqual(0, Stored(offer.Trade.OfferId).Escrow.Count);
                Assert.AreEqual(TradeOfferStatus.Accepted, Stored(offer.Trade.OfferId).Offer.Status);
                Assert.AreEqual(sellerDesigns, f.Economy.Snapshot.Designs.Length, "The seller keeps its tooling.");
                Assert.AreEqual(0, AgencyEconomyStore.Snapshot(Buyer(f)).Designs.Length, "No tooling is sold with stock.");
                Assert.AreEqual(sellerFunds + 400, f.Economy.Snapshot.Funds, 1e-9);
                Assert.AreEqual(buyerFunds - 400, AgencyEconomyStore.Snapshot(Buyer(f)).Funds, 1e-9);
                var design = AgencyEconomyStore.Snapshot(Buyer(f)).Entitlements.Single();
                Assert.AreEqual(TradeEntitlementKind.StockDesign, design.Kind);
                Assert.AreEqual(Fp, design.Fingerprint);
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Craft), design.BlueprintData);

                var again = StockOffer(Buyer(f), 1, buyerFunds: 100);
                Assert.IsTrue(f.Economy.Execute(again).Success);
                Assert.IsTrue(f.Buy(Decision(EconomyOperation.TradeAccept, again.Trade.OfferId)).Success);
                Assert.AreEqual(1, AgencyEconomyStore.Snapshot(Buyer(f)).Entitlements.Length, "A repeat purchase of the same craft dedupes.");
                Assert.AreEqual(4, Row(Doc(), Buyer(f)).Stock.Single().Units, "Same terms merge into one row.");
                Assert.AreEqual(bought.LotId, Row(Doc(), Buyer(f)).Stock.Single().LotId, "Merging keeps the target's LotId.");
            }
        }

        [DataTestMethod] // needs S1 (StockHeld)
        [DataRow("before-document")]
        [DataRow("committed")]
        [DataRow("projections-written")]
        public void AcceptIsAtomicAcrossPersistenceFaults(string faultBoundary)
        {
            using (var f = Start())
            {
                Seed(Seller(f), 4);
                var offer = SeedOffer(Seller(f), Buyer(f), 3).Offer;
                var accept = Decision(EconomyOperation.TradeAccept, offer.OfferId);
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == faultBoundary) throw new IOException("injected"); };
                var result = f.Buy(accept);
                Assert.IsFalse(result.Success);
                AgencyEconomyStore.PersistenceCheckpoint = null;
                if (faultBoundary == "before-document")
                {
                    Assert.AreEqual(3, Stored(offer.OfferId).Escrow.Sum(l => l.Units), "Nothing committed; the escrow stays.");
                    Assert.AreEqual(0, Units(Buyer(f)));
                    return;
                }
                Assert.IsTrue(result.RecoveryRequired);
                AgencyVesselMap.Load(); AgencyVesselMap.RecoverJournal(); AgencyEconomyStore.Load(); f.Refresh();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.AreEqual(3, Units(Buyer(f)), "The durable accept survives the restart exactly once.");
                Assert.AreEqual(1, Units(Seller(f)));
                Assert.AreEqual(0, Stored(offer.OfferId).Escrow.Count);
            }
        }

        [TestMethod] // needs S1 (StockLotSlots for create)
        public void StockAcceptWithToolingOffIsRefusedAndTheEscrowStays()
        {
            using (var f = Start())
            {
                Seed(Seller(f), 3);
                var offer = StockOffer(Buyer(f), 2);
                Assert.IsTrue(f.Economy.Execute(offer).Success);
                GeneralSettings.SettingsStore.AgencyTooling = false;
                var result = f.Buy(Decision(EconomyOperation.TradeAccept, offer.Trade.OfferId));
                Assert.IsFalse(result.Success);
                Assert.AreEqual("Stock offers need agency tooling.", result.Reason);
                GeneralSettings.SettingsStore.AgencyTooling = true;
                Assert.AreEqual(2, Stored(offer.Trade.OfferId).Escrow.Sum(l => l.Units));
                Assert.AreEqual(0, Units(Buyer(f)));
                Assert.AreEqual(0, AgencyEconomyStore.Snapshot(Buyer(f)).Designs.Length);
            }
        }

        [TestMethod]
        public void CareerAcceptRefusesSandboxBuiltEscrowAndDeclineStillReturnsIt()
        {
            using (var f = Start())
            {
                // Offered while the server ran Sandbox, accepted after it switched to Career.
                Seed(Seller(f), 3, prepaid: 0, fundsBuilt: false);
                var offer = SeedOffer(Seller(f), Buyer(f), 2).Offer;
                var buyerFunds = AgencyEconomyStore.Snapshot(Buyer(f)).Funds;
                var result = f.Buy(Decision(EconomyOperation.TradeAccept, offer.OfferId));
                Assert.IsFalse(result.Success);
                StringAssert.Contains(result.Reason, "built without funds");
                Assert.AreEqual(buyerFunds, AgencyEconomyStore.Snapshot(Buyer(f)).Funds);
                Assert.AreEqual(2, Stored(offer.OfferId).Escrow.Sum(l => l.Units));
                Assert.IsTrue(f.Buy(Decision(EconomyOperation.TradeDecline, offer.OfferId)).Success);
                Assert.AreEqual(3, Units(Seller(f)));
            }
        }

        [TestMethod] // needs S1 (StockHeld)
        public void BuyerHeldCapRefusesTheAcceptAndKeepsTheEscrow()
        {
            using (var f = Start())
            {
                Seed(Buyer(f), StockDefaults.MaxHeldUnits - 1, prepaid: 9);
                Seed(Seller(f), 2);
                var offer = SeedOffer(Seller(f), Buyer(f), 2).Offer;
                var result = f.Buy(Decision(EconomyOperation.TradeAccept, offer.OfferId));
                Assert.IsFalse(result.Success);
                Assert.AreEqual("Buyer cannot hold more of this design.", result.Reason);
                Assert.AreEqual(2, Stored(offer.OfferId).Escrow.Sum(l => l.Units));
                Assert.AreEqual(StockDefaults.MaxHeldUnits - 1, Units(Buyer(f)));
            }
        }

        [TestMethod] // needs S1 (StockLotSlots, StockHeld)
        public void CombinedLotBoundGatesCreateAndAccept()
        {
            using (var f = Start())
            {
                // 64 distinct-term rows fill the seller's lot slots.
                for (var i = 0; i < StockDefaults.MaxLots; i++) Seed(Seller(f), 2, prepaid: 1 + i, ticks: i + 1);
                var split = f.Economy.Execute(StockOffer(Buyer(f), 1));
                Assert.IsFalse(split.Success, "Splitting a lot would need a 65th slot.");
                StringAssert.Contains(split.Reason, "Too many stock batches");
                Assert.AreEqual(StockDefaults.MaxLots, Row(Doc(), Seller(f)).Stock.Count);
                var whole = StockOffer(Buyer(f), 2);
                var created = f.Economy.Execute(whole);
                Assert.IsTrue(created.Success, "Taking a whole lot moves its slot into escrow. " + created.Reason);

                // The buyer is full too, and the escrow row's terms (sourced from the seller) match none of its rows.
                for (var i = 0; i < StockDefaults.MaxLots; i++) Seed(Buyer(f), 1, prepaid: 100 + i, ticks: i + 1);
                var accepted = f.Buy(Decision(EconomyOperation.TradeAccept, whole.Trade.OfferId));
                Assert.IsFalse(accepted.Success);
                StringAssert.Contains(accepted.Reason, "too many stock batches");
                Assert.AreEqual(2, Stored(whole.Trade.OfferId).Escrow.Sum(l => l.Units));
                Assert.AreEqual(StockDefaults.MaxLots, Row(Doc(), Buyer(f)).Stock.Count);

                // Closing returns the escrow without failing, and slots stay within the bound.
                Assert.IsTrue(f.Economy.Execute(Decision(EconomyOperation.TradeCancel, whole.Trade.OfferId)).Success);
                Assert.AreEqual(StockDefaults.MaxLots, Row(Doc(), Seller(f)).Stock.Count);
                Assert.AreEqual(2 * StockDefaults.MaxLots, Units(Seller(f)));
            }
        }

        // ---- Close paths ----

        [DataTestMethod]
        [DataRow("decline")]
        [DataRow("cancel")]
        [DataRow("expiry")]
        [DataRow("owner-change")]
        public void EveryCloseReturnsTheEscrowExactlyOnce(string path)
        {
            using (var f = Start())
            {
                var lot = Seed(Seller(f), 5);
                var stored = SeedOffer(Seller(f), Buyer(f), 3);
                Assert.AreEqual(2, Units(Seller(f)));
                var expected = path == "decline" ? TradeOfferStatus.Declined : path == "cancel" ? TradeOfferStatus.Cancelled : path == "expiry" ? TradeOfferStatus.Expired : TradeOfferStatus.Invalidated;
                EconomyCommand decision = null;
                var buyerOwner = AgencyStore.Agencies[Buyer(f)].OwnerUniqueId;
                try
                {
                    switch (path)
                    {
                        case "decline": decision = Decision(EconomyOperation.TradeDecline, stored.Offer.OfferId); Assert.IsTrue(f.Buy(decision).Success); break;
                        case "cancel": decision = Decision(EconomyOperation.TradeCancel, stored.Offer.OfferId); Assert.IsTrue(f.Economy.Execute(decision).Success); break;
                        case "expiry": AgencyEconomyStore.UtcNow = () => DateTime.UtcNow.AddHours(25); AgencyEconomyStore.CancelPending(); break;
                        case "owner-change": AgencyStore.Agencies[Buyer(f)].OwnerUniqueId = "someone-else"; AgencyEconomyStore.CancelPending(); break;
                    }
                    var closed = Stored(stored.Offer.OfferId);
                    Assert.AreEqual(expected, closed.Offer.Status);
                    Assert.AreEqual(2, closed.Offer.Revision);
                    Assert.AreEqual(0, closed.Escrow.Count);
                    Assert.AreEqual(5, Units(Seller(f)));
                    var row = Row(Doc(), Seller(f)).Stock.Single();
                    Assert.AreEqual(lot.LotId, row.LotId, "Returned units merge into the same-terms row, which keeps its LotId.");

                    // A replay returns the stored receipt, a fresh decision fails, and a further sweep changes nothing.
                    if (path == "decline") { Assert.IsTrue(f.Buy(decision).Success); Assert.IsFalse(f.Buy(Decision(EconomyOperation.TradeDecline, stored.Offer.OfferId, 2)).Success); }
                    if (path == "cancel") { Assert.IsTrue(f.Economy.Execute(decision).Success); Assert.IsFalse(f.Economy.Execute(Decision(EconomyOperation.TradeCancel, stored.Offer.OfferId, 2)).Success); }
                    AgencyEconomyStore.CancelPending();
                    Assert.AreEqual(5, Units(Seller(f)), "Never returned twice.");
                    Assert.AreEqual(0, Units(Buyer(f)));
                }
                finally { AgencyStore.Agencies[Buyer(f)].OwnerUniqueId = buyerOwner; }
            }
        }

        [TestMethod]
        public void TitleChangeOnACombinedVesselAndStockOfferReturnsTheEscrow()
        {
            using (var f = Start())
            {
                var id = Guid.NewGuid();
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(AgencyTradeTest.Proto(id, 61));
                AgencyVesselMap.Set(id, Seller(f));
                Seed(Seller(f), 4);
                var stored = SeedOffer(Seller(f), Buyer(f), 4, id);
                Assert.AreEqual(0, Units(Seller(f)));
                AgencyVesselMap.Set(id, Buyer(f));
                var closed = Stored(stored.Offer.OfferId);
                Assert.AreEqual(TradeOfferStatus.Invalidated, closed.Offer.Status);
                Assert.AreEqual(0, closed.Escrow.Count);
                Assert.AreEqual(4, Units(Seller(f)));
                Assert.IsFalse(f.Buy(Decision(EconomyOperation.TradeAccept, stored.Offer.OfferId)).Success);
                Assert.AreEqual(0, Units(Buyer(f)));
            }
        }

        [TestMethod]
        public void EscrowOfADeletedSellerIsDiscardedLiveAndRefusedStrict()
        {
            using (var f = Start())
            {
                var gone = Guid.NewGuid();
                var escrow = new List<DesignStockLot> { new DesignStockLot { LotId = Guid.NewGuid(), Fingerprint = Fp, Units = 2, PrepaidPerUnit = 5, LaunchMultiplier = .07, BuilderAgencyId = gone, FundsBuilt = true } };
                var stored = SeedOffer(gone, Buyer(f), 2, escrow: escrow);
                var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<EconomyDocument>(Newtonsoft.Json.JsonConvert.SerializeObject(Doc()));
                var close = typeof(AgencyEconomyStore).GetMethod("CloseOffer", BindingFlags.Static | BindingFlags.NonPublic);
                var strict = Assert.ThrowsException<TargetInvocationException>(() => close.Invoke(null, new object[] { copy, copy.TradeOffers[stored.Offer.OfferId], TradeOfferStatus.Cancelled, true }));
                Assert.IsInstanceOfType(strict.InnerException, typeof(InvalidDataException), "The downgrade tool must not drop escrow silently.");

                AgencyEconomyStore.CancelPending();
                var closed = Stored(stored.Offer.OfferId);
                Assert.AreEqual(TradeOfferStatus.Invalidated, closed.Offer.Status);
                Assert.AreEqual(0, closed.Escrow.Count);
                Assert.IsFalse(Doc().Agencies.ContainsKey(gone), "No row is invented for a deleted agency.");
            }
        }

        [TestMethod] // needs S1 (StockLotSlots, StockHeld)
        public void AcceptAndCancelRaceResolvesToExactlyOneOutcome()
        {
            using (var f = Start())
            {
                Seed(Seller(f), 6);
                var first = StockOffer(Buyer(f), 3);
                Assert.IsTrue(f.Economy.Execute(first).Success);
                Assert.IsTrue(f.Economy.Execute(Decision(EconomyOperation.TradeCancel, first.Trade.OfferId)).Success);
                Assert.IsFalse(f.Buy(Decision(EconomyOperation.TradeAccept, first.Trade.OfferId)).Success, "Cancelled first: the accept loses.");
                Assert.AreEqual(6, Units(Seller(f))); Assert.AreEqual(0, Units(Buyer(f)));

                var second = StockOffer(Buyer(f), 3);
                Assert.IsTrue(f.Economy.Execute(second).Success);
                Assert.IsTrue(f.Buy(Decision(EconomyOperation.TradeAccept, second.Trade.OfferId)).Success);
                Assert.IsFalse(f.Economy.Execute(Decision(EconomyOperation.TradeCancel, second.Trade.OfferId)).Success, "Accepted first: the cancel loses.");
                Assert.AreEqual(3, Units(Seller(f))); Assert.AreEqual(3, Units(Buyer(f)));
            }
        }

        // ---- Resale and conservation ----

        [TestMethod] // needs S1 (StockLotSlots, StockHeld)
        public void ResaleWithoutToolingRoundTripConservesUnitsAndPrepayment()
        {
            using (var f = Start())
            {
                Seed(Seller(f), 3, prepaid: 5, ticks: 1);
                Seed(Seller(f), 2, prepaid: 8, ticks: 2);
                double Prepaid() => Doc().Agencies.Values.SelectMany(a => a.Stock).Sum(l => l.Units * l.PrepaidPerUnit) + Doc().TradeOffers.Values.SelectMany(o => o.Escrow).Sum(l => l.Units * l.PrepaidPerUnit);
                var before = Prepaid();

                var sale = StockOffer(Buyer(f), 4, buyerFunds: 300);
                Assert.IsTrue(f.Economy.Execute(sale).Success);
                Assert.IsTrue(f.Buy(Decision(EconomyOperation.TradeAccept, sale.Trade.OfferId)).Success);
                Assert.AreEqual(0, AgencyEconomyStore.Snapshot(Buyer(f)).Designs.Length, "The reseller holds no tooling.");

                // The buyer sells three back using the craft file it received.
                var resale = StockOffer(Seller(f), 3, buyerFunds: 0);
                resale.Trade.SellerFunds = 0;
                resale.Trade.BlueprintData = AgencyEconomyStore.Snapshot(Buyer(f)).Entitlements.Single().BlueprintData;
                var created = f.Buy(resale);
                Assert.IsTrue(created.Success, created.Reason);
                Assert.IsNull(Stored(resale.Trade.OfferId).Design);
                Assert.IsTrue(f.Economy.Execute(Decision(EconomyOperation.TradeAccept, resale.Trade.OfferId)).Success);

                Assert.AreEqual(5, Units(Seller(f)) + Units(Buyer(f)));
                Assert.AreEqual(4, Units(Seller(f)));
                Assert.AreEqual(before, Prepaid(), 1e-9, "No trade creates or destroys prepaid value.");
                Assert.IsTrue(Row(Doc(), Seller(f)).Stock.Where(l => l.SourceAgencyId == Buyer(f)).All(l => l.BuilderAgencyId == Seller(f)), "Builder survives resale; source names the last seller.");
            }
        }

        // ---- Research ----

        [TestMethod]
        public void ResearchAcceptsALotIdOnlyForThePreparedLaunchReservingIt()
        {
            using (var f = Start())
            {
                var buyer = Buyer(f);
                var lotId = Guid.NewGuid();
                var launch = Guid.NewGuid();
                var document = Doc();
                document.Launches[launch] = new EconomyLaunch { LaunchId = launch, AgencyId = buyer, State = LaunchState.Prepared, Stock = new StockTerms { LotId = lotId, Fingerprint = Fp, PrepaidPerUnit = 5, LaunchMultiplier = .07, BuilderAgencyId = Seller(f), SourceAgencyId = Seller(f), FundsBuilt = true } };
                var designId = Guid.NewGuid();
                var permanentId = Guid.NewGuid();
                document.Entitlements[buyer] = new List<TradeEntitlement>
                {
                    new TradeEntitlement { EntitlementId = designId, Kind = TradeEntitlementKind.StockDesign, Fingerprint = Fp },
                    new TradeEntitlement { EntitlementId = permanentId, Kind = TradeEntitlementKind.Permanent, Fingerprint = Fp }
                };
                var id = Guid.NewGuid();
                var vessel = new Server.System.Vessel.Classes.Vessel(AgencyTradeTest.Proto(id, 71));
                var other = new Server.System.Vessel.Classes.Vessel("pid = " + Guid.NewGuid().ToString("N") + "\nname = Other\nroot = 0\nPART\n{\nname = tank\nuid = 72\n}\n" + string.Concat(new[] { "ORBIT", "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }.Select(n => n + "\n{\n}\n")));

                Assert.IsTrue(AgencyEconomyStore.ValidateTradeEntitlement(buyer, lotId, vessel, launch));
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, lotId, other, launch), "Wrong part list.");
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, lotId, vessel, Guid.NewGuid()), "Another launch.");
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, lotId, vessel, Guid.Empty), "No launch.");
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(Seller(f), lotId, vessel, launch), "Another agency.");
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, designId, vessel, launch), "A StockDesign entitlement grants no research.");
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, designId, vessel, Guid.Empty), "A StockDesign entitlement grants no research.");

                GeneralSettings.SettingsStore.AgencyTooling = false;
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, lotId, vessel, launch), "Tooling off.");
                GeneralSettings.SettingsStore.AgencyTooling = true;

                GeneralSettings.SettingsStore.AgencyTrade = false;
                Assert.IsTrue(AgencyEconomyStore.ValidateTradeEntitlement(buyer, lotId, vessel, launch), "Bought stock still launches with trade off.");
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, permanentId, vessel, Guid.Empty), "Trade off still ignores trade entitlements.");
                GeneralSettings.SettingsStore.AgencyTrade = true;
                Assert.IsTrue(AgencyEconomyStore.ValidateTradeEntitlement(buyer, permanentId, vessel, Guid.Empty), "Permanent entitlements are unchanged.");

                document.Launches[launch].State = LaunchState.Registered;
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, lotId, vessel, launch), "Only a Prepared launch.");
            }
        }

        // ---- Entitlements and validation ----

        [TestMethod]
        public void DeliveredStockDesignDropsItsBytesAndKeepsItsHash()
        {
            using (var f = Start())
            {
                var bytes = Encoding.UTF8.GetBytes(Craft);
                var id = Guid.NewGuid();
                Doc().Entitlements[Buyer(f)] = new List<TradeEntitlement> { new TradeEntitlement { EntitlementId = id, Kind = TradeEntitlementKind.StockDesign, Fingerprint = Fp, BlueprintName = "Stock Probe", Editor = "VAB", BlueprintData = bytes, BlueprintHash = Sha(bytes) } };
                var delivered = f.Buy(new EconomyCommand { Operation = EconomyOperation.TradeDelivered, Trade = new TradeCommand { EntitlementId = id } });
                Assert.IsTrue(delivered.Success, delivered.Reason);
                var held = AgencyEconomyStore.Snapshot(Buyer(f)).Entitlements.Single();
                Assert.IsTrue(held.Delivered);
                Assert.AreEqual(0, held.BlueprintData.Length);
                Assert.AreEqual(Sha(bytes), held.BlueprintHash);
            }
        }

        [DataTestMethod]
        [DataRow("units-mismatch")]
        [DataRow("closed-with-escrow")]
        [DataRow("other-fingerprint")]
        [DataRow("non-stock-escrow")]
        [DataRow("stock-design-redeemed")]
        public void CorruptStockTradeStateFailsPersist(string kind)
        {
            using (var f = Start())
            {
                Seed(Seller(f), 4);
                var stored = SeedOffer(Seller(f), Buyer(f), 3);
                switch (kind)
                {
                    case "units-mismatch": stored.Offer.StockUnits = 2; break;
                    case "closed-with-escrow": stored.Offer.Status = TradeOfferStatus.Declined; break;
                    case "other-fingerprint": stored.Escrow[0].Fingerprint = "other"; break;
                    case "non-stock-escrow": stored.Offer.DesignMode = TradeDesignMode.SingleLaunch; break;
                    case "stock-design-redeemed": Doc().Entitlements[Buyer(f)] = new List<TradeEntitlement> { new TradeEntitlement { EntitlementId = Guid.NewGuid(), Kind = TradeEntitlementKind.StockDesign, Fingerprint = Fp, Redeemed = true } }; break;
                }
                var funds = f.Economy.Snapshot.Funds;
                Assert.IsFalse(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = -1 }).Success, kind);
                Assert.AreEqual(funds, f.Economy.Snapshot.Funds);
            }
        }

        [TestMethod] // needs S1 (TryStoreBlueprint)
        public void ToolingAndDesignAcceptGivesTheBuyerTheBlueprintRef()
        {
            using (var f = Start())
            {
                var offer = f.Offer(true);
                Assert.IsTrue(f.Economy.Execute(offer).Success);
                Assert.IsTrue(f.Buy(Decision(EconomyOperation.TradeAccept, offer.Trade.OfferId)).Success);
                var reference = Row(Doc(), Buyer(f)).Blueprints[offer.Trade.DesignFingerprint];
                Assert.AreEqual(Sha(offer.Trade.BlueprintData), reference.Hash);
                Assert.AreEqual("VAB", reference.Editor);
            }
        }

        [TestMethod]
        public void ToolingAndDesignAcceptKeepsTheBuyersExistingBlueprintRef()
        {
            using (var f = Start())
            {
                var offer = f.Offer(true);
                Assert.IsTrue(f.Economy.Execute(offer).Success);
                var mine = new ToolingBlueprintRef { Fingerprint = offer.Trade.DesignFingerprint, Name = "Mine", Editor = "SPH", Hash = new string('a', 64), Size = 10, SavedUtcTicks = 1 };
                Row(Doc(), Buyer(f)).Blueprints[offer.Trade.DesignFingerprint] = mine;
                var accepted = f.Buy(Decision(EconomyOperation.TradeAccept, offer.Trade.OfferId));
                Assert.IsTrue(accepted.Success, accepted.Reason);
                var kept = Row(Doc(), Buyer(f)).Blueprints[offer.Trade.DesignFingerprint];
                Assert.AreEqual(mine.Hash, kept.Hash);
                Assert.AreEqual("SPH", kept.Editor);
                Assert.AreEqual(1, AgencyEconomyStore.Snapshot(Buyer(f)).Designs.Length);
            }
        }
    }
}
