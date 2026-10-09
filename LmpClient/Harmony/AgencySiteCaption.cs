using System;
using System.Collections.Generic;
using KSP.UI.Screens.Mapview;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;

namespace LmpClient.Harmony
{
    /// <summary>
    /// Plan 41 S4: assigned-agency line on launch-site map captions. Patches <c>LaunchSite.UpdateNodeCaption</c>
    /// (stock and Making History sites). The KSC pad and runway share one internal <c>KSCSiteNode</c> whose caption
    /// cannot tell the two sites apart, and KK draws its own icons, so those are covered by
    /// <see cref="LmpClient.Windows.Agency.LaunchSiteFlagOverlay"/> only.
    /// </summary>
    internal static class AgencySiteCaption
    {
        private sealed class CachedLine { internal string TmpName, Colour, Line; }

        private static readonly Dictionary<string, CachedLine> Lines = new Dictionary<string, CachedLine>(StringComparer.Ordinal);

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            var method = HarmonyLib.AccessTools.Method(typeof(LaunchSite), nameof(LaunchSite.UpdateNodeCaption), new[] { typeof(MapNode), typeof(MapNode.CaptionData) })
                         ?? throw new MissingMethodException(typeof(LaunchSite).FullName, nameof(LaunchSite.UpdateNodeCaption));
            harmony.Patch(method, postfix: new HarmonyLib.HarmonyMethod(typeof(AgencySiteCaption), nameof(Postfix)));
        }

        private static void Postfix(LaunchSite __instance, MapNode.CaptionData data)
        {
            try
            {
                if (data == null || __instance == null || string.IsNullOrEmpty(__instance.name)) return;
                var agency = AgencySystem.Singleton;
                if (agency == null) return;
                var snapshot = agency.LaunchSitesSnapshot;
                if (!LaunchSiteFlagPolicy.ShouldShow(SettingsSystem.CurrentSettings.AgencySiteFlags, LaunchSiteAccess.Enabled, snapshot.Ready,
                        snapshot.Assignments.TryGetValue(__instance.name, out var owner) ? owner : Guid.Empty, false)) return;
                if (!AgencyPresentation.TryGetAgencyStyle(owner, out var style)) return;
                if (!Lines.TryGetValue(__instance.name, out var cached)) Lines[__instance.name] = cached = new CachedLine();
                if (!ReferenceEquals(cached.TmpName, style.TmpName) || !ReferenceEquals(cached.Colour, style.TextColourHex))
                {
                    cached.TmpName = style.TmpName;
                    cached.Colour = style.TextColourHex;
                    cached.Line = style.TextColourHex == null
                        ? "Assigned: " + style.TmpName
                        : "Assigned: <color=" + style.TextColourHex + ">" + style.TmpName + "</color>";
                }
                data.captionLine3 = cached.Line;
            }
            catch (Exception e)
            {
                LunaLog.LogWarning("[AgencySiteCaption] " + e.Message);
            }
        }
    }
}
