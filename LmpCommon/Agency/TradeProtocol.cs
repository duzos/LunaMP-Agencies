using System;
namespace LmpCommon.Agency
{
    public enum TradeOfferStatus : byte { Open, Accepted, Declined, Cancelled, Expired, Invalidated }
    /// <summary>What the buyer receives with a design. The zero value keeps persisted offers meaning tooling plus design.</summary>
    public enum TradeDesignMode : byte { ToolingAndDesign = 0, SingleLaunch = 1, Stock = 2 }
    /// <summary>A permanent research allowance for an exact part list, one prepaid launch of it, or the craft file of bought stock (which grants no research by itself).</summary>
    public enum TradeEntitlementKind : byte { Permanent = 0, SingleLaunch = 1, StockDesign = 2 }
    public sealed class TradeCommand
    {
        public Guid OfferId, BuyerAgencyId, VesselId, EntitlementId;
        public long ExpectedRevision;
        public double SellerFunds, SellerScience, BuyerFunds, BuyerScience;
        public string DesignFingerprint, BlueprintName, Editor;
        public byte[] BlueprintData = Array.Empty<byte>();
        public TradeDesignMode DesignMode;
        /// <summary>Stock only: the units to sell.</summary>
        public int StockUnits;
    }
    public sealed class TradeOffer
    {
        public Guid OfferId, SellerAgencyId, BuyerAgencyId, VesselId;
        public long Revision, ExpiresUtcTicks;
        public TradeOfferStatus Status;
        public double SellerFunds, SellerScience, BuyerFunds, BuyerScience;
        public string DesignFingerprint, BlueprintName, Editor, VesselName;
        public TradeDesignMode DesignMode;
        /// <summary>Single launch only: what the seller's agency pays at accept so the buyer's launch is cheap. Frozen at creation.</summary>
        public double PrepaidLaunchFunds, LaunchMultiplier;
        /// <summary>Stock only: the escrowed units and the sum of their prepayments.</summary>
        public int StockUnits;
        public double StockPrepaidTotal;
    }
    public sealed class TradeEntitlement
    {
        public Guid EntitlementId, SellerAgencyId, VesselId;
        public string Fingerprint, BlueprintName, Editor, BlueprintHash;
        public byte[] BlueprintData = Array.Empty<byte>();
        public bool Delivered;
        public TradeEntitlementKind Kind;
        /// <summary>Single launch only: the seller's prepayment and the part multiplier it was priced at.</summary>
        public double PrepaidFunds, LaunchMultiplier;
        /// <summary>Single launch only: the launch that reserved or redeemed this voucher; empty while available.</summary>
        public Guid LaunchId;
        public bool Redeemed;
    }
    public static class TradeLimits
    {
        public const int MaxBlueprintBytes = 512 * 1024;
        public const int MaxAgencyBlueprintBytes = 2 * 1024 * 1024;
        public const int MaxOpenOffers = 64;
        public const int MaxEntitlements = 512;
    }
}
