using System;
using System.Collections.Generic;

namespace LmpCommon.Agency
{
    public static class AgencyLaunchSitePolicy
    {
        public const int MaxSiteIdLength = 256;
        public const int MaxAssignments = 2048;
        public static bool IsValidSiteId(string siteId)
        {
            if (string.IsNullOrWhiteSpace(siteId) || siteId.Length > MaxSiteIdLength) return false;
            foreach (var character in siteId) if (char.IsControl(character)) return false;
            return true;
        }
        public static bool CanLaunch(bool enabled, bool snapshotReady, Guid myAgency, string siteId, IReadOnlyDictionary<string, Guid> assignments)
        {
            if (!enabled) return true;
            return snapshotReady && myAgency != Guid.Empty && IsValidSiteId(siteId)
                && assignments != null && assignments.TryGetValue(siteId, out var owner) && owner == myAgency;
        }
    }
}
