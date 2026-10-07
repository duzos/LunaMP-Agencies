using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
namespace KspControl.EditorModel
{
    public sealed class PathCheck
    {
        public bool Ok { get; private set; }
        public string ReasonCode { get; private set; }
        public string FullPath { get; private set; }
        public static PathCheck Success(string path) { return new PathCheck { Ok = true, FullPath = path }; }
        public static PathCheck Fail(string code) { return new PathCheck { Ok = false, ReasonCode = code }; }
    }
    /// <summary>
    /// Pure path confinement (R1-section 9). Roots are injected; the only side effect is the injected reparse-point predicate.
    /// Ships files live flat in saves/&lt;Save&gt;/Ships/&lt;facility&gt;; workspace files live in KspControlData/&lt;Save&gt;/.
    /// </summary>
    public sealed class CraftPaths
    {
        private static readonly Regex NameRegex = new Regex("^[A-Za-z0-9 ._-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly Regex IdRegex = new Regex("^[A-Za-z0-9_-]{8,128}$", RegexOptions.CultureInvariant);
        private static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        public const string CraftExtension = ".craft";
        private readonly string kspRoot, saveFolder;
        private readonly Func<string, bool> isReparsePoint;
        public CraftPaths(string kspRoot, string saveFolder, Func<string, bool> isReparsePoint)
        {
            if (string.IsNullOrEmpty(kspRoot) || !Path.IsPathRooted(kspRoot)) throw new ArgumentException("ksp root must be absolute", "kspRoot");
            if (!IsName(saveFolder)) throw new ArgumentException("invalid save folder", "saveFolder");
            this.kspRoot = Path.GetFullPath(kspRoot); this.saveFolder = saveFolder;
            this.isReparsePoint = isReparsePoint ?? (p => false);
        }
        public static bool IsName(string name)
        {
            if (name == null || !NameRegex.IsMatch(name)) return false;
            if (name[0] == '.' || name.Contains("..")) return false;
            if (name.EndsWith(" ", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal)) return false;
            var stem = name; int dot = stem.IndexOf('.'); if (dot >= 0) stem = stem.Substring(0, dot);
            foreach (var r in Reserved) if (string.Equals(stem.TrimEnd(' '), r, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        public static bool IsId(string id) { return id != null && IdRegex.IsMatch(id); }
        /// <summary>Replaces disallowed characters with '_', drops leading dots/spaces, collapses "..", truncates to 64; empty becomes "craft".</summary>
        public static string Sanitize(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? "")
                sb.Append(c < 128 && (char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '_' || c == '-') ? c : '_');
            var s = sb.ToString();
            while (s.Contains("..")) s = s.Replace("..", ".");
            s = s.TrimStart('.', ' ');
            if (s.Length > 64) s = s.Substring(0, 64);
            s = s.TrimEnd('.', ' ');
            if (!IsName(s)) s = "craft";
            return s;
        }
        private string Saves { get { return Path.Combine(kspRoot, "saves"); } }
        public string ShipsDirectory(string facility)
        {
            if (facility != "VAB" && facility != "SPH") return null;
            return Path.Combine(Saves, saveFolder, "Ships", facility);
        }
        /// <summary>Resolves a bare file name WITHOUT extension (".craft" is implied), as used for saves.</summary>
        public PathCheck ResolveNewShip(string facility, string name)
        {
            if (facility != "VAB" && facility != "SPH") return PathCheck.Fail("facility_mismatch");
            if (!IsName(name)) return PathCheck.Fail("invalid_file_name");
            if (name.EndsWith(CraftExtension, StringComparison.OrdinalIgnoreCase)) return PathCheck.Fail("invalid_file_name");
            return ResolveShipFile(facility, name + CraftExtension);
        }
        /// <summary>Resolves an existing file name including the ".craft" extension, as listed by craft_list.</summary>
        public PathCheck ResolveExistingShip(string facility, string fileName)
        {
            if (facility != "VAB" && facility != "SPH") return PathCheck.Fail("facility_mismatch");
            if (fileName == null) return PathCheck.Fail("invalid_file_name");
            if (fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || Path.IsPathRooted(fileName)) return PathCheck.Fail("path_outside_save");
            if (!fileName.EndsWith(CraftExtension, StringComparison.OrdinalIgnoreCase)) return PathCheck.Fail("invalid_extension");
            var stem = fileName.Substring(0, fileName.Length - CraftExtension.Length);
            if (!IsName(stem)) return PathCheck.Fail("invalid_file_name");
            return ResolveShipFile(facility, fileName);
        }
        private PathCheck ResolveShipFile(string facility, string fileName)
        {
            var dir = ShipsDirectory(facility);
            var full = Path.GetFullPath(Path.Combine(dir, fileName));
            var dirFull = Path.GetFullPath(dir);
            if (!string.Equals(Path.GetDirectoryName(full), dirFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return PathCheck.Fail("path_outside_save");
            return CheckAncestors(full, Saves);
        }
        private PathCheck CheckAncestors(string full, string stopAt)
        {
            var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return PathCheck.Fail("path_outside_save");
            var cur = full;
            while (cur != null && cur.Length > stop.Length)
            {
                if (isReparsePoint(cur)) return PathCheck.Fail("reparse_point");
                cur = Path.GetDirectoryName(cur);
            }
            if (isReparsePoint(stop)) return PathCheck.Fail("reparse_point");
            return PathCheck.Success(full);
        }
        // ----- workspace: <KSP root>/KspControlData/<save>/{staging,recovery,thumbs-backup,ledger.json} -----
        public string WorkspaceRoot { get { return Path.Combine(kspRoot, "KspControlData", saveFolder); } }
        public string ControlDirectory { get { return Path.Combine(kspRoot, "KspControlData", "control"); } }
        public string SuspensionsFile { get { return Path.Combine(ControlDirectory, "suspensions.json"); } }
        public string LedgerFile { get { return Path.Combine(WorkspaceRoot, "ledger.json"); } }
        private static bool IsUnder(string full, string directory)
        {
            var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, dir, StringComparison.OrdinalIgnoreCase) || full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>
        /// True when tool writes may use this save's workspace. KspControlData/control holds the persisted suspensions, so no tool
        /// path may reach it, and a save literally named "control" would put its workspace there (carry-forward from P2.2).
        /// </summary>
        public bool WorkspaceAllowed { get { return !string.Equals(saveFolder, "control", StringComparison.OrdinalIgnoreCase); } }
        private PathCheck Workspace(string sub, string fileName)
        {
            var full = Path.GetFullPath(Path.Combine(WorkspaceRoot, sub, fileName));
            var root = Path.GetFullPath(Path.Combine(kspRoot, "KspControlData"));
            if (!WorkspaceAllowed || IsUnder(full, ControlDirectory)) return PathCheck.Fail("path_outside_save");
            return CheckAncestors(full, root);
        }
        /// <summary>The recovery, staging or thumbs-backup directory of this save, or null when the workspace is denied or unsafe.</summary>
        public string WorkspaceDirectory(string sub)
        {
            if (sub != "staging" && sub != "recovery" && sub != "thumbs-backup") return null;
            var check = Workspace(sub, "x");
            return check.Ok ? Path.GetDirectoryName(check.FullPath) : null;
        }
        /// <summary>Snapshot metadata beside the recovery craft: kc-snap-&lt;id&gt;.json.</summary>
        public PathCheck RecoveryMetaPath(string snapshotId)
        {
            return IsId(snapshotId) ? Workspace("recovery", "kc-snap-" + snapshotId + ".json") : PathCheck.Fail("invalid_snapshot_id");
        }
        // ----- KSP thumbnails: <KSP root>/thumbs/<Save>_<facility>_<name>.png (read and removed by housekeeping only) -----
        public string ThumbnailsDirectory { get { return Path.Combine(kspRoot, "thumbs"); } }
        public string ThumbnailPrefix(string facility) { return saveFolder + "_" + facility + "_"; }
        /// <summary>The thumbnail KSP writes for a ship file or ship name, or null when the facility or stem is not a plain name.</summary>
        public string ThumbnailFile(string facility, string stem)
        {
            if ((facility != "VAB" && facility != "SPH") || string.IsNullOrEmpty(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stem.Contains("..")) return null;
            return Path.Combine(ThumbnailsDirectory, ThumbnailPrefix(facility) + stem + ".png");
        }
        public PathCheck StagingPath(string requestId)
        {
            return IsId(requestId) ? Workspace("staging", "kc-" + requestId + CraftExtension) : PathCheck.Fail("invalid_request_id");
        }
        public PathCheck UpgradedStagingPath(string requestId)
        {
            return IsId(requestId) ? Workspace("staging", "kc-" + requestId + ".upgraded" + CraftExtension) : PathCheck.Fail("invalid_request_id");
        }
        public PathCheck RecoveryPath(string snapshotId)
        {
            return IsId(snapshotId) ? Workspace("recovery", "kc-snap-" + snapshotId + CraftExtension) : PathCheck.Fail("invalid_snapshot_id");
        }
        public PathCheck ThumbnailBackupPath(string requestId)
        {
            return IsId(requestId) ? Workspace("thumbs-backup", requestId + ".png") : PathCheck.Fail("invalid_request_id");
        }
    }
}
