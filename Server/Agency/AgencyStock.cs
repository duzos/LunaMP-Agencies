using LmpCommon.Agency;
using Server.Client;
using Server.Context;
using Server.Diagnostics;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Server.Agency
{
    /// <summary>
    /// Design stock (plan 40): bulk builds, stock launches, lot bookkeeping and tooling blueprint files.
    /// Signatures are frozen by slice S0; slice S1 owns the bodies.
    /// </summary>
    public static partial class AgencyEconomyStore
    {
        /// <summary>The server's stock discount settings, normalized so a bad value can never take the economy offline.</summary>
        private static StockRates CurrentStockRates()
        {
            var settings = GeneralSettings.SettingsStore;
            return LmpCommon.Agency.StockRates.Normalize(settings.StockMaxDiscount, settings.StockFullDiscountUnits);
        }

        private static IEnumerable<StoredTradeOffer> OpenOffersSoldBy(EconomyDocument d, Guid agency) =>
            d.TradeOffers.Values.Where(o => o?.Offer != null && o.Offer.Status == TradeOfferStatus.Open && o.Offer.SellerAgencyId == agency && o.Escrow != null);

        private static bool Revertible(EconomyLaunch l) => l.State == LaunchState.Prepared || l.State == LaunchState.Registered && !l.ExternallySettled;

        /// <summary>
        /// Units of <paramref name="fp"/> the agency holds for the 999 cap: its lot units, escrow units in its open offers, and its stock launches
        /// that are Prepared or Registered and not externally settled (revertible).
        /// </summary>
        internal static int StockHeld(EconomyDocument d, Guid agency, string fp)
        {
            if (d == null || string.IsNullOrEmpty(fp)) return 0;
            long held = 0;
            if (d.Agencies.TryGetValue(agency, out var a) && a?.Stock != null)
                held += a.Stock.Where(l => l != null && l.Fingerprint == fp).Sum(l => (long)l.Units);
            held += OpenOffersSoldBy(d, agency).SelectMany(o => o.Escrow).Where(l => l != null && l.Fingerprint == fp).Sum(l => (long)l.Units);
            held += d.Launches.Values.Count(l => l != null && l.AgencyId == agency && l.Stock != null && l.Stock.Fingerprint == fp && Revertible(l));
            return (int)Math.Min(int.MaxValue, held);
        }

        /// <summary>Lot slots the agency uses: its stock rows, escrow rows in its open offers, and one per Prepared stock launch. Never above <see cref="StockDefaults.MaxLots"/>.</summary>
        internal static int StockLotSlots(EconomyDocument d, Guid agency)
        {
            if (d == null) return 0;
            return (d.Agencies.TryGetValue(agency, out var a) && a?.Stock != null ? a.Stock.Count : 0)
                + OpenOffersSoldBy(d, agency).Sum(o => o.Escrow.Count)
                + d.Launches.Values.Count(l => l != null && l.AgencyId == agency && l.State == LaunchState.Prepared && l.Stock != null);
        }

        private static void RequireLotSlots(EconomyDocument d, Guid agency)
        {
            if (StockLotSlots(d, agency) > StockDefaults.MaxLots) throw new InvalidOperationException("Too many stock batches; launch or sell some stock first.");
        }

        /// <summary>BuildStock: prices the stored manifest, checks the client's expected charge and the held cap, charges and merges a new lot.</summary>
        private static void BuildStock(EconomyDocument d, EconomyAgency agency, ClientStructure client, EconomyCommand command, EconomyResult result)
        {
            var fingerprint = command.StockFingerprint;
            var design = string.IsNullOrEmpty(fingerprint) ? null : agency.Designs.SingleOrDefault(x => x.Fingerprint == fingerprint);
            if (design == null) throw new InvalidOperationException("Only a design your agency has tooled can be built.");
            var quote = StockPolicy.Quote(design, command.StockUnits, Rates(), CurrentStockRates());
            if (!quote.Success) throw new InvalidOperationException(quote.Reason);
            var expected = UsesFunds ? quote.Total : 0;
            if (double.IsNaN(command.ExpectedCharge) || double.IsInfinity(command.ExpectedCharge) || Math.Abs(command.ExpectedCharge - expected) > 1e-6 * Math.Max(1, quote.Total))
                throw new InvalidOperationException("Price changed; review the new quote.");
            if ((long)StockHeld(d, client.AgencyId, fingerprint) + quote.Units > StockDefaults.MaxHeldUnits)
                throw new InvalidOperationException("Stock limit is " + StockDefaults.MaxHeldUnits + " units per design.");
            Charge(agency, expected);
            StockPolicy.Merge(agency.Stock, new DesignStockLot
            {
                LotId = Guid.NewGuid(), Fingerprint = fingerprint, Units = quote.Units,
                PrepaidPerUnit = UsesFunds ? quote.PrepaidPerUnit : 0, LaunchMultiplier = quote.UnitMultiplier,
                BuilderAgencyId = client.AgencyId, SourceAgencyId = Guid.Empty, FundsBuilt = UsesFunds, CreatedUtcTicks = UtcNow().Ticks
            });
            RequireLotSlots(d, client.AgencyId);
            result.Reason = $"Built {quote.Units} for {expected:N1} funds.";
        }

        /// <summary>
        /// Stock PrepareLaunch: reserves one unit of the named lot. Returns the unit's terms, sets the launch charge and multiplier. A lot that
        /// reaches 0 is removed at once (the launch carries its terms).
        /// </summary>
        private static StockTerms ReserveStock(EconomyAgency agency, EconomyCommand command, ToolingQuote quote, ref double launchCharge, ref double launchMultiplier)
        {
            var lot = agency.Stock.FirstOrDefault(l => l != null && l.LotId == command.StockLotId && l.Units > 0) ?? throw new InvalidOperationException("No stock left for this design.");
            if (lot.Fingerprint != quote.Fingerprint) throw new InvalidOperationException("Stock does not match this design.");
            // Sandbox-built lots (nothing prepaid) pay the normal tooled rate in Career; shared with the client so the editor shows what is charged.
            launchCharge = StockPolicy.EffectiveLaunchCharge(quote, lot, UsesFunds, Rates().TooledLaunch, out launchMultiplier);
            var terms = StockPolicy.TermsOf(lot);
            lot.Units -= 1;
            if (lot.Units == 0) agency.Stock.Remove(lot);
            quote.LaunchCost = launchCharge;
            return terms;
        }

        /// <summary>
        /// Returns one unit with <paramref name="launch"/>'s stock terms to its agency. Never fails: merges into a same-terms row, else appends a row
        /// when a slot is free, else credits the prepaid price (when funds-built). A missing agency row drops the unit with a log line.
        /// </summary>
        internal static void GiveBackUnit(EconomyDocument d, EconomyLaunch launch)
        {
            if (d == null || launch?.Stock == null) return;
            var terms = launch.Stock;
            try
            {
                EconomyAgency agency;
                if (!d.Agencies.TryGetValue(launch.AgencyId, out agency) || agency == null)
                {
                    if (!AgencyStore.Agencies.ContainsKey(launch.AgencyId))
                    {
                        LunaLog.Warning($"[Economy] Stock unit dropped: agency {launch.AgencyId} no longer exists (design {terms.Fingerprint}, launch {launch.LaunchId}).");
                        return;
                    }
                    agency = Agency(d, launch.AgencyId);
                }
                if (agency.Stock == null) agency.Stock = new List<DesignStockLot>();
                var unit = StockPolicy.LotFrom(terms, Guid.NewGuid(), 1, UtcNow().Ticks);
                var same = agency.Stock.FirstOrDefault(l => StockPolicy.SameTerms(l, unit));
                if (same != null) { same.Units += 1; return; }
                if (StockLotSlots(d, launch.AgencyId) < StockDefaults.MaxLots) { agency.Stock.Add(unit); return; }
                var credit = terms.FundsBuilt && ToolingPolicy.FiniteNonNegative(terms.PrepaidPerUnit) ? terms.PrepaidPerUnit : 0;
                agency.Funds += credit;
                LunaLog.Warning($"[Economy] Stock lot slots full: agency {launch.AgencyId} design {terms.Fingerprint} unit returned as {credit:N1} funds.");
            }
            catch (Exception e)
            {
                LunaLog.Error($"[Economy] Stock unit for launch {launch.LaunchId} could not be returned: {e.Message}");
            }
        }

        /// <summary>True while anything still needs the design entry for this fingerprint: held or escrowed lots, Prepared or revertible stock launches.</summary>
        internal static bool StockDesignRetainable(EconomyDocument d, Guid agency, string fingerprint) => StockHeld(d, agency, fingerprint) > 0;

        /// <summary>Lot, escrow, launch-terms and blueprint-ref invariants. Runs inside <see cref="Validate"/>, so on every Load and Persist. No file I/O.</summary>
        private static void ValidateStockLinks(EconomyDocument d)
        {
            var ids = new HashSet<Guid>();
            void Lot(DesignStockLot lot)
            {
                if (lot == null || lot.LotId == Guid.Empty || string.IsNullOrEmpty(lot.Fingerprint) || lot.Units < 1 || lot.Units > StockDefaults.MaxHeldUnits
                    || !ToolingPolicy.FiniteNonNegative(lot.PrepaidPerUnit) || !ToolingPolicy.FiniteNonNegative(lot.LaunchMultiplier) || !ids.Add(lot.LotId))
                    throw new InvalidDataException("Invalid stock lot.");
            }
            var serverHashes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in d.Agencies)
            {
                var agency = entry.Value;
                if (agency.Stock == null || agency.Blueprints == null) throw new InvalidDataException("Invalid stock registry.");
                foreach (var lot in agency.Stock) Lot(lot);
                long total = 0;
                foreach (var blueprint in agency.Blueprints)
                {
                    var r = blueprint.Value;
                    if (r == null || r.Fingerprint != blueprint.Key || !agency.Designs.Any(x => x.Fingerprint == blueprint.Key) || r.Size < 1 || r.Size > ToolingLimits.MaxToolingBlueprintBytes
                        || !IsBlueprintHash(r.Hash) || r.Editor != "VAB" && r.Editor != "SPH")
                        throw new InvalidDataException("Invalid tooling blueprint reference.");
                    total += r.Size;
                    serverHashes[r.Hash] = r.Size;
                }
                if (total > ToolingLimits.MaxAgencyToolingBlueprintBytes) throw new InvalidDataException("Tooling blueprint storage limit exceeded.");
            }
            if (serverHashes.Values.Sum(s => (long)s) > ToolingLimits.MaxServerToolingBlueprintBytes) throw new InvalidDataException("Server tooling blueprint storage limit exceeded.");
            foreach (var offer in d.TradeOffers.Values)
            {
                if (offer?.Escrow == null) throw new InvalidDataException("Invalid trade escrow.");
                foreach (var lot in offer.Escrow) Lot(lot);
            }
            foreach (var launch in d.Launches.Values)
                if (launch.Stock != null && (launch.VoucherId != Guid.Empty || string.IsNullOrEmpty(launch.Stock.Fingerprint) || !ToolingPolicy.FiniteNonNegative(launch.Stock.PrepaidPerUnit) || !ToolingPolicy.FiniteNonNegative(launch.Stock.LaunchMultiplier)))
                    throw new InvalidDataException("Invalid stock launch terms.");
            var agencies = d.Agencies.Keys.Concat(d.TradeOffers.Values.Select(o => o.Offer?.SellerAgencyId ?? Guid.Empty)).Concat(d.Launches.Values.Select(l => l.AgencyId)).Distinct();
            foreach (var agency in agencies)
                if (StockLotSlots(d, agency) > StockDefaults.MaxLots) throw new InvalidDataException("Stock lot slots exceeded.");
        }

        private static Dictionary<string, int> StockHeldByFingerprint(EconomyDocument d, Guid agency)
        {
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            if (d.Agencies.TryGetValue(agency, out var a) && a?.Stock != null) fingerprints.UnionWith(a.Stock.Where(l => l != null).Select(l => l.Fingerprint));
            fingerprints.UnionWith(OpenOffersSoldBy(d, agency).SelectMany(o => o.Escrow).Where(l => l != null).Select(l => l.Fingerprint));
            fingerprints.UnionWith(d.Launches.Values.Where(l => l != null && l.AgencyId == agency && l.Stock != null && Revertible(l)).Select(l => l.Stock.Fingerprint));
            var held = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var fp in fingerprints.Where(f => !string.IsNullOrEmpty(f)).OrderBy(f => f, StringComparer.Ordinal).Take(EconomySnapshot.MaxStockHeldEntries))
                held[fp] = StockHeld(d, agency, fp);
            return held;
        }

        // ---- Tooling blueprints: content-addressed files under Universe/AgencyBlueprints ----

        internal static string BlueprintDirectory => Path.Combine(ServerContext.UniverseDirectory, "AgencyBlueprints");
        private static string BlueprintPath(string hash) => Path.Combine(BlueprintDirectory, hash + ".craft");
        private static readonly Regex BlueprintHashPattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex BlueprintFilePattern = new Regex("^[0-9a-f]{64}\\.craft$", RegexOptions.CultureInvariant);
        private static readonly Regex BlueprintTempPattern = new Regex("^[0-9a-f]{64}\\.craft\\.tmp$", RegexOptions.CultureInvariant);
        private static bool IsBlueprintHash(string hash) => hash != null && BlueprintHashPattern.IsMatch(hash);

        /// <summary>Test hook: invoked with the target path just before a blueprint file is written. Throwing an IOException simulates a disk error.</summary>
        internal static Action<string> BlueprintWriteCheckpoint;

        /// <summary>Files written by the running Execute (cleaned up if it fails) and hashes it replaced (cleaned up if it commits). Only touched under the gate.</summary>
        private static readonly List<string> ExecuteNewBlueprintFiles = new List<string>();
        private static readonly List<string> ExecuteReplacedBlueprintHashes = new List<string>();

        /// <summary>Control characters become spaces and an overlong name is cut to <see cref="ToolingDesignNames.MaxLength"/>; null when nothing is left.</summary>
        private static string SanitizeDesignName(string name) => ToolingDesignNames.Sanitize(name);

        /// <summary>
        /// Gives every nameless tooled design the craft name of its saved blueprint: the ref's stored name, else the blueprint file's top-level
        /// "ship = " line (the ref's name is filled too). Only the agency's own refs and files are read. Returns true when anything changed.
        /// </summary>
        internal static bool BackfillDesignNames(EconomyDocument d)
        {
            var changed = false;
            foreach (var agency in d?.Agencies.Values ?? Enumerable.Empty<EconomyAgency>())
            {
                if (agency?.Designs == null) continue;
                foreach (var design in agency.Designs)
                {
                    if (design == null || design.Name != null && SanitizeDesignName(design.Name) == design.Name) continue;
                    var name = SanitizeDesignName(design.Name);
                    ToolingBlueprintRef blueprint = null;
                    if (name == null && agency.Blueprints != null && design.Fingerprint != null && agency.Blueprints.TryGetValue(design.Fingerprint, out blueprint) && blueprint != null)
                    {
                        name = SanitizeDesignName(blueprint.Name) ?? BlueprintFileShipName(blueprint.Hash);
                        if (name != null && SanitizeDesignName(blueprint.Name) == null) blueprint.Name = name;
                    }
                    if (name == design.Name) continue;
                    design.Name = name;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>The "ship = " name in a stored blueprint file, reading only its header. Null when the file is missing or unreadable.</summary>
        private static string BlueprintFileShipName(string hash)
        {
            if (!IsBlueprintHash(hash)) return null;
            try
            {
                var path = BlueprintPath(hash);
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var buffer = new byte[(int)Math.Min(stream.Length, ToolingDesignNames.MaxHeaderBytes)];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < buffer.Length) Array.Resize(ref buffer, read);
                    return ToolingDesignNames.ShipNameFromCraft(buffer);
                }
            }
            catch (Exception) { return null; }
        }

        private static bool FileHasHash(string path, string hash)
        {
            try { return File.Exists(path) && Hash(File.ReadAllBytes(path)) == hash; }
            catch (Exception) { return false; }
        }

        private static bool BlueprintReferenced(EconomyDocument d, string hash) => d.Agencies.Values.Any(a => a?.Blueprints != null && a.Blueprints.Values.Any(r => r?.Hash == hash));

        /// <summary>
        /// Validates and stores a tooling blueprint for a tooled design: content-addressed file under Universe/AgencyBlueprints plus a ref in
        /// <paramref name="d"/>. Every storage miss returns false with <paramref name="reason"/> set and never fails the Tool. A path is added to
        /// <paramref name="newFiles"/> only when this call wrote the file, so a failed Commit can delete it.
        /// </summary>
        internal static bool TryStoreBlueprint(EconomyDocument d, Guid agency, string fingerprint, byte[] bytes, string editor, string name, List<string> newFiles, Func<string, bool> isScience, out string reason)
        {
            reason = null;
            if (d == null || string.IsNullOrEmpty(fingerprint) || !d.Agencies.TryGetValue(agency, out var holder) || holder == null || !holder.Designs.Any(x => x.Fingerprint == fingerprint))
            { reason = "design is not tooled"; return false; }
            if (bytes == null || bytes.Length == 0 || bytes.Length > ToolingLimits.MaxToolingBlueprintBytes) { reason = "storage limit"; return false; }
            try
            {
                if (ToolingPolicy.Fingerprint(BlueprintManifest(bytes, editor, isScience)) != fingerprint) { reason = "does not match"; return false; }
            }
            catch (Exception) { reason = "does not match"; return false; }
            if (holder.Blueprints == null) holder.Blueprints = new Dictionary<string, ToolingBlueprintRef>();
            var hash = Hash(bytes);
            var path = BlueprintPath(hash);
            holder.Blueprints.TryGetValue(fingerprint, out var existing);
            if (existing != null && existing.Hash == hash && FileHasHash(path, hash)) return true;
            if (existing != null && existing.Hash != hash && UtcNow().Ticks >= existing.SavedUtcTicks && UtcNow().Ticks - existing.SavedUtcTicks < TimeSpan.FromSeconds(ToolingLimits.BlueprintReplaceCooldownSeconds).Ticks)
            { reason = "saved less than a minute ago"; return false; }
            var agencyTotal = holder.Blueprints.Where(p => p.Key != fingerprint && p.Value != null).Sum(p => (long)p.Value.Size) + bytes.Length;
            if (agencyTotal > ToolingLimits.MaxAgencyToolingBlueprintBytes) { reason = "storage limit"; return false; }
            var hashes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var a in d.Agencies)
                foreach (var r in a.Value?.Blueprints ?? new Dictionary<string, ToolingBlueprintRef>())
                    if (r.Value?.Hash != null && !(a.Key == agency && r.Key == fingerprint)) hashes[r.Value.Hash] = r.Value.Size;
            hashes[hash] = bytes.Length;
            if (hashes.Values.Sum() > ToolingLimits.MaxServerToolingBlueprintBytes) { reason = "storage limit"; return false; }
            if (!FileHasHash(path, hash))
            {
                try
                {
                    BlueprintWriteCheckpoint?.Invoke(path);
                    AtomicWriteBytes(path, bytes);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    reason = "disk error";
                    return false;
                }
                newFiles?.Add(path);
            }
            if (existing != null && existing.Hash != hash && existing.Hash != null) ExecuteReplacedBlueprintHashes.Add(existing.Hash);
            name = SanitizeDesignName(name) ?? ToolingDesignNames.ShipNameFromCraft(bytes);
            holder.Blueprints[fingerprint] = new ToolingBlueprintRef { Fingerprint = fingerprint, Name = name, Editor = editor, Hash = hash, Size = bytes.Length, SavedUtcTicks = UtcNow().Ticks };
            // A design tooled without a name (before agencies.9, or the name was unusable) takes the saved craft's name.
            var design = holder.Designs.FirstOrDefault(x => x.Fingerprint == fingerprint);
            if (design != null && SanitizeDesignName(design.Name) == null && name != null) design.Name = name;
            return true;
        }

        /// <summary>Writes bytes to a temporary file and moves it over <paramref name="path"/>.</summary>
        internal static void AtomicWriteBytes(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
            }
        }

        /// <summary>After a failed Execute: delete files it wrote unless the live document already references their hash. Never throws.</summary>
        private static void DiscardNewBlueprintFiles()
        {
            foreach (var path in ExecuteNewBlueprintFiles)
            {
                try
                {
                    var hash = Path.GetFileNameWithoutExtension(path);
                    if (!BlueprintReferenced(_document, hash) && File.Exists(path)) File.Delete(path);
                }
                catch (Exception) { }
            }
            ExecuteNewBlueprintFiles.Clear();
            ExecuteReplacedBlueprintHashes.Clear();
        }

        /// <summary>After a committed Execute: delete files of replaced refs that nothing references any more. Best effort; Load is the backstop.</summary>
        private static void DeleteReplacedBlueprintFiles()
        {
            foreach (var hash in ExecuteReplacedBlueprintHashes.Distinct().ToArray())
            {
                try { if (IsBlueprintHash(hash) && !BlueprintReferenced(_document, hash)) File.Delete(BlueprintPath(hash)); }
                catch (Exception) { }
            }
            ExecuteNewBlueprintFiles.Clear();
            ExecuteReplacedBlueprintHashes.Clear();
        }

        /// <summary>At the end of a successful Load: removes unreferenced 64-hex .craft files and stray 64-hex .craft.tmp files. Nothing else is touched.</summary>
        private static void CleanupBlueprintFiles(EconomyDocument d)
        {
            try
            {
                if (!Directory.Exists(BlueprintDirectory)) return;
                var referenced = new HashSet<string>(d.Agencies.Values.Where(a => a?.Blueprints != null).SelectMany(a => a.Blueprints.Values).Where(r => r?.Hash != null).Select(r => r.Hash), StringComparer.Ordinal);
                foreach (var path in Directory.GetFiles(BlueprintDirectory))
                {
                    var file = Path.GetFileName(path);
                    var orphan = BlueprintFilePattern.IsMatch(file) && !referenced.Contains(file.Substring(0, 64)) || BlueprintTempPattern.IsMatch(file);
                    if (!orphan) continue;
                    try { File.Delete(path); } catch (Exception) { }
                }
            }
            catch (Exception e) { LunaLog.Warning("[Economy] Tooling blueprint cleanup failed: " + e.Message); }
        }

        /// <summary>FetchBlueprint: read-only. Returns the requesting agency's saved craft for a fingerprint after re-hashing the file.</summary>
        private static void FetchBlueprint(ClientStructure client, EconomyCommand command, EconomyResult result)
        {
            result.Revision = _document.Revision;
            if (string.IsNullOrEmpty(command.StockFingerprint) || !_document.Agencies.TryGetValue(client.AgencyId, out var agency) || agency?.Blueprints == null
                || !agency.Blueprints.TryGetValue(command.StockFingerprint, out var saved) || saved == null || !agency.Designs.Any(x => x.Fingerprint == command.StockFingerprint))
                throw new InvalidOperationException("No saved craft for this design.");
            byte[] bytes;
            try { bytes = File.ReadAllBytes(BlueprintPath(saved.Hash)); }
            catch (Exception) { throw new InvalidOperationException("Saved craft unavailable; save it to tooling again."); }
            if (bytes.Length != saved.Size || Hash(bytes) != saved.Hash) throw new InvalidOperationException("Saved craft unavailable; save it to tooling again.");
            result.BlueprintData = bytes;
            result.BlueprintEditor = saved.Editor;
            result.BlueprintName = saved.Name;
            result.BlueprintHash = saved.Hash;
            result.Success = true;
            result.Reason = "Saved craft ready.";
        }

        // ---- Maintenance sweep ----

        private static DateTime _maintenanceLoggedAt = DateTime.MinValue;
        private static string _maintenanceLoggedMessage;
        private static DateTime _cancelPendingBackoffUntil = DateTime.MinValue;
        /// <summary>Test observability: how many maintenance errors were actually logged (after rate limiting).</summary>
        internal static int MaintenanceErrorsLogged;

        /// <summary>The one limiter shared by the sweep and CancelPending: a message is logged at most once a minute unless it changes.</summary>
        private static void LogMaintenanceError(string message)
        {
            var now = DateTime.UtcNow;
            lock (AgencyVesselMap.TransactionGate)
            {
                if (message == _maintenanceLoggedMessage && now - _maintenanceLoggedAt < TimeSpan.FromSeconds(60) && now >= _maintenanceLoggedAt) return;
                _maintenanceLoggedAt = now;
                _maintenanceLoggedMessage = message;
                MaintenanceErrorsLogged++;
            }
            LunaLog.Error("[Economy] Maintenance sweep failed: " + message);
            PlaytestDiagnostics.Write("economy.sweep.error", () => message);
        }

        private static void ResetMaintenanceState()
        {
            _maintenanceLoggedAt = DateTime.MinValue;
            _maintenanceLoggedMessage = null;
            _cancelPendingBackoffUntil = DateTime.MinValue;
        }

        /// <summary>The once-a-second server sweep (ownership expiry, Prepared launch expiry, trade pruning). Logs failures, rate limited, and never throws.</summary>
        public static void MaintenanceSweep()
        {
            try { VesselOwnershipSystem.SweepExpired(); }
            catch (Exception e) { LogMaintenanceError(e.GetType().Name + ": " + e.Message); }
            try { CancelPending(); }
            catch (Exception e) { LogMaintenanceError(e.GetType().Name + ": " + e.Message); }
        }
    }
}
