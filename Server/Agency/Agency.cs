using LmpCommon.Agency;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Agency
{
    /// <summary>
    /// Server-side authoritative agency entity. Wraps <see cref="LmpCommon.Agency.AgencyInfo"/>
    /// with the additional fields needed on the server: join requests, lock, and
    /// a cache of its unlocked tech node count (derived from the tech scenario).
    ///
    /// All mutations go through <see cref="AgencySystem"/> so that persistence,
    /// network broadcast, and invariant checks stay together.
    /// </summary>
    public class Agency
    {
        public readonly object Lock = new object();

        public Guid Id;
        public string Name = string.Empty;
        public string OwnerUniqueId = string.Empty;
        public string OwnerDisplayName = string.Empty;
        public readonly List<Member> Members = new List<Member>();
        public readonly List<JoinRequestInfo> PendingJoinRequests = new List<JoinRequestInfo>();
        public double Funds;
        public float Science;
        public float Reputation;
        public long CreatedUtcTicks;
        public bool IsSolo;
        public int UnlockedTechCount;

        public bool HasMember(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return false;
            lock (Lock)
                return Members.Any(m => m.UniqueId == uniqueId);
        }

        public bool HasPendingJoinRequest(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return false;
            lock (Lock)
                return PendingJoinRequests.Any(r => r.PlayerUniqueId == uniqueId);
        }

        public AgencyInfo ToInfo()
        {
            lock (Lock)
            {
                var info = new AgencyInfo
                {
                    Id = Id,
                    Name = Name ?? string.Empty,
                    OwnerUniqueId = OwnerUniqueId ?? string.Empty,
                    OwnerDisplayName = OwnerDisplayName ?? string.Empty,
                    Funds = Funds,
                    Science = Science,
                    Reputation = Reputation,
                    CreatedUtcTicks = CreatedUtcTicks,
                    IsSolo = IsSolo,
                    UnlockedTechCount = UnlockedTechCount,
                    MemberUniqueIds = Members.Select(m => m.UniqueId ?? string.Empty).ToArray(),
                    MemberDisplayNames = Members.Select(m => m.DisplayName ?? string.Empty).ToArray(),
                };
                return info;
            }
        }

        public class Member
        {
            public string UniqueId = string.Empty;
            public string DisplayName = string.Empty;
        }
    }
}
