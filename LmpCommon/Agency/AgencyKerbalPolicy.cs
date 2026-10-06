namespace LmpCommon.Agency
{
    /// <summary>Only new agencies may opt out of default crew; an initialized roster is authoritative.</summary>
    public static class AgencyKerbalPolicy
    {
        public static bool ShouldSeedDefaults(bool rosterExists, bool isNewAgency, bool perAgencyEnabled, bool zeroStartingEnabled)
            => perAgencyEnabled && !rosterExists && (!isNewAgency || !zeroStartingEnabled);
    }
}
