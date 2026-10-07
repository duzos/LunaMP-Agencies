using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KspControl.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    internal enum LedgerState { Missing, Ok, Corrupt }

    /// <summary>One ship file KspControl wrote: the hash it had when written, which operation wrote it, and when.</summary>
    internal sealed class LedgerEntry
    {
        public string Facility { get; set; }
        public string FileName { get; set; }
        public string Sha256 { get; set; }
        public string RequestId { get; set; }
        public string SavedUtc { get; set; }
    }

    /// <summary>
    /// The record of ship files this service created (plan R1-section 9, R3-section 9): <c>KspControlData/&lt;save&gt;/ledger.json</c>.
    /// Only an entry here, whose hash still matches the file, makes a file replaceable. A missing ledger owns nothing; an unreadable or
    /// malformed one is <see cref="LedgerState.Corrupt"/> and is never guessed at. Pure over <see cref="IOperationFiles"/>.
    /// </summary>
    internal sealed class CraftLedger
    {
        public const int MaxEntries = 2000;
        public const int MaxFileBytes = 1024 * 1024;
        private const int Version = 1;
        private readonly List<LedgerEntry> entries = new List<LedgerEntry>();

        public LedgerState State { get; private set; }
        /// <summary>False when the ledger could not be read: a save must refuse rather than overwrite what it cannot account for.</summary>
        public bool Usable { get { return State != LedgerState.Corrupt; } }
        public int Count { get { return entries.Count; } }

        private CraftLedger(LedgerState state) { State = state; }

        public static CraftLedger Load(IOperationFiles files, string path)
        {
            try
            {
                if (!files.Exists(path)) return new CraftLedger(LedgerState.Missing);
                var bytes = files.ReadAllBytes(path);
                if (bytes.Length > MaxFileBytes) return new CraftLedger(LedgerState.Corrupt);
                var parsed = Parse(new UTF8Encoding(false, true).GetString(bytes));
                return parsed ?? new CraftLedger(LedgerState.Corrupt);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is DecoderFallbackException)
            { return new CraftLedger(LedgerState.Corrupt); }
        }

        private static CraftLedger Parse(string text)
        {
            try
            {
                JObject root;
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 })
                    root = JObject.Load(reader);
                var version = root["version"];
                var list = root["entries"] as JArray;
                if (version == null || version.Type != JTokenType.Integer || (int)version != Version || list == null || list.Count > MaxEntries) return null;
                var ledger = new CraftLedger(LedgerState.Ok);
                foreach (var item in list)
                {
                    var o = item as JObject;
                    if (o == null) return null;
                    var entry = new LedgerEntry { Facility = Str(o["facility"]), FileName = Str(o["fileName"]), Sha256 = Str(o["sha256"]), RequestId = Str(o["requestId"]), SavedUtc = Str(o["savedUtc"]) };
                    if (!Valid(entry) || ledger.Find(entry.Facility, entry.FileName) != null) return null;
                    ledger.entries.Add(entry);
                }
                return ledger;
            }
            catch (Exception) { return null; }
        }

        private static string Str(JToken token) { return token != null && token.Type == JTokenType.String ? (string)token : null; }

        private static bool Valid(LedgerEntry e)
        {
            if (e.Facility != "VAB" && e.Facility != "SPH") return false;
            if (e.FileName == null || !e.FileName.EndsWith(Pure.CraftPaths.CraftExtension, StringComparison.OrdinalIgnoreCase)) return false;
            if (!Pure.CraftPaths.IsName(e.FileName.Substring(0, e.FileName.Length - Pure.CraftPaths.CraftExtension.Length))) return false;
            return OperationLimits.IsSha256(e.Sha256) && OperationLimits.IsRequestId(e.RequestId) && e.SavedUtc != null && e.SavedUtc.Length <= 40;
        }

        /// <summary>The entry for a ship file (file name with extension), or null. A corrupt ledger owns nothing.</summary>
        public LedgerEntry Find(string facility, string fileName)
        {
            if (State == LedgerState.Corrupt) return null;
            return entries.FirstOrDefault(e => e.Facility == facility && string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Adds or replaces the entry for the file. False (and nothing changes) when a new entry would exceed the cap.</summary>
        public bool Record(LedgerEntry entry)
        {
            if (State == LedgerState.Corrupt || !Valid(entry)) return false;
            var existing = Find(entry.Facility, entry.FileName);
            if (existing == null && entries.Count >= MaxEntries) return false;
            if (existing != null) entries.Remove(existing);
            entries.Add(entry);
            State = LedgerState.Ok;
            return true;
        }

        /// <summary>Drops entries for which <paramref name="fileStillExists"/> is false. Returns how many were dropped.</summary>
        public int Prune(Func<LedgerEntry, bool> fileStillExists)
        {
            return entries.RemoveAll(e => !fileStillExists(e));
        }

        public string ToJson()
        {
            var list = new JArray(entries.OrderBy(e => e.Facility, StringComparer.Ordinal).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.FileName, StringComparer.Ordinal)
                .Select(e => (JToken)new JObject { ["facility"] = e.Facility, ["fileName"] = e.FileName, ["sha256"] = e.Sha256, ["requestId"] = e.RequestId, ["savedUtc"] = e.SavedUtc }));
            return new JObject { ["version"] = Version, ["entries"] = list }.ToString(Formatting.Indented);
        }

        public void Save(IOperationFiles files, string path)
        {
            if (State == LedgerState.Corrupt) throw new InvalidOperationException("ledger_corrupt");
            files.WriteAtomic(path, new UTF8Encoding(false).GetBytes(ToJson()));
        }
    }

    internal enum SaveAction { Create, Replace, Refuse }

    internal sealed class SaveDecision
    {
        public SaveAction Action { get; private set; }
        public string Reason { get; private set; }
        public string Detail { get; private set; }
        /// <summary>The hash of the file being replaced.</summary>
        public string ReplacedSha256 { get; private set; }
        public static SaveDecision Create() { return new SaveDecision { Action = SaveAction.Create }; }
        public static SaveDecision Replace(string replaced) { return new SaveDecision { Action = SaveAction.Replace, ReplacedSha256 = replaced }; }
        public static SaveDecision Refuse(string reason, string detail) { return new SaveDecision { Action = SaveAction.Refuse, Reason = reason, Detail = detail }; }
    }

    /// <summary>
    /// The overwrite rule of editor_save_craft (plan R1-section 9, base acceptance): a file that exists is never overwritten unless it is
    /// ledger-owned and its current hash equals both the ledger hash and the caller's <c>replaceExpectedSha256</c>. Pure.
    /// </summary>
    internal static class SavePolicy
    {
        public static SaveDecision Decide(bool targetExists, string currentSha256, string replaceExpectedSha256, LedgerEntry owned, LedgerState ledger)
        {
            if (ledger == LedgerState.Corrupt) return SaveDecision.Refuse(OperationReasons.LedgerUnavailable, "the ownership ledger cannot be read; remove or repair ledger.json");
            if (!targetExists)
                return replaceExpectedSha256 == null ? SaveDecision.Create() : SaveDecision.Refuse(OperationReasons.FileChanged, "replaceExpectedSha256 was given but the file does not exist");
            if (replaceExpectedSha256 == null) return SaveDecision.Refuse(OperationReasons.FileExists, "a ship file with this name exists; a human file is never overwritten");
            if (owned == null) return SaveDecision.Refuse(OperationReasons.FileNotKspControlOwned, "KspControl did not create this file");
            if (currentSha256 == null || !string.Equals(currentSha256, replaceExpectedSha256, StringComparison.Ordinal))
                return SaveDecision.Refuse(OperationReasons.FileChanged, "the file no longer has the hash replaceExpectedSha256 names");
            if (!string.Equals(currentSha256, owned.Sha256, StringComparison.Ordinal))
                return SaveDecision.Refuse(OperationReasons.FileChanged, "the file was modified since KspControl wrote it");
            return SaveDecision.Replace(currentSha256);
        }
    }
}
