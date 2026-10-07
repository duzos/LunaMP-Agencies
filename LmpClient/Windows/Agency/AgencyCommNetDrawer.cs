using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        private static Guid commNetSelected;
        private static string commNetSearch = string.Empty;
        private static Vector2 commNetOwnScroll, commNetTargetsScroll;
        private static GUIStyle commNetText, commNetButton, commNetToggle;
        private static Guid radarRangeVessel;
        private static DateTime radarRangeExpires;
        private static double radarRange;
        public static void ResetCommNetUi() { commNetSelected = Guid.Empty; commNetSearch = string.Empty; }

        private static void DrawCommNetTab()
        {
            if (commNetText == null)
            {
                commNetText = new GUIStyle(GUI.skin.label) { wordWrap = true, fixedWidth = 0, fixedHeight = 0, stretchWidth = true, richText = false };
                commNetButton = new GUIStyle(GUI.skin.button) { wordWrap = true };
                commNetToggle = new GUIStyle(GUI.skin.toggle) { wordWrap = false };
            }
            var settings = SettingsSystem.ServerSettings;
            var agreementsEnabled = settings.AgencyCommNetPerAgency && settings.AgencyCommNetOptIn;
            var radarEnabled = settings.AgencyHideCraft;
            if (!agreementsEnabled && !radarEnabled)
            {
                GUILayout.Label("Craft detection and CommNet agreements are disabled on this server.", commNetText);
                return;
            }
            var system = AgencySystem.Singleton;
            GUILayout.Label(radarEnabled ? "Radar and communications" : "CommNet agreements", GUI.skin.box);
            if (agreementsEnabled)
                GUILayout.Label("Your agency's craft always link. Foreign craft need agreement at both ends. Accept all gives your side's permission; the other craft must still agree.", commNetText);
            if (!system.CommNetReady) { GUILayout.Label("Craft settings are unavailable or still syncing.", commNetText); return; }
            if (agreementsEnabled && !LmpClient.Harmony.CommNet_AgencyFilter.Ready)
                GUILayout.Label("CommNet hook unavailable: " + LmpClient.Harmony.CommNet_AgencyFilter.DiagnosticReason, commNetText);
            var endpoints = system.GetCommNetEndpoints();
            var preferences = system.GetCommNetPreferences();
            var names = new Dictionary<Guid, string>();
            if (FlightGlobals.Vessels != null)
                foreach (var vessel in FlightGlobals.Vessels)
                    if (vessel != null && VisibilityClient.CanSee(vessel)) names[vessel.id] = vessel.vesselName ?? ShortVesselId(vessel.id);
            var own = endpoints.Values.Where(e => e.OwnerAgencyId == system.MyAgencyId && e.OwnerAgencyId != Guid.Empty)
                .OrderBy(e => VesselDisplayName(names, e.VesselId), StringComparer.OrdinalIgnoreCase).ToArray();
            if (!own.Any(e => e.VesselId == commNetSelected)) commNetSelected = own.FirstOrDefault()?.VesselId ?? Guid.Empty;
            if (own.Length == 0) { GUILayout.Label("Launch or claim a craft to configure its radar or relay agreements.", commNetText); return; }
            var compact = Screen.width < 620;
            var ownWidth = compact ? Mathf.Max(160, Singleton.WindowRect.width - 85) : 160;
            var targetWidth = Mathf.Max(150, Singleton.WindowRect.width - (compact ? 95 : 305));
            if (compact) GUILayout.BeginVertical(); else GUILayout.BeginHorizontal();
            if (compact) GUILayout.BeginVertical(GUI.skin.box, GUILayout.Height(105));
            else GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(190));
            GUILayout.Label("Your craft");
            commNetOwnScroll = GUILayout.BeginScrollView(commNetOwnScroll);
            foreach (var endpoint in own)
                if (GUILayout.Toggle(commNetSelected == endpoint.VesselId, new GUIContent(VesselDisplayName(names, endpoint.VesselId), VesselDisplayName(names, endpoint.VesselId)), commNetButton, GUILayout.Width(ownWidth)))
                    commNetSelected = endpoint.VesselId;
            GUILayout.EndScrollView(); GUILayout.EndVertical();
            GUILayout.BeginVertical();
            var source = endpoints[commNetSelected];
            preferences.TryGetValue(commNetSelected, out var mine);
            var validMine = mine != null && mine.Source.OwnerAgencyId == source.OwnerAgencyId && mine.Source.OwnershipRevision == source.OwnershipRevision;
            var acceptAll = validMine && mine.AcceptAll;
            GUILayout.Label(VesselDisplayName(names, commNetSelected), commNetText);
            if (radarEnabled)
            {
                var scanning = validMine && mine.ActiveScanning;
                var radarMode = GUILayout.Toolbar(scanning ? 1 : 0, new[] { "Passive listening", "Active radar" });
                if ((radarMode == 1) != scanning)
                    system.MessageSender.SendCommNetCommand(CommNetOperation.SetActiveScanning, commNetSelected, enabled: radarMode == 1);
                if (radarRangeVessel != commNetSelected || DateTime.UtcNow >= radarRangeExpires)
                {
                    radarRangeVessel = commNetSelected;
                    radarRangeExpires = DateTime.UtcNow.AddSeconds(1);
                    var vessel = FlightGlobals.Vessels?.FirstOrDefault(v => v && v.id == commNetSelected);
                    radarRange = vessel ? VisibilityPolicy.RadarRadius(VisibilitySensors.TotalPower(vessel), settings.AgencyDetectionRangeMultiplier, settings.AgencyActiveDetectionRangeMultiplier) : 0;
                }
                GUILayout.Label(radarRange > 0
                    ? $"Radar range {radarRange / 1000:N1} km, the same in both modes. Active: sees all craft in range and is heard by listeners whose range reaches you. Passive: hears only active craft. Planets block both."
                    : "No usable antenna detected. Radar needs an enabled antenna.", commNetText);
                GUILayout.Label("Radar mode does not change communications or sharing agreements.", commNetText);
            }
            if (agreementsEnabled)
            {
                var all = GUILayout.Toggle(acceptAll, "Accept all foreign craft");
                if (all != acceptAll) system.MessageSender.SendCommNetCommand(CommNetOperation.SetAcceptAll, commNetSelected, enabled: all);
                GUILayout.BeginHorizontal(); GUILayout.Label("Search", GUILayout.Width(48)); commNetSearch = GUILayout.TextField(commNetSearch); GUILayout.EndHorizontal();
                commNetTargetsScroll = GUILayout.BeginScrollView(commNetTargetsScroll);
                var count = 0;
                foreach (var target in endpoints.Values.Where(e => e.OwnerAgencyId != Guid.Empty && e.OwnerAgencyId != source.OwnerAgencyId && VisibilityClient.CanSee(e.VesselId))
                    .OrderBy(e => VesselDisplayName(names, e.VesselId), StringComparer.OrdinalIgnoreCase))
                {
                    var label = VesselDisplayName(names, target.VesselId) + " · " + OwnershipAgencyName(target.OwnerAgencyId);
                    if (!string.IsNullOrEmpty(commNetSearch) && label.IndexOf(commNetSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    count++;
                    var selected = validMine && mine.Targets.Any(t => t.VesselId == target.VesselId && t.OwnerAgencyId == target.OwnerAgencyId && t.OwnershipRevision == target.OwnershipRevision);
                    preferences.TryGetValue(target.VesselId, out var other);
                    var otherAgrees = other != null && other.Source.OwnerAgencyId == target.OwnerAgencyId && other.Source.OwnershipRevision == target.OwnershipRevision &&
                        (other.AcceptAll || other.Targets.Any(t => t.VesselId == source.VesselId && t.OwnerAgencyId == source.OwnerAgencyId && t.OwnershipRevision == source.OwnershipRevision));
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.BeginHorizontal(GUILayout.Width(targetWidth));
                    var next = GUILayout.Toggle(selected, new GUIContent(string.Empty, label), commNetToggle, GUILayout.Width(24), GUILayout.Height(24));
                    GUILayout.Label(new GUIContent(label, label), commNetText, GUILayout.Width(targetWidth - 32));
                    GUILayout.EndHorizontal();
                    if (next != selected) system.MessageSender.SendCommNetCommand(CommNetOperation.SetTarget, commNetSelected, target.VesselId, next);
                    GUILayout.Label((acceptAll || selected) && otherAgrees ? "Agreed · links available when in antenna range" : otherAgrees ? "They agree · enable your side to connect" : acceptAll || selected ? "Waiting for their agreement" : "No agreement", commNetText);
                    GUILayout.EndVertical();
                }
                if (count == 0) GUILayout.Label("No matching foreign craft.", commNetText);
                GUILayout.EndScrollView();
            }
            else GUILayout.Label("CommNet agreements are disabled on this server. Radar remains available.", commNetText);
            GUILayout.EndVertical();
            if (compact) GUILayout.EndVertical(); else GUILayout.EndHorizontal();
            if (system.LatestCommNetResult != null) GUILayout.Label("Server: " + system.LatestCommNetResult.Reason, commNetText);
            GUILayout.Label("Changes appear after server confirmation. Ownership changes reset radar mode and affected agreements.", commNetText);
        }
    }
}
