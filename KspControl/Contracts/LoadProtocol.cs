using System.Text.RegularExpressions;

namespace KspControl.Contracts
{
    /// <summary>Reason codes of editor_load_craft (plan R3-section 4, R4-section 6.4), beyond the shared operation codes.</summary>
    public static class LoadReasons
    {
        /// <summary>The pipeline changed the node and allowUpgrade was false. Refused with notDispatched=true.</summary>
        public const string CraftRequiresUpgrade = "craft_requires_upgrade";
        /// <summary>The upgrade pipeline did not report success (it threw, or opened its failure popup). Refused with notDispatched=true.</summary>
        public const string CraftUpgradeFailed = "craft_upgrade_failed";
        public const string CraftInvalidLinks = "craft_invalid_links";
        public const string ModuleNotInstalled = "module_not_installed";
        public const string SourceChangedDuringLoad = "source_changed_during_load";
        /// <summary>The source file's hash is not expectedSha256.</summary>
        public const string FileChanged = "file_changed";
        public const string CraftNotFound = "craft_not_found";
        public const string CraftUnreadable = "craft_unreadable";
        public const string CraftTooLarge = "craft_too_large";
    }

    /// <summary>Argument bounds of editor_load_craft, shared by host validation and bridge checks.</summary>
    public static class LoadLimits
    {
        public const int FileNameMax = 70;
        private static readonly Regex Sha256 = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        public static bool IsSha256(string value) { return value != null && Sha256.IsMatch(value); }
        public static bool IsFacility(string value) { return value == "VAB" || value == "SPH"; }
    }
}
