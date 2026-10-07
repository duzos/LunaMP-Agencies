using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>The metadata kept beside a recovery craft (plan R3-section 8). "wasUnsaved" is "true", "false" or "unknown"; unknown counts as unsaved.</summary>
    internal sealed class SnapshotRecord
    {
        public string SnapshotId { get; set; }
        public string CreatedUtc { get; set; }
        public string Facility { get; set; }
        public string UiName { get; set; }
        public string UiDescription { get; set; }
        public string FlagUrl { get; set; }
        public string Sha256 { get; set; }
        public string Fingerprint { get; set; }
        public int PartCount { get; set; }
        public string WasUnsaved { get; set; } = "unknown";
        public string VesselNameAtLastSave { get; set; }
        public string VesselNameAtLastSaveSanitized { get; set; }
        public int? UndoLevel { get; set; }
        public int? UndoIndexAtLastSave { get; set; }
        public List<string> Crew { get; set; }
        public string CraftPath { get; set; }

        public EditorUi Ui { get { return new EditorUi(UiName, UiDescription, FlagUrl); } }
        public bool CountsAsUnsaved { get { return WasUnsaved != "false"; } }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["snapshotId"] = SnapshotId, ["createdUtc"] = CreatedUtc, ["facility"] = Facility,
                ["uiName"] = UiName, ["uiDescription"] = UiDescription, ["flagUrl"] = FlagUrl,
                ["sha256"] = Sha256, ["fingerprint"] = Fingerprint, ["partCount"] = PartCount,
                ["wasUnsaved"] = WasUnsaved == "true" ? new JValue(true) : WasUnsaved == "false" ? new JValue(false) : new JValue("unknown"),
                ["vesselNameAtLastSave"] = VesselNameAtLastSave == null ? (JToken)"unavailable" : VesselNameAtLastSave,
                ["vesselNameAtLastSave_Sanitized"] = VesselNameAtLastSaveSanitized == null ? (JToken)"unavailable" : VesselNameAtLastSaveSanitized,
                ["undoLevel"] = UndoLevel.HasValue ? (JToken)UndoLevel.Value : "unavailable",
                ["undoIndexAtLastSave"] = UndoIndexAtLastSave.HasValue ? (JToken)UndoIndexAtLastSave.Value : "unavailable",
                ["crew"] = Crew == null ? (JToken)"unavailable" : new JArray(Crew)
            };
            return o;
        }

        /// <summary>Strict parse of a metadata file. Null when it is not a well-formed record.</summary>
        public static SnapshotRecord TryParse(string json)
        {
            try
            {
                JObject o;
                // Timestamps stay strings: the default reader would turn "2026-..." into a date token.
                using (var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None, MaxDepth = 8 })
                    o = JObject.Load(reader);
                var record = new SnapshotRecord
                {
                    SnapshotId = Str(o["snapshotId"]), CreatedUtc = Str(o["createdUtc"]), Facility = Str(o["facility"]),
                    UiName = Str(o["uiName"]) ?? "", UiDescription = Str(o["uiDescription"]) ?? "", FlagUrl = Str(o["flagUrl"]) ?? "",
                    Sha256 = Str(o["sha256"]), Fingerprint = Str(o["fingerprint"]), PartCount = o["partCount"] != null && o["partCount"].Type == JTokenType.Integer ? (int)o["partCount"] : -1
                };
                var unsaved = o["wasUnsaved"];
                record.WasUnsaved = unsaved != null && unsaved.Type == JTokenType.Boolean ? ((bool)unsaved ? "true" : "false") : "unknown";
                var name = Str(o["vesselNameAtLastSave"]); record.VesselNameAtLastSave = name == "unavailable" ? null : name;
                var sanitized = Str(o["vesselNameAtLastSave_Sanitized"]); record.VesselNameAtLastSaveSanitized = sanitized == "unavailable" ? null : sanitized;
                record.UndoLevel = o["undoLevel"] != null && o["undoLevel"].Type == JTokenType.Integer ? (int?)(int)o["undoLevel"] : null;
                record.UndoIndexAtLastSave = o["undoIndexAtLastSave"] != null && o["undoIndexAtLastSave"].Type == JTokenType.Integer ? (int?)(int)o["undoIndexAtLastSave"] : null;
                var crew = o["crew"] as JArray;
                if (crew != null) record.Crew = crew.Select(c => (string)c).ToList();
                if (!OperationLimits.IsSnapshotId(record.SnapshotId) || record.Sha256 == null || record.Sha256.Length != 64 || record.Fingerprint == null
                    || record.PartCount < 0 || record.CreatedUtc == null || (record.Facility != "VAB" && record.Facility != "SPH")) return null;
                return record;
            }
            catch (Exception) { return null; }
        }

        private static string Str(JToken token) { return token != null && token.Type == JTokenType.String ? (string)token : null; }
    }

    internal sealed class SnapshotOutcome
    {
        public SnapshotRecord Record { get; private set; }
        public string Problem { get; private set; }
        public static SnapshotOutcome Ok(SnapshotRecord record) { return new SnapshotOutcome { Record = record }; }
        public static SnapshotOutcome Fail(string problem) { return new SnapshotOutcome { Problem = problem }; }
    }

    /// <summary>
    /// Takes, verifies, lists and prunes recovery snapshots (plan R1-section 8, R3-section 8). Pure over <see cref="IOperationFiles"/>
    /// and <see cref="IEditorPort"/>, so every failure branch is tested with fakes.
    /// </summary>
    internal sealed class SnapshotStore
    {
        public const int DefaultRetention = 50;
        private readonly IOperationFiles files;
        private readonly Pure.CraftPaths paths;
        private readonly Func<DateTime> utcNow;
        private readonly Func<string> newId;

        public SnapshotStore(IOperationFiles files, Pure.CraftPaths paths, Func<DateTime> utcNow = null, Func<string> newId = null)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.newId = newId ?? (() => "s" + Guid.NewGuid().ToString("N").Substring(0, 20));
        }

        /// <summary>A copy of the craft whose header ship, description and missionFlag carry the UI values (SaveShip reads ship.shipName, which only SetBackup syncs).</summary>
        public static Pure.ConfigNode WithUiHeader(Pure.ConfigNode craft, EditorUi ui)
        {
            var copy = new Pure.ConfigNode(craft.Name);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in craft.Entries)
            {
                if (!entry.IsValue) { copy.AddNode(entry.Child); continue; }
                string replacement = null;
                if (entry.Key == "ship") replacement = ui.Name;
                else if (entry.Key == "description") replacement = ui.Description;
                else if (entry.Key == "missionFlag") replacement = ui.FlagUrl;
                if (replacement != null && seen.Add(entry.Key)) copy.AddValue(entry.Key, replacement);
                else copy.AddValue(entry.Key, entry.Value);
            }
            return copy;
        }

        /// <summary>The fingerprint snapshots and restores compare: the registry-applied projection of the craft with the UI header, plus the UI fields.</summary>
        public static string Fingerprint(Pure.ConfigNode craft, EditorUi ui)
        {
            return Pure.CraftFingerprint.Compute(WithUiHeader(craft, ui), Pure.RoundtripVolatileKeys.Default(), ui.Name, ui.Description, ui.FlagUrl);
        }

        public static int PartCountOf(Pure.ConfigNode craft) { return craft.Children("PART").Count(); }

        /// <summary>
        /// Writes the capture to recovery/kc-snap-&lt;id&gt;.craft and verifies it before anything is dispatched: read-back hash, parse,
        /// part count against the live editor, AllPartsFound and fingerprint equality. Any failure removes the files and reports why.
        /// </summary>
        public SnapshotOutcome Take(EditorCraft capture, IEditorPort port, string facility)
        {
            if (capture == null) return SnapshotOutcome.Fail("capture_unavailable");
            var id = newId();
            var craftPath = paths.RecoveryPath(id); var metaPath = paths.RecoveryMetaPath(id);
            if (!craftPath.Ok || !metaPath.Ok) return SnapshotOutcome.Fail(craftPath.Ok ? metaPath.ReasonCode : craftPath.ReasonCode);
            try
            {
                var ui = capture.Ui;
                var node = WithUiHeader(capture.Craft, ui);
                var bytes = new UTF8Encoding(false).GetBytes(Pure.ConfigText.Print(node));
                var hash = OperationHash.Sha256Hex(bytes);
                files.WriteAtomic(craftPath.FullPath, bytes);
                var back = files.ReadAllBytes(craftPath.FullPath);
                if (!string.Equals(OperationHash.Sha256Hex(back), hash, StringComparison.Ordinal)) return Discard(craftPath.FullPath, metaPath.FullPath, "hash_mismatch");
                Pure.ConfigNode reloaded;
                try { reloaded = Pure.ConfigText.Parse(new UTF8Encoding(false, true).GetString(back)); }
                catch (Pure.ConfigParseException) { return Discard(craftPath.FullPath, metaPath.FullPath, "unparsable"); }
                var parts = PartCountOf(node);
                if (parts == 0 || parts != PartCountOf(reloaded) || parts != port.PartCount) return Discard(craftPath.FullPath, metaPath.FullPath, "part_count_mismatch");
                string missing;
                if (!port.AllPartsFound(craftPath.FullPath, out missing)) return Discard(craftPath.FullPath, metaPath.FullPath, "parts_not_found");
                var fingerprint = Fingerprint(node, ui);
                if (!string.Equals(fingerprint, Fingerprint(reloaded, ui), StringComparison.Ordinal)) return Discard(craftPath.FullPath, metaPath.FullPath, "fingerprint_mismatch");

                var unsaved = port.Unsaved;
                var fields = port.ReadSaveFields();
                var crew = port.ReadCrew();
                var record = new SnapshotRecord
                {
                    SnapshotId = id, CreatedUtc = utcNow().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture), Facility = facility,
                    UiName = ui.Name, UiDescription = ui.Description, FlagUrl = ui.FlagUrl, Sha256 = hash, Fingerprint = fingerprint, PartCount = parts,
                    WasUnsaved = unsaved.HasValue ? (unsaved.Value ? "true" : "false") : "unknown",
                    VesselNameAtLastSave = fields == null ? null : fields.Name, VesselNameAtLastSaveSanitized = fields == null ? null : fields.Sanitized,
                    UndoLevel = fields == null ? (int?)null : fields.UndoLevel, UndoIndexAtLastSave = fields == null ? (int?)null : fields.UndoIndexAtLastSave,
                    Crew = crew == null ? null : crew.ToList(), CraftPath = craftPath.FullPath
                };
                files.WriteAtomic(metaPath.FullPath, new UTF8Encoding(false).GetBytes(record.ToJson().ToString(Newtonsoft.Json.Formatting.Indented)));
                return SnapshotOutcome.Ok(record);
            }
            catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException)
            { return Discard(craftPath.FullPath, metaPath.FullPath, "io_" + error.GetType().Name); }
        }

        private SnapshotOutcome Discard(string craft, string meta, string problem)
        {
            try { files.Delete(craft); files.Delete(meta); } catch (System.IO.IOException) { /* a leftover recovery file is harmless and never loaded */ }
            return SnapshotOutcome.Fail(problem);
        }

        /// <summary>Reads one record. Null when the id, the metadata file or its content is not valid.</summary>
        public SnapshotRecord TryGet(string snapshotId)
        {
            if (!OperationLimits.IsSnapshotId(snapshotId)) return null;
            var meta = paths.RecoveryMetaPath(snapshotId); var craft = paths.RecoveryPath(snapshotId);
            if (!meta.Ok || !craft.Ok) return null;
            try
            {
                if (!files.Exists(meta.FullPath)) return null;
                var record = SnapshotRecord.TryParse(new UTF8Encoding(false, true).GetString(files.ReadAllBytes(meta.FullPath)));
                if (record == null || !string.Equals(record.SnapshotId, snapshotId, StringComparison.Ordinal)) return null;
                record.CraftPath = craft.FullPath;
                return record;
            }
            catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException || error is DecoderFallbackException) { return null; }
        }

        /// <summary>Null when the craft file exists and hashes to the recorded value; otherwise why not.</summary>
        public string Verify(SnapshotRecord record)
        {
            try
            {
                if (record.CraftPath == null || !files.Exists(record.CraftPath)) return "snapshot_file_missing";
                return string.Equals(OperationHash.Sha256Hex(files.ReadAllBytes(record.CraftPath)), record.Sha256, StringComparison.Ordinal) ? null : "snapshot_hash_mismatch";
            }
            catch (System.IO.IOException) { return "snapshot_file_unreadable"; }
        }

        /// <summary>Newest first, at most <paramref name="count"/> records.</summary>
        public List<SnapshotRecord> Recent(int count)
        {
            var result = new List<SnapshotRecord>();
            var directory = paths.WorkspaceDirectory("recovery");
            if (directory == null) return result;
            IReadOnlyList<FileEntry> entries;
            try { entries = files.List(directory, ".json"); } catch (System.IO.IOException) { return result; }
            foreach (var entry in entries.Where(e => e.Name.StartsWith("kc-snap-", StringComparison.Ordinal)).Take(500))
            {
                var id = entry.Name.Substring("kc-snap-".Length, entry.Name.Length - "kc-snap-".Length - ".json".Length);
                var record = TryGet(id);
                if (record != null) result.Add(record);
            }
            return result.OrderByDescending(r => r.CreatedUtc, StringComparer.Ordinal).Take(count).ToList();
        }

        /// <summary>
        /// Keeps the newest <paramref name="keep"/>. A snapshot of an unsaved craft is never pruned while it is the latest one for
        /// that craft name. Returns the ids deleted.
        /// </summary>
        public List<string> Prune(int keep, string alsoKeep = null)
        {
            var removed = new List<string>();
            var all = Recent(int.MaxValue);
            var protectedIds = new HashSet<string>(StringComparer.Ordinal);
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            if (alsoKeep != null) protectedIds.Add(alsoKeep); // a restore's own source must survive the snapshot taken just before it
            foreach (var record in all)
                if (record.CountsAsUnsaved && seenNames.Add(record.UiName ?? "")) protectedIds.Add(record.SnapshotId);
            for (var i = keep; i < all.Count; i++)
            {
                var record = all[i];
                if (protectedIds.Contains(record.SnapshotId)) continue;
                try
                {
                    files.Delete(paths.RecoveryPath(record.SnapshotId).FullPath); files.Delete(paths.RecoveryMetaPath(record.SnapshotId).FullPath);
                    removed.Add(record.SnapshotId);
                }
                catch (System.IO.IOException) { /* retried at the next snapshot */ }
            }
            return removed;
        }
    }
}
