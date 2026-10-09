using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.ShareFunds;
using LmpClient.Systems.ShareScience;

namespace LmpClient.Systems.Agency
{
    public static partial class ToolingClient
    {
        private static readonly ConcurrentQueue<EconomySnapshot> snapshots = new ConcurrentQueue<EconomySnapshot>();
        private static readonly ConcurrentQueue<EconomyResult> results = new ConcurrentQueue<EconomyResult>();
        private static readonly object stateLock = new object();
        private static EconomySnapshot snapshot;
        private static long revision = -1, nextSequence;
        private static Guid economySession;
        private static readonly Dictionary<uint, Tuple<Guid,string>> evaParents = new Dictionary<uint, Tuple<Guid,string>>();
        private static DateTime nextQuote;
        private static string editorQuoteHash;
        private static PendingLaunch pending;
        private static bool resumeLaunch, resumeRevert;
        private static Guid pendingRevert;
        private static DateTime settlementDeadline;
        private static EditorFacility revertFacility;
        private static bool revertToLaunch;
        private static readonly HashSet<Guid> settling = new HashSet<Guid>();
        internal static void MarkRecovering(Guid id) { lock (stateLock) { settling.Add(id); if (settlementDeadline == default(DateTime)) settlementDeadline = DateTime.UtcNow.AddSeconds(45); } }
        internal static bool IsRecovering(Guid id) { lock (stateLock) return Enabled && settling.Contains(id); }
        private static readonly Dictionary<Guid, LaunchBinding> bindings = new Dictionary<Guid, LaunchBinding>();
        private const string LaunchLock = "LMP_ToolingLaunch";
        internal static bool ApplyingBalance;
        public static bool Enabled => MainSystem.NetworkState >= ClientState.Handshaking && SettingsSystem.ServerSettings.AgencyTooling;
        public static bool BalanceAuthorityEnabled => MainSystem.NetworkState >= ClientState.Handshaking && (SettingsSystem.ServerSettings.AgencyTooling || SettingsSystem.ServerSettings.AgencyTrade);
        public static bool BalanceReady { get { lock (stateLock) return BalanceAuthorityEnabled && snapshot != null && snapshot.Ready && snapshot.AgencyId == AgencySystem.Singleton.MyAgencyId; } }
        public static bool Ready { get { lock (stateLock) return Enabled && snapshot != null && snapshot.Ready && snapshot.AgencyId == AgencySystem.Singleton.MyAgencyId; } }
        public static string LatestStatus { get; private set; }
        public static ToolingQuote EditorQuote { get; private set; }
        /// <summary>The free-launch voucher the editor quote already accounts for, or null.</summary>
        public static TradeEntitlement EditorVoucher { get; private set; }
        /// <summary>The launch charge the server quoted in the most recent confirmed PrepareLaunch result, and a counter that increases with each one.</summary>
        internal static double LastLaunchCharge { get; private set; }
        internal static long LaunchChargeSerial { get; private set; }
        /// <summary>The server-confirmed agency balance, false while the economy snapshot is not ready for this agency.</summary>
        internal static bool TryConfirmedFunds(out double funds)
        {
            lock (stateLock)
            {
                funds = 0;
                if (!BalanceReady) return false;
                funds = snapshot.Funds; return true;
            }
        }
        /// <summary>Cancels a launch that has not started loading the flight scene, through the same CancelLaunch command the scene-change path uses.</summary>
        internal static bool CancelPendingLaunchIfIdle()
        {
            if (pending == null || pending.Started) return false;
            CancelLaunch(); return true;
        }
        private sealed class PendingLaunch
        {
            internal Guid Request, Launch, Token, Voucher;
            /// <summary>The stock lot this launch reserves, and its fingerprint, captured at BeginLaunch.</summary>
            internal Guid StockLot;
            internal string StockFingerprint;
            /// <summary>Available units of that design when the launch began, for the "Using 1 of N stock" line.</summary>
            internal int StockHeld;
            internal string Path, FileHash, Flag, Site, ManifestHash, Crew;
            internal ToolingManifest Manifest;
            internal VesselCrewManifest CrewManifest;
            internal bool Started;
            internal GameScenes Scene;
            internal Dictionary<uint,int> CraftIndices;
            internal DateTime Deadline;
        }
        private sealed class LaunchBinding { internal Guid Launch, Token; internal Dictionary<uint,int> Indices; }
        internal static void Receive(EconomySnapshot value) { if (value != null) snapshots.Enqueue(value); }
        internal static void Receive(EconomyResult value) { if (value != null) results.Enqueue(value); }
        public static void RequestQuoteRefresh() { nextQuote = DateTime.MinValue; }
        internal static Guid Send(EconomyCommand command)
        {
            if (command.RequestId == Guid.Empty) command.RequestId = Guid.NewGuid();
            lock (stateLock)
            {
                if (economySession == Guid.Empty)
                {
                    LatestStatus = "Waiting for economy session.";
                    Diagnostics.PlaytestDiagnostics.Write("client.economy.command-refused", () => $"operation={command.Operation} reason=no-session");
                    return Guid.Empty;
                }
                command.SessionId = economySession; command.Sequence = ++nextSequence;
                var data = global::LmpClient.Network.NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyEconomyCommandMsgData>();
                data.Command = command;
                // Queue under the sequence lock; the general sender schedules tasks that could reorder commands.
                global::LmpClient.Network.NetworkSender.QueueOutgoingMessage(global::LmpClient.Network.NetworkMain.CliMsgFactory.CreateNew<LmpCommon.Message.Client.AgencyCliMsg>(data));
                Diagnostics.PlaytestDiagnostics.Write("client.economy.command", () => $"operation={command.Operation} request={command.RequestId} session={command.SessionId} sequence={command.Sequence} fundsDelta={command.FundsDelta} scienceDelta={command.ScienceDelta}");
                return command.RequestId;
            }
        }
        public static Guid PurchaseTooling()
        {
            try
            {
                var manifest = CurrentManifest();
                if (!Ready || EditorQuote == null || !EditorQuote.Success || editorQuoteHash != ToolingPolicy.ManifestHash(manifest))
                    throw new InvalidOperationException("Craft changed or tooling is still syncing. Review the updated quote.");
                LatestStatus = "Purchasing tooling...";
                // The craft blueprint makes the design loadable later; a failed capture still buys the tooling, without it.
                byte[] bytes = null; string editor = null, name = null;
                try { if (!CaptureEditorBlueprint(out bytes, out editor, out name)) bytes = null; }
                catch (Exception e) { bytes = null; Diagnostics.PlaytestDiagnostics.Write("client.tooling.blueprint-capture", () => "failed=" + e.Message); }
                if (name == null) { try { name = EditorLogic.fetch?.ship?.shipName; } catch (Exception) { name = null; } }
                return Send(new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest), DesignName = name,
                    BlueprintData = bytes ?? Array.Empty<byte>(), BlueprintEditor = bytes == null ? null : editor });
            }
            catch (Exception e) { LatestStatus = e.Message; return Guid.Empty; }
        }
        private static ToolingManifest CurrentManifest() => ToolingManifestBuilder.Build(EditorLogic.fetch?.ship, ShipConstruction.ShipManifest);
        internal static ToolingRates Rates()
        {
            var settings = SettingsSystem.ServerSettings;
            return new ToolingRates(settings.ToolingCostMultiplier, settings.TooledLaunchMultiplier, settings.UntooledLaunchMultiplier, settings.ToolingCombineMultiplier);
        }
        /// <summary>The agency's own price for a craft, ignoring any free-launch voucher.</summary>
        internal static ToolingQuote StandardQuote(ToolingManifest manifest)
        {
            StandardQuoteCalls++;
            lock (stateLock)
                return ToolingPolicy.Quote(manifest, snapshot?.Designs ?? Array.Empty<ToolingDesign>(), Rates());
        }
        private static ToolingQuote Quote(ToolingManifest manifest) => Quote(manifest, out _, out _);
        /// <summary>
        /// The price the server will charge. A matching free-launch voucher wins (it is a one-off gift); otherwise a cheaper held stock lot applies.
        /// Either turns the launch into inventory and top-up only.
        /// </summary>
        private static ToolingQuote Quote(ToolingManifest manifest, out TradeEntitlement voucher, out DesignStockLot stockLot)
        {
            var standard = StandardQuote(manifest);
            stockLot = null;
            voucher = TradeClient.SelectVoucher(standard);
            if (voucher != null) return WithLaunchCost(standard, TradePolicy.VoucherLaunchCharge(standard, voucher.PrepaidFunds, voucher.LaunchMultiplier));
            stockLot = SelectStock(standard);
            return stockLot == null ? standard : WithLaunchCost(standard, StockPolicy.LaunchCharge(standard, stockLot));
        }
        private static ToolingQuote WithLaunchCost(ToolingQuote standard, double launchCost) => new ToolingQuote
        {
            Success = standard.Success, Reason = standard.Reason, Fingerprint = standard.Fingerprint, ToolingCost = standard.ToolingCost,
            ScienceCost = standard.ScienceCost, NonScienceCost = standard.NonScienceCost, CargoCost = standard.CargoCost, AlreadyTooled = standard.AlreadyTooled, CoverSearchExhausted = standard.CoverSearchExhausted, Matches = standard.Matches,
            LaunchCost = launchCost
        };
        internal static bool HasTooling(string fingerprint)
        {
            lock (stateLock) return snapshot?.Designs != null && snapshot.Designs.Any(d => d.Fingerprint == fingerprint);
        }
        /// <summary>True while a launch is reserved or starting; its voucher, if any, is the only one that counts for research.</summary>
        internal static bool LaunchPending => pending != null;
        internal static Guid PendingVoucher => pending?.Voucher ?? Guid.Empty;
        /// <summary>The stock lot the launch in progress reserved (compared by id even after the row is pruned), and its fingerprint.</summary>
        internal static Guid PendingStockLot => pending?.StockLot ?? Guid.Empty;
        internal static string PendingStockFingerprint => pending?.StockFingerprint;

        // Plan 40 design stock: the pure client state. The KSP-bound half (capture, hashing, craft files, editor load) is ToolingClient.Blueprints.cs.
        private static bool useStock = true;
        private static Guid buildRequest;
        private static Guid loadRequest;
        private static string loadFingerprint;
        private static DateTime loadDeadline;
        private static DateTime nextBlueprintCheck;
        private static string blueprintCheckKey;
        private static readonly Dictionary<string, byte[]> blueprintCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private const int FetchTimeoutSeconds = 15, BlueprintCheckSeconds = 2;
        /// <summary>Test hook: counts StandardQuote calls, so a test can prove a check stayed on its cheap path.</summary>
        internal static int StandardQuoteCalls;
        private static bool UsesFunds => SettingsSystem.ServerSettings.GameMode == GameMode.Career;
        /// <summary>The "Use stock" choice. On by default; a matching lot is applied automatically when it is cheaper.</summary>
        public static bool UseStock
        {
            get => useStock;
            set { if (useStock == value) return; useStock = value; RequestQuoteRefresh(); }
        }
        /// <summary>The stock lot the editor quote applies, or null.</summary>
        public static DesignStockLot EditorStock { get; private set; }
        /// <summary>True when the open editor craft is tooled and its saved blueprint is missing or differs. Computed in Tick, never in OnGUI.</summary>
        public static bool EditorBlueprintNeedsSave { get; private set; }
        public static string LoadStatus { get; private set; }
        /// <summary>Where the most recent LoadTooledDesign call is, and which design it is for.</summary>
        public static DesignLoadState LoadState { get; private set; }
        public static string LoadingFingerprint => loadFingerprint;
        /// <summary>True while a BuildStock request waits for its result.</summary>
        public static bool BuildPending => buildRequest != Guid.Empty;
        /// <summary>Held units of a design, as the server counts them for the 999 cap (lots, escrow, Prepared and revertible stock launches).</summary>
        public static int StockUnits(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return 0;
            lock (stateLock)
            {
                if (snapshot?.StockHeldByFingerprint != null && snapshot.StockHeldByFingerprint.TryGetValue(fingerprint, out var held)) return Math.Max(0, held);
                return AvailableUnitsLocked(fingerprint);
            }
        }
        /// <summary>Units of a design in this agency's own stock rows: what can be launched or put on offer now.</summary>
        public static int AvailableStockUnits(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return 0;
            lock (stateLock) return AvailableUnitsLocked(fingerprint);
        }
        private static int AvailableUnitsLocked(string fingerprint) =>
            (int)Math.Min(int.MaxValue, (snapshot?.Stock ?? Array.Empty<DesignStockLot>()).Where(l => l != null && l.Units > 0 && l.Fingerprint == fingerprint).Sum(l => (long)l.Units));
        /// <summary>Units of a design in this agency's open outgoing stock offers.</summary>
        public static int OfferedUnits(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return 0;
            lock (stateLock)
            {
                if (snapshot == null) return 0;
                return (snapshot.Offers ?? Array.Empty<TradeOffer>()).Where(o => o != null && o.Status == TradeOfferStatus.Open && o.DesignMode == TradeDesignMode.Stock &&
                    o.SellerAgencyId == snapshot.AgencyId && o.DesignFingerprint == fingerprint).Sum(o => Math.Max(0, o.StockUnits));
            }
        }
        /// <summary>
        /// A lower bound of the server's lot slots: stock rows plus at least one escrow row per open outgoing stock offer. Prepared stock launches
        /// are not visible in the snapshot, so the server stays the authority (it refuses cleanly when full).
        /// </summary>
        public static int StockLotSlots()
        {
            lock (stateLock)
            {
                if (snapshot == null) return 0;
                return (snapshot.Stock ?? Array.Empty<DesignStockLot>()).Count(l => l != null) + (snapshot.Offers ?? Array.Empty<TradeOffer>()).Count(o => o != null &&
                    o.Status == TradeOfferStatus.Open && o.DesignMode == TradeDesignMode.Stock && o.SellerAgencyId == snapshot.AgencyId);
            }
        }
        public static bool StockSlotsFull => StockLotSlots() >= StockDefaults.MaxLots;
        public static IReadOnlyList<ToolingDesign> GetDesignsSnapshot()
        {
            lock (stateLock) return (snapshot?.Designs ?? Array.Empty<ToolingDesign>()).Where(d => d != null)
                .Select(d => new ToolingDesign { Fingerprint = d.Fingerprint, Manifest = d.Manifest, ToolingBasis = d.ToolingBasis, Name = d.Name }).ToArray();
        }
        public static IReadOnlyList<DesignStockLot> GetStockSnapshot()
        {
            lock (stateLock) return (snapshot?.Stock ?? Array.Empty<DesignStockLot>()).Where(l => l != null).Select(CopyLot).ToArray();
        }
        private static DesignStockLot CopyLot(DesignStockLot l) => new DesignStockLot { LotId = l.LotId, Fingerprint = l.Fingerprint, Units = l.Units, PrepaidPerUnit = l.PrepaidPerUnit,
            LaunchMultiplier = l.LaunchMultiplier, BuilderAgencyId = l.BuilderAgencyId, SourceAgencyId = l.SourceAgencyId, FundsBuilt = l.FundsBuilt, CreatedUtcTicks = l.CreatedUtcTicks };
        /// <summary>The server's stock discount settings, normalized.</summary>
        public static StockRates StockRates()
        {
            var settings = SettingsSystem.ServerSettings;
            return LmpCommon.Agency.StockRates.Normalize(settings.StockMaxDiscount, settings.StockFullDiscountUnits);
        }
        /// <summary>The price of building units of a tooled design, from the stored manifest the server also prices from.</summary>
        public static StockQuote QuoteBuild(string fingerprint, int units)
        {
            if (!Ready) return new StockQuote { Success = false, Reason = "Syncing tooled designs...", Fingerprint = fingerprint, Units = units };
            ToolingDesign design;
            lock (stateLock) design = (snapshot?.Designs ?? Array.Empty<ToolingDesign>()).FirstOrDefault(d => d != null && d.Fingerprint == fingerprint);
            return StockPolicy.Quote(design, units, Rates(), StockRates());
        }
        /// <summary>
        /// Builds stock of a tooled design. <paramref name="expectedCharge"/> is the total the player confirmed; it must still match the live quote.
        /// The command carries the live total in Career and 0 otherwise, which is what the server compares against.
        /// </summary>
        public static Guid BuildStock(string fingerprint, int units, double expectedCharge)
        {
            try
            {
                if (!Ready) throw new InvalidOperationException("Waiting for agency economy.");
                if (buildRequest != Guid.Empty) throw new InvalidOperationException("A stock build is already in progress.");
                var quote = QuoteBuild(fingerprint, units);
                if (!quote.Success) throw new InvalidOperationException(quote.Reason);
                var charge = UsesFunds ? quote.Total : 0;
                if (!SameCharge(expectedCharge, quote.Total) && !(!UsesFunds && expectedCharge == 0)) throw new InvalidOperationException("Price changed; review the new quote.");
                if ((long)StockUnits(fingerprint) + units > StockDefaults.MaxHeldUnits) throw new InvalidOperationException("Stock limit is " + StockDefaults.MaxHeldUnits + " units per design.");
                var request = Send(new EconomyCommand { Operation = EconomyOperation.BuildStock, StockFingerprint = fingerprint, StockUnits = units, ExpectedCharge = charge });
                if (request == Guid.Empty) return Guid.Empty;
                buildRequest = request;
                LatestStatus = "Building " + units + " stock...";
                return request;
            }
            catch (Exception e) { LatestStatus = e.Message; return Guid.Empty; }
        }
        private static bool SameCharge(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(b));
        /// <summary>
        /// The lot a launch of this quoted design would use: Ready, tooling on and Use stock on, then StockPolicy.SelectLot. When the agency's lot
        /// slots are full only a 1-unit lot is chosen, because emptying a row frees the slot the Prepared launch takes; otherwise no lot applies.
        /// </summary>
        internal static DesignStockLot SelectStock(ToolingQuote quote)
        {
            if (!Ready || !useStock || quote == null || !quote.Success) return null;
            DesignStockLot[] lots;
            lock (stateLock) lots = (snapshot?.Stock ?? Array.Empty<DesignStockLot>()).Where(l => l != null).ToArray();
            if (StockLotSlots() >= StockDefaults.MaxLots) lots = lots.Where(l => l.Units == 1).ToArray();
            return StockPolicy.SelectLot(lots, quote, UsesFunds);
        }
        /// <summary>
        /// True when held stock (or the pending stock launch) unlocks research for this exact part list. Needs only tooling and Use stock, not trade.
        /// A pending launch counts by its reserved LotId even after the snapshot pruned the row; otherwise a held unit is required before any pricing.
        /// </summary>
        internal static bool HasStockResearch(ToolingManifest manifest)
        {
            if (!Ready || !useStock || manifest == null) return false;
            string fingerprint;
            try { fingerprint = ToolingPolicy.Fingerprint(manifest); }
            catch (ArgumentException) { return false; }
            var launch = pending;
            if (launch != null) return launch.StockLot != Guid.Empty && launch.StockFingerprint == fingerprint;
            lock (stateLock)
                if (!(snapshot?.Stock ?? Array.Empty<DesignStockLot>()).Any(l => l != null && l.Units > 0 && l.Fingerprint == fingerprint)) return false;
            return SelectStock(StandardQuote(manifest)) != null;
        }
        /// <summary>Saved-blueprint metadata for a tooled design, or null when none is saved.</summary>
        public static ToolingBlueprintInfo BlueprintInfo(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return null;
            lock (stateLock)
            {
                var info = (snapshot?.DesignBlueprints ?? Array.Empty<ToolingBlueprintInfo>()).FirstOrDefault(b => b != null && b.Fingerprint == fingerprint);
                return info == null ? null : new ToolingBlueprintInfo { Fingerprint = info.Fingerprint, Name = info.Name, Editor = info.Editor, Hash = info.Hash, Bytes = info.Bytes };
            }
        }
        /// <summary>Sends a Tool command carrying the open editor craft's blueprint. Only for an already tooled craft, so it never charges.</summary>
        public static Guid SaveBlueprintToTooling()
        {
            try
            {
                var manifest = CurrentManifest();
                var hash = ToolingPolicy.ManifestHash(manifest);
                if (!Ready || EditorQuote == null || !EditorQuote.Success || editorQuoteHash != hash) throw new InvalidOperationException("Craft changed or tooling is still syncing. Review the updated quote.");
                if (!EditorQuote.AlreadyTooled) throw new InvalidOperationException("Tool this design first; saving the craft is free once it is tooled.");
                if (!CaptureEditorBlueprint(out var bytes, out var editor, out var name)) throw new InvalidOperationException("The craft could not be captured; check that its parts match the tooled design.");
                LatestStatus = "Saving craft to tooling...";
                var request = Send(new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = hash, DesignName = name, BlueprintData = bytes, BlueprintEditor = editor });
                if (request != Guid.Empty) blueprintCheckKey = null;
                return request;
            }
            catch (Exception e) { LatestStatus = e.Message; return Guid.Empty; }
        }
        /// <summary>
        /// Loads a tooled design's saved craft into the editor (plan 40 section 6.3): preconditions, replace confirm, fetch (cached per hash),
        /// validation, missing-part check, library file, then the stock editor load. Returns where the load is; LoadStatus explains it.
        /// </summary>
        public static DesignLoadState LoadTooledDesign(string fingerprint, bool confirmedReplace)
        {
            if (LoadState == DesignLoadState.Fetching && loadRequest != Guid.Empty)
            {
                if (loadFingerprint == fingerprint) return DesignLoadState.Fetching;
                LoadStatus = "Another design is still loading."; return DesignLoadState.Refused;
            }
            loadFingerprint = fingerprint;
            if (!Ready) return SetLoad(DesignLoadState.Refused, "Waiting for agency economy.");
            var info = BlueprintInfo(fingerprint);
            if (info == null) return SetLoad(DesignLoadState.Refused, "Open this craft in the editor and press Save craft to tooling (free).");
            if (!LoadSceneAllowed()) return SetLoad(DesignLoadState.Refused, "Return to the Space Center to load a design.");
            if (!confirmedReplace && EditorHasCraft()) return SetLoad(DesignLoadState.NeedsConfirm, "Replace current editor craft? Unsaved changes are lost.");
            byte[] cached;
            lock (stateLock) blueprintCache.TryGetValue(CacheKey(info), out cached);
            if (cached != null) return ContinueLoad(info, cached);
            var request = Send(new EconomyCommand { Operation = EconomyOperation.FetchBlueprint, StockFingerprint = fingerprint });
            if (request == Guid.Empty) return SetLoad(DesignLoadState.Failed, "Waiting for economy session.");
            SetLoad(DesignLoadState.Fetching, "Fetching saved craft...");
            loadRequest = request; loadDeadline = DateTime.UtcNow.AddSeconds(FetchTimeoutSeconds);
            return DesignLoadState.Fetching;
        }
        private static DesignLoadState SetLoad(DesignLoadState state, string status)
        {
            loadRequest = Guid.Empty; loadDeadline = default(DateTime);
            LoadState = state; LoadStatus = status;
            return state;
        }
        private static bool LoadSceneAllowed() => HighLogic.LoadedScene == GameScenes.SPACECENTER || HighLogic.LoadedScene == GameScenes.EDITOR;
        private static bool EditorHasCraft() => HighLogic.LoadedSceneIsEditor && EditorLogic.fetch?.ship?.parts != null && EditorLogic.fetch.ship.parts.Count > 0;
        private static string CacheKey(ToolingBlueprintInfo info) => info.Fingerprint + "|" + info.Hash;
        internal static string HexHash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
        private static void HandleFetchedBlueprint(EconomyResult result)
        {
            var fingerprint = loadFingerprint;
            loadRequest = Guid.Empty;
            try
            {
                if (!result.Success) { SetLoad(DesignLoadState.Failed, result.Reason ?? "Saved craft unavailable."); return; }
                var info = BlueprintInfo(fingerprint);
                var bytes = result.BlueprintData;
                if (info == null || bytes == null || bytes.Length == 0 || bytes.Length > ToolingLimits.MaxToolingBlueprintBytes || (result.BlueprintEditor != "VAB" && result.BlueprintEditor != "SPH") ||
                    !string.Equals(HexHash(bytes), info.Hash, StringComparison.Ordinal) || !string.Equals(result.BlueprintHash, info.Hash, StringComparison.OrdinalIgnoreCase))
                { SetLoad(DesignLoadState.Failed, "Saved craft failed validation."); return; }
                info.Editor = result.BlueprintEditor;
                var missing = MissingBlueprintParts(bytes);
                if (missing != null && missing.Length > 0) { SetLoad(DesignLoadState.Refused, MissingPartsText(missing)); return; }
                if (BlueprintFingerprint(bytes) != fingerprint) { SetLoad(DesignLoadState.Failed, "Saved craft failed validation."); return; }
                lock (stateLock) blueprintCache[CacheKey(info)] = bytes;
                ContinueLoad(info, bytes);
            }
            catch (Exception e) { SetLoad(DesignLoadState.Failed, "Saved craft failed validation: " + e.Message); }
        }
        private static string MissingPartsText(string[] missing) =>
            "Missing parts: " + string.Join(", ", missing.Take(8)) + (missing.Length > 8 ? " and " + (missing.Length - 8) + " more" : string.Empty) + " (install the mods or re-save the craft).";
        private static DesignLoadState ContinueLoad(ToolingBlueprintInfo info, byte[] bytes)
        {
            try
            {
                if (!LoadSceneAllowed()) return SetLoad(DesignLoadState.Refused, "Return to the Space Center to load a design.");
                var missing = MissingBlueprintParts(bytes);
                if (missing != null && missing.Length > 0) return SetLoad(DesignLoadState.Refused, MissingPartsText(missing));
                var path = WriteTooledCraftFile(info, bytes);
                SetLoad(DesignLoadState.Loading, "Loading " + (string.IsNullOrEmpty(info.Name) ? "design" : info.Name) + "...");
                LoadTooledCraftFile(path, info.Editor);
                RequestQuoteRefresh();
                return SetLoad(DesignLoadState.Loaded, "Loaded " + (string.IsNullOrEmpty(info.Name) ? "design" : info.Name) + " (" + info.Editor + ").");
            }
            catch (Exception e) { return SetLoad(DesignLoadState.Failed, "Could not load the saved craft: " + e.Message); }
        }
        /// <summary>Recomputes EditorBlueprintNeedsSave from the 500 ms editor tick, only when the craft or its saved hash changed, or every 2 s.</summary>
        private static void RefreshBlueprintNeedsSave(ToolingQuote quote)
        {
            if (quote == null || !quote.Success || !quote.AlreadyTooled) { EditorBlueprintNeedsSave = false; blueprintCheckKey = null; return; }
            var saved = BlueprintInfo(quote.Fingerprint)?.Hash;
            var key = editorQuoteHash + "|" + saved;
            if (key == blueprintCheckKey && DateTime.UtcNow < nextBlueprintCheck) return;
            blueprintCheckKey = key; nextBlueprintCheck = DateTime.UtcNow.AddSeconds(BlueprintCheckSeconds);
            if (saved == null) { EditorBlueprintNeedsSave = true; return; }
            EditorBlueprintNeedsSave = TryEditorBlueprintHash(out var hash) && !string.Equals(hash, saved, StringComparison.OrdinalIgnoreCase);
        }
        internal static ToolingQuote DisplayQuote(ShipConstruct ship, ShipTemplate template, VesselCrewManifest crew)
        {
            if (!Ready) return null;
            if (template != null) return Quote(ToolingManifestBuilder.FromConfig(template.config, crew));
            return ship == null ? null : Quote(ToolingManifestBuilder.Build(ship, crew));
        }
        internal static bool BeginLaunch(string path, string flag, string site, VesselCrewManifest crew)
        {
            if (!Enabled) return true;
            if (resumeLaunch) { resumeLaunch = false; return true; }
            if (pending != null) return false;
            try
            {
                if (!Ready) throw new InvalidOperationException("Waiting for agency economy.");
                var manifest = ToolingManifestBuilder.FromFile(path, crew);
                Quote(manifest, out var voucher, out var stockLot);
                pending = new PendingLaunch { Voucher = voucher?.EntitlementId ?? Guid.Empty, Request = Guid.NewGuid(), Launch = Guid.NewGuid(), Path = path, Flag = flag, Site = site,
                    StockLot = stockLot?.LotId ?? Guid.Empty, StockFingerprint = stockLot?.Fingerprint, StockHeld = stockLot == null ? 0 : AvailableStockUnits(stockLot.Fingerprint),
                    FileHash = HashFile(path), Scene = HighLogic.LoadedScene, CraftIndices = CraftIndices(path), Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest), CrewManifest = crew,
                    Crew = CrewKey(crew), Deadline = DateTime.UtcNow.AddSeconds(45) };
                Send(new EconomyCommand { RequestId = pending.Request, Operation = EconomyOperation.PrepareLaunch, LaunchId = pending.Launch, VoucherId = pending.Voucher,
                    StockLotId = pending.StockLot, Manifest = manifest, ManifestHash = pending.ManifestHash });
                InputLockManager.SetControlLock(ControlTypes.EDITOR_LAUNCH, LaunchLock);
                LatestStatus = "Reserving launch funds...";
            }
            catch (Exception e) { LatestStatus = e.Message; pending = null; }
            return false;
        }
        private static Dictionary<uint,int> CraftIndices(string path)
        {
            var result = new Dictionary<uint,int>();
            var nodes = ConfigNode.Load(path).GetNodes("PART");
            for (var i = 0; i < nodes.Length; i++)
            {
                var key = nodes[i].GetValue("part");
                if (string.IsNullOrEmpty(key) || !uint.TryParse(key.Substring(key.LastIndexOf('_') + 1), out var id) || result.ContainsKey(id))
                    throw new InvalidOperationException("Craft part identifiers are invalid.");
                result.Add(id, i);
            }
            return result;
        }
        internal static void SceneChanged(GameScenes scene)
        {
            if (pending != null && !pending.Started) CancelLaunch();
            ApplyCachedBalance();
            RequestQuoteRefresh();
        }
        internal static void ApplyCachedBalance()
        {
            EconomySnapshot state; lock (stateLock) state = snapshot;
            if (!BalanceAuthorityEnabled || state == null || !state.Ready) return;
            ApplyingBalance = true;
            try
            {
                if (Funding.Instance) ShareFundsSystem.Singleton.SetFundsWithoutTriggeringEvent(state.Funds);
                if (ResearchAndDevelopment.Instance) ShareScienceSystem.Singleton.SetScienceWithoutTriggeringEvent((float)state.Science);
            }
            finally { ApplyingBalance = false; }
        }
        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }
        private static string CrewKey(VesselCrewManifest crew) => crew == null ? string.Empty : string.Join("|", crew.GetAllCrew(false).Select(c => c.name).OrderBy(n => n, StringComparer.Ordinal));
        internal static void BindLaunch(Vessel vessel, ShipConstruct ship)
        {
            if (!Enabled || pending == null || !pending.Started || !vessel) return;
            var indices = new Dictionary<uint,int>();
            foreach (var part in ship.parts) indices[part.flightID] = pending.CraftIndices.TryGetValue(part.craftID, out var index) ? index : -1;
            lock (stateLock) bindings[vessel.id] = new LaunchBinding { Launch = pending.Launch, Token = pending.Token, Indices = indices };
        }
        internal static void FillLaunchTail(ProtoVessel vessel, LmpCommon.Message.Data.Vessel.VesselProtoMsgData data)
        {
            data.EconomySplitOperationId = Guid.Empty; data.EconomySplitParentData = Array.Empty<byte>();
            data.EconomyParentVesselId = Guid.Empty; data.EconomyEvaCrew = null;
            data.EconomyLaunchId = Guid.Empty; data.EconomyLaunchToken = Guid.Empty; data.EconomyManifestIndices = Array.Empty<int>();
            if (!Enabled) return;
            lock (stateLock)
            {
                if (vessel.protoPartSnapshots.Count > 0 && evaParents.TryGetValue(vessel.protoPartSnapshots[0].flightID, out var eva))
                { data.EconomyParentVesselId = eva.Item1; data.EconomyEvaCrew = eva.Item2; }
                if (bindings.TryGetValue(vessel.vesselID, out var binding))
                {
                    data.EconomyLaunchId = binding.Launch; data.EconomyLaunchToken = binding.Token;
                    data.EconomyManifestIndices = vessel.protoPartSnapshots.Select(p => binding.Indices.TryGetValue(p.flightID, out var index) ? index : -1).ToArray();
                }
            }
        }
        internal static void BindEva(GameEvents.FromToAction<Part, Part> data)
        {
            if (!Enabled || !data.from || !data.to) return;
            var crew = data.to.protoModuleCrew.FirstOrDefault()?.name;
            if (string.IsNullOrEmpty(crew)) return;
            lock (stateLock) evaParents[data.to.flightID] = Tuple.Create(data.from.vessel.id, crew);
        }
        internal static bool BeforeRollout() => !Enabled;
        internal static float AffordableCost(ShipConstruct ship)
        {
            if (!Ready) return float.MaxValue;
            var quote = Quote(ToolingManifestBuilder.Build(ship, ShipConstruction.ShipManifest));
            return quote.Success && quote.LaunchCost <= float.MaxValue ? (float)quote.LaunchCost : float.MaxValue;
        }
        internal static float AffordableTemplateCost(ShipTemplate ship)
        {
            if (!Ready) return float.MaxValue;
            var quote = Quote(ToolingManifestBuilder.FromConfig(ship.config, ShipConstruction.ShipManifest));
            return quote.Success && quote.LaunchCost <= float.MaxValue ? (float)quote.LaunchCost : float.MaxValue;
        }
        internal static void SendDelta(double funds, double science)
        {
            if (!BalanceAuthorityEnabled || ApplyingBalance || funds == 0 && science == 0) return;
            Send(new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = funds, ScienceDelta = science });
        }
        internal static PaidVesselRecord PaidVessel(Guid id)
        {
            lock (stateLock) return snapshot?.Vessels?.FirstOrDefault(v => v.VesselId == id);
        }
        internal static bool BeginRevert(EditorFacility facility, bool toLaunch)
        {
            if (MainSystem.NetworkState >= ClientState.Connected && !SettingsSystem.ServerSettings.CanRevert)
            {
                resumeRevert = false;
                LatestStatus = "Reverting is disabled by the server.";
                Diagnostics.PlaytestDiagnostics.Write("client.revert.denied", () => $"toLaunch={toLaunch} reason=server-policy");
                return false;
            }
            if (!Enabled || resumeRevert) { resumeRevert = false; return true; }
            if (pendingRevert != Guid.Empty || !Ready || !FlightGlobals.ActiveVessel) return false;
            var paid = PaidVessel(FlightGlobals.ActiveVessel.id);
            var launches = paid?.Parts?.Select(p => p.LaunchId).Where(id => id != Guid.Empty).Distinct().ToArray();
            if (launches == null || launches.Length != 1) { LatestStatus = "This craft has no single eligible launch to revert."; return false; }
            revertFacility = facility; revertToLaunch = toLaunch;
            pendingRevert = Send(new EconomyCommand { Operation = toLaunch ? EconomyOperation.RevertLaunch : EconomyOperation.Revert,
                LaunchId = launches[0], VesselId = FlightGlobals.ActiveVessel.id });
            settlementDeadline = DateTime.UtcNow.AddSeconds(45);
            LatestStatus = "Checking revert eligibility..."; return false;
        }
        internal static Guid Recover(EconomyCommand command) => Send(command);
        internal static void Tick()
        {
            // The server sends the initial economy snapshot during handshake, before feature
            // settings arrive. Keep it queued until those flags can be evaluated reliably.
            if (MainSystem.NetworkState < ClientState.SettingsSynced) return;
            TickBoarding();
            if (splitting != null && DateTime.UtcNow > splitting.Deadline) { RecoveryDisconnect("Split confirmation timed out."); return; }
            while (snapshots.TryDequeue(out var state))
            {
                if (!BalanceAuthorityEnabled || state.AgencyId != AgencySystem.Singleton.MyAgencyId || state.Revision < revision)
                {
                    Diagnostics.PlaytestDiagnostics.Write("client.economy.snapshot.dropped", () =>
                        $"agency={state.AgencyId} currentAgency={AgencySystem.Singleton.MyAgencyId} revision={state.Revision} currentRevision={revision} reason={(!BalanceAuthorityEnabled ? "disabled" : state.AgencyId != AgencySystem.Singleton.MyAgencyId ? "agency-mismatch" : "stale-revision")} offers={string.Join(",", (state.Offers ?? Array.Empty<TradeOffer>()).Select(o => o.OfferId.ToString("N")))}");
                    continue;
                }
                lock (stateLock)
                {
                    if (economySession != state.SessionId) { economySession = state.SessionId; nextSequence = state.LastSequence; }
                    else nextSequence = Math.Max(nextSequence, state.LastSequence);
                    snapshot = state; revision = state.Revision;
                    TradeClient.Receive(state);
                }
                Diagnostics.PlaytestDiagnostics.Write("client.economy.snapshot", () => $"agency={state.AgencyId} ready={state.Ready} revision={state.Revision} session={state.SessionId} funds={state.Funds} science={state.Science}");
                ApplyingBalance = true;
                try
                {
                    if (Funding.Instance) ShareFundsSystem.Singleton.SetFundsWithoutTriggeringEvent(state.Funds);
                    if (ResearchAndDevelopment.Instance) ShareScienceSystem.Singleton.SetScienceWithoutTriggeringEvent((float)state.Science);
                }
                finally { ApplyingBalance = false; }
                RequestQuoteRefresh();
            }
            while (results.TryDequeue(out var result)) { TradeClient.HandleResult(result); Handle(result); }
            global::LmpClient.Systems.VesselRemoveSys.VesselRemoveMessageSender.FlushRetainedRemovals();
            TradeClient.Tick();
            if (settlementDeadline != default(DateTime) && DateTime.UtcNow > settlementDeadline)
            {
                settlementDeadline = default(DateTime);
                RecoveryDisconnect("Economy settlement timed out. Reconnect to reload authoritative state.");
                return;
            }
            if (pending != null && (!Enabled || DateTime.UtcNow > pending.Deadline || !pending.Started && HighLogic.LoadedScene != pending.Scene)) CancelLaunch();
            if (loadRequest != Guid.Empty && DateTime.UtcNow > loadDeadline) SetLoad(DesignLoadState.Failed, "The saved craft did not arrive. Try again.");
            if (!Enabled || !HighLogic.LoadedSceneIsEditor || DateTime.UtcNow < nextQuote) return;
            nextQuote = DateTime.UtcNow.AddMilliseconds(500);
            try
            {
                var manifest = Ready && EditorLogic.fetch?.ship != null ? CurrentManifest() : null;
                TradeEntitlement voucher = null; DesignStockLot stockLot = null;
                EditorQuote = manifest == null ? null : Quote(manifest, out voucher, out stockLot);
                EditorVoucher = voucher; EditorStock = stockLot == null ? null : CopyLot(stockLot);
                editorQuoteHash = manifest == null ? null : ToolingPolicy.ManifestHash(manifest);
            }
            catch (Exception e) { EditorQuote = null; EditorVoucher = null; EditorStock = null; LatestStatus = e.Message; }
            try { RefreshBlueprintNeedsSave(EditorQuote); }
            catch (Exception) { EditorBlueprintNeedsSave = false; }
            LmpClient.Harmony.AgencyCostDisplay.Refresh();
        }
        private static void Handle(EconomyResult result)
        {
            if (BalanceAuthorityEnabled && result.RecoveryRequired) { RecoveryDisconnect(result.Reason); return; }
            if (!Enabled) return;
            if (result.Operation == EconomyOperation.FetchBlueprint)
            {
                // A read-only fetch never changes the economy status line; the load has its own.
                if (loadRequest != Guid.Empty && result.RequestId == loadRequest) HandleFetchedBlueprint(result);
                return;
            }
            LatestStatus = result.Reason;
            if (buildRequest != Guid.Empty && result.RequestId == buildRequest) { buildRequest = Guid.Empty; return; }
            if (HandleBoarding(result) || HandleSplit(result)) return;
            if (result.Operation == EconomyOperation.Recover)
            {
                if (!result.Success) { RecoveryDisconnect(result.Reason); return; }
                lock (stateLock) { settling.Remove(result.VesselId); if (settling.Count == 0 && pendingRevert == Guid.Empty) settlementDeadline = default(DateTime); }
            }
            if (result.RecoveryRequired) { RecoveryDisconnect(result.Reason); return; }
            // Empty ids are never a match: a RegisterEva result carries neither a launch nor a request of this client.
            if (pending != null && result.LaunchId != Guid.Empty && result.Operation == EconomyOperation.RegisterLaunch && result.LaunchId == pending.Launch)
            {
                if (!result.Success) { RecoveryDisconnect(result.Reason); return; }
                lock (stateLock) bindings.Remove(result.VesselId);
                pending = null; InputLockManager.RemoveControlLock(LaunchLock); return;
            }
            if (pendingRevert != Guid.Empty && result.RequestId == pendingRevert)
            {
                pendingRevert = Guid.Empty;
                lock (stateLock) { if (settling.Count == 0) settlementDeadline = default(DateTime); }
                if (result.Success)
                {
                    RecoveryDisconnect("Revert completed. Reconnect to load the authoritative saved state.");
                }
                return;
            }
            if (pending == null || result.RequestId != pending.Request || result.Operation != EconomyOperation.PrepareLaunch) return;
            // A refused stock launch (for example the last unit went to another launch) falls back to normal pricing on the next quote.
            if (!result.Success) { pending = null; InputLockManager.RemoveControlLock(LaunchLock); RequestQuoteRefresh(); return; }
            pending.Token = result.LaunchToken;
            if (result.Quote != null) { LastLaunchCharge = result.Quote.LaunchCost; LaunchChargeSerial++; }
            if (pending.StockLot != Guid.Empty)
                LatestStatus = "Using 1 of " + Math.Max(1, pending.StockHeld) + " stock" + (result.Quote != null && UsesFunds ? "; you pay " + result.Quote.LaunchCost.ToString("N0") + " (inventory/extra only)." : ".");
            try
            {
                if (HighLogic.LoadedScene != pending.Scene || HashFile(pending.Path) != pending.FileHash || CrewKey(pending.CrewManifest) != pending.Crew ||
                    ToolingPolicy.ManifestHash(ToolingManifestBuilder.FromFile(pending.Path, pending.CrewManifest)) != pending.ManifestHash || result.ExpiresUtcTicks <= DateTime.UtcNow.Ticks)
                    throw new InvalidOperationException("Launch changed while awaiting confirmation.");
                pending.Started = true; pending.Deadline = new DateTime(result.ExpiresUtcTicks, DateTimeKind.Utc);
                resumeLaunch = true;
                FlightDriver.StartWithNewLaunch(pending.Path, pending.Flag, pending.Site, pending.CrewManifest);
                resumeLaunch = false;
            }
            catch (Exception e) { LatestStatus = e.Message; CancelLaunch(); }
        }
        private static void CancelLaunch()
        {
            var cancelled = pending; pending = null; resumeLaunch = false; InputLockManager.RemoveControlLock(LaunchLock);
            if (cancelled != null && Enabled) Send(new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = cancelled.Launch, LaunchToken = cancelled.Token });
            if (cancelled?.Started == true) RecoveryDisconnect("Launch registration failed. Reconnect to reload authoritative state.");
        }
        internal static void RecoveryDisconnect(string reason)
        {
            global::LmpClient.Network.NetworkConnection.Disconnect(reason ?? "Economy recovery required.");
            MainSystem.Singleton.ForceQuit = true;
        }
        internal static void Clear()
        {
            LmpClient.Harmony.AgencyCostDisplay.Clear();
            lock (stateLock) { snapshot = null; revision = -1; nextSequence = 0; economySession = Guid.Empty; bindings.Clear(); settling.Clear(); evaParents.Clear(); }
            while (snapshots.TryDequeue(out _)) { } while (results.TryDequeue(out _)) { }
            settlementDeadline = default(DateTime);
            boarding = null; InputLockManager.RemoveControlLock(BoardingLock);
            splitting = null; splitQueue.Clear(); splitBytes = 0; InputLockManager.RemoveControlLock(SplitLock);
            pending = null; pendingRevert = Guid.Empty; resumeLaunch = resumeRevert = false; EditorQuote = null; EditorVoucher = null; LatestStatus = null;
            EditorStock = null; EditorBlueprintNeedsSave = false; blueprintCheckKey = null; nextBlueprintCheck = default(DateTime); buildRequest = Guid.Empty;
            loadRequest = Guid.Empty; loadDeadline = default(DateTime); loadFingerprint = null; LoadState = DesignLoadState.Idle; LoadStatus = null;
            lock (stateLock) blueprintCache.Clear();
            InputLockManager.RemoveControlLock(LaunchLock);
        }
    }
}
