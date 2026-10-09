using LmpCommon.Agency;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Server.Agency;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerTest.Agency
{
    /// <summary>Science parts are not part of a design: they never cost tooling, never change the fingerprint and are added at raw cost on every launch.</summary>
    [TestClass, DoNotParallelize]
    public class AgencyScienceFingerprintTest
    {
        private static ToolingPart Part(string name, double cost, bool science = false) => new ToolingPart { Name = name, UnitCost = cost, IsScience = science };
        private static ToolingManifest Craft(params ToolingPart[] parts) => new ToolingManifest { Parts = parts };
        private static ToolingManifest WithLab() => Craft(Part("probe", 100), Part("tank", 400), Part("lab", 50, true));
        private static ToolingManifest WithGoo() => Craft(Part("probe", 100), Part("tank", 400), Part("goo", 80, true), Part("thermometer", 90, true));
        private static ToolingManifest Bare() => Craft(Part("probe", 100), Part("tank", 400));

        private static EconomyResult Quote(AgencyEconomyTest.Fixture f, ToolingManifest manifest)
            => f.Execute(new EconomyCommand { Operation = EconomyOperation.Quote, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });

        [TestMethod]
        public void ASciencePartSwapKeepsTheDesignTooledAndEveryLaunchAddsItsScienceAtRawCost()
        {
            using (var f = new AgencyTradeTest.Fixture())
            {
                var before = f.Economy.Snapshot.Funds;
                var tooled = AgencyStockTest.Tool(f.Economy.Execute, WithLab());
                Assert.IsTrue(tooled.Success, tooled.Reason);
                var rates = AgencyEconomyTest.Fixture.SettingsRates();
                Assert.AreEqual(500d * rates.Tooling, before - f.Economy.Snapshot.Funds, 1e-6, "Tooling charges the non-science parts only.");

                foreach (var manifest in new[] { WithGoo(), Bare() })
                {
                    var quote = Quote(f.Economy, manifest);
                    Assert.IsTrue(quote.Success, quote.Reason);
                    Assert.IsTrue(quote.Quote.AlreadyTooled, "Different science parts are the same tooled design.");
                    Assert.AreEqual(0d, quote.Quote.ToolingCost);
                    Assert.AreEqual(manifest.Parts.Where(p => p.IsScience).Sum(p => p.UnitCost) + 500 * rates.TooledLaunch, quote.Quote.LaunchCost, 1e-9);
                }
                Assert.IsTrue(AgencyStockTest.Tool(f.Economy.Execute, WithGoo()).Success);
                Assert.AreEqual(1, f.Economy.Snapshot.Designs.Length, "Re-tooling with other science adds no design and charges nothing.");

                // A stock unit prepays the non-science parts; the launch adds its own science parts on top, undiscounted.
                var fp = ToolingPolicy.Fingerprint(WithLab());
                Assert.IsTrue(f.Economy.Execute(AgencyStockTest.BuildCommand(f.Economy.Client.AgencyId, fp, 1)).Success);
                var lot = f.Economy.Snapshot.Stock.Single();
                Assert.AreEqual(500d * rates.TooledLaunch, lot.PrepaidPerUnit, 1e-9);
                var funds = f.Economy.Snapshot.Funds;
                var manifestGoo = WithGoo();
                var prepared = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), StockLotId = lot.LotId, Manifest = manifestGoo, ManifestHash = ToolingPolicy.ManifestHash(manifestGoo) });
                Assert.IsTrue(prepared.Success, prepared.Reason);
                Assert.AreEqual(170d, funds - f.Economy.Snapshot.Funds, 1e-6, "Only the 80 + 90 of science parts is charged.");
            }
        }

        [TestMethod]
        public void ABlueprintWithSciencePartsIsSavedForTheToolingItMatches()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                var craft = Encoding.UTF8.GetBytes("ship = Lab Probe\ntype = VAB\nPART\n{\npart = probe_1\n}\nPART\n{\npart = tank_2\n}\nPART\n{\npart = lab_3\n}\n");
                var tooled = AgencyStockTest.Tool(f.Execute, WithLab(), craft);
                Assert.IsTrue(tooled.Success, tooled.Reason);
                Assert.AreEqual("Operation committed.", tooled.Reason, "The craft was saved, not skipped.");
                Assert.AreEqual(ToolingPolicy.Fingerprint(Bare()), f.Snapshot.DesignBlueprints.Single().Fingerprint);
            }
        }

        [TestMethod]
        public void TheClassifierPrefersTheCraftsOwnManifestThenAnythingStored()
        {
            var document = new EconomyDocument();
            document.Agencies[Guid.NewGuid()] = new EconomyAgency { Designs = new List<ToolingDesign> { new ToolingDesign { Manifest = WithLab() } } };
            var stored = AgencyEconomyStore.ScienceClassifier(document);
            Assert.IsTrue(stored("lab"));
            Assert.IsFalse(stored("probe"));
            Assert.IsFalse(stored("goo"));
            var context = AgencyEconomyStore.ScienceClassifier(document, WithGoo(), Craft(Part("lab", 1)));
            Assert.IsTrue(context("goo"));
            Assert.IsFalse(context("lab"), "The priced craft's own classification wins over stored records.");
            Assert.IsFalse(context(null));
        }

        [TestMethod]
        public void LoadReKeysDesignsStockAndLicencesSavedWithScienceInTheirFingerprint()
        {
            using (var f = new AgencyTradeTest.Fixture())
            {
                var agency = f.Economy.Client.AgencyId;
                Assert.IsTrue(AgencyStockTest.Tool(f.Economy.Execute, AgencyStockTest.Probe()).Success);
                var legacyLab = ToolingPolicy.LegacyFingerprint(WithLab());
                var legacyGoo = ToolingPolicy.LegacyFingerprint(WithGoo());
                var current = ToolingPolicy.Fingerprint(Bare());
                Assert.AreNotEqual(legacyLab, legacyGoo);

                // A pre-agencies.11 file: the same craft tooled twice with different science parts, stock of one, a licence for the other.
                var old = JsonConvert.DeserializeObject<EconomyDocument>(File.ReadAllText(AgencyEconomyStore.FilePath));
                var mine = old.Agencies[agency];
                mine.Designs.Add(new ToolingDesign { Fingerprint = legacyLab, Manifest = WithLab(), ToolingBasis = 2500 });
                mine.Designs.Add(new ToolingDesign { Fingerprint = legacyGoo, Manifest = WithGoo(), ToolingBasis = 0, Name = "Lander" });
                var lotId = Guid.NewGuid();
                mine.Stock.Add(new DesignStockLot { LotId = lotId, Fingerprint = legacyLab, Units = 3, PrepaidPerUnit = 100, LaunchMultiplier = .1, BuilderAgencyId = agency, FundsBuilt = true, CreatedUtcTicks = 1 });
                old.Entitlements[f.Buyer.AgencyId] = new List<TradeEntitlement> { new TradeEntitlement { EntitlementId = Guid.NewGuid(), SellerAgencyId = agency, Kind = TradeEntitlementKind.Permanent, Fingerprint = legacyGoo } };
                File.WriteAllText(AgencyEconomyStore.FilePath, JsonConvert.SerializeObject(old));

                AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready, "A file with science in its fingerprints still loads.");
                var designs = AgencyStockTest.Document().Agencies[agency].Designs;
                Assert.AreEqual(2, designs.Count, "The probe design is untouched; the two science variants are one design now.");
                Assert.AreEqual(AgencyStockTest.ProbeFingerprint, designs[0].Fingerprint);
                Assert.AreEqual(current, designs[1].Fingerprint);
                Assert.AreEqual("Lander", designs[1].Name, "A merged design keeps a name one of its variants had.");
                Assert.AreEqual(current, AgencyStockTest.Document().Agencies[agency].Stock.Single(l => l.LotId == lotId).Fingerprint);
                Assert.AreEqual(100d, AgencyStockTest.Document().Agencies[agency].Stock.Single(l => l.LotId == lotId).PrepaidPerUnit, "Funds are never touched.");
                Assert.AreEqual(current, AgencyStockTest.Document().Entitlements[f.Buyer.AgencyId].Single().Fingerprint);
                var persisted = JsonConvert.DeserializeObject<EconomyDocument>(File.ReadAllText(AgencyEconomyStore.FilePath));
                Assert.AreEqual(current, persisted.Agencies[agency].Designs[1].Fingerprint, "The re-key is persisted.");

                f.Refresh();
                var quote = Quote(f.Economy, Craft(Part("probe", 100), Part("tank", 400), Part("barometer", 70, true)));
                Assert.IsTrue(quote.Quote.AlreadyTooled);

                var revision = AgencyStockTest.Document().Revision;
                AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.AreEqual(revision, AgencyStockTest.Document().Revision, "The migration is idempotent: a second load changes nothing.");
            }
        }
    }
}
