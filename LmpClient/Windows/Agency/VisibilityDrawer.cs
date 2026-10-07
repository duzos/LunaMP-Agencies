using System;
using System.Linq;
using LmpClient.Systems.Agency;
using LmpCommon.Agency;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        private static Guid visibilityCraft, visibilityAgency;
        private static int visibilityMode;
        private static string visibilitySearch = "", visibilityCraftSearch = "";
        private static Vector2 visibilityScroll, visibilityCraftScroll;
        private static GUIStyle visibilityText, visibilityHeading, visibilityButton;
        private static readonly string[] VisibilityRuleLabels = { "Inherit", "Share", "Private" };
        private static void DrawVisibilityTab()
        {
            if (visibilityText == null)
            {
                visibilityText = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
                visibilityHeading = new GUIStyle(visibilityText) { fontStyle = FontStyle.Bold };
                visibilityButton = new GUIStyle(GUI.skin.button) { wordWrap = true, richText = false };
            }
            if (!VisibilityClient.Enabled) { GUILayout.Label("Craft hiding is disabled on this server.", visibilityText); return; }
            if (!string.IsNullOrEmpty(VisibilityClient.DiagnosticReason)) GUILayout.Label(VisibilityClient.DiagnosticReason, visibilityText);
            if (visibilityAgency != AgencySystem.Singleton.MyAgencyId) { visibilityAgency = AgencySystem.Singleton.MyAgencyId; visibilityCraft = Guid.Empty; }
            if (!VisibilityClient.Ready) { GUILayout.Label("Waiting for craft visibility and ownership data...", visibilityText); return; }
            GUILayout.Label("Choose who can see your agency's craft", visibilityHeading);
            GUILayout.Label("Private craft appear within antenna detection range with a clear planet/moon sight line, or at close physics range. Detection updates as craft move; sharing reveals them at any distance.", visibilityText);
            if (!string.IsNullOrEmpty(VisibilityClient.LatestStatus)) GUILayout.Label(VisibilityClient.LatestStatus, visibilityText);
            visibilityMode = GUILayout.Toolbar(visibilityMode, new[] { "All agency craft", "Individual craft" });
            var owner = AgencySystem.Singleton.AmIOwnerOfMine();
            if (!owner) GUILayout.Label("Only your agency owner can change sharing.", visibilityText);
            var endpoints = VisibilityClient.GetEndpointsSnapshot();
            if (visibilityMode == 1)
            {
                GUILayout.Label("Choose a craft", visibilityHeading);
                visibilityCraftSearch = GUILayout.TextField(visibilityCraftSearch, 80);
                visibilityCraftScroll = GUILayout.BeginScrollView(visibilityCraftScroll, GUILayout.Height(110));
                var found = false;
                foreach (var endpoint in endpoints.Where(e => e.OwnerAgencyId == visibilityAgency))
                {
                    var name = VisibilityCraftName(endpoint.VesselId);
                    if (name.IndexOf(visibilityCraftSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    found = true;
                    if (GUILayout.Toggle(visibilityCraft == endpoint.VesselId, name, visibilityButton)) visibilityCraft = endpoint.VesselId;
                }
                if (!found) GUILayout.Label("No matching craft owned by your agency.", visibilityText);
                GUILayout.EndScrollView();
                if (!endpoints.Any(e => e.VesselId == visibilityCraft && e.OwnerAgencyId == visibilityAgency)) visibilityCraft = Guid.Empty;
                if (visibilityCraft == Guid.Empty) return;
                GUILayout.Label(VisibilityCraftName(visibilityCraft), visibilityHeading);
                GUILayout.Label("Inherit follows agency sharing. Share always reveals this craft. Private removes explicit sharing, but cannot prevent range detection.", visibilityText);
            }
            GUILayout.BeginHorizontal(); GUILayout.Label("Find agency", GUILayout.Width(88)); visibilitySearch = GUILayout.TextField(visibilitySearch, 80); GUILayout.EndHorizontal();
            var grants = VisibilityClient.GetAgencyGrantsSnapshot();
            var overrides = VisibilityClient.GetCraftOverridesSnapshot();
            visibilityScroll = GUILayout.BeginScrollView(visibilityScroll);
            foreach (var agency in AgencySystem.Singleton.KnownAgencies.Values.Where(a => a.Id != visibilityAgency && (a.Name ?? "").IndexOf(visibilitySearch, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(a => a.Name))
            {
                var shared = grants.Any(g => g.OwnerAgencyId == visibilityAgency && g.TargetAgencyId == agency.Id);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(agency.Name, visibilityHeading);
                var enabled = GUI.enabled; GUI.enabled = enabled && owner;
                if (visibilityMode == 0)
                {
                    GUILayout.Label(shared ? "Your agency shares its craft with this agency." : "Visible only when detected, unless a craft override shares it.", visibilityText);
                    if (GUILayout.Button(shared ? "Stop sharing agency craft" : "Share agency craft", visibilityButton)) VisibilityClient.SetAgencyShare(agency.Id, !shared);
                }
                else
                {
                    var endpoint = endpoints.First(e => e.VesselId == visibilityCraft);
                    var entry = overrides.FirstOrDefault(o => o.Source.VesselId == visibilityCraft && o.Source.OwnerAgencyId == endpoint.OwnerAgencyId && o.Source.OwnershipRevision == endpoint.OwnershipRevision && o.TargetAgencyId == agency.Id);
                    var rule = entry?.Rule ?? VisibilityOverride.Inherit;
                    var selected = GUILayout.SelectionGrid((int)rule, VisibilityRuleLabels, 3, visibilityButton);
                    if (selected != (int)rule) VisibilityClient.SetVesselShare(visibilityCraft, agency.Id, (VisibilityOverride)selected);
                    GUILayout.Label(rule == VisibilityOverride.Inherit ? "Agency default: " + (shared ? "shared" : "private") : rule == VisibilityOverride.Allow ? "Shared at every distance." : "Only sensor or physics range can reveal this craft.", visibilityText);
                }
                GUI.enabled = enabled;
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
        }
        private static string VisibilityCraftName(Guid id) => FlightGlobals.Vessels?.FirstOrDefault(v => v != null && v.id == id)?.vesselName ?? id.ToString("N").Substring(0, 8) + " (not loaded)";
    }
}
