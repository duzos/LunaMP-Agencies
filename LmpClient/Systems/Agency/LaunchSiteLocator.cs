using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LmpCommon.Agency;
using UnityEngine;

namespace LmpClient.Systems.Agency
{
    /// <summary>One assigned launch site with enough data to place a marker (plan 41 S4).</summary>
    internal sealed class SiteMark
    {
        public string Id;
        public CelestialBody Body;
        public double Lat, Lon, Alt;
        public Guid Agency;
        /// <summary>The KK launch site object (null for stock/MH sites).</summary>
        public object Kk;
        public bool Hidden;
        /// <summary>False when a KK site could only be placed from KK rounded refLat/refLon.</summary>
        public bool Precise = true;
    }

    /// <summary>
    /// World positions of assigned launch sites (stock, MH, KK) for the site flag overlay (plan 41 S4).
    /// The list is rebuilt only when the assignment snapshot, the catalog snapshot or the scene changes (or
    /// while KK is still loading); main thread only.
    /// </summary>
    internal static class LaunchSiteLocator
    {
        private static readonly List<SiteMark> Marks = new List<SiteMark>();
        private static AgencyLaunchSiteSnapshot lastSnapshot;
        private static object lastCatalog;
        private static GameScenes lastScene = (GameScenes)(-1);
        private static float nextRetry, nextHiddenCheck;
        private static bool retry;

        private static bool kkResolved, kkUsable;
        private static FieldInfo kkBody, kkStatic, kkLat, kkLon, kkAlt, siGameObject;
        private static MethodInfo kkHiddenGetter;

        /// <summary>Marks for the current assignments. The list is reused; do not keep it across frames.</summary>
        internal static List<SiteMark> GetMarks()
        {
            var agency = AgencySystem.Singleton;
            if (agency == null) { if (Marks.Count > 0) Marks.Clear(); return Marks; }
            var snapshot = agency.LaunchSitesSnapshot;
            var catalog = LaunchSiteCatalog.GetSnapshot();
            var now = Time.unscaledTime;
            var changed = !ReferenceEquals(snapshot, lastSnapshot) || !ReferenceEquals(catalog, lastCatalog) || lastScene != HighLogic.LoadedScene;
            if (changed || (retry && now >= nextRetry))
            {
                lastSnapshot = snapshot; lastCatalog = catalog; lastScene = HighLogic.LoadedScene;
                nextRetry = now + 1f;
                try { Rebuild(snapshot); }
                catch (Exception e)
                {
                    Marks.Clear();
                    LunaLog.LogWarning("[AgencySiteFlags] Could not locate launch sites: " + e.Message);
                }
                // Retry (at most once a second) while KK has not built its catalog or a KK site is only roughly placed.
                retry = KkLaunchSiteIntegration.CatalogPending || HasImprecise();
            }
            if (now >= nextHiddenCheck)
            {
                nextHiddenCheck = now + 1f;
                RefreshHidden();
            }
            return Marks;
        }

        private static bool HasImprecise()
        {
            // Statics only exist in the KSC and flight scenes; elsewhere the rounded position is all there is.
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER && HighLogic.LoadedScene != GameScenes.FLIGHT) return false;
            for (var i = 0; i < Marks.Count; i++) if (!Marks[i].Precise) return true;
            return false;
        }

        private static void RefreshHidden()
        {
            for (var i = 0; i < Marks.Count; i++)
                if (Marks[i].Kk != null) Marks[i].Hidden = ReadHidden(Marks[i].Kk);
        }

        private static void Rebuild(AgencyLaunchSiteSnapshot snapshot)
        {
            Marks.Clear();
            if (!snapshot.Ready || snapshot.Assignments.Count == 0 || PSystemSetup.Instance == null) return;
            var assignments = snapshot.Assignments;
            AddStock(assignments, LaunchSiteCatalog.ReadMember(PSystemSetup.Instance, "SpaceCenterFacilities") as IEnumerable, "hostBody");
            AddStock(assignments, LaunchSiteCatalog.ReadMember(PSystemSetup.Instance, "LaunchSites") as IEnumerable, "Body");
            AddKk(assignments);
        }

        private static bool Has(string id)
        {
            for (var i = 0; i < Marks.Count; i++) if (Marks[i].Id == id) return true;
            return false;
        }

        private static void AddStock(IReadOnlyDictionary<string, Guid> assignments, IEnumerable sites, string bodyMember)
        {
            if (sites == null) return;
            foreach (var site in sites)
            {
                var id = LaunchSiteCatalog.ReadMember(site, "name") as string;
                if (string.IsNullOrEmpty(id) || !assignments.TryGetValue(id, out var agency) || Has(id)) continue;
                var body = LaunchSiteCatalog.ReadMember(site, bodyMember) as CelestialBody;
                if (!body) continue;
                var spawns = LaunchSiteCatalog.ReadMember(site, "spawnPoints") as Array;
                if (spawns == null || spawns.Length == 0 || spawns.GetValue(0) == null) continue;
                var spawn = spawns.GetValue(0);
                var lat = ReadDouble(spawn, "latitude"); var lon = ReadDouble(spawn, "longitude"); var alt = ReadDouble(spawn, "altitude");
                if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsNaN(alt)) continue;
                Marks.Add(new SiteMark { Id = id, Body = body, Lat = lat, Lon = lon, Alt = alt, Agency = agency });
            }
        }

        private static double ReadDouble(object value, string name)
        {
            var read = LaunchSiteCatalog.ReadMember(value, name);
            return read is double d ? d : double.NaN;
        }

        private static void AddKk(IReadOnlyDictionary<string, Guid> assignments)
        {
            if (KkLaunchSiteIntegration.State == KkIntegrationState.Absent) return;
            if (!ResolveKk()) return;
            foreach (var site in KkLaunchSiteIntegration.Sites())
            {
                var id = KkLaunchSiteIntegration.SiteId(site);
                if (string.IsNullOrEmpty(id) || id == "LaunchPad" || id == "Runway" || !assignments.TryGetValue(id, out var agency) || Has(id)) continue;
                var body = kkBody.GetValue(site) as CelestialBody;
                if (!body) continue;
                var mark = new SiteMark { Id = id, Body = body, Agency = agency, Kk = site, Hidden = ReadHidden(site) };
                if (!TryPlaceFromStatic(site, mark))
                {
                    mark.Precise = false;
                    mark.Lat = Convert.ToDouble(kkLat.GetValue(site));
                    mark.Lon = Convert.ToDouble(kkLon.GetValue(site));
                    mark.Alt = Convert.ToDouble(kkAlt.GetValue(site));
                }
                Marks.Add(mark);
            }
        }

        // KK rounds refLat/refLon to two decimals, so prefer the live static instance position.
        private static bool TryPlaceFromStatic(object site, SiteMark mark)
        {
            var instance = kkStatic.GetValue(site);
            if (instance == null) return false;
            var go = siGameObject.GetValue(instance) as GameObject;
            if (!go) return false;
            mark.Body.GetLatLonAlt(go.transform.position, out var lat, out var lon, out var alt);
            if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsNaN(alt)) return false;
            mark.Lat = lat; mark.Lon = lon; mark.Alt = alt;
            return true;
        }

        private static bool ReadHidden(object site)
        {
            if (kkHiddenGetter == null) return false;
            try { return kkHiddenGetter.Invoke(site, null) is bool hidden && hidden; }
            catch { return true; } // unreadable: fail closed
        }

        private static bool ResolveKk()
        {
            if (kkResolved) return kkUsable;
            var siteType = AccessTools.TypeByName("KerbalKonstructs.Core.KKLaunchSite");
            if (siteType == null) return false; // KK not loaded yet: try again later
            kkResolved = true;
            try
            {
                kkBody = AccessTools.Field(siteType, "body") ?? throw new MissingFieldException("body");
                kkStatic = AccessTools.Field(siteType, "staticInstance") ?? throw new MissingFieldException("staticInstance");
                kkLat = AccessTools.Field(siteType, "refLat") ?? throw new MissingFieldException("refLat");
                kkLon = AccessTools.Field(siteType, "refLon") ?? throw new MissingFieldException("refLon");
                kkAlt = AccessTools.Field(siteType, "refAlt") ?? throw new MissingFieldException("refAlt");
                kkHiddenGetter = AccessTools.PropertyGetter(siteType, "LaunchSiteIsHidden") ?? throw new MissingMethodException("LaunchSiteIsHidden");
                siGameObject = AccessTools.Field(kkStatic.FieldType, "gameObject") ?? throw new MissingFieldException("StaticInstance.gameObject");
                if (kkBody.FieldType != typeof(CelestialBody) || siGameObject.FieldType != typeof(GameObject) || kkHiddenGetter.ReturnType != typeof(bool))
                    throw new InvalidOperationException("KK site member types differ");
                kkUsable = true;
            }
            catch (Exception e)
            {
                kkUsable = false;
                LunaLog.LogWarning("[AgencySiteFlags] KK site flags disabled: " + e.Message);
            }
            return kkUsable;
        }
    }
}
