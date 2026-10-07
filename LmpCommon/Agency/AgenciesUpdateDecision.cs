namespace LmpCommon.Agency
{
    /// <summary>What the helper reported about the previous install attempt.</summary>
    public sealed class AgenciesLastResult
    {
        public bool Success;
        public int Build;
        public int PreviousBuild;
        public string Message;
    }

    public enum AgenciesUpdateAction
    {
        None,
        Prompt,
        AutoDownload,
        ShowFailure
    }

    public static class AgenciesUpdateDecision
    {
        /// <summary>
        /// Decides what to do about <paramref name="latest"/> while running <paramref name="current"/>.
        /// <paramref name="lastResult"/> may be null. <paramref name="failedBuild"/> is the persisted build whose last
        /// install failed or did not take effect (0 = none). <paramref name="forced"/> is set when the server demands a
        /// newer build: it overrides a skipped version but never a downgrade.
        /// <list type="bullet">
        /// <item>latest &lt;= current: None, always.</item>
        /// <item>The last install of <paramref name="latest"/> failed or did not take effect (a result for that build while it is still newer than current, or failedBuild == latest): ShowFailure, never automatic. A newer release clears it. A skipped build stays quiet unless forced, so Skip on the failure prompt sticks.</item>
        /// <item>Skipped and not forced: None.</item>
        /// <item>Automatic updates on: AutoDownload. Otherwise: Prompt.</item>
        /// </list>
        /// </summary>
        public static AgenciesUpdateAction Decide(int current, int latest, int skipped, bool auto, AgenciesLastResult lastResult, int failedBuild, bool forced)
        {
            if (latest <= current) return AgenciesUpdateAction.None;

            var skippedAndQuiet = skipped == latest && !forced;

            // Here latest > current, so a result recorded for latest (success or not) means it never took effect.
            var failedBefore = failedBuild == latest || (lastResult != null && lastResult.Build == latest);
            if (failedBefore) return skippedAndQuiet ? AgenciesUpdateAction.None : AgenciesUpdateAction.ShowFailure;

            if (skippedAndQuiet) return AgenciesUpdateAction.None;
            return auto ? AgenciesUpdateAction.AutoDownload : AgenciesUpdateAction.Prompt;
        }
    }
}
