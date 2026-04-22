using System;

namespace LmpCommon.Agency
{
    public class JoinRequestInfo
    {
        public Guid AgencyId;
        public string PlayerUniqueId = string.Empty;
        public string PlayerDisplayName = string.Empty;
        public long RequestedUtcTicks;
    }
}
