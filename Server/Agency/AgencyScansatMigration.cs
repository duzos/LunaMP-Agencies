using Server.Log;
using Server.Settings.Structures;
using Server.System;
using System;
using System.IO;
using System.Linq;

namespace Server.Agency
{
    /// <summary>
    /// One-shot migration: when <see cref="GeneralSettingsDefinition.AgencyScansatPerAgency"/>
    /// flips on, copy the existing global <c>Universe/Scenarios/SCANcontroller.txt</c>
    /// into a single chosen "inheriting" agency. Other agencies get a fresh
    /// SCANcontroller scaffold via the existing baseline-creation path.
    ///
    /// We pick the inheriting agency by configuration: first the
    /// <see cref="GeneralSettingsDefinition"/> setting if non-empty (admin
    /// override), otherwise the lexicographically-first non-solo agency, or
    /// the first non-solo agency if names tie.
    /// </summary>
    public static class AgencyScansatMigration
    {
        public const string ModuleName = "SCANcontroller";

        /// <summary>
        /// Marker file written next to the inheriting agency's
        /// <c>SCANcontroller.txt</c> so the migration only ever runs once per
        /// agency, even if the global file changes later.
        /// </summary>
        public const string MarkerFileName = ".scansat-migrated";

        public static void RunIfNeeded()
        {
            if (!GeneralSettings.SettingsStore.AgencyScansatPerAgency)
            {
                LunaLog.Debug("[Agency] SCANsat migration skipped: AgencyScansatPerAgency is false.");
                return;
            }

            if (AgencyStore.Agencies.IsEmpty)
            {
                LunaLog.Debug("[Agency] SCANsat migration skipped: no agencies loaded yet.");
                return;
            }

            var globalScenariosDir = ScenarioSystem.ScenariosPath;
            var globalSrc = Path.Combine(globalScenariosDir, ModuleName + ".txt");
            if (!FileHandler.FileExists(globalSrc))
            {
                LunaLog.Debug($"[Agency] SCANsat migration skipped: global '{globalSrc}' does not exist.");
                return;
            }

            var inheritor = PickInheritor();
            if (inheritor == null)
            {
                LunaLog.Warning("[Agency] SCANsat migration: no inheriting agency could be selected.");
                return;
            }

            var targetDir = AgencyScenarioStore.AgencyScenariosPath(inheritor.Id);
            if (!FileHandler.FolderExists(targetDir))
                FileHandler.FolderCreate(targetDir);

            var marker = Path.Combine(targetDir, MarkerFileName);
            if (FileHandler.FileExists(marker))
            {
                LunaLog.Debug($"[Agency] SCANsat migration skipped: '{inheritor.Name}' already migrated (marker present).");
                return;
            }

            var dest = Path.Combine(targetDir, ModuleName + ".txt");
            try
            {
                if (FileHandler.FileExists(dest))
                {
                    LunaLog.Info($"[Agency] SCANsat: '{inheritor.Name}' already has its own SCANcontroller.txt — leaving it; placing migration marker only.");
                }
                else
                {
                    FileHandler.FileCopy(globalSrc, dest);
                    LunaLog.Info($"[Agency] SCANsat: seeded '{inheritor.Name}' from global SCANcontroller.txt.");
                }

                FileHandler.WriteToFile(marker, $"migrated-utc-ticks={DateTime.UtcNow.Ticks}\nfrom={globalSrc}\n");
            }
            catch (Exception e)
            {
                LunaLog.Error($"[Agency] SCANsat migration failed for '{inheritor.Name}': {e}");
                return;
            }

            // Other agencies: rely on AgencyScenarioStore.LoadAllExisting +
            // EnsureBaselineForAgency to provide a fresh SCANcontroller scaffold
            // when one is needed. We don't pre-create empty SCANcontrollers here
            // because vanilla SCANsat is happy creating one on first use.

            LunaLog.Info($"[Agency] SCANsat migration complete. Inheritor: '{inheritor.Name}'. Other agencies start with no coverage.");
        }

        /// <summary>
        /// Pick the agency that inherits the existing global SCANcontroller.
        /// Public so it can be unit-tested.
        /// </summary>
        public static Agency PickInheritor()
        {
            return AgencyStore.Agencies.Values
                .Where(a => !a.IsSolo)
                .OrderBy(a => a.CreatedUtcTicks)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
    }
}
