using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Server.Agency;
using Server.Settings.Structures;
using Server.System;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ServerTest.Agency
{
    /// <summary>Trade mode two: a design sold with one prepaid launch and no tooling.</summary>
    [TestClass, DoNotParallelize]
    public class AgencyTradeModesTest
    {
        private const string Craft = "ship = Probe\ntype = VAB\nPART\n{\npart = probe_1\n}\n";
        private static ToolingCargo Kit() => new ToolingCargo { Name = "kit", Count = 1, UnitCost = 40, ContainerPartIndex = 0 };
        private static ToolingManifest Probe(params ToolingCargo[] cargo) => new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } }, Cargo = cargo };

        // Untooled launches cost 2x here, as shipped; tooled launches cost 0.1x.
        private static AgencyTradeTest.Fixture Start()
        {
            var fixture = new AgencyTradeTest.Fixture();
            AgencyEconomyTest.Fixture.UseRates(new ToolingRates(10, .1, 2, .1));
            return fixture;
        }

        private static EconomyCommand SingleOffer(AgencyTradeTest.Fixture f, ToolingManifest manifest = null, double sellerFunds = 100, double buyerFunds = 1000)
        {
            manifest = manifest ?? Probe();
            return new EconomyCommand
            {
                Operation = EconomyOperation.TradeCreate, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest),
                Trade = new TradeCommand { OfferId = Guid.NewGuid(), BuyerAgencyId = f.Buyer.AgencyId, DesignMode = TradeDesignMode.SingleLaunch, DesignFingerprint = ToolingPolicy.Fingerprint(manifest), BlueprintName = "Purchased Probe", Editor = "VAB", BlueprintData = Encoding.UTF8.GetBytes(Craft), SellerFunds = sellerFunds, BuyerFunds = buyerFunds }
            };
        }

        private static EconomyCommand Accept(Guid offer) => new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer, ExpectedRevision = 1 } };
        private static TradeEntitlement[] Held(AgencyTradeTest.Fixture f) => AgencyEconomyStore.Snapshot(f.Buyer.AgencyId).Entitlements;
        private static double BuyerFunds(AgencyTradeTest.Fixture f) => AgencyEconomyStore.Snapshot(f.Buyer.AgencyId).Funds;

        private static TradeEntitlement BuyVoucher(AgencyTradeTest.Fixture f, EconomyCommand offer = null)
        {
            offer = offer ?? SingleOffer(f);
            var before = Held(f).Select(e => e.EntitlementId).ToArray();
            var created = f.Economy.Execute(offer);
            Assert.IsTrue(created.Success, created.Reason);
            var accepted = f.Buy(Accept(offer.Trade.OfferId));
            Assert.IsTrue(accepted.Success, accepted.Reason);
            return Held(f).Single(e => !before.Contains(e.EntitlementId));
        }

        private static EconomyResult Prepare(AgencyTradeTest.Fixture f, ToolingManifest manifest, Guid voucher, out Guid launch)
        {
            launch = Guid.NewGuid();
            return f.Buy(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = launch, VoucherId = voucher, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
        }

        private static Guid Register(AgencyTradeTest.Fixture f, EconomyResult prepared, Guid launch, uint uid)
        {
            var id = Guid.NewGuid();
            var raw = AgencyTradeTest.Proto(id, uid);
            var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
            message.VesselId = id; message.EconomyLaunchId = launch; message.EconomyLaunchToken = prepared.LaunchToken; message.EconomyManifestIndices = new[] { 0 };
            var registered = AgencyEconomyStore.Register(f.Buyer, message, raw, new Server.System.Vessel.Classes.Vessel(raw));
            Assert.IsTrue(registered.Success, registered.Reason);
            return id;
        }

        // A craft whose cover search exceeds ToolingPolicy.MaxSearchStates: twenty distinct parts, each already tooled alone.
        private static ToolingManifest Complex() => new ToolingManifest { Parts = Enumerable.Range(0, 20).Select(i => new ToolingPart { Name = "n" + i, UnitCost = 10 }).ToArray() };
        private static void ToolSingles(AgencyTradeTest.Fixture f)
        {
            for (var i = 0; i < 20; i++)
            {
                var single = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "n" + i, UnitCost = 10 } } };
                var tooled = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = single, ManifestHash = ToolingPolicy.ManifestHash(single) });
                Assert.IsTrue(tooled.Success, tooled.Reason);
            }
        }

        [TestMethod]
        public void OverLimitCraftStillPreparesLaunchAtUntooledPrice()
        {
            using (var f = Start())
            {
                ToolSingles(f);
                var before = f.Economy.Snapshot.Funds;
                var manifest = Complex();
                var prepared = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
                Assert.IsTrue(prepared.Success, prepared.Reason);
                Assert.IsTrue(prepared.Quote.CoverSearchExhausted);
                Assert.IsFalse(prepared.Quote.AlreadyTooled);
                Assert.AreEqual(200 * 2d, prepared.Quote.LaunchCost, 1e-9);
                Assert.AreEqual(before - 400d, f.Economy.Snapshot.Funds, 1e-9);
            }
        }

        [TestMethod]
        public void OverLimitCraftStillCreatesSingleLaunchOffer()
        {
            using (var f = Start())
            {
                ToolSingles(f);
                var manifest = Complex();
                var blueprint = "ship = Big\ntype = VAB\n" + string.Concat(Enumerable.Range(0, 20).Select(i => "PART\n{\npart = n" + i + "_" + (100 + i) + "\n}\n"));
                var offer = SingleOffer(f, manifest);
                offer.Trade.BlueprintData = Encoding.UTF8.GetBytes(blueprint);
                var created = f.Economy.Execute(offer);
                Assert.IsTrue(created.Success, created.Reason);
            }
        }

        private static object Document() => typeof(AgencyEconomyStore).GetField("_document", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        [DataTestMethod]
        [DataRow("before-document")]
        [DataRow("committed")]
        [DataRow("projections-written")]
        public void SellerWithoutToolingSellsOneLaunchAndPrepaysItAtomically(string faultBoundary)
        {
            using (var f = Start())
            {
                var offer = SingleOffer(f);
                Assert.IsTrue(f.Economy.Execute(offer).Success);
                Assert.AreEqual(0, f.Economy.Snapshot.Designs.Length, "The seller never tooled this design.");
                var accept = Accept(offer.Trade.OfferId);
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == faultBoundary) throw new IOException("injected"); };
                var failed = f.Buy(accept);
                Assert.IsFalse(failed.Success);
                AgencyEconomyStore.PersistenceCheckpoint = null;
                if (faultBoundary == "before-document")
                {
                    Assert.AreEqual(50000d, f.Economy.Snapshot.Funds);
                    Assert.AreEqual(20000d, BuyerFunds(f));
                    Assert.AreEqual(0, Held(f).Length);
                    accept.Sequence = 0;
                    Assert.IsTrue(f.Buy(accept).Success);
                }
                else
                {
                    Assert.IsTrue(failed.RecoveryRequired);
                    AgencyVesselMap.Load(); AgencyVesselMap.RecoverJournal(); AgencyEconomyStore.Load(); f.Refresh();
                }
                // 50000 - 100 (price) + 1000 (buyer pays) - 200 (prepaid launch at the untooled 2x).
                Assert.AreEqual(50700d, f.Economy.Snapshot.Funds);
                Assert.AreEqual(19100d, BuyerFunds(f));
                var voucher = Held(f).Single();
                Assert.AreEqual(TradeEntitlementKind.SingleLaunch, voucher.Kind);
                Assert.AreEqual(200d, voucher.PrepaidFunds);
                Assert.AreEqual(2d, voucher.LaunchMultiplier);
                Assert.IsFalse(voucher.Redeemed);
                Assert.AreEqual(Guid.Empty, voucher.LaunchId);
                Assert.AreEqual(0, AgencyEconomyStore.Snapshot(f.Buyer.AgencyId).Designs.Length, "No tooling is transferred.");
                Assert.AreEqual(0, f.Economy.Snapshot.Designs.Length);
            }
        }

        [TestMethod]
        public void ToolingSellerPrepaysAtTheTooledRate()
        {
            using (var f = Start())
            {
                var manifest = Probe();
                Assert.IsTrue(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }).Success);
                var seller = f.Economy.Snapshot.Funds;
                var voucher = BuyVoucher(f);
                Assert.AreEqual(10d, voucher.PrepaidFunds, 1e-9);
                Assert.AreEqual(.1, voucher.LaunchMultiplier, 1e-9);
                Assert.AreEqual(seller - 100 + 1000 - 10, f.Economy.Snapshot.Funds, 1e-9);
                Assert.AreEqual(1, f.Economy.Snapshot.Designs.Length, "The seller keeps their tooling.");
            }
        }

        [TestMethod]
        public void SellerWhoCannotPrepayBlocksTheAcceptWithoutChangingBalances()
        {
            using (var f = Start())
            {
                var offer = SingleOffer(f, sellerFunds: 0, buyerFunds: 0);
                Assert.IsTrue(f.Economy.Execute(offer).Success);
                Assert.IsTrue(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = -49950 }).Success);
                var accepted = f.Buy(Accept(offer.Trade.OfferId));
                Assert.IsFalse(accepted.Success);
                StringAssert.Contains(accepted.Reason, "prepay");
                Assert.AreEqual(50d, f.Economy.Snapshot.Funds);
                Assert.AreEqual(20000d, BuyerFunds(f));
                Assert.AreEqual(0, Held(f).Length);
                Assert.AreEqual(TradeOfferStatus.Open, f.Economy.Snapshot.Offers.Single().Status);
            }
        }

        [DataTestMethod]
        [DataRow("tooling-off")]
        [DataRow("fingerprint-mismatch")]
        [DataRow("hash-mismatch")]
        [DataRow("no-manifest")]
        [DataRow("no-fingerprint")]
        public void InvalidSingleLaunchOffersAreRejected(string kind)
        {
            using (var f = Start())
            {
                var offer = SingleOffer(f);
                switch (kind)
                {
                    case "tooling-off": GeneralSettings.SettingsStore.AgencyTooling = false; break;
                    case "fingerprint-mismatch": offer.Manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "other", UnitCost = 100 } } }; offer.ManifestHash = ToolingPolicy.ManifestHash(offer.Manifest); break;
                    case "hash-mismatch": offer.ManifestHash = "bogus"; break;
                    case "no-manifest": offer.Manifest = null; offer.ManifestHash = null; break;
                    case "no-fingerprint": offer.Trade.DesignFingerprint = null; break;
                }
                Assert.IsFalse(f.Economy.Execute(offer).Success, kind);
                Assert.AreEqual(0, f.Economy.Snapshot.Offers.Length);
            }
        }

        [TestMethod]
        public void DesignOnlySingleLaunchOfferWithNoCurrencyIsAnAsset()
        {
            using (var f = Start())
            {
                var offer = SingleOffer(f, sellerFunds: 0, buyerFunds: 0);
                var created = f.Economy.Execute(offer);
                Assert.IsTrue(created.Success, created.Reason);
                Assert.IsTrue(f.Buy(Accept(offer.Trade.OfferId)).Success);
                Assert.AreEqual(1, Held(f).Length);
            }
        }

        [TestMethod]
        public void AcceptingASingleLaunchOfferIsRefusedWhileToolingIsOff()
        {
            using (var f = Start())
            {
                var offer = SingleOffer(f);
                Assert.IsTrue(f.Economy.Execute(offer).Success);
                GeneralSettings.SettingsStore.AgencyTooling = false;
                var accepted = f.Buy(Accept(offer.Trade.OfferId));
                Assert.IsFalse(accepted.Success);
                Assert.AreEqual(0, Held(f).Length);
                Assert.AreEqual(20000d, BuyerFunds(f));
            }
        }

        [TestMethod]
        public void VoucherLaunchLifecycleChargesReleasesRedeemsAndThenReturnsToUntooledPricing()
        {
            using (var f = Start())
            {
                var voucher = BuyVoucher(f);
                var manifest = Probe(Kit());
                // Wrong design: the voucher simply does not apply, and nothing is charged or reserved.
                var other = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "other", UnitCost = 100 } } };
                var wrong = Prepare(f, other, voucher.EntitlementId, out _);
                Assert.IsFalse(wrong.Success);
                StringAssert.Contains(wrong.Reason, "does not match");
                Assert.AreEqual(19100d, BuyerFunds(f));
                // Reserve: cargo 40, and the prepaid 200 covers the 200 of parts.
                var prepared = Prepare(f, manifest, voucher.EntitlementId, out var launch);
                Assert.IsTrue(prepared.Success, prepared.Reason);
                Assert.AreEqual(40d, prepared.Quote.LaunchCost);
                Assert.AreEqual(19060d, BuyerFunds(f));
                Assert.AreEqual(launch, Held(f).Single().LaunchId);
                Assert.IsFalse(Prepare(f, manifest, voucher.EntitlementId, out _).Success, "A reserved voucher cannot be reserved twice.");
                Assert.AreEqual(19060d, BuyerFunds(f));
                // CancelLaunch releases it and refunds.
                Assert.IsTrue(f.Buy(new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = launch }).Success);
                Assert.AreEqual(Guid.Empty, Held(f).Single().LaunchId);
                Assert.AreEqual(19100d, BuyerFunds(f));
                // A disconnect releases it.
                Assert.IsTrue(Prepare(f, manifest, voucher.EntitlementId, out _).Success);
                AgencyEconomyStore.CancelPending(f.Buyer);
                Assert.AreEqual(Guid.Empty, Held(f).Single().LaunchId);
                Assert.AreEqual(19100d, BuyerFunds(f));
                f.Refresh();
                // A server restart releases it.
                Assert.IsTrue(Prepare(f, manifest, voucher.EntitlementId, out _).Success);
                AgencyEconomyStore.Load(); f.Refresh();
                Assert.AreEqual(Guid.Empty, Held(f).Single().LaunchId);
                Assert.AreEqual(19100d, BuyerFunds(f));
                // Register redeems it.
                var final = Prepare(f, manifest, voucher.EntitlementId, out var finalLaunch);
                Assert.IsTrue(final.Success, final.Reason);
                Register(f, final, finalLaunch, 811);
                Assert.IsTrue(Held(f).Single().Redeemed);
                Assert.AreEqual(finalLaunch, Held(f).Single().LaunchId);
                var spent = Prepare(f, manifest, voucher.EntitlementId, out _);
                Assert.IsFalse(spent.Success, "A redeemed voucher cannot be used again.");
                Assert.AreEqual(19060d, BuyerFunds(f));
                // The design is now an ordinary untooled one: 2x its parts plus inventory.
                var normal = Prepare(f, manifest, Guid.Empty, out _);
                Assert.IsTrue(normal.Success, normal.Reason);
                Assert.AreEqual(240d, normal.Quote.LaunchCost);
                Assert.AreEqual(18820d, BuyerFunds(f));
            }
        }

        [TestMethod]
        public void ExtraPartCostBeyondThePrepaymentIsToppedUpByTheBuyer()
        {
            using (var f = Start())
            {
                var voucher = BuyVoucher(f);
                // The seller priced the part at 100 (prepaid 200). This buyer's part costs 130, so 260 - 200 = 60 is extra.
                var pricier = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 130 } } };
                var prepared = Prepare(f, pricier, voucher.EntitlementId, out _);
                Assert.IsTrue(prepared.Success, prepared.Reason);
                Assert.AreEqual(60d, prepared.Quote.LaunchCost, 1e-9);
            }
        }

        [TestMethod]
        public void RevertRestoresTheVoucherButRevertLaunchKeepsItSpentAndRecoveryNeverExceedsThePrepayment()
        {
            using (var f = Start())
            {
                var voucher = BuyVoucher(f);
                var manifest = Probe(Kit());
                var first = Prepare(f, manifest, voucher.EntitlementId, out var launch);
                Register(f, first, launch, 821);
                Assert.IsTrue(Held(f).Single().Redeemed);
                var reverted = f.Buy(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = launch });
                Assert.IsTrue(reverted.Success, reverted.Reason);
                Assert.IsFalse(Held(f).Single().Redeemed);
                Assert.AreEqual(Guid.Empty, Held(f).Single().LaunchId);
                Assert.AreEqual(19100d, BuyerFunds(f));
                // Same voucher again; this time RevertLaunch (restart from the pad) leaves it spent.
                var second = Prepare(f, manifest, voucher.EntitlementId, out var secondLaunch);
                Assert.IsTrue(second.Success, second.Reason);
                var vessel = Register(f, second, secondLaunch, 822);
                var kept = f.Buy(new EconomyCommand { Operation = EconomyOperation.RevertLaunch, LaunchId = secondLaunch });
                Assert.IsTrue(kept.Success, kept.Reason);
                Assert.IsTrue(Held(f).Single().Redeemed);
                var before = BuyerFunds(f);
                // Recovery refunds stock x factor x multiplier but never more than was paid at the voucher's multiplier.
                var recovered = f.Buy(new EconomyCommand { Operation = EconomyOperation.Recover, VesselId = vessel, RecoveryFactor = 1, RecoveredParts = new[] { new RecoveryPart { FlightId = 822, StockValue = 1000 } } });
                Assert.IsTrue(recovered.Success, recovered.Reason);
                Assert.AreEqual(200d, BuyerFunds(f) - before, 1e-9);
            }
        }

        [TestMethod]
        public void SpentVoucherIsPrunedOnceItsLaunchIsRetired()
        {
            using (var f = Start())
            {
                var voucher = BuyVoucher(f);
                var manifest = Probe();
                var prepared = Prepare(f, manifest, voucher.EntitlementId, out var launch);
                var vessel = Register(f, prepared, launch, 831);
                Assert.IsTrue(f.Buy(new EconomyCommand { Operation = EconomyOperation.Recover, VesselId = vessel, RecoveryFactor = 1, RecoveredParts = new[] { new RecoveryPart { FlightId = 831, StockValue = 100 } } }).Success);
                Assert.AreEqual(1, Held(f).Length, "The launch is still within its live session.");
                AgencyEconomyStore.CancelPending(f.Buyer);
                Assert.AreEqual(0, Held(f).Length);
            }
        }

        [TestMethod]
        public void TwoPurchasesOfTheSameDesignGiveTwoVouchersAndLaterPermanentRightsStillAdd()
        {
            using (var f = Start())
            {
                var one = BuyVoucher(f);
                var two = BuyVoucher(f);
                Assert.AreNotEqual(one.EntitlementId, two.EntitlementId);
                Assert.AreEqual(2, Held(f).Length);
                // Tooling plus design of the same part list after a voucher is its own permanent entitlement.
                var tooling = f.Offer(true);
                Assert.IsTrue(f.Economy.Execute(tooling).Success);
                Assert.IsTrue(f.Buy(Accept(tooling.Trade.OfferId)).Success);
                Assert.AreEqual(3, Held(f).Length);
                Assert.AreEqual(1, Held(f).Count(e => e.Kind == TradeEntitlementKind.Permanent));
                Assert.AreEqual(1, AgencyEconomyStore.Snapshot(f.Buyer.AgencyId).Designs.Length);
            }
        }

        [TestMethod]
        public void ResearchCheckAcceptsAVoucherOnlyForTheLaunchThatReservedIt()
        {
            using (var f = Start())
            {
                var voucher = BuyVoucher(f);
                var id = Guid.NewGuid();
                var vessel = new Server.System.Vessel.Classes.Vessel(AgencyTradeTest.Proto(id, 841));
                var buyer = f.Buyer.AgencyId;
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, voucher.EntitlementId, vessel, Guid.NewGuid()), "Unreserved.");
                var prepared = Prepare(f, Probe(), voucher.EntitlementId, out var launch);
                Assert.IsTrue(prepared.Success, prepared.Reason);
                Assert.IsTrue(AgencyEconomyStore.ValidateTradeEntitlement(buyer, voucher.EntitlementId, vessel, launch));
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, voucher.EntitlementId, vessel, Guid.NewGuid()), "Another launch.");
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, voucher.EntitlementId, vessel, Guid.Empty), "No launch.");
                GeneralSettings.SettingsStore.AgencyTooling = false;
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, voucher.EntitlementId, vessel, launch), "Tooling off.");
                GeneralSettings.SettingsStore.AgencyTooling = true;
                Register(f, prepared, launch, 841);
                Assert.IsFalse(AgencyEconomyStore.ValidateTradeEntitlement(buyer, voucher.EntitlementId, vessel, launch), "Redeemed.");
            }
        }

        [TestMethod]
        public void NonCareerModesNeverChargeOrCreditFundsForVoucherLaunches()
        {
            var mode = GeneralSettings.SettingsStore.GameMode;
            try
            {
                using (var f = Start())
                {
                    GeneralSettings.SettingsStore.GameMode = GameMode.Science;
                    var voucher = BuyVoucher(f);
                    Assert.AreEqual(0d, voucher.PrepaidFunds, "No funds exist to prepay.");
                    var funds = BuyerFunds(f);
                    var manifest = Probe(Kit());
                    var prepared = Prepare(f, manifest, voucher.EntitlementId, out var launch);
                    Assert.IsTrue(prepared.Success, prepared.Reason);
                    Assert.AreEqual(funds, BuyerFunds(f));
                    Assert.IsTrue(f.Buy(new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = launch }).Success);
                    Assert.AreEqual(funds, BuyerFunds(f), "A cancel must not credit funds that were never taken.");
                    var again = Prepare(f, manifest, voucher.EntitlementId, out var second);
                    Register(f, again, second, 851);
                    Assert.IsTrue(f.Buy(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = second }).Success);
                    Assert.AreEqual(funds, BuyerFunds(f), "A revert must not credit funds that were never taken.");
                    Assert.IsFalse(Held(f).Single().Redeemed);
                }
            }
            finally { GeneralSettings.SettingsStore.GameMode = mode; }
        }

        [TestMethod]
        public void AcceptOutsideCareerStoresNoPrepayEvenIfTheOfferPromisedOne()
        {
            var mode = GeneralSettings.SettingsStore.GameMode;
            try
            {
                using (var f = Start())
                {
                    var offer = SingleOffer(f);
                    var created = f.Economy.Execute(offer);
                    Assert.IsTrue(created.Success, created.Reason);
                    GeneralSettings.SettingsStore.GameMode = GameMode.Science;
                    var accepted = f.Buy(Accept(offer.Trade.OfferId));
                    Assert.IsTrue(accepted.Success, accepted.Reason);
                    Assert.AreEqual(0d, Held(f).Single().PrepaidFunds, "Nothing was charged, so a later career switch must not redeem a prepay.");
                }
            }
            finally { GeneralSettings.SettingsStore.GameMode = mode; }
        }

        private static JObject Written(AgencyTradeTest.Fixture f)
        {
            var path = AgencyEconomyStore.FilePath;
            return JObject.Parse(File.ReadAllText(path));
        }

        [TestMethod]
        public void LegacyDocumentWithoutTheNewFieldsLoadsAndAnOpenDesignOfferStillMeansToolingPlusDesign()
        {
            using (var f = Start())
            {
                var offer = f.Offer(true);
                Assert.IsTrue(f.Economy.Execute(offer).Success);
                var json = Written(f);
                foreach (var row in json["TradeOffers"].Children<JProperty>().Select(p => (JObject)p.Value["Offer"]))
                { row.Remove("DesignMode"); row.Remove("PrepaidLaunchFunds"); row.Remove("LaunchMultiplier"); }
                json["Version"] = 1;
                File.WriteAllText(AgencyEconomyStore.FilePath, json.ToString());
                AgencyEconomyStore.Load(); f.Refresh();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.AreEqual(TradeDesignMode.ToolingAndDesign, f.Economy.Snapshot.Offers.Single().DesignMode);
                var accepted = f.Buy(Accept(offer.Trade.OfferId));
                Assert.IsTrue(accepted.Success, accepted.Reason);
                var held = Held(f).Single();
                Assert.AreEqual(TradeEntitlementKind.Permanent, held.Kind);
                Assert.AreEqual(1, AgencyEconomyStore.Snapshot(f.Buyer.AgencyId).Designs.Length);
                Assert.AreEqual(3, (int)Written(f)["Version"], "Every write by this build stores version 3.");
            }
        }

        [TestMethod]
        public void NewerDocumentVersionFailsClosedAndAnOrphanedReservationIsReleasedAtLoad()
        {
            using (var f = Start())
            {
                var voucher = BuyVoucher(f);
                var json = Written(f);
                json["Version"] = 4;
                File.WriteAllText(AgencyEconomyStore.FilePath, json.ToString());
                AgencyEconomyStore.Load();
                Assert.IsFalse(AgencyEconomyStore.Ready, "An unknown future version must not be reinterpreted.");

                json["Version"] = 2;
                var entitlement = json["Entitlements"].Children<JProperty>().SelectMany(p => p.Value.Children<JObject>()).Single();
                entitlement["LaunchId"] = Guid.NewGuid().ToString();
                File.WriteAllText(AgencyEconomyStore.FilePath, json.ToString());
                AgencyEconomyStore.Load(); f.Refresh();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.AreEqual(Guid.Empty, Held(f).Single().LaunchId, "A reservation with no prepared launch behind it is released.");
            }
        }
    }
}
