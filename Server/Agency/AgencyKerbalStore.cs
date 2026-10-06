using LmpCommon.Agency;
using Server.Diagnostics;
using Server.Settings.Structures;
using Server.Log;
using Server.Properties;
using Server.System;
using System;
using System.IO;

namespace Server.Agency
{
    /// <summary>
    /// Per-agency kerbal roster. Each agency gets its own subfolder under
    /// <c>Universe/Agencies/&lt;id&gt;/Kerbals/</c> with kerbal ConfigNode files
    /// (one per kerbal, filename = kerbal name). Active only when
    /// <c>GeneralSettings.AgencyKerbalsPerAgency</c> is true.
    /// </summary>
    public static class AgencyKerbalStore
    {
        public const string KerbalFileFormat = ".txt";

        public static string KerbalsPath(Guid agencyId) =>
            Path.Combine(AgencyStore.AgencyDirectory(agencyId), "Kerbals");

        public static string KerbalPath(Guid agencyId, string kerbalName) =>
            Path.Combine(KerbalsPath(agencyId), kerbalName + KerbalFileFormat);

        private static readonly object RosterInitializationLock = new object();

        /// <summary>Initialize a newly created agency according to the configured starting-crew policy.</summary>
        public static void InitializeNewAgency(Guid agencyId) => InitializeRoster(agencyId, isNewAgency: true);

        /// <summary>
        /// Legacy fallback for an absent roster. Existing directories, even empty, are authoritative.
        /// Once the one-time global migration has completed, an absent roster is no longer legacy:
        /// it is initialized exactly like a newly created agency.
        /// </summary>
        public static void EnsureDefaultRoster(Guid agencyId) => InitializeRoster(agencyId, isNewAgency: false);

        private static void InitializeRoster(Guid agencyId, bool isNewAgency)
        {
            var perAgency = GeneralSettings.SettingsStore.AgencyKerbalsPerAgency;
            var zeroStarting = GeneralSettings.SettingsStore.AgencyZeroStartingKerbals;
            if (!perAgency) return;
            var dir = KerbalsPath(agencyId);
            string reason;
            var count = 0;
            lock (RosterInitializationLock)
            {
                var exists = FileHandler.FolderExists(dir);
                if (!exists && !isNewAgency && GlobalMigrationCompleted) isNewAgency = true;
                var seedDefaults = AgencyKerbalPolicy.ShouldSeedDefaults(exists, isNewAgency, perAgency, zeroStarting);
                if (!exists) FileHandler.FolderCreate(dir);
                if (seedDefaults)
                {
                    FileHandler.CreateFile(Path.Combine(dir, "Jebediah Kerman.txt"), Resources.Jebediah_Kerman);
                    FileHandler.CreateFile(Path.Combine(dir, "Bill Kerman.txt"), Resources.Bill_Kerman);
                    FileHandler.CreateFile(Path.Combine(dir, "Bob Kerman.txt"), Resources.Bob_Kerman);
                    FileHandler.CreateFile(Path.Combine(dir, "Valentina Kerman.txt"), Resources.Valentina_Kerman);
                }
                reason = exists ? "existing-roster" : !isNewAgency ? "legacy-defaults" : seedDefaults ? "new-defaults" : "new-empty";
                if (PlaytestDiagnostics.Enabled) count = Directory.GetFiles(dir).Length;
            }
            PlaytestDiagnostics.Write("kerbal.initialize", () => $"agency={agencyId} reason={reason} count={count} perAgency={perAgency} zeroStarting={zeroStarting}");
        }

        /// <summary>
        /// Marker written under <c>Universe/Agencies/</c> once the global-to-agency kerbal
        /// migration has run, so it only ever happens once per universe.
        /// </summary>
        public const string MigrationMarkerFileName = ".kerbals-migrated";

        public static string MigrationMarkerPath => Path.Combine(AgencyStore.AgenciesPath, MigrationMarkerFileName);

        /// <summary>True once the one-time global-to-agency kerbal migration has completed for this universe.</summary>
        public static bool GlobalMigrationCompleted => FileHandler.FileExists(MigrationMarkerPath);

        /// <summary>
        /// First-time migration: seed every existing agency with the current
        /// contents of the global <c>Universe/Kerbals/</c> folder. Runs once
        /// per universe, when <c>AgencyKerbalsPerAgency</c> is first on for a
        /// server that was previously running with global kerbals; the
        /// <see cref="MigrationMarkerFileName"/> marker records that it has
        /// run, including when there was nothing to copy. An agency that
        /// already has a Kerbals folder is left alone.
        /// After the marker exists, an agency without a Kerbals folder (for
        /// example one created while per-agency kerbals were off) is
        /// initialized as a new agency, honouring
        /// <c>AgencyZeroStartingKerbals</c>, instead of inheriting the global roster.
        /// </summary>
        public static void MigrateGlobalKerbalsIfNeeded()
        {
            if (GlobalMigrationCompleted)
            {
                foreach (var agency in AgencyStore.Agencies.Values)
                {
                    if (!FileHandler.FolderExists(KerbalsPath(agency.Id)))
                        InitializeNewAgency(agency.Id);
                }
                return;
            }

            CopyGlobalRosterToAgencies();
            WriteMigrationMarker();
        }

        private static void WriteMigrationMarker()
        {
            try
            {
                var path = MigrationMarkerPath;
                var dir = Path.GetDirectoryName(path);
                if (!FileHandler.FolderExists(dir)) FileHandler.FolderCreate(dir);
                FileHandler.WriteToFile(path, $"migrated-utc-ticks={DateTime.UtcNow.Ticks}\n");
            }
            catch (Exception e)
            {
                LunaLog.Warning($"[Agency] Could not write the kerbal migration marker; the migration will be re-checked on the next start: {e.Message}");
            }
        }

        private static void CopyGlobalRosterToAgencies()
        {
            if (!FileHandler.FolderExists(KerbalSystem.KerbalsPath))
            {
                LunaLog.Debug("[Agency] Kerbal migration skipped: no global Kerbals folder.");
                return;
            }

            var globalFiles = FileHandler.GetFilesInPath(KerbalSystem.KerbalsPath);
            if (globalFiles.Length == 0) return;

            foreach (var agency in AgencyStore.Agencies.Values)
            {
                var target = KerbalsPath(agency.Id);
                if (FileHandler.FolderExists(target))
                {
                    // Agency already has a roster; leave it alone.
                    continue;
                }

                FileHandler.FolderCreate(target);
                foreach (var src in globalFiles)
                {
                    var dest = Path.Combine(target, Path.GetFileName(src));
                    try { FileHandler.FileCopy(src, dest); }
                    catch (Exception e) { LunaLog.Warning($"[Agency] Failed to seed kerbal {Path.GetFileName(src)} into '{agency.Name}': {e.Message}"); }
                }
                PlaytestDiagnostics.Write("kerbal.migration", () => $"agency={agency.Id} reason=global-copy count={Directory.GetFiles(target).Length} zeroStarting={GeneralSettings.SettingsStore.AgencyZeroStartingKerbals}");
                LunaLog.Info($"[Agency] Seeded '{agency.Name}' with {globalFiles.Length} kerbals from the global roster.");
            }
        }
    }
}
