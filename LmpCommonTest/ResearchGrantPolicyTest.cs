using System;
using System.Linq;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    /// <summary>The pre-check that decides which craft files are parsed to unlock a bought design's locked parts in craft lists.</summary>
    [TestClass]
    public class ResearchGrantPolicyTest
    {
        private static TradeEntitlement Entitlement(TradeEntitlementKind kind, string fingerprint, bool redeemed = false) =>
            new TradeEntitlement { EntitlementId = Guid.NewGuid(), Kind = kind, Fingerprint = fingerprint, Redeemed = redeemed };

        [TestMethod]
        public void PermanentAllowancesAlwaysCountAndStockCraftFilesNever()
        {
            var held = new[]
            {
                Entitlement(TradeEntitlementKind.Permanent, "tooling"),
                Entitlement(TradeEntitlementKind.StockDesign, "stock-file"),
                Entitlement(TradeEntitlementKind.SingleLaunch, "voucher"),
                Entitlement(TradeEntitlementKind.SingleLaunch, "spent", redeemed: true),
                Entitlement(TradeEntitlementKind.Permanent, null),
                null
            };
            CollectionAssert.AreEquivalent(new[] { "tooling", "voucher" }, ResearchGrantPolicy.EntitlementFingerprints(held, true).ToArray());
            CollectionAssert.AreEquivalent(new[] { "tooling" }, ResearchGrantPolicy.EntitlementFingerprints(held, false).ToArray(), "Vouchers that won't apply unlock nothing.");
            Assert.AreEqual(0, ResearchGrantPolicy.EntitlementFingerprints(null, true).Count());
        }

        [TestMethod]
        public void OnlyHeldUnitsAndAReservedLaunchCount()
        {
            var lots = new[]
            {
                new DesignStockLot { LotId = Guid.NewGuid(), Fingerprint = "held", Units = 2 },
                new DesignStockLot { LotId = Guid.NewGuid(), Fingerprint = "empty", Units = 0 },
                new DesignStockLot { LotId = Guid.NewGuid(), Fingerprint = null, Units = 3 },
                null
            };
            CollectionAssert.AreEquivalent(new[] { "held" }, ResearchGrantPolicy.StockFingerprints(lots, null).ToArray());
            CollectionAssert.AreEquivalent(new[] { "held", "reserved" }, ResearchGrantPolicy.StockFingerprints(lots, "reserved").ToArray());
            CollectionAssert.AreEquivalent(new[] { "reserved" }, ResearchGrantPolicy.StockFingerprints(null, "reserved").ToArray());
        }

        [TestMethod]
        public void CandidatesAreAnOrdinalUnionAndEmptyWithoutGrants()
        {
            var set = ResearchGrantPolicy.Candidates(new[] { "a", "b" }, null, new[] { "b", "A" });
            CollectionAssert.AreEquivalent(new[] { "a", "b", "A" }, set.ToArray());
            Assert.AreEqual(0, ResearchGrantPolicy.Candidates(ResearchGrantPolicy.EntitlementFingerprints(null, true), ResearchGrantPolicy.StockFingerprints(null, null)).Count,
                "No bought design or stock: craft lists keep KSP's cached fast path.");
        }
    }
}
