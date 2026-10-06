using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Systems.Agency
{
    public sealed class LaunchSiteCatalogEntry
    {
        public string Id { get; }
        public string Name { get; }
        public string Source { get; }
        internal LaunchSiteCatalogEntry(string id, string name, string source) { Id = id; Name = name; Source = source; }
    }

    public static class LaunchSiteAccess
    {
        public static bool Enabled => MainSystem.NetworkState >= ClientState.Handshaking && SettingsSystem.ServerSettings.AgencyLaunchSitesPerAgency;
        public static bool CanLaunch(string siteId) => GetBlockReason(siteId) == null;
        public static string GetBlockReason(string siteId)
        {
            if (!Enabled) return null;
            var agency = AgencySystem.Singleton;
            var snapshot = agency.LaunchSitesSnapshot;
            if (!snapshot.Ready) return "Waiting for agency launch-site assignments.";
            if (agency.MyAgencyId == Guid.Empty) return "You do not have an agency.";
            if (!agency.IsLaunchSiteAllowed(siteId)) return "This launch site is not assigned to your agency.";
            if (KkLaunchSiteIntegration.IsKkSite(siteId) && KkLaunchSiteIntegration.State != KkIntegrationState.Ready)
                return "Kerbal Konstructs integration is unavailable. See KSP.log.";
            return null;
        }
        internal static bool Deny(string siteId)
        {
            var reason = GetBlockReason(siteId);
            if (reason == null) return false;
            ScreenMessages.PostScreenMessage(reason, 5f, ScreenMessageStyle.UPPER_CENTER);
            Diagnostics.PlaytestDiagnostics.Write("client.launch.denied", () => $"site={siteId} agency={AgencySystem.Singleton.MyAgencyId} reason={reason}");
            return true;
        }
    }

    public static class LaunchSiteCatalog
    {
        private static IReadOnlyList<LaunchSiteCatalogEntry> snapshot = Array.AsReadOnly(new LaunchSiteCatalogEntry[0]);
        private static int dirty = 1;
        private static int generation;
        private static GameScenes lastScene;
        private static bool lastEnabled;
        private static bool lastAppliedEnabled;
        private static DateTime retryAfter;
        public static IReadOnlyList<LaunchSiteCatalogEntry> GetSnapshot() => Volatile.Read(ref snapshot);
        public static void RequestRefresh() { Interlocked.Increment(ref generation); Interlocked.Exchange(ref dirty, 1); }

        // Invoked by MainSystem.Update postfix, including after disconnect, to restore vanilla lists.
        internal static void Tick()
        {
            if (lastScene != HighLogic.LoadedScene || lastEnabled != LaunchSiteAccess.Enabled)
            {
                lastScene = HighLogic.LoadedScene;
                lastEnabled = LaunchSiteAccess.Enabled;
                RequestRefresh();
            }
            if (DateTime.UtcNow < retryAfter || Interlocked.Exchange(ref dirty, 0) == 0) return;
            try
            {
                if (PSystemSetup.Instance == null) { Retry(); return; }
                var sites = new Dictionary<string, LaunchSiteCatalogEntry>(StringComparer.Ordinal);
                AddStock(sites, ReadMember(PSystemSetup.Instance, "SpaceCenterFacilities") as IEnumerable);
                AddStock(sites, ReadMember(PSystemSetup.Instance, "LaunchSites") as IEnumerable);
                foreach (var site in KkLaunchSiteIntegration.Sites())
                {
                    var id = KkLaunchSiteIntegration.SiteId(site);
                    if (string.IsNullOrEmpty(id)) continue;
                    // KK also wraps KSC. Keep these ordinary stock sites even if optional KK is unsupported.
                    if (id == "LaunchPad" || id == "Runway") continue;
                    sites[id] = new LaunchSiteCatalogEntry(id, id, "Kerbal Konstructs");
                }
                Volatile.Write(ref snapshot, Array.AsReadOnly(sites.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray()));
                var refreshComplete = true;
                if (LaunchSiteAccess.Enabled || lastAppliedEnabled)
                {
                    refreshComplete = KkLaunchSiteIntegration.Refresh(Volatile.Read(ref generation));
                    if (HighLogic.LoadedScene == GameScenes.EDITOR)
                        AccessTools.Method(typeof(EditorDriver), "setupValidLaunchSites", Type.EmptyTypes)?.Invoke(null, null);
                    if (HighLogic.LoadedScene == GameScenes.EDITOR || HighLogic.LoadedScene == GameScenes.SPACECENTER)
                    {
                        var controller = AccessTools.TypeByName("KSP.UI.UILaunchsiteController");
                        if (controller != null)
                            foreach (var instance in Resources.FindObjectsOfTypeAll(controller))
                                AccessTools.Method(controller, "resetItems", Type.EmptyTypes)?.Invoke(instance, null);
                    }
                }
                lastAppliedEnabled = LaunchSiteAccess.Enabled || (!refreshComplete && lastAppliedEnabled);
                if (!refreshComplete) Retry();
                Diagnostics.PlaytestDiagnostics.Write("client.launch.refresh", () => $"enabled={lastEnabled} catalog={sites.Count} kk={KkLaunchSiteIntegration.State}");
            }
            catch (Exception e)
            {
                Diagnostics.PlaytestDiagnostics.Write("client.launch.refresh-error", () => e.GetType().Name);
                Retry();
            }
        }
        private static void Retry() { retryAfter = DateTime.UtcNow.AddSeconds(2); Interlocked.Exchange(ref dirty, 1); }
        private static void AddStock(IDictionary<string, LaunchSiteCatalogEntry> sites, IEnumerable loaded)
        {
            if (loaded == null) return;
            foreach (var site in loaded)
            {
                var id = ReadMember(site, "name") as string;
                if (string.IsNullOrEmpty(id)) continue;
                var name = ReadMember(site, "facilityDisplayName") as string ?? ReadMember(site, "launchSiteName") as string ?? id;
                sites[id] = new LaunchSiteCatalogEntry(id, name, "Stock");
            }
        }
        internal static object ReadMember(object value, string name)
        {
            if (value == null) return null;
            var type = value.GetType();
            return AccessTools.Field(type, name)?.GetValue(value) ?? AccessTools.Property(type, name)?.GetValue(value, null);
        }
    }
}
