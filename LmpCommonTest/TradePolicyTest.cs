using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class TradePolicyTest
    {
        [TestMethod]
        public void TwoSidedExchangeConservesBothCurrencies()
        {
            var result = TradePolicy.Settle(1000, 100, 500, 50, 100, 10, 250, 20);
            Assert.AreEqual(1150d, result.SellerFunds); Assert.AreEqual(350d, result.BuyerFunds);
            Assert.AreEqual(110d, result.SellerScience); Assert.AreEqual(40d, result.BuyerScience);
        }
        [TestMethod]
        public void ReciprocalOffersCannotFundUnaffordableGrossContributions()
        {
            Assert.ThrowsException<InvalidOperationException>(() => TradePolicy.Settle(0, 10, 0, 10, 100, 0, 100, 0));
            Assert.ThrowsException<InvalidOperationException>(() => TradePolicy.Settle(100, 0, 100, 0, 0, 10, 0, 10));
        }
        [TestMethod]
        public void InvalidAndOverflowingBalancesRejectWithoutPartialResults()
        {
            Assert.IsFalse(TradePolicy.ValidateAmounts(double.NaN, 0, 0, 0));
            Assert.IsFalse(TradePolicy.ValidateAmounts(0, -1, 0, 0));
            Assert.IsFalse(TradePolicy.ValidateAmounts(0, 0, double.PositiveInfinity, 0));
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.Settle(ToolingPolicy.MaxCost, 0, 1, 0, 0, 0, 1, 0));
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.Settle(-1, 0, 0, 0, 0, 0, 0, 0));
        }
        [TestMethod]
        public void EntitlementUsesExactPhysicalCountsAndIgnoresPriceAndLayout()
        {
            var original = new ToolingManifest { Parts = new[] { Part("tank"), Part("engine"), Part("tank") } };
            var fingerprint = ToolingPolicy.Fingerprint(original);
            var rearranged = new ToolingManifest { Parts = new[] { Part("engine"), Part("tank"), Part("tank") } };
            rearranged.Parts[0].UnitCost = 900;
            Assert.IsTrue(TradePolicy.CanUseEntitlement(rearranged, new[] { fingerprint }));
            rearranged.Parts = new[] { Part("engine"), Part("tank") };
            Assert.IsFalse(TradePolicy.CanUseEntitlement(rearranged, new[] { fingerprint }));
            Assert.IsFalse(TradePolicy.CanUseEntitlement(original, Array.Empty<string>()));
            // Science parts never change the licensed design; a licence stored with the old all-parts fingerprint still grants its exact craft.
            var withScience = new ToolingManifest { Parts = new[] { Part("tank"), Part("engine"), Part("tank"), new ToolingPart { Name = "goo", IsScience = true } } };
            Assert.IsTrue(TradePolicy.CanUseEntitlement(withScience, new[] { fingerprint }));
            Assert.IsTrue(TradePolicy.CanUseEntitlement(withScience, new[] { ToolingPolicy.LegacyFingerprint(withScience) }));
            Assert.IsFalse(TradePolicy.CanUseEntitlement(null, new[] { fingerprint }));
        }
        private static ToolingQuote Quote(double science, double nonScience, double cargo, double launch = 0) => new ToolingQuote { Success = true, Fingerprint = "f", ScienceCost = science, NonScienceCost = nonScience, CargoCost = cargo, LaunchCost = launch };
        [TestMethod]
        public void PrepaidLaunchCostIsTheNonSciencePartsAtTheSellerRateOnly()
        {
            Assert.AreEqual(100d * 2, TradePolicy.PrepaidLaunchCost(Quote(300, 100, 55), 2), 1e-9);
            Assert.AreEqual(100d * .1, TradePolicy.PrepaidLaunchCost(Quote(300, 100, 55), .1), 1e-9);
            // The buyer then pays the science parts it launches at raw cost on top of inventory.
            Assert.AreEqual(55d + 300, TradePolicy.VoucherLaunchCharge(Quote(300, 100, 55), TradePolicy.PrepaidLaunchCost(Quote(300, 100, 55), 2), 2), 1e-9);
            Assert.AreEqual(0d, TradePolicy.PrepaidLaunchCost(Quote(0, 0, 55), 2), 1e-9);
        }
        [TestMethod]
        public void VoucherChargeIsInventoryPlusAnyPartCostAboveThePrepayment()
        {
            var quote = Quote(300, 100, 40);
            Assert.AreEqual(40d, TradePolicy.VoucherLaunchCharge(quote, 500, 2), 1e-9, "Prepaid exactly covers the parts: inventory only.");
            Assert.AreEqual(40d, TradePolicy.VoucherLaunchCharge(quote, 900, 2), 1e-9, "Overpayment is never refunded to the buyer.");
            Assert.AreEqual(40d + 60, TradePolicy.VoucherLaunchCharge(quote, 440, 2), 1e-9, "Extra fuel or parts are topped up.");
        }
        [TestMethod]
        public void VoucherPolicyRejectsNonFiniteAndNegativeInputs()
        {
            var quote = Quote(1, 1, 1);
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.PrepaidLaunchCost(quote, double.NaN));
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.PrepaidLaunchCost(quote, -1));
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.PrepaidLaunchCost(new ToolingQuote { Success = false }, 1));
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.PrepaidLaunchCost(Quote(ToolingPolicy.MaxCost, ToolingPolicy.MaxCost, 0), 2));
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.VoucherLaunchCharge(quote, -1, 1));
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.VoucherLaunchCharge(quote, 1, double.PositiveInfinity));
            Assert.ThrowsException<ArgumentException>(() => TradePolicy.VoucherLaunchCharge(null, 1, 1));
        }
        [TestMethod]
        public void SelectVoucherSkipsSpentReservedAndForeignVouchersAndOnlyTakesAWorthwhileOneInCareer()
        {
            TradeEntitlement V(string fingerprint = "f", double prepaid = 200, bool redeemed = false, bool reserved = false) => new TradeEntitlement { EntitlementId = Guid.NewGuid(), Kind = TradeEntitlementKind.SingleLaunch, Fingerprint = fingerprint, PrepaidFunds = prepaid, LaunchMultiplier = 2, Redeemed = redeemed, LaunchId = reserved ? Guid.NewGuid() : Guid.Empty };
            var quote = Quote(0, 100, 40, launch: 240);
            var spent = V(redeemed: true); var reserved = V(reserved: true); var foreign = V("other"); var worthless = V(prepaid: 0); var good = V(); var later = V();
            var permanent = new TradeEntitlement { EntitlementId = Guid.NewGuid(), Kind = TradeEntitlementKind.Permanent, Fingerprint = "f" };
            Assert.AreSame(good, TradePolicy.SelectVoucher(new[] { permanent, spent, reserved, foreign, good, later }, quote, true));
            Assert.IsNull(TradePolicy.SelectVoucher(new[] { permanent, spent, reserved, foreign }, quote, true));
            Assert.IsNull(TradePolicy.SelectVoucher(new[] { worthless }, quote, true), "A voucher that saves nothing is left unspent in career.");
            Assert.AreSame(worthless, TradePolicy.SelectVoucher(new[] { worthless }, quote, false), "Without funds the voucher still grants its research allowance.");
            Assert.IsNull(TradePolicy.SelectVoucher(null, quote, true));
            Assert.IsNull(TradePolicy.SelectVoucher(new[] { good }, new ToolingQuote { Success = false }, true));
        }
        private static ToolingPart Part(string name) => new ToolingPart { Name = name, UnitCost = 100 };
    }
}
