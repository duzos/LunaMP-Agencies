using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Display names of tooled designs. A design shows its craft name wherever one is known: the stored tooling name, then the saved blueprint's
    /// name or its craft "ship = " line, then a local craft with the same fingerprint, and only then the fingerprint prefix.
    /// </summary>
    public static class ToolingDesignNames
    {
        public const int MaxLength = 80;
        /// <summary>Only the top of a craft file is scanned for its top-level "ship = " value.</summary>
        public const int MaxHeaderBytes = 64 * 1024;

        /// <summary>
        /// A safe display name: control characters become spaces, runs of white space collapse, trimmed, at most <see cref="MaxLength"/> characters
        /// (never splitting a surrogate pair). Null when nothing printable is left.
        /// </summary>
        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var builder = new StringBuilder(Math.Min(raw.Length, MaxLength * 2));
            var space = false;
            foreach (var c in raw)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = builder.Length > 0; continue; }
                if (space) { builder.Append(' '); space = false; }
                builder.Append(c);
                if (builder.Length >= MaxLength + 1) break;
            }
            if (builder.Length == 0) return null;
            if (builder.Length > MaxLength)
            {
                var cut = MaxLength;
                if (char.IsHighSurrogate(builder[cut - 1])) cut--;
                builder.Length = cut;
            }
            var text = builder.ToString().Trim();
            return text.Length == 0 ? null : text;
        }

        /// <summary>The craft's top-level "ship = " value, sanitized, or null. Values inside nodes (PART, MODULE ...) are ignored.</summary>
        public static string ShipNameFromCraft(byte[] craft)
        {
            if (craft == null || craft.Length == 0) return null;
            string text;
            try { text = new UTF8Encoding(false, false).GetString(craft, 0, Math.Min(craft.Length, MaxHeaderBytes)); }
            catch (Exception) { return null; }
            return ShipNameFromCraft(text);
        }

        public static string ShipNameFromCraft(string craft)
        {
            if (string.IsNullOrEmpty(craft)) return null;
            if (craft.Length > MaxHeaderBytes) craft = craft.Substring(0, MaxHeaderBytes);
            var depth = 0;
            using (var reader = new StringReader(craft))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (depth == 0)
                    {
                        var equals = trimmed.IndexOf('=');
                        if (equals > 0 && string.Equals(trimmed.Substring(0, equals).Trim(), "ship", StringComparison.Ordinal))
                            return Sanitize(trimmed.Substring(equals + 1));
                    }
                    foreach (var c in trimmed)
                    {
                        if (c == '{') depth++;
                        else if (c == '}' && depth > 0) depth--;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// The fingerprint of a craft from its PART "part" (or "name") values, as <see cref="ToolingPolicy.Fingerprint"/> computes it: physical part
        /// names only (the trailing _flightId is dropped), leaving out the names <paramref name="isScience"/> marks as science parts. Null when the
        /// list is not a valid manifest.
        /// </summary>
        public static string FingerprintFromPartIds(IEnumerable<string> partIds, Func<string, bool> isScience = null)
        {
            if (partIds == null) return null;
            try
            {
                var names = partIds.Select(PartName).ToArray();
                if (names.Length == 0 || names.Length > ToolingPolicy.MaxParts || names.Any(string.IsNullOrWhiteSpace)) return null;
                return ToolingPolicy.Fingerprint(new ToolingManifest { Parts = names.Select(n => new ToolingPart { Name = n, UnitCost = 0, IsScience = isScience != null && isScience(n) }).ToArray() });
            }
            catch (ArgumentException) { return null; }
        }

        /// <summary>
        /// The "part" (else "name") value of every top-level PART node of craft text, in order. Nested nodes (MODULE, STOREDPARTS ...) are skipped,
        /// so stored inventory parts are not counted, matching the tooling manifest's physical parts.
        /// </summary>
        public static List<string> PartIdsFromCraft(string craft)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(craft)) return ids;
            var depth = 0;
            string pendingNode = null, part = null, name = null;
            var inPart = false;
            using (var reader = new StringReader(craft))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    var comment = trimmed.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) trimmed = trimmed.Substring(0, comment).Trim();
                    if (trimmed.Length == 0) continue;
                    if (trimmed == "{")
                    {
                        if (depth == 0) { inPart = pendingNode == "PART"; part = name = null; }
                        depth++; pendingNode = null; continue;
                    }
                    if (trimmed == "}")
                    {
                        if (depth > 0) depth--;
                        if (depth == 0 && inPart) { ids.Add(part ?? name); inPart = false; }
                        pendingNode = null; continue;
                    }
                    var equals = trimmed.IndexOf('=');
                    if (equals < 0) { pendingNode = trimmed; continue; }
                    pendingNode = null;
                    if (depth != 1 || !inPart) continue;
                    var key = trimmed.Substring(0, equals).Trim();
                    if (key == "part" && part == null) part = trimmed.Substring(equals + 1).Trim();
                    else if (key == "name" && name == null) name = trimmed.Substring(equals + 1).Trim();
                }
            }
            return ids;
        }

        /// <summary>The fingerprint of craft text (see <see cref="FingerprintFromPartIds"/>), or null.</summary>
        public static string CraftFingerprint(string craft, Func<string, bool> isScience = null) => FingerprintFromPartIds(PartIdsFromCraft(craft), isScience);

        private static string PartName(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();
            var split = id.LastIndexOf('_');
            return split > 0 && uint.TryParse(id.Substring(split + 1), out _) ? id.Substring(0, split) : id;
        }

        /// <summary>The first usable name in priority order, sanitized, or null.</summary>
        public static string Resolve(params string[] candidates)
        {
            if (candidates == null) return null;
            foreach (var candidate in candidates)
            {
                var name = Sanitize(candidate);
                if (name != null) return name;
            }
            return null;
        }

        /// <summary>The short fingerprint shown beside a name for disambiguation, and alone when no name is known.</summary>
        public static string ShortFingerprint(string fingerprint) => string.IsNullOrEmpty(fingerprint) ? "" : fingerprint.Substring(0, Math.Min(8, fingerprint.Length));

        public static string Fallback(string fingerprint) => "Design " + ShortFingerprint(fingerprint);
    }
}
