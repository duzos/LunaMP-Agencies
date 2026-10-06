using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace LmpCommon.Agency
{
    /// <summary>Immutable client mirror, copied before pooled message data can be reused.</summary>
    public sealed class AgencyLaunchSiteSnapshot
    {
        public bool Ready { get; }
        public long Revision { get; }
        public IReadOnlyDictionary<string, Guid> Assignments { get; }
        public static AgencyLaunchSiteSnapshot Empty { get; } = new AgencyLaunchSiteSnapshot(false, -1, new Dictionary<string, Guid>(StringComparer.Ordinal));
        private AgencyLaunchSiteSnapshot(bool ready, long revision, Dictionary<string, Guid> assignments)
        {
            Ready = ready; Revision = revision;
            Assignments = new ReadOnlyDictionary<string, Guid>(assignments);
        }
        public static AgencyLaunchSiteSnapshot Apply(AgencyLaunchSiteSnapshot current, bool present, long revision, LaunchSiteAssignment[] entries)
        {
            if (!present || entries == null || entries.Length > AgencyLaunchSitePolicy.MaxAssignments || revision < 0) return Empty;
            var copy = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null || !AgencyLaunchSitePolicy.IsValidSiteId(entry.SiteId) || entry.AgencyId == Guid.Empty || copy.ContainsKey(entry.SiteId)) return Empty;
                copy.Add(entry.SiteId, entry.AgencyId);
            }
            if (current != null && current.Ready && revision < current.Revision) return current;
            return new AgencyLaunchSiteSnapshot(true, revision, copy);
        }
    }
}
