using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using LmpCommon.Enums;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// Cached, exception-safe reads of the design-stock client state for IMGUI. The client members can throw (not ready, or not implemented yet),
    /// and a throw inside OnGUI breaks the layout, so every read goes through here. The cache is rebuilt at most twice a second and only
    /// during the Layout event, so a frame's Layout and Repaint passes always see the same data.
    /// </summary>
    internal static class StockUi
    {
        private static readonly Dictionary<string, int> available = new Dictionary<string, int>();
        private static float nextRefresh;
        private static int lotCount;
        internal static int LotCount => lotCount;
        internal static bool LotSlotsFull => lotCount >= StockDefaults.MaxLots;
        internal static bool CanRefresh => Event.current == null || Event.current.type == EventType.Layout;

        /// <summary>Rebuilds the cache if it is due. Call at the start of any drawer that reads it. Returns true when it rebuilt.</summary>
        internal static bool Refresh(bool force = false)
        {
            if (!CanRefresh) return false;
            if (!force && Time.realtimeSinceStartup < nextRefresh) return false;
            nextRefresh = Time.realtimeSinceStartup + 0.5f;
            available.Clear();
            lotCount = 0;
            try
            {
                var lots = ToolingClient.GetStockSnapshot();
                if (lots != null)
                    foreach (var lot in lots)
                    {
                        if (lot == null || string.IsNullOrEmpty(lot.Fingerprint) || lot.Units <= 0) continue;
                        lotCount++;
                        available.TryGetValue(lot.Fingerprint, out var units);
                        available[lot.Fingerprint] = units + lot.Units;
                    }
            }
            catch (Exception) { available.Clear(); lotCount = 0; }
            return true;
        }

        /// <summary>Available (sellable, launchable) units of a design in this agency's lots.</summary>
        internal static int Available(string fingerprint)
        {
            Refresh();
            return fingerprint != null && available.TryGetValue(fingerprint, out var units) ? units : 0;
        }

        internal static IEnumerable<string> StockFingerprints() { Refresh(); return available.Keys; }

        internal static int Held(string fingerprint)
        {
            try { return ToolingClient.StockUnits(fingerprint); } catch (Exception) { return 0; }
        }
        internal static int Offered(string fingerprint)
        {
            try { return ToolingClient.OfferedUnits(fingerprint); } catch (Exception) { return 0; }
        }
        internal static ToolingBlueprintInfo Blueprint(string fingerprint)
        {
            try { return ToolingClient.BlueprintInfo(fingerprint); } catch (Exception) { return null; }
        }
        internal static ReceivedTradeDesign BoughtDesign(string fingerprint)
        {
            try { return TradeClient.StockDesignFor(fingerprint); } catch (Exception) { return null; }
        }
        internal static StockQuote Quote(string fingerprint, int units)
        {
            try { return ToolingClient.QuoteBuild(fingerprint, units); } catch (Exception) { return null; }
        }
        internal static IReadOnlyList<ToolingDesign> Designs()
        {
            try { return ToolingClient.GetDesignsSnapshot(); } catch (Exception) { return null; }
        }
        internal static bool Digits(string text, out string digits)
        {
            digits = new string((text ?? string.Empty).Where(char.IsDigit).Take(4).ToArray());
            return digits == text;
        }
        internal static int ParseUnits(string text, int max)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var units)) units = 1;
            return Mathf.Clamp(units, 1, Math.Max(1, max));
        }
        internal static string ShortFingerprint(string fingerprint) => string.IsNullOrEmpty(fingerprint) ? "" : fingerprint.Substring(0, Math.Min(8, fingerprint.Length));
    }

    public partial class AgencyWindow
    {
        private sealed class DesignRow
        {
            internal string Fingerprint, Name, Detail, Facility;
            internal bool Bought;
            internal int Available, Offered, Held, Units;
            internal Guid SourceAgency;
            internal ToolingBlueprintInfo Blueprint;
            internal ReceivedTradeDesign BoughtDesign;
            internal StockQuote Quote;
        }

        private static string designsSearch = "";
        private static bool designsSortStock;
        private static Guid designsAgency;
        private static string designsSave;
        private static readonly Dictionary<string, string> designsQty = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> designsDetail = new Dictionary<string, string>();
        private static readonly List<DesignRow> designsRows = new List<DesignRow>();
        private static int designsTotal, designsShown, designsBoughtTotal, designsBoughtShown;
        private static bool designsDirty = true;
        private static float designsNextRebuild;
        private static string designsBuiltSearch;
        private static bool designsBuiltSort;
        private static string designsBuildConfirm, designsLoadConfirm, designsStatusFingerprint, designsStatus;
        private static int designsConfirmUnits;
        private static double designsConfirmTotal;
        private static Guid designsBoughtLoadConfirm;
        private static string designsPendingFingerprint;
        private static int designsPendingHeld;
        private static float designsPendingDeadline;
        private static StockRates designsRates = StockRates.Default;
        private static Vector2 designsScroll;
        private static GUIStyle designsText, designsHeading, designsButton;

        /// <summary>The Tooled designs tab (plan 40): build stock, search and sort, load a design into the editor, and sell stock.</summary>
        private static void DrawDesignsTab()
        {
            if (designsText == null)
            {
                designsText = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
                designsHeading = new GUIStyle(designsText) { fontStyle = FontStyle.Bold };
                designsButton = new GUIStyle(GUI.skin.button) { wordWrap = true, richText = false };
            }
            if (!ToolingClient.Enabled) { GUILayout.Label("Tooling is off on this server.", designsText); return; }
            if (designsAgency != AgencySystem.Singleton.MyAgencyId || designsSave != HighLogic.SaveFolder)
            {
                designsAgency = AgencySystem.Singleton.MyAgencyId;
                designsSave = HighLogic.SaveFolder;
                designsSearch = ""; designsSortStock = false;
                designsQty.Clear();
                designsBuildConfirm = designsLoadConfirm = designsStatus = designsStatusFingerprint = designsPendingFingerprint = null;
                designsBoughtLoadConfirm = Guid.Empty;
                designsDirty = true;
            }
            if (!ToolingClient.Ready) { GUILayout.Label("Syncing tooled designs…", designsText); return; }
            var usesFunds = SettingsSystem.ServerSettings.GameMode == GameMode.Career;
            if (!usesFunds) GUILayout.Label("No funds are charged in this game mode.", designsText);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(52));
            designsSearch = GUILayout.TextField(designsSearch ?? string.Empty, 80);
            if (GUILayout.Button(designsSortStock ? "Stock ▼" : "Name ▲", designsButton, GUILayout.Width(80))) designsSortStock = !designsSortStock;
            GUILayout.EndHorizontal();

            RebuildDesignRows();
            GUILayout.Label("Showing " + designsShown + " of " + designsTotal + " designs" + (designsBoughtTotal > 0 ? " · " + designsBoughtShown + " of " + designsBoughtTotal + " bought stock" : ""), designsText);
            if (designsTotal + designsBoughtTotal == 0)
                GUILayout.Label("No tooled designs yet. Tool a design in the editor, then build stock of it here.", designsText);
            else if (designsShown + designsBoughtShown == 0)
                GUILayout.Label("No designs match \"" + designsSearch + "\".", designsText);

            designsScroll = GUILayout.BeginScrollView(designsScroll);
            var boughtHeader = false;
            foreach (var row in designsRows)
            {
                if (row.Bought && !boughtHeader) { boughtHeader = true; GUILayout.Label("Bought stock", designsHeading); }
                if (row.Bought) DrawBoughtRow(row); else DrawDesignRow(row, usesFunds);
            }
            GUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(ToolingClient.LatestStatus)) GUILayout.Label(ToolingClient.LatestStatus, designsText);
        }

        private static string DesignDisplayName(ToolingDesign design)
        {
            if (!string.IsNullOrWhiteSpace(design.Name)) return design.Name;
            return "Design " + StockUi.ShortFingerprint(design.Fingerprint);
        }

        private static string DesignDetail(ToolingDesign design)
        {
            if (design.Fingerprint != null && designsDetail.TryGetValue(design.Fingerprint, out var cached)) return cached;
            var parts = design.Manifest?.Parts ?? Array.Empty<ToolingPart>();
            var top = parts.GroupBy(p => p.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(3).Select(g => g.Count() + " × " + g.Key);
            var text = parts.Length + " parts: " + string.Join(", ", top.ToArray());
            if (design.Fingerprint != null) designsDetail[design.Fingerprint] = text;
            return text;
        }

        /// <summary>Rebuilds the filtered, sorted rows on the Layout event when the search or sort changed, an edit made them dirty, or half a second passed.</summary>
        private static void RebuildDesignRows()
        {
            if (!StockUi.CanRefresh) return;
            var changed = designsDirty || designsBuiltSearch != designsSearch || designsBuiltSort != designsSortStock;
            if (!changed && Time.realtimeSinceStartup < designsNextRebuild) return;
            designsNextRebuild = Time.realtimeSinceStartup + 0.5f;
            designsDirty = false; designsBuiltSearch = designsSearch; designsBuiltSort = designsSortStock;
            StockUi.Refresh(true);
            designsRates = ToolingClient.StockRates();
            designsRows.Clear();
            var search = designsSearch ?? string.Empty;
            var designs = StockUi.Designs() ?? new List<ToolingDesign>();
            var rows = new List<DesignRow>();
            designsTotal = 0;
            foreach (var design in designs)
            {
                if (design == null || string.IsNullOrEmpty(design.Fingerprint)) continue;
                designsTotal++;
                var name = DesignDisplayName(design);
                if (name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var info = StockUi.Blueprint(design.Fingerprint);
                var row = new DesignRow
                {
                    Fingerprint = design.Fingerprint, Name = name, Detail = DesignDetail(design), Blueprint = info, Facility = info?.Editor,
                    Available = StockUi.Available(design.Fingerprint), Offered = StockUi.Offered(design.Fingerprint), Held = StockUi.Held(design.Fingerprint)
                };
                designsQty.TryGetValue(row.Fingerprint, out var qty);
                row.Units = StockUi.ParseUnits(qty, StockDefaults.MaxBuildUnits);
                row.Quote = StockUi.Quote(row.Fingerprint, row.Units);
                rows.Add(row);
            }
            designsShown = rows.Count;
            var sorted = designsSortStock
                ? rows.OrderByDescending(r => r.Available).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Fingerprint, StringComparer.Ordinal)
                : rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Fingerprint, StringComparer.Ordinal);
            designsRows.AddRange(sorted);

            // Bought stock: lots whose design this agency has not tooled.
            var tooled = new HashSet<string>(designs.Where(d => d != null && d.Fingerprint != null).Select(d => d.Fingerprint));
            var bought = new List<DesignRow>();
            designsBoughtTotal = 0;
            IReadOnlyList<DesignStockLot> lots = null;
            try { lots = ToolingClient.GetStockSnapshot(); } catch (Exception) { }
            foreach (var fp in StockUi.StockFingerprints().Where(f => !tooled.Contains(f)).ToArray())
            {
                designsBoughtTotal++;
                var received = StockUi.BoughtDesign(fp);
                var name = !string.IsNullOrWhiteSpace(received?.Name) ? received.Name : "Design " + StockUi.ShortFingerprint(fp);
                if (name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var source = lots?.Where(l => l != null && l.Fingerprint == fp && l.SourceAgencyId != Guid.Empty).Select(l => l.SourceAgencyId).FirstOrDefault() ?? Guid.Empty;
                bought.Add(new DesignRow
                {
                    Fingerprint = fp, Name = name, Bought = true, Available = StockUi.Available(fp), Offered = StockUi.Offered(fp),
                    SourceAgency = source, BoughtDesign = received, Facility = received?.Editor
                });
            }
            designsBoughtShown = bought.Count;
            designsRows.AddRange(designsSortStock
                ? bought.OrderByDescending(r => r.Available).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Fingerprint, StringComparer.Ordinal)
                : bought.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Fingerprint, StringComparer.Ordinal));
        }

        private static void DrawStockLine(DesignRow row)
        {
            GUILayout.Label("Stock: " + row.Available + " available" + (row.Offered > 0 ? " · " + row.Offered + " offered" : ""), designsText);
        }

        private static void DrawDesignRow(DesignRow row, bool usesFunds)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(row.Name, designsHeading);
            GUILayout.Label(row.Detail, designsText);
            DrawStockLine(row);

            // Quantity and live quote.
            designsQty.TryGetValue(row.Fingerprint, out var qty);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Quantity", GUILayout.Width(62));
            var edited = GUILayout.TextField(qty ?? "1", 4, GUILayout.Width(56));
            StockUi.Digits(edited, out edited);
            if (edited != (qty ?? "1")) { designsQty[row.Fingerprint] = edited; designsDirty = true; }
            GUILayout.EndHorizontal();

            var quote = row.Quote;
            var canBuild = quote != null && quote.Success;
            string blocked = null;
            if (quote == null) blocked = "Waiting for pricing…";
            else if (!quote.Success) blocked = quote.Reason;
            else
            {
                GUILayout.Label(usesFunds
                        ? quote.Units + " × " + quote.PrepaidPerUnit.ToString("N1", CultureInfo.CurrentCulture) + " each = " + Funds(quote.Total) + " (" + (quote.Discount * 100).ToString("0.#", CultureInfo.CurrentCulture) + "% volume discount; a tooled launch is " + quote.TooledLaunchEach.ToString("N1", CultureInfo.CurrentCulture) + " each)"
                        : quote.Units + " units, no funds charged in this game mode.", designsText);
                if (usesFunds && designsRates.MaxDiscount > 0 && quote.Units < designsRates.FullDiscountUnits)
                    GUILayout.Label("Build " + designsRates.FullDiscountUnits + "+ for " + (designsRates.MaxDiscount * 100).ToString("0.#", CultureInfo.CurrentCulture) + "% off", designsText);
                if (usesFunds && ToolingClient.TryConfirmedFunds(out var funds) && funds < quote.Total) { canBuild = false; blocked = "Not enough funds."; }
                if (row.Held + quote.Units > StockDefaults.MaxHeldUnits) { canBuild = false; blocked = "Stock limit is " + StockDefaults.MaxHeldUnits + " units per design."; }
            }
            var inFlight = designsPendingFingerprint == row.Fingerprint;
            if (inFlight && (Time.realtimeSinceStartup > designsPendingDeadline || row.Held != designsPendingHeld)) { designsPendingFingerprint = null; inFlight = false; }
            if (inFlight) { canBuild = false; blocked = "Building…"; }
            if (designsBuildConfirm == row.Fingerprint && (quote == null || !quote.Success || quote.Units != designsConfirmUnits || Math.Abs(quote.Total - designsConfirmTotal) > 1e-6)) designsBuildConfirm = null;

            var enabled = GUI.enabled;
            GUI.enabled = enabled && canBuild;
            if (designsBuildConfirm == row.Fingerprint)
            {
                GUILayout.Label(usesFunds ? "Spend " + Funds(designsConfirmTotal) + " to build " + designsConfirmUnits + "? Stock is paid now and cannot be refunded." : "Build " + designsConfirmUnits + " units at no charge?", designsText);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Confirm build", designsButton)) ConfirmBuild(row, quote, usesFunds);
                GUI.enabled = enabled;
                if (GUILayout.Button("Cancel", designsButton)) designsBuildConfirm = null;
                GUILayout.EndHorizontal();
            }
            else
            {
                if (GUILayout.Button("Build…", designsButton)) { designsBuildConfirm = row.Fingerprint; designsConfirmUnits = quote.Units; designsConfirmTotal = quote.Total; }
            }
            GUI.enabled = enabled;
            if (!string.IsNullOrEmpty(blocked) && designsBuildConfirm != row.Fingerprint) GUILayout.Label(blocked, designsText);

            DrawLoadControls(row);
            DrawSellControls(row);
            if (designsStatusFingerprint == row.Fingerprint && !string.IsNullOrEmpty(designsStatus)) GUILayout.Label(designsStatus, designsText);
            GUILayout.EndVertical();
        }

        private static void ConfirmBuild(DesignRow row, StockQuote quote, bool usesFunds)
        {
            designsBuildConfirm = null;
            try
            {
                ToolingClient.BuildStock(row.Fingerprint, quote.Units, usesFunds ? quote.Total : 0);
                designsPendingFingerprint = row.Fingerprint; designsPendingHeld = row.Held; designsPendingDeadline = Time.realtimeSinceStartup + 5f;
                designsStatusFingerprint = row.Fingerprint; designsStatus = null;
            }
            catch (Exception e) { designsStatusFingerprint = row.Fingerprint; designsStatus = "Could not build: " + e.Message; }
        }

        private static void DrawLoadControls(DesignRow row)
        {
            var inEditor = HighLogic.LoadedSceneIsEditor;
            var canLoad = row.Blueprint != null && (inEditor || HighLogic.LoadedScene == GameScenes.SPACECENTER);
            var enabled = GUI.enabled;
            if (designsLoadConfirm == row.Fingerprint)
            {
                GUILayout.Label("Replace current editor craft? Unsaved changes are lost.", designsText);
                GUILayout.BeginHorizontal();
                GUI.enabled = enabled && canLoad;
                if (GUILayout.Button("Replace", designsButton)) RunLoad(row, true);
                GUI.enabled = enabled;
                if (GUILayout.Button("Cancel", designsButton)) designsLoadConfirm = null;
                GUILayout.EndHorizontal();
                return;
            }
            GUILayout.BeginHorizontal();
            GUI.enabled = enabled && canLoad;
            if (GUILayout.Button("Load" + (row.Facility != null ? " (" + row.Facility + ")" : ""), designsButton)) RunLoad(row, false);
            GUI.enabled = enabled;
            GUILayout.EndHorizontal();
            if (row.Blueprint == null) GUILayout.Label("Open this craft in the editor and press Save craft to tooling (free).", designsText);
            else if (!canLoad) GUILayout.Label("Return to the Space Center to load a design.", designsText);
        }

        private static void RunLoad(DesignRow row, bool confirmed)
        {
            designsStatusFingerprint = row.Fingerprint;
            try
            {
                var state = ToolingClient.LoadTooledDesign(row.Fingerprint, confirmed);
                designsLoadConfirm = state == DesignLoadState.NeedsConfirm ? row.Fingerprint : null;
                designsStatus = state == DesignLoadState.NeedsConfirm ? null : string.IsNullOrEmpty(ToolingClient.LoadStatus) ? state.ToString() : ToolingClient.LoadStatus;
            }
            catch (Exception e) { designsLoadConfirm = null; designsStatus = "Could not load: " + e.Message; }
        }

        private static void DrawSellControls(DesignRow row)
        {
            if (!TradeClient.Enabled || row.Available <= 0) return;
            if (!AgencySystem.Singleton.AmIOwnerOfMine()) { GUILayout.Label("Your agency owner sells stock.", designsText); return; }
            if (GUILayout.Button("Sell…", designsButton)) StartSellStock(row.Fingerprint);
            if (StockUi.LotSlotsFull) GUILayout.Label("Lot slots are full: turn off Use stock, or sell or launch a smaller lot.", designsText);
        }

        private static void DrawBoughtRow(DesignRow row)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(row.Name, designsHeading);
            GUILayout.Label("Stock: " + row.Available + " available" + (row.Offered > 0 ? " · " + row.Offered + " offered" : "") + (row.SourceAgency != Guid.Empty ? " · bought from " + TradeAgencyName(row.SourceAgency) : ""), designsText);
            var received = row.BoughtDesign;
            if (received != null && !string.IsNullOrEmpty(received.LocalPath))
            {
                var confirming = designsBoughtLoadConfirm == received.Id;
                if (confirming) GUILayout.Label("Loading replaces the current editor craft. Save any changes you want to keep first.", designsText);
                if (GUILayout.Button(confirming ? "Replace and load design" : "Load in editor", designsButton))
                {
                    if (confirming)
                    {
                        try { TradeClient.LoadDesign(received.Id, true); } catch (Exception e) { designsStatusFingerprint = row.Fingerprint; designsStatus = "Could not load: " + e.Message; }
                        designsBoughtLoadConfirm = Guid.Empty;
                    }
                    else designsBoughtLoadConfirm = received.Id;
                }
                if (confirming && GUILayout.Button("Cancel", designsButton)) designsBoughtLoadConfirm = Guid.Empty;
                GUILayout.Label("Turn on Use stock (or research the parts) before loading: locked parts are removed on load.", designsText);
            }
            else GUILayout.Label(received != null && !string.IsNullOrEmpty(received.DeliveryStatus) ? received.DeliveryStatus : "Craft file missing from your library.", designsText);
            DrawSellControls(row);
            if (designsStatusFingerprint == row.Fingerprint && !string.IsNullOrEmpty(designsStatus)) GUILayout.Label(designsStatus, designsText);
            GUILayout.EndVertical();
        }

        /// <summary>Opens the Trade tab's New offer page in Sell stock mode with this design preselected. The player still chooses the craft file.</summary>
        private static void StartSellStock(string fingerprint)
        {
            if (tradeAgency != AgencySystem.Singleton.MyAgencyId || tradeSave != HighLogic.SaveFolder)
            {
                // The Trade tab has not been opened for this agency and save yet. Do its reset now so it doesn't wipe the preselection later.
                tradeAgency = AgencySystem.Singleton.MyAgencyId;
                tradeSave = HighLogic.SaveFolder;
                tradeBuyer = tradeVessel = tradeConfirm = tradeLoadConfirm = Guid.Empty;
                tradeBlueprintPath = tradeBlueprintLabel = null;
                tradeInfoManifest = null; tradeInfoKey = tradeInfoError = null;
                if (tradeBrowser != null) tradeBrowser.Dismiss();
                tradeBrowser = null;
            }
            _tab = 6;
            tradeTab = 1;
            tradeDraft = null; tradeDraftManifest = null; tradeError = null;
            tradeDesignMode = (int)TradeDesignMode.Stock;
            tradeDesignSource = 2;
            tradeStockFingerprint = fingerprint;
            tradeStockUnits = "1";
        }
    }
}
