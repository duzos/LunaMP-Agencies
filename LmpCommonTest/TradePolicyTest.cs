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
            Assert.IsFalse(TradePolicy.CanUseEntitlement(null, new[] { fingerprint }));
        }
        private static ToolingPart Part(string name) => new ToolingPart { Name = name, UnitCost = 100 };
    }
}
