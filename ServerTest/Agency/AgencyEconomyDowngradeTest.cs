using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Server.Agency;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerTest.Agency
{
    /// <summary>The agencies.9 to agencies.8 economy down-migration (plan 40, R1.8 and R2.4).</summary>
    [TestClass, DoNotParallelize]
    public class AgencyEconomyDowngradeTest
    {
        private static readonly ToolingManifest Probe = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } } };
        private static readonly string Fingerprint = ToolingPolicy.Fingerprint(Probe);
        private string _dir;

        [TestInitialize] public void Init() { _dir = Path.Combine(Path.GetTempPath(), "LMPDowngrade_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
        [TestCleanup] public void Done() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private static DesignStockLot Lot(int units, double prepaid, bool fundsBuilt = true) => new DesignStockLot
        {
            LotId = Guid.NewGuid(), Fingerprint = Fingerprint, Units = units, PrepaidPerUnit = prepaid, LaunchMultiplier = .07, FundsBuilt = fundsBuilt, CreatedUtcTicks = 1
        };

        private sealed class World
        {
            public readonly Guid Seller = Guid.NewGuid(), Buyer = Guid.NewGuid(), Offer = Guid.NewGuid(), PreparedStock = Guid.NewGuid(), RegisteredStock = Guid.NewGuid();
            public EconomyDocument Doc;
        }

        // Seller: 1000 funds, a 5x20 funds-built lot and a 3x20 sandbox lot, 4 units escrowed in an open offer, one Prepared stock launch.
        // Buyer: 500 funds and a 2x10 lot.
        private static World Build()
        {
            var w = new World();
            var doc = new EconomyDocument { Version = 3, Revision = 7 };
            doc.Agencies[w.Seller] = new EconomyAgency
            {
                Funds = 1000,
                Designs = { new ToolingDesign { Fingerprint = Fingerprint, Manifest = Probe, ToolingBasis = 500, Name = "Probe" } },
                Stock = { Lot(5, 20), Lot(3, 20, false) },
                Blueprints = { [Fingerprint] = new ToolingBlueprintRef { Fingerprint = Fingerprint, Name = "Probe", Editor = "VAB", Hash = new string('a', 64), Size = 10 } }
            };
            doc.Agencies[w.Buyer] = new EconomyAgency { Funds = 500, Stock = { Lot(2, 10) } };
            doc.TradeOffers[w.Offer] = new StoredTradeOffer
            {
                Offer = new TradeOffer { OfferId = w.Offer, SellerAgencyId = w.Seller, BuyerAgencyId = w.Buyer, Status = TradeOfferStatus.Open, DesignMode = TradeDesignMode.Stock, DesignFingerprint = Fingerprint, StockUnits = 4, StockPrepaidTotal = 80 },
                Escrow = { Lot(4, 20) }
            };
            doc.Entitlements[w.Buyer] = new List<TradeEntitlement>
            {
                new TradeEntitlement { EntitlementId = Guid.NewGuid(), Kind = TradeEntitlementKind.StockDesign, Fingerprint = Fingerprint },
                new TradeEntitlement { EntitlementId = Guid.NewGuid(), Kind = TradeEntitlementKind.Permanent, Fingerprint = Fingerprint }
            };
            var terms = new StockTerms { LotId = Guid.NewGuid(), Fingerprint = Fingerprint, PrepaidPerUnit = 20, LaunchMultiplier = .07, FundsBuilt = true };
            doc.Launches[w.PreparedStock] = new EconomyLaunch { LaunchId = w.PreparedStock, AgencyId = w.Seller, State = LaunchState.Prepared, Charge = 9, Multiplier = .07, Stock = terms };
            doc.Launches[w.RegisteredStock] = new EconomyLaunch { LaunchId = w.RegisteredStock, AgencyId = w.Buyer, State = LaunchState.Registered, Charge = 4, Multiplier = .07, Stock = terms };
            doc.Operations[Guid.NewGuid()] = new EconomyOperationReceipt { Sequence = 1 };
            doc.SessionSequences[Guid.NewGuid()] = 5;
            w.Doc = doc;
            return w;
        }

        private static double Funds(EconomyDocument d) => d.Agencies.Values.Sum(a => a.Funds);

        private string Write(EconomyDocument doc) { var p = Path.Combine(_dir, "AgencyEconomy.json"); File.WriteAllText(p, JsonConvert.SerializeObject(doc)); return p; }

        [TestMethod]
        public void ConvertsAndConservesFundsWithoutAgencyStore()
        {
            var w = Build();
            var before = Funds(w.Doc);
            var v2 = AgencyEconomyStore.ToVersion2(w.Doc, out var summary);

            // Seller: Prepared charge 9 and its unit 20, escrow 4x20, funds-built lot 5x20 (the sandbox 3x20 lot credits 0). Buyer: lot 2x10.
            Assert.AreEqual(1000 + 9 + 20 + 80 + 100, v2.Agencies[w.Seller].Funds, 1e-9);
            Assert.AreEqual(500 + 20, v2.Agencies[w.Buyer].Funds, 1e-9);
            Assert.AreEqual(before + 9 + 20 + 80 + 100 + 20, Funds(v2), 1e-9);
            Assert.AreEqual(Funds(v2) - before, summary.FundsCredited + 9, 1e-9);
            Assert.AreEqual(2, v2.Version);
            Assert.IsFalse(v2.Agencies.Values.Any(a => a.Stock.Count > 0 || a.Blueprints.Count > 0));
            Assert.AreEqual(0, v2.TradeOffers.Count, "The stock offer is removed after it is cancelled.");
            Assert.AreEqual(1, summary.OffersCancelled);
            Assert.AreEqual(1, summary.StockOffersRemoved);
            Assert.AreEqual(1, summary.StockDesignEntitlementsRemoved);
            Assert.AreEqual(TradeEntitlementKind.Permanent, v2.Entitlements[w.Buyer].Single().Kind);
            Assert.AreEqual(LaunchState.Cancelled, v2.Launches[w.PreparedStock].State);
            Assert.AreEqual(LaunchState.Registered, v2.Launches[w.RegisteredStock].State);
            Assert.IsTrue(v2.Launches.Values.All(l => l.Stock == null));
            Assert.AreEqual(4, v2.Launches[w.RegisteredStock].Charge, 1e-9);
            Assert.AreEqual(0, v2.Operations.Count);
            Assert.AreEqual(0, v2.SessionSequences.Count);
            Assert.AreEqual(1, v2.Agencies[w.Seller].Designs.Count, "Tooled designs are kept.");
            Assert.AreEqual(3, w.Doc.Version, "The input document is not mutated.");
            Assert.AreEqual(2, w.Doc.Agencies[w.Seller].Stock.Count);
        }

        [TestMethod]
        public void SandboxBuiltUnitsCreditNothing()
        {
            var w = Build();
            w.Doc.Agencies[w.Buyer].Stock[0] = Lot(7, 15, false);
            var v2 = AgencyEconomyStore.ToVersion2(w.Doc, out _);
            Assert.AreEqual(500, v2.Agencies[w.Buyer].Funds, 1e-9);
        }

        [TestMethod]
        public void MissingSellerRowIsAnErrorAndFileIsUntouched()
        {
            var w = Build();
            w.Doc.Agencies.Remove(w.Seller);
            // Prepared launch is the seller's too; move it so the offer is the first thing that fails.
            w.Doc.Launches[w.PreparedStock].AgencyId = w.Buyer;
            Assert.ThrowsException<DowngradeRefusedException>(() => AgencyEconomyStore.ToVersion2(w.Doc, out _));

            var path = Write(w.Doc);
            var original = File.ReadAllText(path);
            var output = new StringWriter();
            Assert.AreEqual(1, AgencyEconomyStore.RunDowngrade(_dir, output));
            StringAssert.Contains(output.ToString(), "no economy row");
            Assert.AreEqual(original, File.ReadAllText(path));
            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.bak.json").Length, "No backup is left behind by a refusal.");
        }

        [TestMethod]
        public void RefusesVersion2Input()
        {
            var w = Build();
            w.Doc.Version = 2;
            var path = Write(w.Doc);
            var original = File.ReadAllText(path);
            Assert.AreEqual(1, AgencyEconomyStore.RunDowngrade(_dir, new StringWriter()));
            Assert.AreEqual(original, File.ReadAllText(path));
        }

        [TestMethod]
        public void RunWritesBackupAndAV2DocumentAndIsIdempotent()
        {
            var w = Build();
            var path = Write(w.Doc);
            var original = File.ReadAllText(path);
            var output = new StringWriter();
            Assert.AreEqual(0, AgencyEconomyStore.RunDowngrade(_dir, output), output.ToString());

            var backups = Directory.GetFiles(_dir, "AgencyEconomy.v3.*.bak.json");
            Assert.AreEqual(1, backups.Length);
            Assert.AreEqual(original, File.ReadAllText(backups[0]));
            Assert.IsFalse(File.Exists(path + ".tmp"));

            var json = File.ReadAllText(path);
            var root = JObject.Parse(json);
            Assert.AreEqual(2, (int)root["Version"]);
            foreach (var name in new[] { "\"Stock\"", "\"Blueprints\"", "\"Escrow\"", "\"StockUnits\"", "\"StockPrepaidTotal\"" })
                Assert.IsFalse(json.Contains(name), name + " must not appear in a version 2 document.");
            Assert.IsNull(((JObject)root["Agencies"][w.Seller.ToString()]["Designs"][0])["Name"], "The design display name is v3-only.");
            var parsed = JsonConvert.DeserializeObject<EconomyDocument>(json);
            Assert.AreEqual(2, parsed.Version);
            Assert.AreEqual(1209, parsed.Agencies[w.Seller].Funds, 1e-9);
            Assert.AreEqual(1, parsed.Agencies[w.Seller].Designs.Count);
            // agencies.8 rejects an undefined enum value anywhere in trade or entitlements.
            Assert.IsTrue(parsed.TradeOffers.Values.All(o => (byte)o.Offer.DesignMode <= 1));
            Assert.IsTrue(parsed.Entitlements.Values.SelectMany(e => e).All(e => (byte)e.Kind <= 1));

            // A second run refuses and changes nothing.
            Assert.AreEqual(1, AgencyEconomyStore.RunDowngrade(_dir, new StringWriter()));
            Assert.AreEqual(json, File.ReadAllText(path));
            Assert.AreEqual(1, Directory.GetFiles(_dir, "AgencyEconomy.v3.*.bak.json").Length);
        }

        [TestMethod]
        public void NoFileIsNotAnError()
        {
            Assert.AreEqual(0, AgencyEconomyStore.RunDowngrade(_dir, new StringWriter()));
        }

        [TestMethod]
        public void CorruptFileIsRefused()
        {
            var path = Path.Combine(_dir, "AgencyEconomy.json");
            File.WriteAllText(path, "{ not json");
            Assert.AreEqual(1, AgencyEconomyStore.RunDowngrade(_dir, new StringWriter()));
            Assert.AreEqual("{ not json", File.ReadAllText(path));
        }
    }
}
