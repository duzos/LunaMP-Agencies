using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace KspControl.EditorModel
{
    /// <summary>Parsed editor revision token: base64url of "v1|epoch|generation|editRevision|fp[0..12]".</summary>
    public sealed class EditorRevisionToken
    {
        /// <summary>Longest encodable token: "v1|" + 40-char epoch + two 19-digit counters + 12-char fingerprint is 96 bytes, 128 base64url characters.</summary>
        public const int MaxLength = 128;
        private static readonly Regex Base64Url = new Regex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
        private static readonly Regex Epoch = new Regex("^[A-Za-z0-9_.:-]{1,40}$", RegexOptions.CultureInvariant);
        private static readonly Regex Fp = new Regex("^[0-9a-f]{12}$", RegexOptions.CultureInvariant);
        public string WorldEpoch { get; private set; }
        public long Generation { get; private set; }
        public long EditRevision { get; private set; }
        public string FingerprintPrefix { get; private set; }
        public static EditorRevisionToken Create(string worldEpoch, long generation, long editRevision, string fingerprintHex)
        {
            if (worldEpoch == null || !Epoch.IsMatch(worldEpoch)) throw new ArgumentException("invalid_epoch");
            if (generation < 0 || editRevision < 0) throw new ArgumentException("negative_counter");
            if (fingerprintHex == null || fingerprintHex.Length < 12) throw new ArgumentException("invalid_fingerprint");
            var fp = fingerprintHex.Substring(0, 12).ToLowerInvariant();
            if (!Fp.IsMatch(fp)) throw new ArgumentException("invalid_fingerprint");
            return new EditorRevisionToken { WorldEpoch = worldEpoch, Generation = generation, EditRevision = editRevision, FingerprintPrefix = fp };
        }
        public string Encode()
        {
            var raw = "v1|" + WorldEpoch + "|" + Generation.ToString(CultureInfo.InvariantCulture) + "|" + EditRevision.ToString(CultureInfo.InvariantCulture) + "|" + FingerprintPrefix;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        public override string ToString() { return Encode(); }
        /// <summary>
        /// Parses a token. The token is unsigned, so forgery here means "not exactly what Encode would produce":
        /// wrong version, padding or non-canonical encoding, extra or missing fields, bad numbers or fingerprint length.
        /// </summary>
        public static bool TryParse(string text, out EditorRevisionToken token)
        {
            token = null;
            if (string.IsNullOrEmpty(text) || text.Length > MaxLength || !Base64Url.IsMatch(text)) return false;
            string raw;
            try
            {
                var b64 = text.Replace('-', '+').Replace('_', '/');
                switch (b64.Length % 4) { case 2: b64 += "=="; break; case 3: b64 += "="; break; case 1: return false; }
                raw = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(b64));
            }
            catch (FormatException) { return false; }
            catch (ArgumentException) { return false; }
            var f = raw.Split('|');
            if (f.Length != 5 || f[0] != "v1") return false;
            long gen, rev;
            if (!long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out gen) || !long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out rev)) return false;
            EditorRevisionToken t;
            try { t = Create(f[1], gen, rev, f[4]); } catch (ArgumentException) { return false; }
            if (f[4].Length != 12) return false;
            if (!string.Equals(t.Encode(), text, StringComparison.Ordinal)) return false; // canonical form only (rejects altered padding bits and leading zeros)
            token = t; return true;
        }
        public bool Matches(string worldEpoch, long generation, long editRevision, string fingerprintHex)
        {
            return WorldEpoch == worldEpoch && Generation == generation && EditRevision == editRevision
                && fingerprintHex != null && fingerprintHex.Length >= 12 && string.Equals(FingerprintPrefix, fingerprintHex.Substring(0, 12), StringComparison.OrdinalIgnoreCase);
        }
    }
}
