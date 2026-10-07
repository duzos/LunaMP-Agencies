using System;
using System.Linq;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace LmpCommonTest
{
    [TestClass]
    public class ToolingPolicyTest
    {
        // Fixed rates, so the older pricing cases below don't move when the shipped defaults do.
        private static readonly ToolingRates Rates = new ToolingRates(10, .1, 1, .1);
        private static ToolingPart Part(string name, double cost, bool science = false) => new ToolingPart { Name = name, UnitCost = cost, IsScience = science };
        private static ToolingManifest Craft(params ToolingPart[] parts) => new ToolingManifest { Parts = parts };
        private static ToolingDesign Design(ToolingManifest manifest, double basis) => new ToolingDesign { Fingerprint = ToolingPolicy.Fingerprint(manifest), Manifest = manifest, ToolingBasis = basis };
        [TestMethod]
        public void FingerprintCountsNamesOnlyAndPriceHashTracksLaunchManifest()
        {
            var first = Craft(Part("tank", 100), Part("engine", 200), Part("tank", 50));
            var second = Craft(Part("tank", 3), Part("tank", 8), Part("engine", 0));
            Assert.AreEqual(ToolingPolicy.Fingerprint(first), ToolingPolicy.Fingerprint(second));
            Assert.AreNotEqual(ToolingPolicy.ManifestHash(first), ToolingPolicy.ManifestHash(second));
            Assert.AreNotEqual(ToolingPolicy.Fingerprint(first), ToolingPolicy.Fingerprint(Craft(Part("tank", 100), Part("engine", 200))));
            Assert.AreNotEqual(ToolingPolicy.Fingerprint(Craft(Part("a|b", 1))), ToolingPolicy.Fingerprint(Craft(Part("a", 1), Part("b", 1))));
        }
        [TestMethod]
        public void CargoContainerAndCrewChangePriceIdentityButNotDesignFingerprint()
        {
            var craft = Craft(Part("pod", 100), Part("box", 20));
            craft.Cargo = new[] { new ToolingCargo { Name = "kit", Count = 1, UnitCost = 10, ContainerPartIndex = 0, CrewName = "Jeb" } };
            var design = ToolingPolicy.Fingerprint(craft); var price = ToolingPolicy.ManifestHash(craft);
            craft.Cargo[0].ContainerPartIndex = 1;
            Assert.AreEqual(design, ToolingPolicy.Fingerprint(craft)); Assert.AreNotEqual(price, ToolingPolicy.ManifestHash(craft));
            price = ToolingPolicy.ManifestHash(craft); craft.Cargo[0].CrewName = "Val";
            Assert.AreNotEqual(price, ToolingPolicy.ManifestHash(craft));
        }
        [TestMethod]        public void ScienceAndCargoStayFullPriceAndExactToolingIsFree()
        {
            var craft = Craft(Part("engine", 100), Part("experiment", 700, true));
            craft.Cargo = new[] { new ToolingCargo { Name = "engine", Count = 2, UnitCost = 100 } };
            var fresh = ToolingPolicy.Quote(craft, Array.Empty<ToolingDesign>(), Rates);
            Assert.IsTrue(fresh.Success, fresh.Reason); Assert.AreEqual(1000d, fresh.ToolingCost); Assert.AreEqual(1000d, fresh.LaunchCost);
            var tooled = ToolingPolicy.Quote(craft, new[] { Design(craft, 1000) }, Rates);
            Assert.AreEqual(0d, tooled.ToolingCost); Assert.AreEqual(910d, tooled.LaunchCost); Assert.AreEqual(700d, tooled.ScienceCost); Assert.AreEqual(200d, tooled.CargoCost);
        }
        [TestMethod]
        public void CoverConsumesDisjointCountsAndFindsCheapestOrderIndependentCombination()
        {
            var craft = Craft(Part("a", 100), Part("a", 100), Part("b", 100), Part("c", 100));
            var designs = new[] { Design(Craft(Part("a",100),Part("b",100)),2000), Design(Craft(Part("a",100),Part("c",100)),2000), Design(Craft(Part("b",100),Part("c",100)),2000) };
            var quote = ToolingPolicy.Quote(craft, designs, Rates);
            Assert.AreEqual(400d, quote.ToolingCost);
            Assert.AreEqual(quote.ToolingCost, ToolingPolicy.Quote(craft, designs.Reverse(), Rates).ToolingCost);
            var single = Craft(Part("a",100),Part("b",100),Part("c",100));
            Assert.AreEqual(1200d, ToolingPolicy.Quote(single, designs, Rates).ToolingCost);
        }
        [TestMethod]
        public void EachToolingDesignCoversAtMostOneInstancePerQuote()
        {
            var small = Craft(Part("a", 1));
            var result = ToolingPolicy.Quote(Craft(Part("a", 100), Part("a", 40), Part("b",10)), new[] { Design(small,10) }, Rates);
            // The design covers the most expensive "a" once; the other "a" and the "b" pay full tooling.
            Assert.IsTrue(result.Success); Assert.AreEqual(10 * (40 + 10) + .1 * 10 * 100, result.ToolingCost, 1e-9); Assert.AreEqual(1, result.Matches.Sum(m=>m.Count));
            Assert.AreEqual(1, result.Matches.Length);
        }
        [TestMethod]
        public void ADesignWithTwoCopiesCoversTwoButASingleCopyDesignCoversOne()
        {
            var craft = Craft(Part("booster", 100), Part("booster", 100));
            var free = new ToolingRates(10, .1, 1, 0);
            Assert.AreEqual(1000d, ToolingPolicy.Quote(craft, new[] { Design(Craft(Part("booster", 100)), 1000) }, free).ToolingCost, "One tooled booster does not unlock unlimited boosters.");
            Assert.AreEqual(0d, ToolingPolicy.Quote(craft, new[] { Design(craft, 2000) }, free).ToolingCost);
            Assert.AreEqual(0d, ToolingPolicy.Quote(Craft(Part("booster", 100), Part("booster", 100), Part("booster", 100)), new[] { Design(Craft(Part("booster", 100), Part("booster", 100)), 2000), Design(Craft(Part("booster", 100)), 1000) }, free).ToolingCost, "Two designs together cover three.");
        }
        [TestMethod]
        public void AddingAPartToATooledDesignCostsOnlyThatPartAtZeroCombine()
        {
            var rates = new ToolingRates(5, .1, 2, 0);
            var tooled = Craft(Enumerable.Range(0, 30).Select(i => Part("p" + i, 700)).ToArray());
            var extended = Craft(tooled.Parts.Concat(new[] { Part("extra", 300) }).ToArray());
            var quote = ToolingPolicy.Quote(extended, new[] { Design(tooled, 105000) }, rates);
            Assert.IsTrue(quote.Success, quote.Reason);
            Assert.AreEqual(1500d, quote.ToolingCost, 1e-9);
            Assert.AreEqual(1500d, ToolingPolicy.Quote(extended, new[] { Design(tooled, 1) }, rates).ToolingCost, 1e-9, "Independent of the saved basis.");
            var two = ToolingPolicy.Quote(Craft(tooled.Parts.Concat(new[] { Part("extra", 300), Part("extra", 300) }).ToArray()), new[] { Design(tooled, 1) }, rates);
            Assert.AreEqual(3000d, two.ToolingCost, 1e-9, "Cost grows linearly with each added part.");
        }
        [TestMethod]
        public void TypicalCraftsWithSeveralDesignsStayWellUnderTheSearchLimit()
        {
            var rates = new ToolingRates(5, .1, 2, .1);
            var names = Enumerable.Range(0, 20).Select(i => "part" + i).ToArray();
            var parts = Enumerable.Range(0, 60).Select(i => Part(names[i % names.Length], 50 + 13 * (i % 7))).ToArray();
            var designs = new System.Collections.Generic.List<ToolingDesign>();
            for (var d = 0; d < 10; d++)
                designs.Add(Design(Craft(Enumerable.Range(0, 4 + d).Select(i => Part(names[(d * 3 + i) % names.Length], 50)).ToArray()), 1000 + d));
            foreach (var combine in new[] { 0d, .1 })
            {
                var quote = ToolingPolicy.Quote(Craft(parts), designs, new ToolingRates(5, .1, 2, combine));
                Assert.IsTrue(quote.Success, quote.Reason);
                Assert.IsTrue(quote.Matches.All(m => m.Count == 1));
                Assert.IsTrue(quote.ToolingCost <= 5 * parts.Sum(x => x.UnitCost) + 1e-9);
            }
        }
        [TestMethod]
        public void OverflowAndSearchLimitReturnFailureInsteadOfPartialQuote()
        {
            var overflow = ToolingPolicy.Quote(Craft(Part("a", ToolingPolicy.MaxCost)), Array.Empty<ToolingDesign>(), Rates);
            Assert.IsFalse(overflow.Success);
            // Many distinct designs that each fit give an exponential number of cover subsets.
            var distinct = Enumerable.Range(0, 20).Select(i => Part("n" + i, 100)).ToArray();
            var singles = distinct.Select(p => Design(Craft(p), 1000)).ToArray();
            var complex = ToolingPolicy.Quote(Craft(distinct), singles, Rates);
            Assert.IsFalse(complex.Success);
            StringAssert.Contains(complex.Reason, "complex");
        }
        [TestMethod]
        public void RejectsNonfiniteNegativeAndOversizedInputsWithoutCharges()
        {
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1d })
                Assert.IsFalse(ToolingPolicy.Quote(Craft(Part("a", invalid)), Array.Empty<ToolingDesign>(), Rates).Success);
            Assert.IsFalse(ToolingPolicy.Quote(Craft(Part("a", 1)),Array.Empty<ToolingDesign>(),new ToolingRates(double.NaN, .1, 1, .1)).Success);
            Assert.IsFalse(ToolingPolicy.Quote(Craft(Part("",1)),Array.Empty<ToolingDesign>(), Rates).Success);
            Assert.IsFalse(ToolingPolicy.Quote(Craft(Enumerable.Repeat(Part("a",1),ToolingPolicy.MaxParts+1).ToArray()),Array.Empty<ToolingDesign>(), Rates).Success);
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1d })
                Assert.IsFalse(ToolingPolicy.Quote(Craft(Part("a", 1)), Array.Empty<ToolingDesign>(), new ToolingRates(10, .1, invalid, .1)).Success, "untooled multiplier " + invalid);
            Assert.IsFalse(ToolingPolicy.Quote(Craft(Part("a", 1)), Array.Empty<ToolingDesign>(), null).Success);
        }
        [TestMethod]
        public void ShippedDefaultsAreTheAgreedRates()
        {
            Assert.AreEqual(5d, ToolingDefaults.ToolingCost); Assert.AreEqual(.1, ToolingDefaults.TooledLaunch); Assert.AreEqual(2d, ToolingDefaults.UntooledLaunch); Assert.AreEqual(0d, ToolingDefaults.Combine);
            var rates = ToolingRates.Default;
            Assert.AreEqual(ToolingDefaults.ToolingCost, rates.Tooling); Assert.AreEqual(ToolingDefaults.TooledLaunch, rates.TooledLaunch); Assert.AreEqual(ToolingDefaults.UntooledLaunch, rates.UntooledLaunch); Assert.AreEqual(ToolingDefaults.Combine, rates.Combine);
        }
        [TestMethod]
        public void ToolingCostIsLinearInUncoveredPartsAtEveryLevelWithDefaultRates()
        {
            var rates = ToolingRates.Default;
            Assert.AreEqual(0d, rates.Combine);
            var designs = new System.Collections.Generic.List<ToolingDesign>();
            var pair = Craft(Part("half", 100), Part("half", 100));
            var seed = ToolingPolicy.Quote(pair, designs, rates);
            Assert.AreEqual(1000d, seed.ToolingCost); designs.Add(Design(pair, seed.ToolingCost));
            // Each saved design covers one instance, so doubling the craft only ever pays for the halves that no saved design covers.
            var costs = new System.Collections.Generic.List<double>();
            for (var halves = 4; halves <= 16; halves *= 2)
            {
                var craft = Craft(Enumerable.Range(0, halves).Select(_ => Part("half", 100)).ToArray());
                var quote = ToolingPolicy.Quote(craft, designs, rates);
                Assert.IsTrue(quote.Success, quote.Reason);
                Assert.AreEqual(0d, quote.Matches.Sum(m => m.CombineCost), 1e-9, "No combine fee at the default rate.");
                costs.Add(quote.ToolingCost); designs.Add(Design(craft, quote.ToolingCost));
            }
            Assert.AreEqual(3, costs.Count);
            Assert.AreEqual(1000d, costs[0], 1e-9, "4 halves: the pair covers two, two pay 5 x 100 each.");
            Assert.AreEqual(1000d, costs[1], 1e-9, "8 halves: the 4-half design covers four, the pair two, two pay full.");
            Assert.AreEqual(5 * 100d * (16 - 8 - 4 - 2), costs[2], 1e-9, "16 halves: designs of 8, 4 and 2 are covered once each.");
        }
        [TestMethod]
        public void NestedCombineFeeStaysTheSameShareOfTheFullToolingValueWhenACombineRateIsConfigured()
        {
            var rates = new ToolingRates(5, .1, 2, .1);
            var designs = new System.Collections.Generic.List<ToolingDesign>();
            var pair = Craft(Part("half", 100), Part("half", 100));
            designs.Add(Design(pair, ToolingPolicy.Quote(pair, designs, rates).ToolingCost));
            var quad = Craft(Enumerable.Range(0, 4).Select(_ => Part("half", 100)).ToArray());
            var quote = ToolingPolicy.Quote(quad, designs, rates);
            // The pair covers two halves for a share of their tooling value; the other two pay full.
            Assert.AreEqual(.1 * 5 * 200 + 5 * 200, quote.ToolingCost, 1e-9);
            Assert.AreEqual(100d, quote.Matches.Sum(m => m.CombineCost), 1e-9);
        }
        [TestMethod]
        public void CombineFeeIgnoresThePaidBasisAndALighterVariantStillMatches()
        {
            var rates = new ToolingRates(5, .1, 2, .1);
            var saved = Craft(Part("tank", 100), Part("engine", 200));
            // The same part list with less fuel in the tank, plus one extra part that still needs tooling.
            var variant = Craft(Part("tank", 60), Part("engine", 200), Part("fin", 50));
            foreach (var basis in new[] { 0d, 1d, 1e9 })
            {
                var quote = ToolingPolicy.Quote(variant, new[] { Design(saved, basis) }, rates);
                Assert.IsTrue(quote.Success, quote.Reason);
                var match = quote.Matches.Single();
                Assert.AreEqual(1, match.Count);
                Assert.AreEqual(rates.Combine * rates.Tooling * (60 + 200), match.CombineCost, 1e-9, "fee follows this craft's own prices, basis " + basis);
                Assert.AreEqual(match.CombineCost + rates.Tooling * 50, quote.ToolingCost, 1e-9);
            }
        }
        [TestMethod]
        public void CombineNeverCostsMoreThanToolingTheCoveredPartsOutright()
        {
            var saved = Craft(Part("a", 100), Part("b", 100));
            var craft = Craft(Part("a", 100), Part("b", 100), Part("c", 100));
            var cheap = ToolingPolicy.Quote(craft, new[] { Design(saved, 1) }, new ToolingRates(10, .1, 1, .5));
            Assert.AreEqual(1, cheap.Matches.Single().Count); Assert.AreEqual(.5 * 2000 + 1000, cheap.ToolingCost, 1e-9);
            var pointless = ToolingPolicy.Quote(craft, new[] { Design(saved, 1) }, new ToolingRates(10, .1, 1, 1));
            Assert.AreEqual(0, pointless.Matches.Length); Assert.AreEqual(3000d, pointless.ToolingCost);
        }
        [TestMethod]
        public void UntooledLaunchUsesTheUntooledMultiplierAndScienceAndCargoStayFullPrice()
        {
            var craft = Craft(Part("engine", 100), Part("experiment", 700, true));
            craft.Cargo = new[] { new ToolingCargo { Name = "engine", Count = 2, UnitCost = 100 } };
            var untooled = ToolingPolicy.Quote(craft, Array.Empty<ToolingDesign>(), ToolingRates.Default);
            Assert.IsFalse(untooled.AlreadyTooled);
            Assert.AreEqual(700d + 200d + 100d * 2, untooled.LaunchCost); Assert.AreEqual(700d, untooled.ScienceCost); Assert.AreEqual(200d, untooled.CargoCost); Assert.AreEqual(100d, untooled.NonScienceCost);
            Assert.AreEqual(500d, untooled.ToolingCost);
            var onlyScienceAndCargo = Craft(Part("experiment", 700, true)); onlyScienceAndCargo.Cargo = craft.Cargo;
            Assert.AreEqual(900d, ToolingPolicy.Quote(onlyScienceAndCargo, Array.Empty<ToolingDesign>(), new ToolingRates(5, .1, 7, .1)).LaunchCost);
        }
        [TestMethod]
        public void TooledLaunchUsesTheTooledMultiplier()
        {
            var craft = Craft(Part("engine", 100), Part("experiment", 700, true));
            craft.Cargo = new[] { new ToolingCargo { Name = "engine", Count = 2, UnitCost = 100 } };
            var tooled = ToolingPolicy.Quote(craft, new[] { Design(craft, 500) }, ToolingRates.Default);
            Assert.IsTrue(tooled.AlreadyTooled); Assert.AreEqual(0d, tooled.ToolingCost);
            Assert.AreEqual(700d + 200d + 100d * ToolingDefaults.TooledLaunch, tooled.LaunchCost, 1e-9);
        }
        [TestMethod]
        public void EditorLaunchAfterToolingMatchesTheQuoteOfTheToolingOnceBought()
        {
            var rates = new ToolingRates(5, .25, 3, .1);
            var craft = Craft(Part("engine", 100), Part("tank", 40), Part("experiment", 700, true));
            craft.Cargo = new[] { new ToolingCargo { Name = "kit", Count = 3, UnitCost = 10 } };
            var before = ToolingPolicy.Quote(craft, Array.Empty<ToolingDesign>(), rates);
            // The editor shows ToolingPolicy.LaunchCost(..., tooled: true, ...) for the not-yet-tooled quote; the server later quotes it for real.
            var shown = ToolingPolicy.LaunchCost(before.ScienceCost, before.CargoCost, before.NonScienceCost, true, rates);
            var after = ToolingPolicy.Quote(craft, new[] { Design(craft, before.ToolingCost) }, rates);
            Assert.AreEqual(after.LaunchCost, shown, 1e-9);
            Assert.AreEqual(before.LaunchCost, ToolingPolicy.LaunchCost(before.ScienceCost, before.CargoCost, before.NonScienceCost, false, rates), 1e-9);
            Assert.AreEqual(700d + 30d + 140d * 3, before.LaunchCost, 1e-9); Assert.AreEqual(700d + 30d + 140d * .25, shown, 1e-9);
        }
    }
}
