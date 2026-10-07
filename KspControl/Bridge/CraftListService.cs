using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// craft.list (plan R1-section 4): the ship files of the current save's Ships folder for one facility, name-ordered, paged, with size,
    /// write time, SHA-256 and whether the ledger says KspControl wrote the file. Read-only; hashes are computed only for the returned page.
    /// Names that cannot be addressed by the save and load tools, and linked files, are counted, not listed.
    /// </summary>
    internal sealed class CraftListService
    {
        /// <summary>Files larger than this are listed without a hash (real modded craft are a few hundred KB).</summary>
        public const long MaxHashedBytes = 8L * 1024 * 1024;
        private readonly Func<Pure.CraftPaths> pathsFactory;
        private readonly IOperationFiles files;

        public CraftListService(Func<Pure.CraftPaths> pathsFactory, IOperationFiles files)
        { this.pathsFactory = pathsFactory; this.files = files; }

        public EditorResult List(JObject args)
        {
            string facility; int offset, limit; string filter;
            var problem = Parse(args ?? new JObject(), out facility, out offset, out limit, out filter);
            if (problem != null) return EditorResult.Fail(ControlReasons.InvalidArgument, problem);
            Pure.CraftPaths paths;
            try { paths = pathsFactory(); } catch (ArgumentException) { return EditorResult.Fail(OperationReasons.PathOutsideSave, "the save folder name is not usable"); }
            var directory = paths.ShipsDirectory(facility);
            var ledgerPath = paths.LedgerPath();
            var ledger = ledgerPath.Ok ? CraftLedger.Load(files, ledgerPath.FullPath) : null;

            IReadOnlyList<FileEntry> listed;
            try { listed = files.List(directory, Pure.CraftPaths.CraftExtension); } catch (IOException) { return EditorResult.Fail(OperationReasons.PathOutsideSave, "the Ships folder could not be read"); }
            int unusable = 0, linked = 0;
            var usable = new List<FileEntry>();
            foreach (var entry in listed)
            {
                var check = paths.ResolveExistingShip(facility, entry.Name);
                if (check.ReasonCode == "reparse_point") { linked++; continue; }
                if (!check.Ok) { unusable++; continue; }
                if (filter.Length > 0 && entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                usable.Add(entry);
            }
            usable = usable.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
            var page = usable.Skip(offset).Take(limit).ToList();
            var items = new JArray();
            foreach (var entry in page)
            {
                string sha = null;
                if (entry.Length <= MaxHashedBytes)
                {
                    try { sha = OperationHash.Sha256Hex(files.ReadAllBytes(entry.Path)); } catch (IOException) { sha = null; }
                }
                var owned = ledger != null && ledger.Usable && ledger.Find(facility, entry.Name) is LedgerEntry record && sha != null && string.Equals(record.Sha256, sha, StringComparison.Ordinal);
                items.Add(new JObject
                {
                    ["fileName"] = entry.Name, ["sizeBytes"] = entry.Length,
                    ["modifiedUtc"] = new DateTime(entry.WriteTicks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    ["sha256"] = sha == null ? JValue.CreateNull() : (JToken)sha,
                    ["kspControlOwned"] = owned
                });
            }
            var next = offset + page.Count;
            return EditorResult.Ok(new JObject
            {
                ["facility"] = facility, ["total"] = usable.Count, ["offset"] = offset, ["nextOffset"] = next < usable.Count ? (JToken)next : JValue.CreateNull(),
                ["crafts"] = items, ["skippedUnaddressableNames"] = unusable, ["skippedLinkedFiles"] = linked,
                ["ledger"] = ledger == null ? "unavailable" : ledger.State == LedgerState.Corrupt ? "corrupt" : ledger.State == LedgerState.Missing ? "missing" : "ok",
                ["ownedMeaning"] = "the ledger lists the file and its current hash equals the hash KspControl recorded when it wrote it"
            });
        }

        private static string Parse(JObject args, out string facility, out int offset, out int limit, out string filter)
        {
            facility = null; offset = 0; limit = ObservationLimits.DefaultPage; filter = "";
            var f = args["facility"];
            if (f == null || f.Type != JTokenType.String || ((string)f != "VAB" && (string)f != "SPH")) return "facility must be \"VAB\" or \"SPH\"";
            facility = (string)f;
            var token = args["offset"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.Integer) return "offset must be an integer";
                var v = (long)token; if (v < 0 || v > ObservationLimits.MaxOffset) return "offset must be 0.." + ObservationLimits.MaxOffset.ToString(CultureInfo.InvariantCulture);
                offset = (int)v;
            }
            token = args["limit"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.Integer) return "limit must be an integer";
                var v = (long)token; if (v < 1 || v > ObservationLimits.MaxPage) return "limit must be 1.." + ObservationLimits.MaxPage.ToString(CultureInfo.InvariantCulture);
                limit = (int)v;
            }
            token = args["filter"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.String) return "filter must be a string";
                filter = (string)token;
                if (filter.Length > ObservationLimits.MaxFilter) return "filter must be at most " + ObservationLimits.MaxFilter.ToString(CultureInfo.InvariantCulture) + " characters";
            }
            return null;
        }
    }
}
