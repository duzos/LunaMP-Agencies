using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KspControl.Contracts
{
    /// <summary>Observable grant states. The bridge computes them; the host only relays them.</summary>
    public static class GrantStates
    {
        public const string Missing = "missing";
        public const string Malformed = "malformed";
        public const string InvalidMac = "invalid_mac";
        public const string Expired = "expired";
        public const string Revoked = "revoked";
        public const string Suspended = "suspended";
        public const string BindingMismatch = "binding_mismatch";
        public const string NotYetApplicable = "not_yet_applicable";
        public const string Valid = "valid";
    }

    public sealed class GrantBindingInfo
    {
        [JsonProperty("installId", Order = 1, Required = Required.Always)] public string InstallId { get; set; }
        [JsonProperty("saveFolder", Order = 2, Required = Required.Always)] public string SaveFolder { get; set; }
        /// <summary>A LunaMP agency GUID string, or the sentinel <c>offline:&lt;saveFolder&gt;</c>.</summary>
        [JsonProperty("agency", Order = 3, Required = Required.Always)] public string Agency { get; set; }

        public bool SameAs(GrantBindingInfo other) => other != null
            && string.Equals(InstallId, other.InstallId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SaveFolder, other.SaveFolder, StringComparison.Ordinal)
            && string.Equals(Agency, other.Agency, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The signed grant content. Field order is fixed so the encoder is deterministic.</summary>
    public sealed class GrantPayload
    {
        public const int CurrentVersion = 1;
        [JsonProperty("version", Order = 1, Required = Required.Always)] public int Version { get; set; } = CurrentVersion;
        [JsonProperty("grantId", Order = 2, Required = Required.Always)] public string GrantId { get; set; }
        [JsonProperty("generation", Order = 3, Required = Required.Always)] public long Generation { get; set; }
        [JsonProperty("issuedUtc", Order = 4, Required = Required.Always)] public string IssuedUtc { get; set; }
        [JsonProperty("expiresUtc", Order = 5, Required = Required.Always)] public string ExpiresUtc { get; set; }
        [JsonProperty("binding", Order = 6, Required = Required.Always)] public GrantBindingInfo Binding { get; set; }
        [JsonProperty("operations", Order = 7, Required = Required.Always)] public string[] Operations { get; set; }
        [JsonProperty("facilities", Order = 8, Required = Required.Always)] public string[] Facilities { get; set; }
        [JsonProperty("unsavedCraftPolicy", Order = 9, Required = Required.Always)] public string UnsavedCraftPolicy { get; set; }
        [JsonProperty("maxParts", Order = 10, Required = Required.Always)] public int MaxParts { get; set; }
        [JsonProperty("spendLimitFunds", Order = 11, Required = Required.Always)] public long SpendLimitFunds { get; set; }
        [JsonProperty("revoked", Order = 12, Required = Required.Always)] public bool Revoked { get; set; }

        public static string FormatUtc(DateTime value) =>
            value.ToUniversalTime().ToString(GrantCodec.UtcFormat, CultureInfo.InvariantCulture);

        [JsonIgnore] public DateTime IssuedAt => GrantCodec.ParseUtc(IssuedUtc);
        [JsonIgnore] public DateTime ExpiresAt => GrantCodec.ParseUtc(ExpiresUtc);
    }

    /// <summary>Wire form: <c>{"payload":"&lt;base64&gt;","mac":"&lt;base64&gt;"}</c>.</summary>
    public sealed class GrantEnvelope
    {
        [JsonProperty("payload")] public string Payload { get; set; }
        [JsonProperty("mac")] public string Mac { get; set; }
    }

    /// <summary>Outcome of checking the envelope signature and decoding the payload. No binding or time checks.</summary>
    public sealed class GrantVerification
    {
        /// <summary>One of <see cref="GrantStates.Valid"/> (signature good), <see cref="GrantStates.Malformed"/>, <see cref="GrantStates.InvalidMac"/>.</summary>
        public string State { get; private set; }
        public string Detail { get; private set; }
        public GrantPayload Payload { get; private set; }
        public bool Ok => State == GrantStates.Valid;
        public static GrantVerification Fail(string state, string detail) => new GrantVerification { State = state, Detail = detail };
        public static GrantVerification Success(GrantPayload payload) => new GrantVerification { State = GrantStates.Valid, Payload = payload };
    }

    /// <summary>Encode, decode and verify grants. The MAC covers the exact decoded payload bytes; there is no canonicalisation.</summary>
    public static class GrantCodec
    {
        public const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        public const int KeyLength = 32;
        public const int MaxEnvelopeCharacters = 65536;
        public const int MaxPayloadBytes = 16384;
        private static readonly string[] UtcFormats = { UtcFormat, "yyyy-MM-dd'T'HH:mm:ss'Z'" };
        private static readonly string[] AllowedPolicies = { "refuse", "snapshot_then_replace" };
        private static readonly string[] AllowedFacilities = { "VAB", "SPH" };

        public static byte[] SerializePayload(GrantPayload payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var json = JsonConvert.SerializeObject(payload, Formatting.None);
            return new UTF8Encoding(false).GetBytes(json);
        }

        /// <summary>Produces the envelope JSON text for a payload. Deterministic for fixed inputs.</summary>
        public static string Encode(GrantPayload payload, byte[] key) => EncodeRaw(SerializePayload(payload), key);

        public static string EncodeRaw(byte[] payloadBytes, byte[] key)
        {
            if (payloadBytes == null) throw new ArgumentNullException(nameof(payloadBytes));
            RequireKey(key);
            var envelope = new GrantEnvelope { Payload = Convert.ToBase64String(payloadBytes), Mac = Convert.ToBase64String(Mac(key, payloadBytes)) };
            return JsonConvert.SerializeObject(envelope, Formatting.None);
        }

        public static byte[] Mac(byte[] key, byte[] payloadBytes)
        {
            using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(payloadBytes);
        }

        /// <summary>Checks the MAC first and parses the payload only if it verifies.</summary>
        public static GrantVerification Verify(string envelopeText, byte[] key)
        {
            if (string.IsNullOrWhiteSpace(envelopeText) || envelopeText.Length > MaxEnvelopeCharacters) return GrantVerification.Fail(GrantStates.Malformed, "envelope_size");
            if (key == null || key.Length != KeyLength) return GrantVerification.Fail(GrantStates.Missing, "key_unavailable");
            string payloadText, macText;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(envelopeText)) { MaxDepth = 8 })
                {
                    var token = JToken.ReadFrom(reader) as JObject;
                    if (token == null) return GrantVerification.Fail(GrantStates.Malformed, "envelope_not_object");
                    payloadText = token["payload"] != null && token["payload"].Type == JTokenType.String ? (string)token["payload"] : null;
                    macText = token["mac"] != null && token["mac"].Type == JTokenType.String ? (string)token["mac"] : null;
                }
            }
            catch (JsonException) { return GrantVerification.Fail(GrantStates.Malformed, "envelope_json"); }
            if (payloadText == null || macText == null) return GrantVerification.Fail(GrantStates.Malformed, "envelope_fields");
            byte[] payloadBytes, macBytes;
            try { payloadBytes = Convert.FromBase64String(payloadText); macBytes = Convert.FromBase64String(macText); }
            catch (FormatException) { return GrantVerification.Fail(GrantStates.Malformed, "envelope_base64"); }
            if (payloadBytes.Length == 0 || payloadBytes.Length > MaxPayloadBytes) return GrantVerification.Fail(GrantStates.Malformed, "payload_size");
            if (!FixedTimeEquals(Mac(key, payloadBytes), macBytes)) return GrantVerification.Fail(GrantStates.InvalidMac, null);
            return ParsePayload(payloadBytes);
        }

        /// <summary>Decodes payload bytes (caller has already verified the MAC, or is the issuing CLI).</summary>
        public static GrantVerification ParsePayload(byte[] payloadBytes)
        {
            try
            {
                var text = new UTF8Encoding(false, true).GetString(payloadBytes);
                GrantPayload payload;
                using (var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 8 })
                    payload = new JsonSerializer { TypeNameHandling = TypeNameHandling.None, CheckAdditionalContent = true }.Deserialize<GrantPayload>(reader);
                var problem = Validate(payload);
                return problem == null ? GrantVerification.Success(payload) : GrantVerification.Fail(GrantStates.Malformed, problem);
            }
            catch (JsonException) { return GrantVerification.Fail(GrantStates.Malformed, "payload_json"); }
            catch (DecoderFallbackException) { return GrantVerification.Fail(GrantStates.Malformed, "payload_utf8"); }
        }

        /// <summary>Returns null when the payload is acceptable, otherwise a short detail code.</summary>
        public static string Validate(GrantPayload p)
        {
            if (p == null) return "payload_null";
            if (p.Version != GrantPayload.CurrentVersion) return "version";
            if (!IsIdentifier(p.GrantId, 128)) return "grant_id";
            if (p.Generation <= 0) return "generation";
            DateTime issued, expires;
            if (!TryParseUtc(p.IssuedUtc, out issued) || !TryParseUtc(p.ExpiresUtc, out expires)) return "timestamps";
            if (expires <= issued) return "timestamps";
            var b = p.Binding;
            if (b == null || !IsText(b.InstallId, 64) || !IsText(b.SaveFolder, 256) || !IsText(b.Agency, 300)) return "binding";
            if (p.Operations == null || p.Operations.Length > 32 || p.Operations.Any(o => !IsIdentifier(o, 64, true))) return "operations";
            if (p.Facilities == null || p.Facilities.Length == 0 || p.Facilities.Length > 2 || p.Facilities.Any(f => Array.IndexOf(AllowedFacilities, f) < 0)) return "facilities";
            if (Array.IndexOf(AllowedPolicies, p.UnsavedCraftPolicy) < 0) return "unsaved_policy";
            if (p.MaxParts < 1 || p.MaxParts > 1000) return "max_parts";
            if (p.SpendLimitFunds < 0) return "spend_limit";
            return null;
        }

        public static bool TryParseUtc(string text, out DateTime value) =>
            DateTime.TryParseExact(text ?? "", UtcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);

        public static DateTime ParseUtc(string text)
        {
            DateTime value;
            if (!TryParseUtc(text, out value)) throw new FormatException("invalid_utc_timestamp");
            return value;
        }

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var difference = 0;
            for (var i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        private static void RequireKey(byte[] key)
        { if (key == null || key.Length != KeyLength) throw new ArgumentException("invalid_key_length"); }

        private static bool IsText(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

        private static bool IsIdentifier(string value, int max, bool allowDotAndColon = false)
        {
            if (string.IsNullOrEmpty(value) || value.Length > max) return false;
            foreach (var c in value)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '_' && c != '-' && !(allowDotAndColon && (c == '.' || c == ':' || c == '@'))) return false;
            return true;
        }
    }

    /// <summary>Computes the identity values a grant binds to. Shared by the CLI (issue) and the bridge (verify).</summary>
    public static class GrantBindingKey
    {
        public const string OfflinePrefix = "offline:";

        /// <summary>First 16 lowercase hex characters of SHA-256 over the upper-invariant full KSP root path.</summary>
        public static string InstallId(string kspRoot)
        {
            if (string.IsNullOrWhiteSpace(kspRoot)) throw new ArgumentException("invalid_root");
            var normal = Path.GetFullPath(kspRoot).TrimEnd('\\', '/').ToUpperInvariant();
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normal));
                var builder = new StringBuilder();
                for (var i = 0; i < 8; i++) builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        /// <summary>The agency GUID when connected, otherwise the sentinel <c>offline:&lt;saveFolder&gt;</c>.</summary>
        public static string AgencyKey(Guid agency, string saveFolder) =>
            agency == Guid.Empty ? OfflinePrefix + saveFolder : agency.ToString("D");
    }

    /// <summary>Turns a signature-checked grant into a status by applying revocation, suspension, time and binding.</summary>
    public static class GrantEvaluator
    {
        public static readonly TimeSpan IssuedSkew = TimeSpan.FromMinutes(5);

        public static GrantStatusInfo Evaluate(GrantVerification verification, DateTime nowUtc, GrantBindingInfo current,
            Func<string, long, bool> isSuspended, long highestSeenGeneration)
        {
            if (verification == null) throw new ArgumentNullException(nameof(verification));
            if (!verification.Ok) return new GrantStatusInfo { Present = verification.State != GrantStates.Missing, State = verification.State, Detail = verification.Detail };
            var p = verification.Payload;
            var status = new GrantStatusInfo
            {
                Present = true, Id = p.GrantId, Generation = p.Generation, Operations = (string[])p.Operations.Clone(), Facilities = (string[])p.Facilities.Clone(),
                UnsavedCraftPolicy = p.UnsavedCraftPolicy, ExpiresUtc = p.ExpiresUtc, SpendLimitFunds = p.SpendLimitFunds
            };
            if (p.Revoked) { status.State = GrantStates.Revoked; return status; }
            if (p.Generation < highestSeenGeneration) { status.State = GrantStates.Revoked; status.Detail = "generation_regressed"; return status; }
            if (nowUtc >= p.ExpiresAt) { status.State = GrantStates.Expired; return status; }
            if (isSuspended != null && isSuspended(p.GrantId, p.Generation)) { status.State = GrantStates.Suspended; return status; }
            if (p.IssuedAt > nowUtc + IssuedSkew) { status.State = GrantStates.NotYetApplicable; return status; }
            if (current == null || !p.Binding.SameAs(current)) { status.State = GrantStates.BindingMismatch; return status; }
            status.State = GrantStates.Valid; return status;
        }
    }
}
