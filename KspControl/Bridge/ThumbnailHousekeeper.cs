using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// Keeps the delayed thumbnail that KSP writes for a loaded craft out of the human's tree (plan R2-section 9, R3-section 9).
    /// Before a dispatch it records <c>&lt;KSP root&gt;/thumbs</c> and backs up any existing thumbnail named after the ship about to be
    /// loaded. Afterwards: a new or changed thumbnail named after one of our staging or recovery files is deleted; one named after
    /// a watched ship name is restored from its backup (or deleted when none existed); everything else new or changed is only
    /// reported, never touched. Pure over <see cref="IOperationFiles"/>.
    /// </summary>
    internal sealed class ThumbnailHousekeeper
    {
        private readonly IOperationFiles files;
        private readonly Pure.CraftPaths paths;
        private readonly string facility;
        private Dictionary<string, FileEntry> baseline;
        private readonly HashSet<string> workspaceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> shipTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ThumbnailHousekeeper(IOperationFiles files, Pure.CraftPaths paths, string facility)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.facility = facility;
        }

        /// <summary>
        /// Registers what the next dispatch may write. <paramref name="workspaceStems"/> are the file stems of staging and recovery files
        /// (for example kc-&lt;requestId&gt;); <paramref name="shipNames"/> are sanitised ship names. The first call also records the baseline.
        /// </summary>
        public void Watch(IEnumerable<string> workspaceStems, IEnumerable<string> shipNames, string backupId)
        {
            if (baseline == null) baseline = Snapshot();
            foreach (var stem in workspaceStems ?? Enumerable.Empty<string>())
            {
                var file = paths.ThumbnailFile(facility, stem);
                if (file != null) workspaceNames.Add(Path.GetFileName(file));
            }
            // One backup file per Watch call (the backup id names it), so a call should carry one ship name; extra names are watched without a backup.
            foreach (var ship in shipNames ?? Enumerable.Empty<string>())
            {
                var file = paths.ThumbnailFile(facility, ship);
                if (file == null) continue;
                var name = Path.GetFileName(file);
                if (shipTargets.ContainsKey(name) || workspaceNames.Contains(name)) continue;
                shipTargets[name] = file;
                if (!Pure.CraftPaths.IsId(backupId)) continue;
                var backupPath = paths.ThumbnailBackupPath(backupId);
                if (!backupPath.Ok) continue;
                try
                {
                    if (files.Exists(file) && !files.IsReparsePoint(file) && !backups.Values.Contains(backupPath.FullPath)) { files.Copy(file, backupPath.FullPath, true); backups[name] = backupPath.FullPath; }
                }
                catch (IOException) { /* no backup: a later change is then deleted rather than restored, and reported */ }
            }
        }

        private Dictionary<string, FileEntry> Snapshot()
        {
            var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var prefix = paths.ThumbnailPrefix(facility);
                foreach (var entry in files.List(paths.ThumbnailsDirectory, ".png"))
                    if (entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result[entry.Name] = entry;
            }
            catch (IOException) { /* an unreadable thumbs directory is reported as unobserved */ }
            return result;
        }

        private List<FileEntry> Changes()
        {
            var changed = new List<FileEntry>();
            if (baseline == null) return changed;
            foreach (var entry in Snapshot().Values)
            {
                FileEntry before;
                if (!baseline.TryGetValue(entry.Name, out before) || !before.SameAs(entry)) changed.Add(entry);
            }
            return changed;
        }

        /// <summary>True once a thumbnail named after a watched staging, recovery or ship name has been written or rewritten.</summary>
        public bool Observed()
        { return Changes().Any(c => workspaceNames.Contains(c.Name) || shipTargets.ContainsKey(c.Name)); }

        /// <summary>Applies the rules to everything that changed since the baseline and lists each action.</summary>
        public void Finish(List<DeclaredOutput> declared)
        {
            foreach (var entry in Changes().OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                try
                {
                    if (workspaceNames.Contains(entry.Name))
                    {
                        if (files.IsReparsePoint(entry.Path)) { declared.Add(Output(entry, "reported", "reparse_point_left_alone")); continue; }
                        files.Delete(entry.Path); declared.Add(Output(entry, "deleted", "named_after_staging_or_recovery_file"));
                    }
                    else if (shipTargets.ContainsKey(entry.Name))
                    {
                        string backup;
                        if (files.IsReparsePoint(entry.Path)) { declared.Add(Output(entry, "reported", "reparse_point_left_alone")); continue; }
                        if (backups.TryGetValue(entry.Name, out backup) && files.Exists(backup))
                        {
                            files.Copy(backup, entry.Path, true); declared.Add(Output(entry, "restored", "ship_named_thumbnail_restored_from_backup"));
                        }
                        else { files.Delete(entry.Path); declared.Add(Output(entry, "deleted", "ship_named_thumbnail_had_no_backup")); }
                    }
                    else declared.Add(Output(entry, "reported", "other_thumbnail_changed"));
                }
                catch (IOException error) { declared.Add(Output(entry, "reported", "housekeeping_failed_" + error.GetType().Name)); }
            }
            foreach (var backup in backups.Values.Distinct().ToList())
            {
                try { files.Delete(backup); declared.Add(new DeclaredOutput { Path = backup, Class = "thumbs-backup", Action = "deleted", Detail = "backup_removed" }); }
                catch (IOException) { declared.Add(new DeclaredOutput { Path = backup, Class = "thumbs-backup", Action = "reported", Detail = "backup_not_removed" }); }
            }
            backups.Clear();
        }

        private static DeclaredOutput Output(FileEntry entry, string action, string detail)
        { return new DeclaredOutput { Path = entry.Path, Class = "thumbnail", Action = action, Detail = detail }; }
    }
}
