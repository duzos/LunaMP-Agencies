using System;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Pure rule for launching a craft with the agency flag (plan 41 S2). A craft whose flag is the player's
    /// default counts as "not explicitly set"; choosing your own default on purpose is indistinguishable
    /// from not choosing, which is accepted - the AgencyAutoCraftFlag toggle is the opt-out.
    /// </summary>
    public static class AgencyCraftFlagPolicy
    {
        public static bool ShouldApply(bool featureOn, Guid myAgency, string agencyFlag, bool agencyFlagInstalled,
            string incomingFlag, string playerDefaultFlag, string gameFlag)
        {
            if (!featureOn || myAgency == Guid.Empty || !agencyFlagInstalled) return false;
            if (!AgencyIdentityDefaults.IsSafeFlagUrl(agencyFlag)) return false;
            if (Same(agencyFlag, AgencyIdentityDefaults.DefaultFlagUrl)) return false;
            if (string.IsNullOrEmpty(incomingFlag)) return true;
            return Same(incomingFlag, playerDefaultFlag) || Same(incomingFlag, gameFlag) ||
                   Same(incomingFlag, AgencyIdentityDefaults.DefaultFlagUrl);
        }

        private static bool Same(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
