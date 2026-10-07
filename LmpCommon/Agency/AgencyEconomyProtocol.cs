using System;
namespace LmpCommon.Agency
{
    public enum EconomyOperation : byte
    {
        Quote, Tool, PrepareLaunch, CancelLaunch, Delta, Recover, Revert, RevertLaunch, RegisterLaunch, BoardEva, Split, TradeCreate, TradeAccept, TradeDecline, TradeCancel, TradeDelivered
    }

    public enum LaunchState : byte { Prepared, Registered, Cancelled, Recovered, Reverted }

    public sealed class RecoveryPart
    {
        public uint FlightId;
        public double StockValue;
    }

    public sealed class EconomyCommand
    {
        public Guid RequestId, LaunchId, LaunchToken, VesselId, SessionId;
        /// <summary>PrepareLaunch only: the single-launch voucher to apply to this launch.</summary>
        public Guid VoucherId;
        public Guid ParentVesselId;
        public string CrewName;
        public byte[] VesselData = Array.Empty<byte>();
        public TradeCommand Trade;
        public long Sequence;
        public EconomyOperation Operation;
        public ToolingManifest Manifest;
        public string ManifestHash;
        public double FundsDelta, ScienceDelta;
        public RecoveryPart[] RecoveredParts = Array.Empty<RecoveryPart>();
        public ToolingCargo[] RecoveredCargo = Array.Empty<ToolingCargo>();
        public double RecoveryFactor = 1;
    }

    public sealed class EconomyResult
    {
        public Guid RequestId, LaunchId, LaunchToken, VesselId, TradeOfferId;
        public EconomyOperation Operation;
        public bool Success, RecoveryRequired;
        public string Reason;
        public long Revision, ExpiresUtcTicks;
        public ToolingQuote Quote;
    }

    public sealed class PaidPart
    {
        public Guid LaunchId;
        public uint FlightId;
        public string Name;
        public double Multiplier = 1, MaximumRefund;
        public bool Legacy;
    }

    public sealed class PaidVesselRecord
    {
        public Guid VesselId;
        public PaidPart[] Parts = Array.Empty<PaidPart>();
        public ToolingCargo[] Cargo = Array.Empty<ToolingCargo>();
    }

    public sealed class LaunchReceiptSummary
    {
        public Guid LaunchId, VesselId;
        public LaunchState State;
        public double Charge;
        public long ExpiresUtcTicks;
    }

    public sealed class EconomySnapshot
    {
        public TradeOffer[] Offers = Array.Empty<TradeOffer>();
        public TradeEntitlement[] Entitlements = Array.Empty<TradeEntitlement>();
        public bool Ready;
        public Guid AgencyId, SessionId;
        public long Revision, LastSequence;
        public double Funds, Science;
        public ToolingDesign[] Designs = Array.Empty<ToolingDesign>();
        public PaidVesselRecord[] Vessels = Array.Empty<PaidVesselRecord>();
        public LaunchReceiptSummary[] Launches = Array.Empty<LaunchReceiptSummary>();
    }
}
