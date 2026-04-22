using System;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Pure data DTO describing an agency and its headline state.
    /// Shared between client and server; no logic lives here.
    ///
    /// Tech-tree progression is NOT carried by this DTO — it lives on the
    /// server in per-agency scenario files and is synced via the existing
    /// ShareProgressTechnology/PartPurchase messages once the client is a
    /// member of the agency.
    /// </summary>
    public class AgencyInfo
    {
        public Guid Id;
        public string Name = string.Empty;
        public string OwnerUniqueId = string.Empty;
        public string OwnerDisplayName = string.Empty;
        public string[] MemberUniqueIds = Array.Empty<string>();
        public string[] MemberDisplayNames = Array.Empty<string>();
        public double Funds;
        public float Science;
        public float Reputation;
        public long CreatedUtcTicks;
        public bool IsSolo;

        /// <summary>
        /// Count of tech nodes unlocked by this agency — lightweight summary
        /// displayed in the agency window without shipping full tech state.
        /// </summary>
        public int UnlockedTechCount;
    }
}
