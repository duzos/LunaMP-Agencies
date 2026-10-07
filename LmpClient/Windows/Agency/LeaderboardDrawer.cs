using System;
using System.Linq;
using LmpCommon.Agency;
using LmpClient.Systems.Agency;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        private static Guid _firstsAgency;
        private static bool _firstsTimeline;
        private static void DrawLeaderboardTab()
        {
            if (_firstsTimeline || _firstsAgency != Guid.Empty)
            {
                if (GUILayout.Button("Back to leaderboard")) { _firstsTimeline = false; _firstsAgency = Guid.Empty; }
                GUILayout.Label(_firstsTimeline ? "All agency firsts" : "Agency firsts");
                _leaderboardScrollPos = GUILayout.BeginScrollView(_leaderboardScrollPos);
                var entries = AgencySystem.Singleton.KnownAgencies.Values.Where(a => !a.IsSolo && (_firstsTimeline || a.Id == _firstsAgency))
                    .SelectMany(a => a.FirstAchievements.Select(f => new { Agency = a, First = f }))
                    .OrderBy(e => e.First.UtcTicks).ThenBy(e => e.First.Key, StringComparer.Ordinal).ThenBy(e => e.Agency.Id).ToArray();
                if (entries.Length == 0) GUILayout.Label("No firsts recorded.");
                foreach (var entry in entries) DrawFirst(entry.Agency, entry.First);
                GUILayout.EndScrollView();
                return;
            }
            GUILayout.Label("Current agency balances. Sort by:");
            _leaderboardSort = GUILayout.Toolbar(_leaderboardSort, _leaderboardSortLabels);
            if (GUILayout.Button("Global firsts timeline")) _firstsTimeline = true;
            var rows = AgencySystem.Singleton.KnownAgencies.Values.Where(a => !a.IsSolo).ToArray();
            switch (_leaderboardSort)
            {
                case 0: rows = rows.OrderByDescending(a => a.FirstAchievementsCount).ThenByDescending(a => a.Funds).ThenBy(a => a.Id).ToArray(); break;
                case 1: rows = rows.OrderByDescending(a => a.Funds).ThenBy(a => a.Id).ToArray(); break;
                case 2: rows = rows.OrderByDescending(a => a.Science).ThenBy(a => a.Id).ToArray(); break;
                case 3: rows = rows.OrderByDescending(a => a.VesselsLaunched).ThenBy(a => a.Id).ToArray(); break;
            }
            _leaderboardScrollPos = GUILayout.BeginScrollView(_leaderboardScrollPos);
            for (var i = 0; i < rows.Length; i++)
            {
                var a = rows[i];
                GUILayout.BeginVertical(GUI.skin.box);
                DrawIdentityLabel(a.Id, (i + 1) + ". " + a.Name + (a.Id == AgencySystem.Singleton.MyAgencyId ? " (you)" : ""));
                GUILayout.Label("Funds: " + a.Funds.ToString("N0") + "   Science: " + a.Science.ToString("N1") + "   Vessels: " + a.VesselsLaunched);
                if (GUILayout.Button("Firsts: " + a.FirstAchievementsCount + " - details")) _firstsAgency = a.Id;
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
        }
        private static void DrawFirst(AgencyInfo agency, FirstAchievement first)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            DrawIdentityLabel(agency.Id, AchievementWire.ReadableName(first.Key) + " - " + agency.Name);
            var validDate = first.UtcTicks > 0 && first.UtcTicks <= DateTime.MaxValue.Ticks;
            GUILayout.Label("Awarded: " + (validDate ? new DateTime(first.UtcTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "unknown"));
            var details = first.Details;
            GUILayout.Label("Game time: " + (details?.UniversalTime != null ? KSPUtil.PrintDate(details.UniversalTime.Value, true, true) : "unknown"));
            GUILayout.Label("Vessel: " + (string.IsNullOrEmpty(details?.VesselName) ? "unknown" : details.VesselName));
            GUILayout.Label("Crew: " + (details?.CrewNames == null ? "unknown" : details.CrewNames.Length == 0 ? "uncrewed" : string.Join(", ", details.CrewNames))
                + (details?.CrewTruncated == true ? " (first " + details.CrewNames.Length + " crew shown)" : ""));
            GUILayout.EndVertical();
        }
    }
}
