using CommNet;
using LmpCommon.Enums;

namespace LmpClient.Systems.SettingsSys
{
    public class SettingsServerStructure
    {
        public WarpMode WarpMode { get; set; } = WarpMode.Subspace;
        public GameParameters ServerParameters { get; set; }
        public GameParameters.AdvancedParams ServerAdvancedParameters { get; set; } = new GameParameters.AdvancedParams();
        public CommNetParams ServerCommNetParameters { get; set; } = new CommNetParams();
        public GameMode GameMode { get; set; }
        public TerrainQuality TerrainQuality { get; set; }
        public bool AllowCheats { get; set; }
        public bool AllowAdmin { get; set; }
        public bool AllowSackKerbals { get; set; }
        public int MaxNumberOfAsteroids { get; set; }
        public int MaxNumberOfComets { get; set; }
        public string ConsoleIdentifier { get; set; } = "";
        public GameDifficulty GameDifficulty { get; set; }
        public float SafetyBubbleDistance { get; set; } = 100f;
        public int MaxVesselParts { get; set; }
        public int VesselUpdatesMsInterval { get; set; }
        public int SecondaryVesselUpdatesMsInterval { get; set; }
        public int MinScreenshotIntervalMs { get; set; }
        public int MaxScreenshotWidth { get; set; }
        public int MaxScreenshotHeight { get; set; }
        public int MinCraftLibraryRequestIntervalMs { get; set; }
        public bool PrintMotdInChat { get; set; }

        // Agencies-feature flags propagated from server. Default false so any
        // code that reads them when the client isn't fully connected sees
        // legacy behaviour.
        public bool AgencyExperimentsPerAgency { get; set; }
        public bool AgencyKerbalsPerAgency { get; set; }
        public bool AgencyScansatPerAgency { get; set; }
        public bool AgencyContractsPoolPerAgency { get; set; }
        public bool AgencyCommNetPerAgency { get; set; }
    }
}
