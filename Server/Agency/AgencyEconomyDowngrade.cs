using LmpCommon.Agency;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.Agency
{
    /// <summary>What a down-migration changed, printed by the tool and asserted by tests.</summary>
    public sealed class DowngradeSummary
    {
        public int PreparedLaunchesRefunded, OffersCancelled, StockOffersRemoved, StockDesignEntitlementsRemoved, StockLaunchesDetached, LotsCleared, UnitsCredited, BlueprintRefsCleared;
        public double FundsCredited;
        public string BackupPath;
    }

    /// <summary>The operator command that rolls a version 3 economy document back to one an agencies.8 server accepts.</summary>
    public static class AgencyEconomyDowngrade
    {
        /// <summary>Runs the downgrade for a universe directory. Returns 0 on success and 1 on refusal or failure; never throws.</summary>
        public static int Run(string universeDirectory) => AgencyEconomyStore.RunDowngrade(universeDirectory, Console.Out);
    }

    /// <summary>Raised when the downgrade must stop without writing anything.</summary>
    internal sealed class DowngradeRefusedException : Exception
    {
        public DowngradeRefusedException(string message) : base(message) { }
    }

    public static partial class AgencyEconomyStore
    {
        /// <summary>
        /// Converts a version 3 document to version 2 in a copy. Pure over the document: it needs no <c>AgencyStore</c> or settings and never persists.
        /// Prepared launches are refunded, open stock offers cancelled, held units credited at their prepaid price (funds-built only), then every
        /// version 3 structure is cleared. With <paramref name="strict"/> a missing agency row is a refusal; the tool always runs strict.
        /// This deliberately has its own minimal lot logic rather than calling the live helpers, which can touch the agency store.
        /// </summary>
        internal static EconomyDocument ToVersion2(EconomyDocument v3, out DowngradeSummary summary, bool strict = true)
        {
            if (v3 == null) throw new DowngradeRefusedException("No economy document.");
            if (v3.Version < EconomyDocument.CurrentVersion)
                throw new DowngradeRefusedException("The economy document is already version " + v3.Version + "; there is nothing to downgrade.");
            if (v3.Journal != null)
                throw new DowngradeRefusedException("The economy document has an unfinished vessel journal. Start and cleanly stop the agencies.9 server once to finish it, then run the downgrade.");
            try { Validate(v3); }
            catch (InvalidDataException e) { throw new DowngradeRefusedException("The economy document is invalid: " + e.Message); }

            var d = Copy(v3);
            var s = new DowngradeSummary();
            EconomyAgency Row(Guid id, string what)
            {
                if (d.Agencies.TryGetValue(id, out var row)) return row;
                if (strict) throw new DowngradeRefusedException(what + " refers to agency " + id + ", which has no economy row; the funds would be lost.");
                return null;
            }
            void Credit(EconomyAgency row, IEnumerable<(int Units, double PrepaidPerUnit, bool FundsBuilt)> units)
            {
                var total = 0d;
                foreach (var unit in units)
                {
                    if (unit.Units < 0) throw new DowngradeRefusedException("A stock lot has a negative unit count.");
                    if (unit.FundsBuilt) total += unit.Units * unit.PrepaidPerUnit;
                    s.UnitsCredited += unit.Units;
                }
                if (!ValidAmount(total) || !ValidAmount(row.Funds + total)) throw new DowngradeRefusedException("A stock credit exceeds the funds limit.");
                row.Funds += total;
                s.FundsCredited += total;
            }

            // 1. Prepared launches: the charge goes back, a reserved voucher is released, a reserved stock unit is credited.
            foreach (var launch in d.Launches.Values.Where(l => l.State == LaunchState.Prepared).OrderBy(l => l.LaunchId))
            {
                var row = Row(launch.AgencyId, "Prepared launch " + launch.LaunchId);
                if (row == null) { launch.State = LaunchState.Cancelled; continue; }
                row.Funds += launch.Charge;
                launch.State = LaunchState.Cancelled;
                var voucher = FindVoucher(d, launch);
                if (voucher != null && !voucher.Redeemed && voucher.LaunchId == launch.LaunchId) voucher.LaunchId = Guid.Empty;
                if (launch.Stock != null) Credit(row, new[] { (1, launch.Stock.PrepaidPerUnit, launch.Stock.FundsBuilt) });
                s.PreparedLaunchesRefunded++;
            }

            // 2. Open offers: cancelled, with any escrow credited to the seller as if it had been returned to stock.
            foreach (var stored in d.TradeOffers.Values.OrderBy(o => o.Offer.OfferId))
            {
                if (stored.Offer.Status == TradeOfferStatus.Open)
                {
                    if (stored.Escrow != null && stored.Escrow.Count > 0)
                    {
                        var seller = Row(stored.Offer.SellerAgencyId, "Offer " + stored.Offer.OfferId);
                        if (seller != null) Credit(seller, stored.Escrow.Select(l => (l.Units, l.PrepaidPerUnit, l.FundsBuilt)));
                    }
                    stored.Offer.Status = TradeOfferStatus.Cancelled;
                    stored.Offer.Revision++;
                    s.OffersCancelled++;
                }
                stored.Escrow = new List<DesignStockLot>();
            }

            // 3. Held units at their prepaid price. Sandbox-built lots credit nothing.
            foreach (var pair in d.Agencies.OrderBy(a => a.Key))
            {
                var row = pair.Value;
                if (row.Stock != null && row.Stock.Count > 0)
                {
                    Credit(row, row.Stock.Select(l => (l.Units, l.PrepaidPerUnit, l.FundsBuilt)));
                    s.LotsCleared += row.Stock.Count;
                }
                s.BlueprintRefsCleared += row.Blueprints?.Count ?? 0;
                row.Stock = new List<DesignStockLot>();
                row.Blueprints = new Dictionary<string, ToolingBlueprintRef>();
            }

            // 4. Remove what agencies.8 would reject: Stock offers and StockDesign entitlements.
            foreach (var id in d.TradeOffers.Where(p => p.Value.Offer.DesignMode == TradeDesignMode.Stock).Select(p => p.Key).ToArray())
            {
                d.TradeOffers.Remove(id);
                s.StockOffersRemoved++;
            }
            foreach (var list in d.Entitlements.Values)
                s.StockDesignEntitlementsRemoved += list.RemoveAll(e => e.Kind == TradeEntitlementKind.StockDesign);

            // 5. Launches and vessels stay; only the stock terms go. A Registered stock launch keeps its Charge and Multiplier.
            foreach (var launch in d.Launches.Values.Where(l => l.Stock != null))
            {
                launch.Stock = null;
                s.StockLaunchesDetached++;
            }

            d.Operations.Clear();
            d.SessionSequences.Clear();
            d.Version = 2;
            d.Revision = checked(d.Revision + 1);
            summary = s;
            return d;
        }

        /// <summary>Serializes a converted document without the v3-only members, so agencies.8 sees exactly its own schema.</summary>
        internal static string ToVersion2Json(EconomyDocument v2)
        {
            var root = JObject.Parse(JsonConvert.SerializeObject(v2));
            foreach (var agency in Objects(root["Agencies"]))
            {
                agency.Remove("Stock");
                agency.Remove("Blueprints");
                foreach (var design in Objects(agency["Designs"])) design.Remove("Name");
            }
            foreach (var stored in Objects(root["TradeOffers"]))
            {
                stored.Remove("Escrow");
                (stored["Offer"] as JObject)?.Remove("StockUnits");
                (stored["Offer"] as JObject)?.Remove("StockPrepaidTotal");
                (stored["Design"] as JObject)?.Remove("Name");
            }
            foreach (var launch in Objects(root["Launches"])) launch.Remove("Stock");
            root["Version"] = 2;
            return root.ToString(Formatting.None);
        }

        private static List<JObject> Objects(JToken container)
        {
            if (container is JObject map) return map.Properties().Select(p => p.Value).OfType<JObject>().ToList();
            if (container is JArray array) return array.OfType<JObject>().ToList();
            return new List<JObject>();
        }

        /// <summary>Reads, converts and rewrites <c>AgencyEconomy.json</c>. The backup is written first and the result replaces the file atomically.</summary>
        internal static int RunDowngrade(string universeDirectory, TextWriter output)
        {
            var path = Path.Combine(universeDirectory, "AgencyEconomy.json");
            try
            {
                if (!File.Exists(path))
                {
                    output.WriteLine("Downgrade: no AgencyEconomy.json in " + universeDirectory + "; nothing to do.");
                    return 0;
                }
                if (new FileInfo(path).Length > MaxFileBytes) throw new DowngradeRefusedException("The economy file is too large.");
                EconomyDocument v3;
                try { v3 = JsonConvert.DeserializeObject<EconomyDocument>(File.ReadAllText(path)) ?? throw new DowngradeRefusedException("The economy file is empty."); }
                catch (JsonException e) { throw new DowngradeRefusedException("The economy file is not valid JSON: " + e.Message); }

                var v2 = ToVersion2(v3, out var summary);
                var json = ToVersion2Json(v2);
                // The result must pass the version 2 checks before anything is written.
                var check = JsonConvert.DeserializeObject<EconomyDocument>(json);
                if (check == null || check.Version != 2) throw new DowngradeRefusedException("Internal error: the converted document is not version 2.");
                Validate(check);
                if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new DowngradeRefusedException("The converted economy file is too large.");

                var backup = Path.Combine(universeDirectory, "AgencyEconomy.v3." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "Z.bak.json");
                File.Copy(path, backup, false);
                summary.BackupPath = backup;
                var temp = path + ".tmp";
                try
                {
                    File.WriteAllText(temp, json, new UTF8Encoding(false));
                    File.Move(temp, path, true);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }

                output.WriteLine("Downgrade complete: AgencyEconomy.json is now version 2 (agencies.8 compatible).");
                output.WriteLine("  Backup: " + backup);
                output.WriteLine("  Prepared launches refunded: " + summary.PreparedLaunchesRefunded);
                output.WriteLine("  Open offers cancelled: " + summary.OffersCancelled + " (stock offers removed: " + summary.StockOffersRemoved + ")");
                output.WriteLine("  Stock lots cleared: " + summary.LotsCleared + ", units credited: " + summary.UnitsCredited + ", funds credited: " + summary.FundsCredited.ToString("0.##", global::System.Globalization.CultureInfo.InvariantCulture));
                output.WriteLine("  Stock craft entitlements removed: " + summary.StockDesignEntitlementsRemoved + ", saved tooling craft references cleared: " + summary.BlueprintRefsCleared + " (Universe/AgencyBlueprints is left in place)");
                output.WriteLine("  Registered stock launches kept: " + summary.StockLaunchesDetached + " (an agencies.8 revert refunds only their top-up; the unit itself is gone)");
                output.WriteLine("Start the agencies.8 server build next.");
                return 0;
            }
            catch (DowngradeRefusedException e)
            {
                output.WriteLine("Downgrade refused, nothing was changed: " + e.Message);
                return 1;
            }
            catch (Exception e)
            {
                output.WriteLine("Downgrade failed (" + e.GetType().Name + "): " + e.Message);
                return 1;
            }
        }
    }
}
