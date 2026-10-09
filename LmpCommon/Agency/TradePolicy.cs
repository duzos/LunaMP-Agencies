using System;
using System.Collections.Generic;
using System.Linq;

namespace LmpCommon.Agency
{
    public sealed class TradeBalances
    {
        public double SellerFunds, SellerScience, BuyerFunds, BuyerScience;
    }

    /// <summary>Pure exchange arithmetic and physical-design eligibility; cargo needs its own stock research check.</summary>
    public static class TradePolicy
    {
        public static bool ValidateAmounts(double sellerFunds, double sellerScience, double buyerFunds, double buyerScience) =>
            ToolingPolicy.FiniteNonNegative(sellerFunds) && ToolingPolicy.FiniteNonNegative(sellerScience) &&
            ToolingPolicy.FiniteNonNegative(buyerFunds) && ToolingPolicy.FiniteNonNegative(buyerScience);

        public static TradeBalances Settle(double sellerFundsBalance, double sellerScienceBalance,
            double buyerFundsBalance, double buyerScienceBalance,
            double sellerFunds, double sellerScience, double buyerFunds, double buyerScience)
        {
            if (!ValidateAmounts(sellerFundsBalance, sellerScienceBalance, buyerFundsBalance, buyerScienceBalance) ||
                !ValidateAmounts(sellerFunds, sellerScience, buyerFunds, buyerScience))
                throw new ArgumentException("Trade amounts and balances must be finite and nonnegative.");
            // Gross affordability prevents reciprocal offers from inventing spending power.
            if (sellerFundsBalance < sellerFunds || sellerScienceBalance < sellerScience ||
                buyerFundsBalance < buyerFunds || buyerScienceBalance < buyerScience)
                throw new InvalidOperationException("An agency cannot afford its offered funds or science.");
            var result = new TradeBalances
            {
                SellerFunds = sellerFundsBalance - sellerFunds + buyerFunds,
                SellerScience = sellerScienceBalance - sellerScience + buyerScience,
                BuyerFunds = buyerFundsBalance - buyerFunds + sellerFunds,
                BuyerScience = buyerScienceBalance - buyerScience + sellerScience
            };
            if (!ValidateAmounts(result.SellerFunds, result.SellerScience, result.BuyerFunds, result.BuyerScience))
                throw new ArgumentException("Trade would exceed the supported balance limit.");
            return result;
        }

        /// <summary>What the seller's agency pays up front for a single free launch: the non-science parts at the seller's own launch rate. Science parts are not part of the design; the buyer pays the ones it launches at raw cost.</summary>
        public static double PrepaidLaunchCost(ToolingQuote quote, double rate)
        {
            if (quote == null || !quote.Success || !ToolingPolicy.FiniteNonNegative(rate)) throw new ArgumentException("A valid quote and launch rate are required.");
            var cost = quote.NonScienceCost * rate;
            if (!ToolingPolicy.FiniteNonNegative(cost)) throw new ArgumentException("Prepaid launch cost exceeds supported range.");
            return cost;
        }

        /// <summary>
        /// What the buyer still pays when launching on a voucher or stock unit: all inventory and the science parts at raw cost, plus any non-science part
        /// cost above what was prepaid (extra fuel and the like). A prepayment left over after the non-science parts (a lighter fuel load, or a lot
        /// built before agencies.11 that also prepaid the design's science) is credited against the science parts instead of being forfeited.
        /// </summary>
        public static double VoucherLaunchCharge(ToolingQuote quote, double prepaid, double multiplier)
        {
            if (quote == null || !quote.Success || !ToolingPolicy.FiniteNonNegative(prepaid) || !ToolingPolicy.FiniteNonNegative(multiplier)) throw new ArgumentException("A valid quote, prepayment and multiplier are required.");
            var charge = quote.CargoCost + Math.Max(0, quote.ScienceCost + quote.NonScienceCost * multiplier - prepaid);
            if (!ToolingPolicy.FiniteNonNegative(charge)) throw new ArgumentException("Voucher launch charge exceeds supported range.");
            return charge;
        }

        /// <summary>
        /// The voucher a launch of this design would use: the first unspent, unreserved one for the exact part list. In career a voucher is
        /// only taken when it actually costs less than the normal launch, so it is never burned for no benefit.
        /// </summary>
        public static TradeEntitlement SelectVoucher(IEnumerable<TradeEntitlement> entitlements, ToolingQuote quote, bool usesFunds)
        {
            if (entitlements == null || quote == null || !quote.Success) return null;
            foreach (var entitlement in entitlements)
            {
                if (entitlement == null || entitlement.Kind != TradeEntitlementKind.SingleLaunch || entitlement.Redeemed || entitlement.LaunchId != Guid.Empty ||
                    !string.Equals(entitlement.Fingerprint, quote.Fingerprint, StringComparison.Ordinal)) continue;
                try
                {
                    if (!usesFunds || VoucherLaunchCharge(quote, entitlement.PrepaidFunds, entitlement.LaunchMultiplier) < quote.LaunchCost) return entitlement;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static bool CanUseEntitlement(ToolingManifest actual, IEnumerable<string> purchasedFingerprints)
        {
            if (actual == null || purchasedFingerprints == null) return false;
            try
            {
                // A licence written before agencies.11 may still carry the old all-parts fingerprint; it keeps granting the exact craft it was bought for.
                var fingerprint = ToolingPolicy.Fingerprint(actual);
                var legacy = ToolingPolicy.LegacyFingerprint(actual);
                return purchasedFingerprints.Any(f => string.Equals(f, fingerprint, StringComparison.Ordinal) || string.Equals(f, legacy, StringComparison.Ordinal));
            }
            catch (ArgumentException) { return false; }
        }
    }
}
