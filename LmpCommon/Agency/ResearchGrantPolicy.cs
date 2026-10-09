using System;
using System.Collections.Generic;
using System.Linq;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Which exact part lists an agency could currently launch with parts it has not researched. This is a cheap superset used to decide
    /// whether a craft file is worth parsing at all; the client's full check (pricing, the toggles, a pending launch) still decides.
    /// </summary>
    public static class ResearchGrantPolicy
    {
        /// <summary>
        /// Permanent allowances (bought tooling or a bought vessel) always count. Unspent single-launch vouchers count when vouchers apply.
        /// StockDesign entitlements never count: bought stock grants research through its held units, not its craft file.
        /// </summary>
        public static IEnumerable<string> EntitlementFingerprints(IEnumerable<TradeEntitlement> entitlements, bool vouchersApply) =>
            (entitlements ?? Enumerable.Empty<TradeEntitlement>()).Where(e => e != null && !string.IsNullOrEmpty(e.Fingerprint) &&
                (e.Kind == TradeEntitlementKind.Permanent || vouchersApply && e.Kind == TradeEntitlementKind.SingleLaunch && !e.Redeemed))
            .Select(e => e.Fingerprint);

        /// <summary>Designs with at least one held stock unit, plus the design of a stock launch in progress (its row may already be pruned).</summary>
        public static IEnumerable<string> StockFingerprints(IEnumerable<DesignStockLot> lots, string pendingStockFingerprint)
        {
            var held = (lots ?? Enumerable.Empty<DesignStockLot>()).Where(l => l != null && l.Units > 0 && !string.IsNullOrEmpty(l.Fingerprint)).Select(l => l.Fingerprint);
            return string.IsNullOrEmpty(pendingStockFingerprint) ? held : held.Concat(new[] { pendingStockFingerprint });
        }

        public static HashSet<string> Candidates(params IEnumerable<string>[] sources)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources) if (source != null) result.UnionWith(source);
            return result;
        }
    }
}
