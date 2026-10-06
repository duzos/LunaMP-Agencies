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

        public static bool CanUseEntitlement(ToolingManifest actual, IEnumerable<string> purchasedFingerprints)
        {
            if (actual == null || purchasedFingerprints == null) return false;
            try
            {
                var fingerprint = ToolingPolicy.Fingerprint(actual);
                return purchasedFingerprints.Contains(fingerprint, StringComparer.Ordinal);
            }
            catch (ArgumentException) { return false; }
        }
    }
}
