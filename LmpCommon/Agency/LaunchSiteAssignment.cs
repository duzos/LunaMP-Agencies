using System;

namespace LmpCommon.Agency
{
    public sealed class LaunchSiteAssignment
    {
        public string SiteId { get; set; } = string.Empty;
        public Guid AgencyId { get; set; }
    }
}
