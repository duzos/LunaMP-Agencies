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

        /// <summary>
        /// Ensure the agency's Kerbals folder exists and contains the four
        /// canonical starting kerbals. Idempotent — won't overwrite existing
        /// kerbals. Called whenever an agency is created or migrated.
        /// </summary>
        public static void EnsureDefaultRoster(Guid agencyId)
        {
            var dir = KerbalsPath(agencyId);
            if (!FileHandler.FolderExists(dir))
                FileHandler.FolderCreate(dir);

            FileHandler.CreateFile(Path.Combine(dir, "Jebediah Kerman.txt"), Resources.Jebediah_Kerman);
            FileHandler.CreateFile(Path.Combine(dir, "Bill Kerman.txt"), Resources.Bill_Kerman);
            FileHandler.CreateFile(Path.Combine(dir, "Bob Kerman.txt"), Resources.Bob_Kerman);
            FileHandler.CreateFile(Path.Combine(dir, "Valentina Kerman.txt"), Resources.Valentina_Kerman);
        }

        /// <summary>
        /// First-time migration: seed every existing agency with the current
        /// contents of the global <c>Universe/Kerbals/</c> folder. Runs once
        /// when <c>AgencyKerbalsPerAgency</c> flips on for a server that was
        /// previously running with global kerbals. Idempotent — an agency
        /// that already has a Kerbals folder is left alone.
        /// </summary>
        public static void MigrateGlobalKerbalsIfNeeded()
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
                LunaLog.Info($"[Agency] Seeded '{agency.Name}' with {globalFiles.Length} kerbals from the global roster.");
            }
        }
    }
}
