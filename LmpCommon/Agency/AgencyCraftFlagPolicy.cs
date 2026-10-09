using System;

namespace LmpCommon.Agency
{
    /// <summary>
    /// Pure rule for launching a craft with the agency flag (plan 41 S2). Only a craft still on the stock default flag
    /// counts as "no flag chosen". The player's own default is NOT treated as unchosen: LMP stores every VAB mission-flag
    /// pick as SelectedFlag (FlagEvents.OnMissionFlagSelect) and the KSC flag pole sets the game flag, so both are
    /// explicit choices. The craft's saved missionFlag is checked too, because the KSC launch dialog passes the game
    /// flag rather than the craft's own flag.
    /// </summary>
    public static class AgencyCraftFlagPolicy
    {
        public static bool ShouldApply(bool featureOn, Guid myAgency, string agencyFlag, bool agencyFlagInstalled,
            string incomingFlag, string craftMissionFlag)
        {
            if (!featureOn || myAgency == Guid.Empty || !agencyFlagInstalled) return false;
            if (!AgencyIdentityDefaults.IsSafeFlagUrl(agencyFlag)) return false;
            if (IsStockDefault(agencyFlag)) return false;
            return IsUnchosen(incomingFlag) && IsUnchosen(craftMissionFlag);
        }

        private static bool IsUnchosen(string flag) => string.IsNullOrEmpty(flag) || IsStockDefault(flag);

        private static bool IsStockDefault(string flag) =>
            string.Equals(flag, AgencyIdentityDefaults.DefaultFlagUrl, StringComparison.OrdinalIgnoreCase);
    }
}
