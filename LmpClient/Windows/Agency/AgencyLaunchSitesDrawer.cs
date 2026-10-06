using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using System;
using System.Linq;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        private static Vector2 _allowedSitesScroll;
        private static GUIStyle _allowedSitesText;

        private static void DrawLaunchSitesSummary(Guid agencyId)
        {
            if (!SettingsSystem.ServerSettings.AgencyLaunchSitesPerAgency) return;
            if (_allowedSitesText == null) _allowedSitesText = new GUIStyle(GUI.skin.label) { wordWrap = true };
            var snapshot = AgencySystem.Singleton.LaunchSitesSnapshot;
            GUILayout.Space(8);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("YOUR LAUNCH SITES");
            if (!snapshot.Ready)
                GUILayout.Label("Waiting for the server's assignments. Launching is locked.", _allowedSitesText);
            else
            {
                var sites = snapshot.Assignments.Where(pair => pair.Value == agencyId)
                    .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                if (sites.Length == 0)
                    GUILayout.Label("No launch sites assigned. Ask an admin to assign a site before launching.", _allowedSitesText);
                else
                {
                    GUILayout.Label(sites.Length + (sites.Length == 1 ? " assigned site" : " assigned sites"));
                    _allowedSitesScroll = GUILayout.BeginScrollView(_allowedSitesScroll, false, false, GUILayout.Height(100));
                    foreach (var site in sites) GUILayout.Label(site, _allowedSitesText, GUILayout.Width(Mathf.Max(200, Singleton.WindowRect.width - 80)));
                    GUILayout.EndScrollView();
                }
            }
            GUILayout.EndVertical();
        }
    }
}
