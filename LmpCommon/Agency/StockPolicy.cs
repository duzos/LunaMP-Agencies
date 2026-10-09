using System;
using System.Collections.Generic;
using System.Linq;

namespace LmpCommon.Agency
{
    /// <summary>Single home of the design-stock defaults and hard limits. Server settings, the settings wire message and the client all start from these.</summary>
    public static class StockDefaults
    {
        /// <summary>Default for the StockMaxDiscount setting: the largest volume discount on the tooled non-science share.</summary>
        public const double MaxDiscount = 0.30;
        /// <summary>Default for the StockFullDiscountUnits setting: the build size that reaches the full discount.</summary>
        public const int FullDiscountUnits = 10;
        /// <summary>Most units one BuildStock request may build.</summary>
        public const int MaxBuildUnits = 100;
        /// <summary>Most units an agency may hold of one fingerprint, counting lots, escrow, Prepared and revertible stock launches.</summary>
        public const int MaxHeldUnits = 999;
        /// <summary>Most lot slots per agency: stock rows, escrow rows in its open offers and Prepared stock launches.</summary>
        public const int MaxLots = 64;
        /// <summary>Settings bounds. A discount of 0.5 or more would make building more units cheaper in total.</summary>
        public const double MaxDiscountLimit = 0.5;
        public const int MinFullDiscountUnits = 2, MaxFullDiscountUnits = 1000;
    }

    /// <summary>The two volume-discount settings handed to <see cref="StockPolicy.Quote"/>. Valid means 0 &lt;= MaxDiscount &lt; 0.5 and 2 &lt;= FullDiscountUnits &lt;= 1000.</summary>
    public sealed class StockRates
    {
        public readonly double MaxDiscount;
        public readonly int FullDiscountUnits;
        public StockRates(double maxDiscount, int fullDiscountUnits) { MaxDiscount = maxDiscount; FullDiscountUnits = fullDiscountUnits; }
        public bool Valid => IsValid(MaxDiscount, FullDiscountUnits);
        public static StockRates Default => new StockRates(StockDefaults.MaxDiscount, StockDefaults.FullDiscountUnits);
        public static bool IsValid(double maxDiscount, int fullDiscountUnits) =>
            !double.IsNaN(maxDiscount) && !double.IsInfinity(maxDiscount) && maxDiscount >= 0 && maxDiscount < StockDefaults.MaxDiscountLimit &&
            fullDiscountUnits >= StockDefaults.MinFullDiscountUnits && fullDiscountUnits <= StockDefaults.MaxFullDiscountUnits;
        /// <summary>The given pair when it is valid, otherwise both defaults. The pair is replaced together so a half-valid setting never mixes with a default.</summary>
        public static StockRates Normalize(double maxDiscount, int fullDiscountUnits) =>
            IsValid(maxDiscount, fullDiscountUnits) ? new StockRates(maxDiscount, fullDiscountUnits) : Default;
    }

    /// <summary>A counted, prepaid batch of launches of one exact design, held by one agency. Terms travel with the units through trades.</summary>
    public sealed class DesignStockLot
    {
        public Guid LotId;
        public string Fingerprint;
        /// <summary>Available units. Units that are reserved by a launch or escrowed in an offer are already removed.</summary>
        public int Units;
        /// <summary>Funds paid per unit for parts (0 outside Career).</summary>
        public double PrepaidPerUnit;
        /// <summary>The non-science multiplier the prepayment covers: TooledLaunch x (1 - discount).</summary>
        public double LaunchMultiplier;
        public Guid BuilderAgencyId;
        /// <summary>The agency this lot was bought from; empty when self-built.</summary>
        public Guid SourceAgencyId;
        /// <summary>True when the lot was built in a mode that charges funds. Sandbox-built units can't be sold in Career.</summary>
        public bool FundsBuilt;
        /// <summary>FIFO order.</summary>
        public long CreatedUtcTicks;
    }

    /// <summary>The terms of one stock unit, copied onto a launch at Prepare. LotId is a research key only and is never dereferenced.</summary>
    public sealed class StockTerms
    {
        public Guid LotId;
        public string Fingerprint;
        public double PrepaidPerUnit, LaunchMultiplier;
        public Guid BuilderAgencyId, SourceAgencyId;
        public bool FundsBuilt;
    }

    public sealed class StockQuote
    {
        public bool Success;
        public string Reason, Fingerprint;
        public int Units;
        public double Discount, UnitMultiplier, ScienceCost, NonScienceCost, PrepaidPerUnit, Total, TooledLaunchEach;
    }

    /// <summary>What the editor Load button needs to know progresses through these states.</summary>
    public enum DesignLoadState { Idle, Refused, NeedsConfirm, Fetching, Loading, Loaded, Failed }

    /// <summary>A server-side reference to a tooling blueprint file (Universe/AgencyBlueprints/&lt;Hash&gt;.craft). Never carries bytes.</summary>
    public sealed class ToolingBlueprintRef
    {
        public string Fingerprint, Name, Editor, Hash;
        public int Size;
        public long SavedUtcTicks;
    }

    /// <summary>Snapshot metadata of a saved tooling blueprint: what can be loaded, without the bytes.</summary>
    public sealed class ToolingBlueprintInfo
    {
        public string Fingerprint, Name, Editor, Hash;
        public int Bytes;
    }

    public static class ToolingLimits
    {
        public const int MaxToolingBlueprintBytes = TradeLimits.MaxBlueprintBytes;
        /// <summary>Sum of Size over one agency's tooling blueprint refs. Not the purchased-design cap in <see cref="TradeLimits.MaxAgencyBlueprintBytes"/>.</summary>
        public const int MaxAgencyToolingBlueprintBytes = 8 * 1024 * 1024;
        /// <summary>Sum of Size over distinct hashes across all agencies.</summary>
        public const long MaxServerToolingBlueprintBytes = 256L * 1024 * 1024;
        public const int BlueprintReplaceCooldownSeconds = 60;
    }

    /// <summary>Pure pricing and bookkeeping of design stock. Only the tooled non-science share is discounted; science parts and inventory are never discounted.</summary>
    public static class StockPolicy
    {
        /// <summary>d(1) = 0; d(n) = D x min(1, (n - 1) / (N - 1)) for n &gt;= 2.</summary>
        public static double Discount(int units, StockRates rates)
        {
            if (rates == null || !rates.Valid) throw new ArgumentException("Invalid stock settings.");
            if (units <= 1) return 0;
            return rates.MaxDiscount * Math.Min(1.0, (units - 1) / (double)(rates.FullDiscountUnits - 1));
        }

        /// <summary>The price of building <paramref name="units"/> of a tooled design from its stored manifest. Stored cargo is ignored. Never throws.</summary>
        public static StockQuote Quote(ToolingDesign design, int units, ToolingRates tooling, StockRates stock)
        {
            try
            {
                if (design == null) throw new ArgumentException("Only a tooled design can be built.");
                if (units < 1 || units > StockDefaults.MaxBuildUnits) throw new ArgumentException("Build between 1 and " + StockDefaults.MaxBuildUnits + " units at a time.");
                if (tooling == null || !tooling.Valid) throw new ArgumentException("Invalid tooling settings.");
                if (stock == null || !stock.Valid) throw new ArgumentException("Invalid stock settings.");
                if (string.IsNullOrEmpty(design.Fingerprint) || ToolingPolicy.Fingerprint(design.Manifest) != design.Fingerprint) throw new ArgumentException("Invalid saved tooling design.");
                var quote = new StockQuote { Success = true, Reason = "Quote ready.", Fingerprint = design.Fingerprint, Units = units };
                quote.ScienceCost = Check(design.Manifest.Parts.Where(p => p.IsScience).Sum(p => p.UnitCost));
                quote.NonScienceCost = Check(design.Manifest.Parts.Where(p => !p.IsScience).Sum(p => p.UnitCost));
                quote.Discount = Discount(units, stock);
                quote.UnitMultiplier = Check(tooling.TooledLaunch * (1 - quote.Discount));
                quote.PrepaidPerUnit = Check(quote.ScienceCost + quote.NonScienceCost * quote.UnitMultiplier);
                quote.Total = Check(quote.PrepaidPerUnit * units);
                quote.TooledLaunchEach = Check(quote.ScienceCost + quote.NonScienceCost * tooling.TooledLaunch);
                return quote;
            }
            catch (ArgumentException error) { return new StockQuote { Success = false, Reason = error.Message, Fingerprint = design?.Fingerprint, Units = units }; }
        }

        /// <summary>What a launch from this lot still charges: all inventory plus any part cost above the unit's prepayment.</summary>
        public static double LaunchCharge(ToolingQuote launch, DesignStockLot lot)
        {
            if (lot == null) throw new ArgumentException("A stock lot is required.");
            return TradePolicy.VoucherLaunchCharge(launch, lot.PrepaidPerUnit, lot.LaunchMultiplier);
        }

        /// <summary>
        /// The lot a launch of this quoted design would use: lowest charge, then oldest, then lowest LotId. In Career a lot is only taken when it
        /// costs less than the normal launch, so stock is never burned for no benefit. Outside Career any matching lot qualifies.
        /// </summary>
        public static DesignStockLot SelectLot(IEnumerable<DesignStockLot> lots, ToolingQuote launch, bool usesFunds)
        {
            if (lots == null || launch == null || !launch.Success) return null;
            DesignStockLot best = null; var bestCharge = 0.0;
            foreach (var lot in lots)
            {
                if (lot == null || lot.Units <= 0 || !string.Equals(lot.Fingerprint, launch.Fingerprint, StringComparison.Ordinal)) continue;
                double charge;
                try { charge = LaunchCharge(launch, lot); }
                catch (ArgumentException) { continue; }
                if (usesFunds && !(charge < launch.LaunchCost)) continue;
                if (best == null || charge < bestCharge || charge == bestCharge && (lot.CreatedUtcTicks < best.CreatedUtcTicks || lot.CreatedUtcTicks == best.CreatedUtcTicks && lot.LotId.CompareTo(best.LotId) < 0))
                { best = lot; bestCharge = charge; }
            }
            return best;
        }

        /// <summary>
        /// Removes <paramref name="units"/> of a fingerprint from <paramref name="lots"/>, oldest first, and returns them as new rows with fresh LotIds and
        /// unchanged terms. A partly taken row shrinks and keeps its LotId; a wholly taken row is removed. Atomic: when short it throws and leaves the list untouched.
        /// <paramref name="fundsBuiltOnly"/> restricts the take to lots built in a funds-charging mode.
        /// </summary>
        public static DesignStockLot[] Take(List<DesignStockLot> lots, string fingerprint, int units, bool fundsBuiltOnly = false)
        {
            if (lots == null || string.IsNullOrEmpty(fingerprint) || units < 1) throw new ArgumentException("A design and at least one unit are required.");
            var eligible = lots.Where(l => l != null && l.Units > 0 && string.Equals(l.Fingerprint, fingerprint, StringComparison.Ordinal) && (!fundsBuiltOnly || l.FundsBuilt))
                .OrderBy(l => l.CreatedUtcTicks).ThenBy(l => l.LotId).ToList();
            var available = eligible.Sum(l => (long)l.Units);
            if (available < units) throw new InvalidOperationException("You have only " + available + " units of this design.");
            var taken = new List<DesignStockLot>();
            var remaining = units;
            foreach (var lot in eligible)
            {
                if (remaining == 0) break;
                var count = Math.Min(remaining, lot.Units);
                var row = Copy(lot);
                row.LotId = Guid.NewGuid();
                row.Units = count;
                taken.Add(row);
                lot.Units -= count;
                if (lot.Units == 0) lots.Remove(lot);
                remaining -= count;
            }
            return taken.ToArray();
        }

        /// <summary>Adds a row to a stock list: merged into a row with the same terms (whose LotId and age are kept), otherwise appended as is.</summary>
        public static void Merge(List<DesignStockLot> into, DesignStockLot lot)
        {
            if (into == null || lot == null || lot.Units < 1) throw new ArgumentException("A stock list and a non-empty lot are required.");
            var target = into.FirstOrDefault(l => l != null && SameTerms(l, lot));
            if (target == null) { into.Add(lot); return; }
            target.Units = checked(target.Units + lot.Units);
        }

        /// <summary>The Merge key: fingerprint, prepayment, multiplier, builder, source and the funds-built stamp.</summary>
        public static bool SameTerms(DesignStockLot a, DesignStockLot b) =>
            a != null && b != null && string.Equals(a.Fingerprint, b.Fingerprint, StringComparison.Ordinal) && a.PrepaidPerUnit.Equals(b.PrepaidPerUnit) &&
            a.LaunchMultiplier.Equals(b.LaunchMultiplier) && a.BuilderAgencyId == b.BuilderAgencyId && a.SourceAgencyId == b.SourceAgencyId && a.FundsBuilt == b.FundsBuilt;

        /// <summary>The terms of one unit of this lot, with LotId set to the lot's id.</summary>
        public static StockTerms TermsOf(DesignStockLot lot)
        {
            if (lot == null) throw new ArgumentException("A stock lot is required.");
            return new StockTerms { LotId = lot.LotId, Fingerprint = lot.Fingerprint, PrepaidPerUnit = lot.PrepaidPerUnit, LaunchMultiplier = lot.LaunchMultiplier,
                BuilderAgencyId = lot.BuilderAgencyId, SourceAgencyId = lot.SourceAgencyId, FundsBuilt = lot.FundsBuilt };
        }

        /// <summary>A new row carrying these terms.</summary>
        public static DesignStockLot LotFrom(StockTerms terms, Guid lotId, int units, long createdUtcTicks)
        {
            if (terms == null) throw new ArgumentException("Stock terms are required.");
            return new DesignStockLot { LotId = lotId, Fingerprint = terms.Fingerprint, Units = units, PrepaidPerUnit = terms.PrepaidPerUnit, LaunchMultiplier = terms.LaunchMultiplier,
                BuilderAgencyId = terms.BuilderAgencyId, SourceAgencyId = terms.SourceAgencyId, FundsBuilt = terms.FundsBuilt, CreatedUtcTicks = createdUtcTicks };
        }

        private static DesignStockLot Copy(DesignStockLot lot) => new DesignStockLot { LotId = lot.LotId, Fingerprint = lot.Fingerprint, Units = lot.Units, PrepaidPerUnit = lot.PrepaidPerUnit,
            LaunchMultiplier = lot.LaunchMultiplier, BuilderAgencyId = lot.BuilderAgencyId, SourceAgencyId = lot.SourceAgencyId, FundsBuilt = lot.FundsBuilt, CreatedUtcTicks = lot.CreatedUtcTicks };

        private static double Check(double value)
        {
            if (!ToolingPolicy.FiniteNonNegative(value)) throw new ArgumentException("Cost exceeds supported finite range.");
            return value;
        }
    }
}
