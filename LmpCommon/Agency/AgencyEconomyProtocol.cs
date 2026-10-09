using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
namespace LmpCommon.Agency
{
    public enum EconomyOperation : byte
    {
        Quote, Tool, PrepareLaunch, CancelLaunch, Delta, Recover, Revert, RevertLaunch, RegisterLaunch, BoardEva, Split, TradeCreate, TradeAccept, TradeDecline, TradeCancel, TradeDelivered,
        // Appended after TradeDelivered so existing byte values and the trade operation range stay unchanged.
        BuildStock, FetchBlueprint
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
        /// <summary>PrepareLaunch only: the stock lot to launch from. Never together with <see cref="VoucherId"/>.</summary>
        public Guid StockLotId;
        /// <summary>BuildStock: the tooled design and unit count. FetchBlueprint: the design whose saved craft to return.</summary>
        public string StockFingerprint;
        public int StockUnits;
        /// <summary>BuildStock only: the total the client quoted (0 outside Career); a mismatch refuses the build.</summary>
        public double ExpectedCharge;
        /// <summary>Tool only: the display name stored as <see cref="ToolingDesign.Name"/>.</summary>
        public string DesignName;
        /// <summary>Tool only: the editor craft bytes to save as the design's loadable blueprint, and its facility (VAB or SPH).</summary>
        public byte[] BlueprintData = Array.Empty<byte>();
        public string BlueprintEditor;
    }

    public sealed class EconomyResult
    {
        public Guid RequestId, LaunchId, LaunchToken, VesselId, TradeOfferId;
        public EconomyOperation Operation;
        public bool Success, RecoveryRequired;
        public string Reason;
        public long Revision, ExpiresUtcTicks;
        public ToolingQuote Quote;
        /// <summary>FetchBlueprint only: the saved tooling craft.</summary>
        public byte[] BlueprintData = Array.Empty<byte>();
        public string BlueprintEditor, BlueprintName, BlueprintHash;
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
        /// <summary>The receiving agency's own stock lots.</summary>
        public DesignStockLot[] Stock = Array.Empty<DesignStockLot>();
        /// <summary>Metadata of the receiving agency's saved tooling blueprints; never the bytes.</summary>
        public ToolingBlueprintInfo[] DesignBlueprints = Array.Empty<ToolingBlueprintInfo>();
        /// <summary>Held units per fingerprint as the server counts them for the cap: lots, escrow, Prepared and revertible stock launches.</summary>
        public Dictionary<string, int> StockHeldByFingerprint = new Dictionary<string, int>(StringComparer.Ordinal);
        public const int MaxStockHeldEntries = 1000;

        [OnDeserialized]
        private void BoundStockHeld(StreamingContext context)
        {
            // Absent in older payloads (the initializer stays), explicit null, or oversized: always end with a bounded ordinal map.
            if (StockHeldByFingerprint == null) { StockHeldByFingerprint = new Dictionary<string, int>(StringComparer.Ordinal); return; }
            if (StockHeldByFingerprint.Count <= MaxStockHeldEntries) return;
            StockHeldByFingerprint = StockHeldByFingerprint.OrderBy(p => p.Key, StringComparer.Ordinal).Take(MaxStockHeldEntries).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }
    }
}
