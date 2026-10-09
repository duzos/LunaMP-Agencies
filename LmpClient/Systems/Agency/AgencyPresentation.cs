using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using LmpCommon.Agency;
using UnityEngine;

namespace LmpClient.Systems.Agency
{
    /// <summary>
    /// One agency's cached presentation (plan 41 S0). Instances are reused per agency and refreshed in
    /// place, so callers may hold the reference for a frame but must not cache field values across
    /// identity changes. Main thread only.
    /// </summary>
    internal sealed class AgencyStyle
    {
        public Guid AgencyId;
        /// <summary>True only when the agency chose a colour.</summary>
        public bool HasColour;
        /// <summary>Raw agency colour for icons and accents; white when <see cref="HasColour"/> is false.</summary>
        public Color Colour = Color.white;
        /// <summary>Readability-lifted colour for text; white when <see cref="HasColour"/> is false.</summary>
        public Color TextColour = Color.white;
        /// <summary>"#RRGGBB" of <see cref="TextColour"/>, or null when <see cref="HasColour"/> is false.</summary>
        public string TextColourHex;
        /// <summary>The agency flag texture, or null when the flag is the stock default or not installed yet.</summary>
        public Texture2D Flag;
        /// <summary>Agency display name (raw, never rich text).</summary>
        public string Name = string.Empty;
        /// <summary>IMGUI content holding <see cref="Name"/>; draw with a <c>richText = false</c> style.</summary>
        public readonly GUIContent NameGuiContent = new GUIContent(string.Empty);
        /// <summary>TMP-safe caption prefix: <c>&lt;color=#hex&gt;&lt;noparse&gt;Name&lt;/noparse&gt;&lt;/color&gt;\n</c> (no colour tag without a colour).</summary>
        public string TmpPrefix = string.Empty;
        /// <summary>TMP-safe agency name: <c>&lt;noparse&gt;Name&lt;/noparse&gt;</c>.</summary>
        public string TmpName = string.Empty;

        internal int BuiltVersion = int.MinValue;
        internal string BuiltName;
        internal string FlagUrl;
        internal bool Known;
        // Per-frame frozen flag for GUILayout helpers (AgencyBadge) so Layout and Repaint agree.
        internal int LayoutFrame = -1;
        internal Texture2D LayoutFlag;
        // Per-frame frozen Known / HasColour for GUILayout callers (TryGetFrameAgencyStyle).
        internal int CheckedFrame = -1;
        internal bool FrameKnown;
        /// <summary><see cref="HasColour"/> as of the first <see cref="AgencyPresentation.TryGetFrameAgencyStyle"/> call this frame.</summary>
        internal bool FrameHasColour;
    }

    /// <summary>
    /// Shared, main-thread-only cache of agency flags, colours and names for every presentation surface
    /// (plan 41 S0). Rebuilt per agency only when <see cref="AgencyIdentityClient.Version"/> or the agency
    /// name changes; the hot paths are a dictionary lookup and a couple of compares, no allocation.
    /// </summary>
    internal static class AgencyPresentation
    {
        private sealed class FlagEntry { internal Texture2D Texture; internal long MissTicks; internal int DbCount; }

        private static readonly Dictionary<Guid, AgencyStyle> Styles = new Dictionary<Guid, AgencyStyle>();
        private static readonly Dictionary<string, FlagEntry> Flags = new Dictionary<string, FlagEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<(Guid Id, string[] Names)> PlayerSource = new List<(Guid Id, string[] Names)>();
        private static IReadOnlyDictionary<string, Guid> playerAgencies = new Dictionary<string, Guid>(StringComparer.Ordinal);
        private static long playerFingerprint = long.MinValue;
        private static float nextPlayerCheck = float.MinValue;
        private static int clearRequested;

        /// <summary>
        /// Drops every cached style, flag and player mapping. Safe from any thread: the work happens on the
        /// next main-thread access.
        /// </summary>
        internal static void Clear() => Interlocked.Exchange(ref clearRequested, 1);

        private static void ApplyPendingClear()
        {
            if (Volatile.Read(ref clearRequested) == 0 || Interlocked.Exchange(ref clearRequested, 0) == 0) return;
            Styles.Clear();
            Flags.Clear();
            PlayerSource.Clear();
            playerAgencies = new Dictionary<string, Guid>(StringComparer.Ordinal);
            playerFingerprint = long.MinValue;
            nextPlayerCheck = float.MinValue;
        }

        /// <summary>
        /// Style for an agency known to this client (listed in <c>KnownAgencies</c> or with a received
        /// identity). Returns false for <see cref="Guid.Empty"/> and unknown agencies. A returned style may
        /// still have no colour and no flag (default identity or a server without the identity protocol).
        /// </summary>
        internal static bool TryGetAgencyStyle(Guid agency, out AgencyStyle style)
        {
            style = null;
            if (agency == Guid.Empty) return false;
            ApplyPendingClear();
            if (!Styles.TryGetValue(agency, out style))
            {
                style = new AgencyStyle { AgencyId = agency };
                Styles[agency] = style;
            }
            var version = AgencyIdentityClient.Version;
            AgencyInfo info = null;
            var agencies = AgencySystem.Singleton?.KnownAgencies;
            if (agencies != null) agencies.TryGetValue(agency, out info);
            var name = info?.Name;
            if (style.BuiltVersion != version || !ReferenceEquals(style.BuiltName, name)) Rebuild(style, version, info);
            else if (style.FlagUrl != null && !style.Flag) style.Flag = GetFlagTexture(style.FlagUrl);
            return style.Known;
        }

        /// <summary>
        /// As <see cref="TryGetAgencyStyle"/>, but the result and <see cref="AgencyStyle.FrameHasColour"/> are frozen at
        /// the first call in a frame, so a GUILayout caller that adds or skips controls on them lays out the same
        /// controls for Layout and Repaint even if an identity update lands in between.
        /// </summary>
        internal static bool TryGetFrameAgencyStyle(Guid agency, out AgencyStyle style)
        {
            var known = TryGetAgencyStyle(agency, out style);
            if (style == null) return false;
            var frame = Time.frameCount;
            if (style.CheckedFrame != frame)
            {
                style.CheckedFrame = frame;
                style.FrameKnown = known;
                style.FrameHasColour = known && style.HasColour;
            }
            return style.FrameKnown;
        }

        /// <summary>
        /// Style for a vessel's owning agency when <see cref="AgencyPresentationPolicy.ShouldStyle"/> passes
        /// with <c>featureOn</c> and <see cref="VisibilityClient.CanSee(Vessel)"/>. Callers pass their own
        /// feature toggle.
        /// </summary>
        internal static bool TryGetVesselStyle(Vessel vessel, bool featureOn, out AgencyStyle style)
        {
            style = null;
            if (!featureOn || !vessel) return false;
            var owner = AgencySystem.Singleton?.GetVesselAgency(vessel.id) ?? Guid.Empty;
            if (!AgencyPresentationPolicy.ShouldStyle(featureOn, owner, owner != Guid.Empty && VisibilityClient.CanSee(vessel))) return false;
            return TryGetAgencyStyle(owner, out style);
        }

        /// <summary><see cref="TryGetVesselStyle(Vessel, bool, out AgencyStyle)"/> with the feature treated as on; the caller gates its own toggle.</summary>
        internal static bool TryGetVesselStyle(Vessel vessel, out AgencyStyle style) => TryGetVesselStyle(vessel, true, out style);

        private static void Rebuild(AgencyStyle style, int version, AgencyInfo info)
        {
            style.BuiltVersion = version;
            style.BuiltName = info?.Name;
            var supported = AgencyIdentityClient.Supported;
            var identity = supported ? AgencyIdentityClient.Get(style.AgencyId) : null;
            style.Known = info != null || (identity != null && identity.Revision > 0);
            style.Name = string.IsNullOrEmpty(info?.Name) ? "Agency" : info.Name;
            style.NameGuiContent.text = style.Name;
            style.TmpName = AgencyPresentationPolicy.EscapeTmp(style.Name);

            style.HasColour = AgencyPresentationPolicy.ShowColour(identity);
            if (style.HasColour)
            {
                style.Colour = new Color32(identity.Red, identity.Green, identity.Blue, 255);
                var (r, g, b) = AgencyPresentationPolicy.ReadableTextColour(identity.Red, identity.Green, identity.Blue);
                style.TextColour = new Color32(r, g, b, 255);
                style.TextColourHex = AgencyPresentationPolicy.ColourHex(r, g, b);
                style.TmpPrefix = "<color=" + style.TextColourHex + ">" + style.TmpName + "</color>\n";
            }
            else
            {
                style.Colour = Color.white;
                style.TextColour = Color.white;
                style.TextColourHex = null;
                style.TmpPrefix = style.TmpName + "\n";
            }

            style.FlagUrl = AgencyPresentationPolicy.ShowFlag(identity) ? identity.FlagUrl : null;
            style.Flag = style.FlagUrl == null ? null : GetFlagTexture(style.FlagUrl);
        }

        /// <summary>
        /// Cached <see cref="GameDatabase.GetTexture"/> (a linear scan). A miss is stored and retried only
        /// when the texture database count changes or five seconds pass. Returns null on a miss.
        /// </summary>
        internal static Texture2D GetFlagTexture(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            ApplyPendingClear();
            if (Flags.TryGetValue(url, out var entry) && entry.Texture) return entry.Texture;
            var database = GameDatabase.Instance;
            if (database == null) return null;
            var count = database.databaseTexture?.Count ?? 0;
            var now = DateTime.UtcNow.Ticks;
            if (entry != null && !AgencyPresentationPolicy.ShouldRetryTexture(entry.MissTicks, now, entry.DbCount, count)) return null;
            if (entry == null)
            {
                if (!AgencyIdentityDefaults.IsSafeFlagUrl(url)) return null;
                entry = new FlagEntry();
                Flags[url] = entry;
            }
            entry.Texture = database.GetTexture(url, false);
            entry.MissTicks = now;
            entry.DbCount = count;
            return entry.Texture ? entry.Texture : null;
        }

        /// <summary>
        /// Agency of a player by LMP display name, or <see cref="Guid.Empty"/> when unknown or claimed by two
        /// agencies. The map is rebuilt at most once per second, and only when the membership fingerprint changes.
        /// </summary>
        internal static Guid GetPlayerAgency(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return Guid.Empty;
            ApplyPendingClear();
            var now = Time.unscaledTime;
            if (now >= nextPlayerCheck)
            {
                nextPlayerCheck = now + 1f;
                RefreshPlayers();
            }
            return playerAgencies.TryGetValue(playerName, out var agency) ? agency : Guid.Empty;
        }

        private static void RefreshPlayers()
        {
            var agencies = AgencySystem.Singleton?.KnownAgencies;
            if (agencies == null) return;
            long count = 0, members = 0, hash = 17;
            foreach (var pair in agencies)
            {
                var info = pair.Value;
                if (info == null) continue;
                count++;
                members += info.MemberDisplayNames?.Length ?? 0;
                // Order-independent: the dictionary may enumerate in any order.
                hash += pair.Key.GetHashCode() * 31L + RuntimeHelpers.GetHashCode(info) + (info.MemberDisplayNames == null ? 0 : RuntimeHelpers.GetHashCode(info.MemberDisplayNames));
            }
            var fingerprint = unchecked(count * 1000003L ^ members * 7919L ^ hash);
            if (fingerprint == playerFingerprint) return;
            playerFingerprint = fingerprint;
            PlayerSource.Clear();
            foreach (var pair in agencies)
                if (pair.Value != null) PlayerSource.Add((pair.Key, pair.Value.MemberDisplayNames));
            playerAgencies = AgencyPresentationPolicy.ResolvePlayerAgencies(PlayerSource);
            PlayerSource.Clear();
        }
    }
}
