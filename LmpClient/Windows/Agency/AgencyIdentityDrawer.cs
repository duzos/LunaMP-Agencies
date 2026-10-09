using System;
using System.Linq;
using LmpClient.Network;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.Flag;
using LmpClient.Systems.PlayerColorSys;
using LmpCommon.Agency;
using LmpCommon.Flags;
using LmpCommon.Message.Data.Agency;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        private static Guid identityDraftAgency;
        private static object identityDraftGame;
        internal static void ResetIdentityEditor() { identityDraftAgency = Guid.Empty; identityDraftGame = null; identityEditing = false; identityFlags = false; identityStatus = null; identitySubmitted = null; }
        private static long identityDraftRevision;
        private static AgencyIdentityInfo identitySubmitted;
        private static bool identityEditing, identityHasColour, identityFlags;
        private static Color identityColour = Color.white;
        private static string identityFlag = AgencyIdentityDefaults.DefaultFlagUrl;
        private static string identityStatus;
        private static int identityFlagPage;
        private const int FlagPageSize = 12;

        // The accent, rather than the label text, uses the chosen RGB so names stay readable.
        private static void DrawIdentityLabel(Guid id, string text)
        {
            GUILayout.BeginHorizontal();
            // Flag slot keeps its width even when the agency has no flag so rows stay aligned.
            if (SettingsSystem.CurrentSettings.AgencyWindowFlags) AgencyBadge.DrawFlag(id, 32, 20, true);
            if (AgencyPresentation.TryGetAgencyStyle(id, out var style) && style.HasColour)
            {
                var previous = GUI.color;
                try
                {
                    GUI.color = style.Colour;
                    GUILayout.Label(Texture2D.whiteTexture, GUILayout.Width(8), GUILayout.Height(20));
                }
                finally { GUI.color = previous; }
            }
            GUILayout.Label(text);
            GUILayout.EndHorizontal();
        }
        private static void LoadIdentityDraft(Guid id)
        {
            var value = AgencyIdentityClient.Get(id);
            identityDraftAgency = id;
            identityDraftGame = HighLogic.CurrentGame;
            identityDraftRevision = value.Revision;
            identityHasColour = value.HasColour;
            identityColour = value.HasColour ? (Color)new Color32(value.Red, value.Green, value.Blue, 255) : Color.white;
            identityFlag = value.FlagUrl;
            identityFlags = false;
            identityFlagPage = 0;
            identityFlagQuery = string.Empty;
            pendingFlagUrl = null;
            identityStatus = null;
            identitySubmitted = null;
        }
        private static void DrawIdentityEditor(AgencyInfo agency)
        {
            if (!AgencyIdentityClient.Supported || !AgencySystem.Singleton.AmIOwnerOfMine())
            { identityEditing = false; identityDraftAgency = Guid.Empty; return; }
            if (identityDraftAgency != agency.Id || !ReferenceEquals(identityDraftGame, HighLogic.CurrentGame))
            { LoadIdentityDraft(agency.Id); identityEditing = false; }
            if (GUILayout.Button(identityEditing ? "Close agency appearance" : "Customize agency flag and colour"))
                identityEditing = !identityEditing;
            if (!identityEditing) return;
            var latest = AgencyIdentityClient.Get(agency.Id);
            CheckPendingFlagUpload(agency.Id);
            if (identitySubmitted != null && latest.Revision > identitySubmitted.Revision && SameAppearance(latest, identitySubmitted))
            {
                // Preserve any draft edits made after Save while accepting the acknowledged revision.
                identityDraftRevision = latest.Revision;
                identitySubmitted = null;
                identityStatus = "Agency appearance saved.";
            }
            if (latest.Revision != identityDraftRevision)
            {
                GUILayout.Label("Agency appearance changed. Reload before saving again.");
                if (GUILayout.Button("Reload saved appearance")) LoadIdentityDraft(agency.Id);
            }
            GUILayout.Label("Flag: " + identityFlag);
            if (GUILayout.Button(identityFlags ? "Close flag choices" : "Choose flag")) identityFlags = !identityFlags;
            if (identityFlags) DrawIdentityFlagChoices();
            identityHasColour = GUILayout.Toggle(identityHasColour, "Use an agency colour");
            if (identityHasColour)
            {
                identityColour.r = IdentitySlider("Red", identityColour.r);
                identityColour.g = IdentitySlider("Green", identityColour.g);
                identityColour.b = IdentitySlider("Blue", identityColour.b);
                var previous = GUI.color;
                try { GUI.color = identityColour; GUILayout.Label(Texture2D.whiteTexture, GUILayout.Width(64), GUILayout.Height(16)); }
                finally { GUI.color = previous; }
                if (GUILayout.Button("Random colour")) identityColour = PlayerColorSystem.GenerateRandomColor();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to defaults"))
            { identityHasColour = false; identityFlag = AgencyIdentityDefaults.DefaultFlagUrl; }
            var enabled = GUI.enabled;
            try
            {
                GUI.enabled = enabled && latest.Revision == identityDraftRevision && pendingFlagUrl == null;
                if (GUILayout.Button("Save agency appearance")) SaveIdentity(agency.Id);
            }
            finally { GUI.enabled = enabled; }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(identityStatus)) GUILayout.Label(identityStatus);
        }
        private static bool SameAppearance(AgencyIdentityInfo a, AgencyIdentityInfo b) =>
            a.FlagUrl == b.FlagUrl && a.HasColour == b.HasColour &&
            (!a.HasColour || (a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue));
        private static float IdentitySlider(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(50));
            value = GUILayout.HorizontalSlider(value, 0, 1);
            GUILayout.Label(((int)(value * 255)).ToString(), GUILayout.Width(30));
            GUILayout.EndHorizontal();
            return value;
        }
        // Every flag texture KSP loaded from a "Flags" folder (what the stock flag browser lists via
        // GameDatabase.GetAllTexturesInFolderType("Flags", true)), plus stock defaults and server-synced flags.
        private static string[] flagChoices = new string[0], flagFiltered = new string[0];
        private static System.Collections.Generic.Dictionary<string, Texture2D> flagThumbs = new System.Collections.Generic.Dictionary<string, Texture2D>();
        private static string pendingFlagUrl;
        private static float pendingFlagSince;
        private const float FlagUploadTimeout = 10f;
        private static int flagCacheServerCount = -1, flagCacheTextureCount = -1;
        private static string identityFlagQuery = string.Empty, flagAppliedQuery;

        // Offer stock flags, flags already on the server, and local PNG flags the server will accept (<= 1 MB).
        // File checks run only when the cache is rebuilt, never per frame.
        internal static string[] BuildFlagChoices()
        {
            var urls = new System.Collections.Generic.HashSet<string>(DefaultFlags.DefaultFlagList, StringComparer.Ordinal);
            foreach (var key in FlagSystem.Singleton.ServerFlags.Keys) urls.Add(key);
            var thumbs = new System.Collections.Generic.Dictionary<string, Texture2D>(StringComparer.Ordinal);
            var db = GameDatabase.Instance;
            if (db != null)
                foreach (var info in db.GetAllTexturesInFolderType("Flags", true))
                {
                    if (info == null || info.isNormalMap || info.name == null) continue;
                    thumbs[info.name] = info.texture;
                    if (!urls.Contains(info.name) && AgencyIdentityDefaults.IsSafeFlagUrl(info.name) && FlagSystem.IsShareableLocalFlag(info.name))
                        urls.Add(info.name);
                }
            flagThumbs = thumbs;
            return urls.Where(AgencyIdentityDefaults.IsSafeFlagUrl).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        }

        // Called on Layout from the editor: completes or fails a save that was waiting for a flag upload.
        private static void CheckPendingFlagUpload(Guid agencyId)
        {
            if (pendingFlagUrl == null || Event.current == null || Event.current.type != EventType.Layout) return;
            if (FlagSystem.Singleton.ServerFlags.ContainsKey(pendingFlagUrl))
            {
                pendingFlagUrl = null;
                if (identityEditing) SaveIdentity(agencyId);
            }
            else if (Time.realtimeSinceStartup - pendingFlagSince > FlagUploadTimeout)
            {
                pendingFlagUrl = null;
                identityStatus = "Upload failed. The server did not accept the flag; choose another flag.";
            }
        }

        // Only refreshed on Layout so the control count is identical for Layout and Repaint.
        private static void RefreshFlagChoices()
        {
            var serverCount = FlagSystem.Singleton.ServerFlags.Count;
            var textureCount = GameDatabase.Instance?.databaseTexture?.Count ?? 0;
            var rebuilt = serverCount != flagCacheServerCount || textureCount != flagCacheTextureCount;
            if (rebuilt)
            {
                flagCacheServerCount = serverCount; flagCacheTextureCount = textureCount;
                flagChoices = BuildFlagChoices();
            }
            if (rebuilt || flagAppliedQuery != identityFlagQuery)
            {
                flagAppliedQuery = identityFlagQuery;
                var q = (identityFlagQuery ?? string.Empty).Trim();
                flagFiltered = q.Length == 0 ? flagChoices
                    : flagChoices.Where(f => f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                identityFlagPage = 0;
            }
        }
        private static void DrawIdentityFlagChoices()
        {
            if (Event.current == null || Event.current.type == EventType.Layout || flagAppliedQuery == null) RefreshFlagChoices();
            var flags = flagFiltered;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(50));
            identityFlagQuery = GUILayout.TextField(identityFlagQuery ?? string.Empty, 64);
            GUILayout.Label(flags.Length + " flags", GUILayout.Width(70));
            GUILayout.EndHorizontal();
            var pages = Math.Max(1, (flags.Length + FlagPageSize - 1) / FlagPageSize);
            identityFlagPage = Math.Max(0, Math.Min(identityFlagPage, pages - 1));
            for (var i = identityFlagPage * FlagPageSize; i < Math.Min(flags.Length, (identityFlagPage + 1) * FlagPageSize); i++)
            {
                var url = flags[i];
                GUILayout.BeginHorizontal();
                flagThumbs.TryGetValue(url, out var texture);
                GUILayout.Label(texture ? (Texture)texture : Texture2D.blackTexture, GUILayout.Width(32), GUILayout.Height(20));
                if (GUILayout.Button(url)) { identityFlag = url; identityFlags = false; }
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Previous")) identityFlagPage = Math.Max(0, identityFlagPage - 1);
            GUILayout.Label((identityFlagPage + 1) + " / " + pages);
            if (GUILayout.Button("Next")) identityFlagPage = Math.Min(pages - 1, identityFlagPage + 1);
            GUILayout.EndHorizontal();
            GUILayout.Label("Only PNG flags of 1 MB or less can be shared; new ones upload when you save.");
        }
        private static void SaveIdentity(Guid id)
        {
            if (!AgencyIdentityClient.Supported || id != AgencySystem.Singleton.MyAgencyId || !AgencySystem.Singleton.AmIOwnerOfMine()) return;
            var hash = string.Empty;
            if (!AgencyIdentityDefaults.IsStockFlag(identityFlag))
            {
                if (!FlagSystem.Singleton.ServerFlags.TryGetValue(identityFlag, out var flag))
                {
                    // Installed locally but not yet on the server: upload once, then finish the save when the server echoes it.
                    if (pendingFlagUrl == identityFlag) return;
                    if (!FlagSystem.IsShareableLocalFlag(identityFlag) || !FlagSystem.Singleton.TrySendFlag(identityFlag))
                    { identityStatus = "This flag can't be shared (only PNG flags of 1 MB or less)."; return; }
                    pendingFlagUrl = identityFlag; pendingFlagSince = Time.realtimeSinceStartup;
                    identityStatus = "Uploading flag to the server; the appearance saves automatically.";
                    return;
                }
                hash = flag.ShaSum.Replace("-", string.Empty);
            }
            var colour = (Color32)identityColour;
            var data = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencySetIdentityMsgData>();
            data.AgencyId = id;
            data.ExpectedRevision = identityDraftRevision;
            data.HasColour = identityHasColour;
            data.Red = colour.r; data.Green = colour.g; data.Blue = colour.b;
            data.FlagUrl = identityFlag; data.FlagSha256 = hash;
            identitySubmitted = new AgencyIdentityInfo { AgencyId = id, Revision = identityDraftRevision,
                HasColour = identityHasColour, Red = colour.r, Green = colour.g, Blue = colour.b, FlagUrl = identityFlag };
            AgencySystem.Singleton.MessageSender.SendMessage(data);
            identityStatus = "Appearance request sent; the server reply appears above.";
        }
    }
}
