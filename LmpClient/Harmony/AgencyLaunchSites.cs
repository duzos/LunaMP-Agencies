using System;
using System.Reflection;
using HarmonyLib;
using LmpClient.Systems.Agency;

namespace LmpClient.Harmony
{
    public static class AgencyLaunchSites
    {
        public static void Install(HarmonyLib.Harmony harmony)
        {
            Attach(harmony, typeof(MainSystem), "Update", Type.EmptyTypes, null, nameof(Update));
            Attach(harmony, typeof(EditorDriver), "setupValidLaunchSites", Type.EmptyTypes, null, nameof(Filter));
            Attach(harmony, typeof(LaunchSiteFacility), "OnClicked", Type.EmptyTypes, nameof(Facility), null);
            Attach(harmony, typeof(FlightDriver), "StartWithNewLaunch", new[] { typeof(string), typeof(string), typeof(string), typeof(VesselCrewManifest) }, nameof(Launch), null);
            KkLaunchSiteIntegration.Install(harmony);
        }
        private static void Attach(HarmonyLib.Harmony harmony, Type type, string name, Type[] parameters, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
                harmony.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(AgencyLaunchSites), prefix), postfix == null ? null : new HarmonyMethod(typeof(AgencyLaunchSites), postfix));
                LunaLog.Log($"[AgencyLaunchSites] Attached {type.Name}.{name}");
            }
            catch (Exception e) { LunaLog.LogError($"[AgencyLaunchSites] Cannot attach {type.Name}.{name}: {e.Message}"); }
        }
        private static void Update() => LaunchSiteCatalog.Tick();
        private static void Filter()
        {
            if (!LaunchSiteAccess.Enabled || EditorDriver.ValidLaunchSites == null) return;
            EditorDriver.ValidLaunchSites.RemoveAll(site => !LaunchSiteAccess.CanLaunch(site));
        }
        private static bool Facility(object __instance)
        {
            if (!LaunchSiteAccess.Enabled) return true;
            var site = LaunchSiteCatalog.ReadMember(__instance, "launchSiteName") as string;
            return !LaunchSiteAccess.Deny(site);
        }
        private static bool Launch(string __2) => !LaunchSiteAccess.Deny(__2);
    }
}
