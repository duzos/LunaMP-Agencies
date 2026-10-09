using System;
using System.Collections.Generic;
using System.Linq;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class StockPolicyTest
    {
        // Science 300, non-science 100 + 400 = 500, one 25-funds inventory item that building ignores.
        private static ToolingManifest Manifest(double tank = 400) => new ToolingManifest
        {
            Parts = new[]
            {
                new ToolingPart { Name = "probe", UnitCost = 100 },
                new ToolingPart { Name = "tank", UnitCost = tank },
                new ToolingPart { Name = "science", UnitCost = 300, IsScience = true }
            },
            Cargo = new[] { new ToolingCargo { Name = "cargo", Count = 1, UnitCost = 25, ContainerPartIndex = 0 } }
        };

        private static ToolingDesign Design(ToolingManifest manifest = null)
        {
            manifest = manifest ?? Manifest();
            return new ToolingDesign { Fingerprint = ToolingPolicy.Fingerprint(manifest), Manifest = manifest, ToolingBasis = 2500 };
        }

        private static ToolingQuote Launch(ToolingManifest manifest = null) => ToolingPolicy.Quote(manifest ?? Manifest(), new[] { Design() }, ToolingRates.Default);

        private static DesignStockLot Lot(int units, double prepaid, double multiplier, long ticks = 1, string fingerprint = null, bool fundsBuilt = true) => new DesignStockLot
        {
            LotId = Guid.NewGuid(), Fingerprint = fingerprint ?? Design().Fingerprint, Units = units, PrepaidPerUnit = prepaid, LaunchMultiplier = multiplier,
            BuilderAgencyId = Builder, FundsBuilt = fundsBuilt, CreatedUtcTicks = ticks
        };
        private static readonly Guid Builder = Guid.NewGuid();

        private static double Total(int units, double science, double nonScience, double tooled, StockRates rates) =>
            units * (science + nonScience * tooled * (1 - StockPolicy.Discount(units, rates)));

        [TestMethod]
        public void DiscountFollowsTheCappedLinearCurve()
        {
            var rates = StockRates.Default;
            Assert.AreEqual(0d, StockPolicy.Discount(0, rates));
            Assert.AreEqual(0d, StockPolicy.Discount(1, rates));
            Assert.AreEqual(1d / 30, StockPolicy.Discount(2, rates), 1e-12);
            Assert.AreEqual(.3 * 8 / 9, StockPolicy.Discount(9, rates), 1e-12);
            Assert.AreEqual(.3, StockPolicy.Discount(10, rates), 1e-12);
            Assert.AreEqual(.3, StockPolicy.Discount(11, rates), 1e-12);
            Assert.AreEqual(.3, StockPolicy.Discount(100, rates), 1e-12);
            Assert.ThrowsException<ArgumentException>(() => StockPolicy.Discount(5, new StockRates(.5, 10)));
            Assert.ThrowsException<ArgumentException>(() => StockPolicy.Discount(5, null));
        }

        [TestMethod]
        public void TotalIsStrictlyIncreasingAtTheDefaults()
        {
            var rates = StockRates.Default;
            for (var n = 2; n <= 200; n++)
                Assert.IsTrue(Total(n, 300, 500, .1, rates) > Total(n - 1, 300, 500, .1, rates), "n=" + n);
            var design = Design();
            var previous = 0d;
            for (var n = 1; n <= StockDefaults.MaxBuildUnits; n++)
            {
                var quote = StockPolicy.Quote(design, n, ToolingRates.Default, rates);
                Assert.IsTrue(quote.Success, quote.Reason);
                Assert.IsTrue(quote.Total > previous, "n=" + n);
                previous = quote.Total;
            }
        }

        [TestMethod]
        public void TotalIsStrictlyIncreasingForEveryValidSetting()
        {
            foreach (var discount in new[] { 0, .2, .45, .4999 })
            foreach (var full in new[] { 2, 5, 10, 50 })
            {
                var rates = new StockRates(discount, full);
                Assert.IsTrue(rates.Valid);
                // No science: the non-science share alone must still never get cheaper in total.
                for (var n = 2; n <= 200; n++)
                    Assert.IsTrue(Total(n, 0, 1000, .1, rates) > Total(n - 1, 0, 1000, .1, rates), $"D={discount} N={full} n={n}");
            }
        }

        [TestMethod]
        public void SmallestMarginalUnitAtTheDefaultsIsFortyPercentOfATooledLaunch()
        {
            var rates = StockRates.Default;
            var step = Total(10, 0, 1, 1, rates) - Total(9, 0, 1, 1, rates);
            Assert.AreEqual(.4, step, 1e-12);
        }

        [TestMethod]
        public void SettingsValidationAndNormalization()
        {
            Assert.IsTrue(StockRates.IsValid(0, 2));
            Assert.IsTrue(StockRates.IsValid(.4999, 1000));
            Assert.IsTrue(StockRates.Default.Valid);
            Assert.IsFalse(StockRates.IsValid(.5, 10));
            Assert.IsFalse(StockRates.IsValid(-.01, 10));
            Assert.IsFalse(StockRates.IsValid(double.NaN, 10));
            Assert.IsFalse(StockRates.IsValid(double.PositiveInfinity, 10));
            Assert.IsFalse(StockRates.IsValid(.3, 1));
            Assert.IsFalse(StockRates.IsValid(.3, 1001));
            Assert.IsFalse(new StockRates(.6, 10).Valid);
            var normalized = StockRates.Normalize(.6, 20);
            Assert.AreEqual(StockDefaults.MaxDiscount, normalized.MaxDiscount);
            Assert.AreEqual(StockDefaults.FullDiscountUnits, normalized.FullDiscountUnits, "A half-valid pair resets both values.");
            normalized = StockRates.Normalize(.25, 20);
            Assert.AreEqual(.25, normalized.MaxDiscount);
            Assert.AreEqual(20, normalized.FullDiscountUnits);
        }

        [TestMethod]
        public void QuoteDiscountsOnlyTheNonScienceShareAndIgnoresCargo()
        {
            var quote = StockPolicy.Quote(Design(), 10, ToolingRates.Default, StockRates.Default);
            Assert.IsTrue(quote.Success, quote.Reason);
            Assert.AreEqual(10, quote.Units);
            Assert.AreEqual(300d, quote.ScienceCost);
            Assert.AreEqual(500d, quote.NonScienceCost);
            Assert.AreEqual(.3, quote.Discount, 1e-12);
            Assert.AreEqual(.07, quote.UnitMultiplier, 1e-12);
            Assert.AreEqual(335d, quote.PrepaidPerUnit, 1e-9);
            Assert.AreEqual(3350d, quote.Total, 1e-9);
            Assert.AreEqual(350d, quote.TooledLaunchEach, 1e-9);

            var single = StockPolicy.Quote(Design(), 1, ToolingRates.Default, StockRates.Default);
            Assert.AreEqual(single.TooledLaunchEach, single.PrepaidPerUnit, 1e-9, "One unit costs exactly a tooled launch's parts.");
            Assert.AreEqual(.1, single.UnitMultiplier, 1e-12);
        }

        [TestMethod]
        public void QuoteRejectsBadRequestsWithoutThrowing()
        {
            var design = Design();
            foreach (var units in new[] { 0, -1, StockDefaults.MaxBuildUnits + 1 })
            {
                var quote = StockPolicy.Quote(design, units, ToolingRates.Default, StockRates.Default);
                Assert.IsFalse(quote.Success, "units=" + units);
                Assert.IsFalse(string.IsNullOrEmpty(quote.Reason));
            }
            var mismatched = new ToolingDesign { Fingerprint = design.Fingerprint, Manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "other", UnitCost = 1 } } } };
            Assert.IsFalse(StockPolicy.Quote(mismatched, 1, ToolingRates.Default, StockRates.Default).Success);
            Assert.IsFalse(StockPolicy.Quote(null, 1, ToolingRates.Default, StockRates.Default).Success);
            Assert.IsFalse(StockPolicy.Quote(design, 1, new ToolingRates(5, double.NaN, 2, 0), StockRates.Default).Success);
            Assert.IsFalse(StockPolicy.Quote(design, 1, ToolingRates.Default, new StockRates(.5, 10)).Success);

            var huge = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "science", UnitCost = ToolingPolicy.MaxCost, IsScience = true } } };
            var overflow = StockPolicy.Quote(Design(huge), 2, ToolingRates.Default, StockRates.Default);
            Assert.IsFalse(overflow.Success, "A total beyond the supported range fails cleanly.");
        }

        [TestMethod]
        public void LaunchChargeIsCargoPlusAnyTopUpAtTheLotMultiplier()
        {
            var lot = Lot(1, 335, .07);
            Assert.AreEqual(25d, StockPolicy.LaunchCharge(Launch(), lot), 1e-9, "Same manifest: inventory only.");
            // A bigger tank changes the unit cost, not the fingerprint: the extra 200 non-science is charged at the lot multiplier.
            Assert.AreEqual(25d + 14, StockPolicy.LaunchCharge(Launch(Manifest(600)), lot), 1e-9);
            Assert.AreEqual(25d, StockPolicy.LaunchCharge(Launch(Manifest(100)), lot), 1e-9, "Cheaper parts never refund.");
            Assert.ThrowsException<ArgumentException>(() => StockPolicy.LaunchCharge(Launch(), null));
        }

        [TestMethod]
        public void SelectLotPrefersLowestChargeThenOldestThenLowestId()
        {
            var launch = Launch();
            var cheapNew = Lot(2, 335, .07, ticks: 50);
            var cheapOld = Lot(2, 335, .07, ticks: 10);
            var dearer = Lot(2, 300, .07, ticks: 1);
            Assert.AreSame(cheapOld, StockPolicy.SelectLot(new[] { dearer, cheapNew, cheapOld }, launch, true));

            var a = Lot(1, 335, .07, ticks: 5); var b = Lot(1, 335, .07, ticks: 5);
            var lower = a.LotId.CompareTo(b.LotId) < 0 ? a : b;
            Assert.AreSame(lower, StockPolicy.SelectLot(new[] { a, b }, launch, true));
            Assert.AreSame(lower, StockPolicy.SelectLot(new[] { b, a }, launch, true));
        }

        [TestMethod]
        public void SelectLotSkipsEmptyAndOtherDesignLotsAndInCareerOnlyTakesACheaperLot()
        {
            var launch = Launch();
            Assert.IsNull(StockPolicy.SelectLot(new[] { Lot(0, 335, .07), Lot(3, 335, .07, fingerprint: "other") }, launch, true));
            Assert.IsNull(StockPolicy.SelectLot(null, launch, true));
            Assert.IsNull(StockPolicy.SelectLot(new[] { Lot(1, 335, .07) }, new ToolingQuote { Success = false }, true));

            // Unprepaid at the full tooled rate costs exactly the normal launch (375): never burned in Career, fine elsewhere.
            var noBenefit = Lot(1, 0, .1, fundsBuilt: false);
            Assert.IsNull(StockPolicy.SelectLot(new[] { noBenefit }, launch, true));
            Assert.AreSame(noBenefit, StockPolicy.SelectLot(new[] { noBenefit }, launch, false));
            // Sandbox-built at the discounted rate is still cheaper in Career (360 < 375).
            var sandbox = Lot(1, 0, .07, fundsBuilt: false);
            Assert.AreSame(sandbox, StockPolicy.SelectLot(new[] { sandbox }, launch, true));
        }

        [TestMethod]
        public void TakeIsFifoAcrossLotsWithFreshIdsAndKeepsPartialRowIds()
        {
            var fp = Design().Fingerprint;
            var older = Lot(3, 335, .07, ticks: 1); var newer = Lot(5, 300, .08, ticks: 2); var other = Lot(9, 1, .1, ticks: 0, fingerprint: "other");
            var olderId = older.LotId; var newerId = newer.LotId;
            var lots = new List<DesignStockLot> { newer, other, older };
            var taken = StockPolicy.Take(lots, fp, 4);
            Assert.AreEqual(2, taken.Length);
            Assert.AreEqual(3, taken[0].Units); Assert.AreEqual(335d, taken[0].PrepaidPerUnit);
            Assert.AreEqual(1, taken[1].Units); Assert.AreEqual(300d, taken[1].PrepaidPerUnit); Assert.AreEqual(.08, taken[1].LaunchMultiplier);
            Assert.IsTrue(taken.All(t => t.LotId != Guid.Empty && t.LotId != olderId && t.LotId != newerId), "Escrow rows always get fresh ids, even for a whole lot.");
            Assert.IsFalse(lots.Contains(older), "A wholly taken row is removed.");
            Assert.AreEqual(4, newer.Units); Assert.AreEqual(newerId, newer.LotId, "A partly taken row shrinks and keeps its id.");
            Assert.AreEqual(9, other.Units);
        }

        [TestMethod]
        public void TakeShortfallThrowsAndLeavesTheSourceUntouched()
        {
            var fp = Design().Fingerprint;
            var lots = new List<DesignStockLot> { Lot(2, 335, .07), Lot(1, 335, .07, fundsBuilt: false) };
            var before = lots.Select(l => l.LotId + ":" + l.Units).ToArray();
            var error = Assert.ThrowsException<InvalidOperationException>(() => StockPolicy.Take(lots, fp, 4));
            StringAssert.Contains(error.Message, "3");
            CollectionAssert.AreEqual(before, lots.Select(l => l.LotId + ":" + l.Units).ToArray());
            Assert.ThrowsException<InvalidOperationException>(() => StockPolicy.Take(lots, fp, 3, fundsBuiltOnly: true), "Sandbox-built units don't count when only funds-built units may be taken.");
            Assert.AreEqual(2, lots.Count);
            Assert.AreEqual(2, StockPolicy.Take(lots, fp, 2, fundsBuiltOnly: true).Sum(l => l.Units));
            Assert.ThrowsException<ArgumentException>(() => StockPolicy.Take(lots, fp, 0));
        }

        [TestMethod]
        public void MergeCombinesIdenticalTermsAndKeepsTheTargetIdentity()
        {
            var target = Lot(2, 335, .07, ticks: 7);
            var targetId = target.LotId;
            var lots = new List<DesignStockLot> { target };
            StockPolicy.Merge(lots, Lot(3, 335, .07, ticks: 99));
            Assert.AreEqual(1, lots.Count);
            Assert.AreEqual(5, target.Units);
            Assert.AreEqual(targetId, target.LotId);
            Assert.AreEqual(7L, target.CreatedUtcTicks);

            var differentSource = Lot(1, 335, .07); differentSource.SourceAgencyId = Guid.NewGuid();
            var differentBuilder = Lot(1, 335, .07); differentBuilder.BuilderAgencyId = Guid.NewGuid();
            var sandbox = Lot(1, 335, .07, fundsBuilt: false);
            var differentPrice = Lot(1, 300, .07);
            var differentRate = Lot(1, 335, .08);
            foreach (var lot in new[] { differentSource, differentBuilder, sandbox, differentPrice, differentRate }) StockPolicy.Merge(lots, lot);
            Assert.AreEqual(6, lots.Count, "Different terms, builder, source or funds stamp stay separate rows.");
            Assert.AreSame(sandbox, lots.Single(l => !l.FundsBuilt), "An unmatched row is appended as is.");
            Assert.ThrowsException<ArgumentException>(() => StockPolicy.Merge(lots, Lot(0, 335, .07)));
        }

        [TestMethod]
        public void TermsTravelBetweenLotsAndLaunches()
        {
            var lot = Lot(4, 335, .07, fundsBuilt: true); lot.SourceAgencyId = Guid.NewGuid();
            var terms = StockPolicy.TermsOf(lot);
            Assert.AreEqual(lot.LotId, terms.LotId);
            var back = StockPolicy.LotFrom(terms, Guid.NewGuid(), 1, 123);
            Assert.IsTrue(StockPolicy.SameTerms(lot, back));
            Assert.AreEqual(1, back.Units);
            Assert.AreEqual(123L, back.CreatedUtcTicks);
            Assert.AreNotEqual(lot.LotId, back.LotId);
        }
    }
}
