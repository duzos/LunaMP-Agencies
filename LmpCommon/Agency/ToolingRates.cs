namespace LmpCommon.Agency
{
    /// <summary>Single home of the tooling price defaults. Server settings, the settings wire message and the client all start from these.</summary>
    public static class ToolingDefaults
    {
        public const double ToolingCost = 5;
        public const double TooledLaunch = 0.1;
        public const double UntooledLaunch = 2.0;
        public const double Combine = 0;
        /// <summary>What a server that predates the untooled multiplier charges: face value.</summary>
        public const double LegacyUntooledLaunch = 1.0;
    }

    /// <summary>The four tooling price multipliers handed to <see cref="ToolingPolicy.Quote"/>.</summary>
    public sealed class ToolingRates
    {
        public readonly double Tooling, TooledLaunch, UntooledLaunch, Combine;
        public ToolingRates(double tooling, double tooledLaunch, double untooledLaunch, double combine)
        {
            Tooling = tooling; TooledLaunch = tooledLaunch; UntooledLaunch = untooledLaunch; Combine = combine;
        }
        public static ToolingRates Default => new ToolingRates(ToolingDefaults.ToolingCost, ToolingDefaults.TooledLaunch, ToolingDefaults.UntooledLaunch, ToolingDefaults.Combine);
        internal bool Valid => ToolingPolicy.FiniteNonNegative(Tooling) && ToolingPolicy.FiniteNonNegative(TooledLaunch) && ToolingPolicy.FiniteNonNegative(UntooledLaunch) && ToolingPolicy.FiniteNonNegative(Combine);
    }
}
