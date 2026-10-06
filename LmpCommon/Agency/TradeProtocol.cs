using System;
namespace LmpCommon.Agency
{
    public enum TradeOfferStatus : byte { Open, Accepted, Declined, Cancelled, Expired, Invalidated }
    public sealed class TradeCommand
    {
        public Guid OfferId, BuyerAgencyId, VesselId, EntitlementId;
        public long ExpectedRevision;
        public double SellerFunds, SellerScience, BuyerFunds, BuyerScience;
        public string DesignFingerprint, BlueprintName, Editor;
        public byte[] BlueprintData = Array.Empty<byte>();
    }
    public sealed class TradeOffer
    {
        public Guid OfferId, SellerAgencyId, BuyerAgencyId, VesselId;
        public long Revision, ExpiresUtcTicks;
        public TradeOfferStatus Status;
        public double SellerFunds, SellerScience, BuyerFunds, BuyerScience;
        public string DesignFingerprint, BlueprintName, Editor, VesselName;
    }
    public sealed class TradeEntitlement
    {
        public Guid EntitlementId, SellerAgencyId, VesselId;
        public string Fingerprint, BlueprintName, Editor, BlueprintHash;
        public byte[] BlueprintData = Array.Empty<byte>();
        public bool Delivered;
    }
    public static class TradeLimits
    {
        public const int MaxBlueprintBytes = 512 * 1024;
        public const int MaxAgencyBlueprintBytes = 2 * 1024 * 1024;
        public const int MaxOpenOffers = 64;
        public const int MaxEntitlements = 512;
    }
}
