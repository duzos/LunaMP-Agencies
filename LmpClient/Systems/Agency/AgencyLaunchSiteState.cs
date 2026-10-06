using System;
using System.Linq;
using LmpCommon.Agency;
using LmpCommon.Message.Data.Agency;

namespace LmpClient.Systems.Agency
{
    public partial class AgencySystem
    {
        private readonly object launchSitesLock = new object();
        private AgencyLaunchSiteSnapshot launchSitesSnapshot = AgencyLaunchSiteSnapshot.Empty;
        public AgencyLaunchSiteSnapshot LaunchSitesSnapshot { get { lock (launchSitesLock) return launchSitesSnapshot; } }
        public volatile string LatestServerReply;

        internal void ApplyLaunchSites(AgencySyncAllMsgData data)
        {
            bool changed;
            lock (launchSitesLock)
            {
                var previous = launchSitesSnapshot;
                launchSitesSnapshot = AgencyLaunchSiteSnapshot.Apply(launchSitesSnapshot, data.LaunchSitesSnapshotPresent, data.LaunchSitesRevision, data.LaunchSites);
                changed = previous.Ready != launchSitesSnapshot.Ready || previous.Revision != launchSitesSnapshot.Revision ||
                          previous.Assignments.Count != launchSitesSnapshot.Assignments.Count ||
                          previous.Assignments.Any(pair => !launchSitesSnapshot.Assignments.TryGetValue(pair.Key, out var owner) || owner != pair.Value);
            }
            if (changed) LaunchSiteCatalog.RequestRefresh();
        }

        internal void ClearLaunchSites()
        {
            lock (launchSitesLock) launchSitesSnapshot = AgencyLaunchSiteSnapshot.Empty;
            LaunchSiteCatalog.RequestRefresh();
        }

        public bool IsLaunchSiteAllowed(string siteId)
        {
            lock (launchSitesLock)
                return AgencyLaunchSitePolicy.CanLaunch(LaunchSiteAccess.Enabled, launchSitesSnapshot.Ready, myAgencyId, siteId, launchSitesSnapshot.Assignments);
        }
    }
}
