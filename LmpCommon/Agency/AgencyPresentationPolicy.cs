using System;
using System.Collections.Generic;
using System.Text;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Pure rules for drawing agency flags, colours and names on the client (plan 41 S0).
    /// No Unity types so every rule is unit-testable.
    /// </summary>
    public static class AgencyPresentationPolicy
    {
        public const int MaxEscapedNameLength = 64;
        public const double DefaultMinLuminance = 0.45;
        public static readonly long TextureRetryTicks = TimeSpan.FromSeconds(5).Ticks;
        public const float MinNameplateRangeKm = 0.2f, MaxNameplateRangeKm = 25f, DefaultNameplateRangeKm = 2.5f;
        private const string NoParseOpen = "<noparse>", NoParseClose = "</noparse>";

        /// <summary>A vessel gets agency styling only when the feature is on, it has an owner and the viewer can fully see it.</summary>
        public static bool ShouldStyle(bool featureOn, Guid owner, bool canSee) => featureOn && owner != Guid.Empty && canSee;

        /// <summary>Draw a flag only for a safe URL that is not the stock default flag.</summary>
        public static bool ShowFlag(AgencyIdentityInfo identity) =>
            identity != null && AgencyIdentityDefaults.IsSafeFlagUrl(identity.FlagUrl) &&
            !string.Equals(identity.FlagUrl, AgencyIdentityDefaults.DefaultFlagUrl, StringComparison.OrdinalIgnoreCase);

        /// <summary>Apply a colour only when the agency chose one.</summary>
        public static bool ShowColour(AgencyIdentityInfo identity) => identity != null && identity.HasColour;

        public static string ColourHex(byte r, byte g, byte b)
        {
            const string digits = "0123456789ABCDEF";
            return new string(new[] { '#', digits[r >> 4], digits[r & 15], digits[g >> 4], digits[g & 15], digits[b >> 4], digits[b & 15] });
        }

        /// <summary>Gamma-space Rec.601 luma in [0, 1].</summary>
        public static double Luma(byte r, byte g, byte b) => (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;

        /// <summary>
        /// Lifts a colour's luma to at least <paramref name="minLuminance"/> by blending it toward white,
        /// which keeps the hue. Colours already bright enough are returned unchanged.
        /// </summary>
        public static (byte R, byte G, byte B) ReadableTextColour(byte r, byte g, byte b, double minLuminance = DefaultMinLuminance)
        {
            if (double.IsNaN(minLuminance)) minLuminance = DefaultMinLuminance;
            minLuminance = Math.Max(0, Math.Min(1, minLuminance));
            var luma = Luma(r, g, b);
            if (luma >= minLuminance) return (r, g, b);
            // Luma is linear in the blend factor: luma(t) = luma + (1 - luma) * t.
            var t = (minLuminance - luma) / (1 - luma);
            return (Lift(r, t), Lift(g, t), Lift(b, t));
        }

        private static byte Lift(byte c, double t) => (byte)Math.Min(255, Math.Ceiling(c + (255 - c) * t));

        /// <summary>
        /// Makes user text inert in TextMeshPro rich text: drops control characters, removes every
        /// <c>&lt;/noparse&gt;</c> (any case, repeated until none can reassemble), caps the length and
        /// wraps the result in <c>&lt;noparse&gt;</c>.
        /// </summary>
        public static string EscapeTmp(string text)
        {
            var value = text ?? string.Empty;
            if (value.Length > 4096) value = value.Substring(0, 4096);
            var builder = new StringBuilder(value.Length);
            foreach (var c in value) builder.Append(char.IsControl(c) ? ' ' : c);
            value = builder.ToString();
            int index;
            while ((index = value.IndexOf(NoParseClose, StringComparison.OrdinalIgnoreCase)) >= 0)
                value = value.Remove(index, NoParseClose.Length);
            if (value.Length > MaxEscapedNameLength)
            {
                var length = MaxEscapedNameLength;
                if (char.IsHighSurrogate(value[length - 1])) length--;
                value = value.Substring(0, length);
            }
            return NoParseOpen + value + NoParseClose;
        }

        /// <summary>
        /// Display name to agency. Names claimed by two different agencies are dropped. Ordinal and
        /// case-sensitive like LMP player names.
        /// </summary>
        public static IReadOnlyDictionary<string, Guid> ResolvePlayerAgencies(IEnumerable<(Guid Id, string[] Names)> agencies)
        {
            var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
            if (agencies == null) return result;
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var agency in agencies)
            {
                if (agency.Id == Guid.Empty || agency.Names == null) continue;
                foreach (var name in agency.Names)
                {
                    if (string.IsNullOrEmpty(name) || ambiguous.Contains(name)) continue;
                    if (result.TryGetValue(name, out var existing))
                    {
                        if (existing == agency.Id) continue;
                        result.Remove(name);
                        ambiguous.Add(name);
                        continue;
                    }
                    result[name] = agency.Id;
                }
            }
            return result;
        }

        /// <summary>Nameplate range setting, clamped to 0.2 - 25 km; NaN or infinity falls back to the default.</summary>
        public static float ClampNameplateRangeKm(float km) =>
            float.IsNaN(km) || float.IsInfinity(km) ? DefaultNameplateRangeKm : Math.Max(MinNameplateRangeKm, Math.Min(MaxNameplateRangeKm, km));

        /// <summary>
        /// A missing flag texture is looked up again only when the texture database grew or shrank, or
        /// <see cref="TextureRetryTicks"/> passed (or the clock went backwards).
        /// </summary>
        public static bool ShouldRetryTexture(long lastMissTicks, long nowTicks, int lastDbCount, int dbCount) =>
            dbCount != lastDbCount || nowTicks < lastMissTicks || nowTicks - lastMissTicks >= TextureRetryTicks;
    }
}
