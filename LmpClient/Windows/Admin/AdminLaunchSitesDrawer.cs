using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LmpClient.Windows.Admin
{
    public partial class AdminWindow
    {
        private static Guid _launchAgency;
        private static string _launchSearch = string.Empty;
        private static Vector2 _launchAgencyScroll;
        private static Vector2 _launchSiteScroll;
        private static string _confirmSite;
        private static Guid _confirmFrom;
        private static Guid _confirmTo;
        private static bool _confirmUnassign;
        private static GUIStyle _launchText;
        private static GUIStyle _launchHeading;

        public static void ResetLaunchSitesUi()
        {
            _launchAgency = Guid.Empty;
            _confirmSite = null;
            _launchSearch = string.Empty;
        }

        private static void DrawLaunchSitesTab()
        {
            if (_launchText == null)
            {
                _launchText = new GUIStyle(GUI.skin.label) { wordWrap = true };
                _launchHeading = new GUIStyle(_launchText) { fontStyle = FontStyle.Bold };
            }
            var system = AgencySystem.Singleton;
            var snapshot = system.LaunchSitesSnapshot;
            var agencies = system.KnownAgencies.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var selected = agencies.FirstOrDefault(a => a.Id == _launchAgency);

            GUILayout.Label("Launch-site assignments", _launchHeading);
            GUILayout.Label(SettingsSystem.ServerSettings.AgencyLaunchSitesPerAgency
                ? "One agency per site. Agencies with no assignments cannot launch, including at KSC."
                : "Enforcement is off. You can prepare assignments here; normal launch access still applies.", _launchText);
            if (!snapshot.Ready)
            {
                GUILayout.Label("Waiting for assignment data. This server may need the agency launch-site update.", _launchText);
                _confirmSite = null;
                return;
            }
            if (KkLaunchSiteIntegration.State == KkIntegrationState.Unsupported)
                GUILayout.Label("Kerbal Konstructs integration unavailable: " + KkLaunchSiteIntegration.DiagnosticReason, _launchText);

            GUILayout.Space(6);
            var compact = Screen.width < 620;
            if (compact) GUILayout.BeginVertical();
            else GUILayout.BeginHorizontal();
            if (compact) GUILayout.BeginVertical(GUI.skin.box, GUILayout.Height(110));
            else GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(190));
            GUILayout.Label("1. Choose an agency", _launchHeading);
            _launchAgencyScroll = GUILayout.BeginScrollView(_launchAgencyScroll);
            foreach (var agency in agencies)
            {
                var count = snapshot.Assignments.Count(pair => pair.Value == agency.Id);
                var label = agency.Name + "\n" + count + (count == 1 ? " site" : " sites") + (agency.IsSolo ? " · solo" : "");
                if (GUILayout.Toggle(agency.Id == _launchAgency, label, GUI.skin.button) && agency.Id != _launchAgency)
                {
                    _launchAgency = agency.Id;
                    _confirmSite = null;
                }
            }
            if (agencies.Length == 0) GUILayout.Label("No agencies received yet.", _launchText);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("2. Assign launch sites", _launchHeading);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(45));
            _launchSearch = GUILayout.TextField(_launchSearch ?? string.Empty);
            if (GUILayout.Button("Refresh", GUILayout.Width(70))) LaunchSiteCatalog.RequestRefresh();
            GUILayout.EndHorizontal();
            GUILayout.Label(selected == null ? "Select an agency to enable assignment actions." : "Assigning to " + selected.Name, _launchText);
            if (!IsPasswordSet()) GUILayout.Label("Enter the admin password above to make changes.", _launchText);

            var catalog = LaunchSiteCatalog.GetSnapshot();
            var loaded = catalog.GroupBy(site => site.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var ids = loaded.Keys.Concat(snapshot.Assignments.Keys).Distinct(StringComparer.Ordinal)
                .Where(id => string.IsNullOrEmpty(_launchSearch) || id.IndexOf(_launchSearch, StringComparison.OrdinalIgnoreCase) >= 0
                    || (loaded.TryGetValue(id, out var entry) && entry.Name.IndexOf(_launchSearch, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            _launchSiteScroll = GUILayout.BeginScrollView(_launchSiteScroll);
            foreach (var id in ids)
            {
                loaded.TryGetValue(id, out var entry);
                snapshot.Assignments.TryGetValue(id, out var owner);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(entry?.Name ?? id, _launchHeading);
                if (entry != null && entry.Name != id) GUILayout.Label(id, _launchText);
                GUILayout.Label(entry == null ? "Not loaded in this client" : entry.Source, _launchText);
                GUILayout.Label(owner == Guid.Empty ? "Unassigned" : "Assigned to " + AgencyLabel(owner), _launchText);
                GUILayout.BeginHorizontal();
                var enabled = GUI.enabled;
                GUI.enabled = enabled && IsPasswordSet() && selected != null && entry != null && owner != _launchAgency;
                if (GUILayout.Button(owner == Guid.Empty ? "Assign to selected" : "Move to selected"))
                {
                    if (owner == Guid.Empty) SendAdmin(AgencyAdminOp.AssignLaunchSite, _launchAgency, id, 0);
                    else ConfirmLaunchChange(id, owner, _launchAgency, false);
                }
                GUI.enabled = enabled && IsPasswordSet() && owner != Guid.Empty;
                if (GUILayout.Button("Unassign", GUILayout.Width(80))) ConfirmLaunchChange(id, owner, Guid.Empty, true);
                GUI.enabled = enabled;
                GUILayout.EndHorizontal();

                if (_confirmSite == id)
                {
                    if (owner != _confirmFrom || (!_confirmUnassign && _launchAgency != _confirmTo)) _confirmSite = null;
                    else DrawLaunchConfirmation(id, snapshot.Assignments);
                }
                GUILayout.EndVertical();
            }
            if (ids.Length == 0) GUILayout.Label("No matching sites. Refresh after the space centre has loaded.", _launchText);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            if (compact) GUILayout.EndVertical();
            else GUILayout.EndHorizontal();
            GUILayout.Label("The list uses sites loaded in this client. Changes appear after the server confirms them.", _launchText);
            if (!string.IsNullOrEmpty(system.LatestServerReply))
                GUILayout.Label("Last server reply: " + system.LatestServerReply, _launchText);
        }

        private static string AgencyLabel(Guid id)
            => AgencySystem.Singleton.KnownAgencies.TryGetValue(id, out var agency) ? agency.Name : id.ToString("N").Substring(0, 8);

        private static void ConfirmLaunchChange(string site, Guid from, Guid to, bool unassign)
        {
            _confirmSite = site;
            _confirmFrom = from;
            _confirmTo = to;
            _confirmUnassign = unassign;
        }

        private static void DrawLaunchConfirmation(string id, IReadOnlyDictionary<string, Guid> assignments)
        {
            GUILayout.Label(_confirmUnassign
                ? "Remove this site from " + AgencyLabel(_confirmFrom) + "?"
                : "Move this site from " + AgencyLabel(_confirmFrom) + " to " + AgencyLabel(_confirmTo) + "?", _launchText);
            if (assignments.Count(pair => pair.Value == _confirmFrom) == 1)
                GUILayout.Label("This leaves the current owner with no assigned launch sites.", _launchText);
            GUILayout.BeginHorizontal();
            var enabled = GUI.enabled;
            GUI.enabled = enabled && IsPasswordSet();
            if (GUILayout.Button(_confirmUnassign ? "Confirm unassign" : "Confirm move"))
            {
                SendAdmin(_confirmUnassign ? AgencyAdminOp.UnassignLaunchSite : AgencyAdminOp.AssignLaunchSite,
                    _confirmUnassign ? _confirmFrom : _confirmTo, id, 0);
                _confirmSite = null;
            }
            GUI.enabled = enabled;
            if (GUILayout.Button("Cancel")) _confirmSite = null;
            GUILayout.EndHorizontal();
        }
    }
}
