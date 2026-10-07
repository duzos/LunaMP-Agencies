using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KspControl.Bridge
{
    /// <summary>One file in a directory listing.</summary>
    internal sealed class FileEntry
    {
        public string Path { get; }
        public string Name { get; }
        public long Length { get; }
        /// <summary>Last write time, UTC ticks.</summary>
        public long WriteTicks { get; }
        public FileEntry(string path, string name, long length, long writeTicks) { Path = path; Name = name; Length = length; WriteTicks = writeTicks; }
        public bool SameAs(FileEntry other) { return other != null && Length == other.Length && WriteTicks == other.WriteTicks; }
    }

    /// <summary>
    /// The only file access of the operation layer (plan R3-section 9). Paths come from <c>CraftPaths</c>, which has already
    /// confined them; this interface adds no policy of its own. Main thread only.
    /// </summary>
    internal interface IOperationFiles
    {
        bool Exists(string path);
        /// <summary>Throws <see cref="IOException"/> when the file is missing or unreadable.</summary>
        byte[] ReadAllBytes(string path);
        /// <summary>Creates the directory, writes a temporary sibling and moves (or replaces) it into place.</summary>
        void WriteAtomic(string path, byte[] bytes);
        /// <summary>
        /// Creates <paramref name="path"/> only if nothing is there: writes <c>kc-&lt;guid&gt;.tmp</c> beside it, flushes it and moves it into place.
        /// Throws <see cref="IOException"/> ("exists") when the target exists, leaving it untouched and the temporary file removed.
        /// </summary>
        void CreateNew(string path, byte[] bytes);
        /// <summary>
        /// Replaces an existing file through a temporary sibling and <c>File.Replace</c> with a <c>kc-&lt;guid&gt;.bak</c> backup. Throws <see cref="FileNotFoundException"/>
        /// when it is missing; never creates it. A plain <see cref="IOException"/> means the target was left exactly as it was; a
        /// <see cref="FileStateChangedException"/> means the target was touched (and put back, if that was possible).
        /// </summary>
        void ReplaceExisting(string path, byte[] bytes);
        /// <summary>The size in bytes, or -1 when the file does not exist.</summary>
        long FileLength(string path);
        /// <summary>Deletes the file. Does nothing when it does not exist.</summary>
        void Delete(string path);
        void Copy(string from, string to, bool overwrite);
        /// <summary>The files directly in a directory whose name ends with <paramref name="suffix"/>, or an empty list when it does not exist.</summary>
        IReadOnlyList<FileEntry> List(string directory, string suffix);
        /// <summary>True when the path itself is a reparse point (symbolic link or junction). False for a missing path.</summary>
        bool IsReparsePoint(string path);
    }

    /// <summary>
    /// A replace failed after it had already touched the target (it was moved aside, or new bytes landed). <see cref="Outcome"/> says what
    /// the file system holds now: restored_previous, restored_new, target_missing or target_changed. Callers must not treat it as "nothing happened".
    /// </summary>
    internal sealed class FileStateChangedException : IOException
    {
        public string Outcome { get; }
        public FileStateChangedException(string outcome, Exception inner) : base("file_state_changed: " + outcome, inner) { Outcome = outcome; }
    }

    /// <summary>
    /// Removes the temporary and backup files this layer writes beside ship files, and nothing else: only the exact names
    /// <c>kc-&lt;32 hex&gt;.tmp</c>, <c>kc-&lt;32 hex&gt;.bak</c> and the legacy <c>&lt;name&gt;.kspcontrol.&lt;32 hex&gt;.tmp</c>. A backup carries no pointer to its
    /// target, so only a backup this process left behind (its delete failed after a successful replace) is ever removed, and only while its
    /// target exists. Any other backup could be the sole copy of a ship, and is reported and left.
    /// </summary>
    internal static class ShipsSweeper
    {
        public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(5);
        private static readonly System.Text.RegularExpressions.Regex Temporary = new System.Text.RegularExpressions.Regex(@"^(kc-[0-9a-f]{32}|.+\.kspcontrol\.[0-9a-f]{32})\.tmp$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex Backup = new System.Text.RegularExpressions.Regex(@"^kc-[0-9a-f]{32}\.bak$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, KeyValuePair<string, long>> Pending = new Dictionary<string, KeyValuePair<string, long>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Remembers a backup whose delete failed after a replace that succeeded, so a later sweep may remove it.</summary>
        public static void RegisterBackup(string backupPath, string target, DateTime utcNow)
        { lock (Gate) Pending[backupPath] = new KeyValuePair<string, long>(target, utcNow.Ticks); }

        public static void ForgetAll() { lock (Gate) Pending.Clear(); }

        private static DateTime Utc(long ticks) { return new DateTime(Math.Max(0, Math.Min(ticks, DateTime.MaxValue.Ticks)), DateTimeKind.Utc); }

        /// <summary>Sweeps one directory. Each action is added to <paramref name="declared"/> (class ships, action deleted or reported).</summary>
        public static void Sweep(IOperationFiles files, string directory, DateTime utcNow, List<DeclaredOutput> declared)
        {
            if (files == null || string.IsNullOrEmpty(directory)) return;
            IReadOnlyList<FileEntry> entries;
            try { entries = files.List(directory, null); } catch (IOException) { return; }
            foreach (var entry in entries)
            {
                try
                {
                    if (Temporary.IsMatch(entry.Name))
                    {
                        if (utcNow - Utc(entry.WriteTicks) < MinimumAge || files.IsReparsePoint(entry.Path)) continue;
                        files.Delete(entry.Path);
                        Add(declared, entry.Path, "deleted", "stale_temporary_file_swept");
                    }
                    else if (Backup.IsMatch(entry.Name))
                    {
                        KeyValuePair<string, long> known; bool tracked;
                        lock (Gate) tracked = Pending.TryGetValue(entry.Path, out known);
                        if (!tracked)
                        {
                            if (utcNow - Utc(entry.WriteTicks) >= MinimumAge) Add(declared, entry.Path, "reported", "backup_without_known_target_left_alone");
                            continue;
                        }
                        if (utcNow - Utc(known.Value) < MinimumAge) continue;
                        if (!files.Exists(known.Key)) { Add(declared, entry.Path, "reported", "backup_target_missing_left_alone"); continue; }
                        files.Delete(entry.Path);
                        lock (Gate) Pending.Remove(entry.Path);
                        Add(declared, entry.Path, "deleted", "stale_backup_swept");
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { Add(declared, entry.Path, "reported", "sweep_failed_" + error.GetType().Name); }
            }
        }

        private static void Add(List<DeclaredOutput> declared, string path, string action, string detail)
        { if (declared != null) declared.Add(new DeclaredOutput { Path = path, Class = "ships", Action = action, Detail = detail }); }
    }

    internal static class OperationHash
    {
        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(bytes ?? new byte[0])) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
        public static string Sha256Hex(string text) { return Sha256Hex(new UTF8Encoding(false).GetBytes(text ?? "")); }
    }

    /// <summary>The disk implementation. Pure System.IO, so it is tested against a temporary directory on both runtimes.</summary>
    internal sealed class DiskOperationFiles : IOperationFiles
    {
        private readonly Action<string, string, string> replace;
        public DiskOperationFiles() : this(null) { }
        /// <summary>The seam over <c>File.Replace(source, destination, backup)</c>, so a test can simulate ReplaceFileW partial failures.</summary>
        internal DiskOperationFiles(Action<string, string, string> replace) { this.replace = replace ?? ((source, destination, backup) => File.Replace(source, destination, backup)); }

        public bool Exists(string path) { return File.Exists(path); }
        public byte[] ReadAllBytes(string path) { return File.ReadAllBytes(path); }
        public long FileLength(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : -1; } catch (IOException) { return -1; } }

        private static string Sibling(string path, string extension) { return Path.Combine(Path.GetDirectoryName(path) ?? "", "kc-" + Guid.NewGuid().ToString("N") + extension); }

        public void WriteAtomic(string path, byte[] bytes)
        {
            var temporary = WriteTemporary(path, bytes);
            try
            {
                if (File.Exists(path)) ReplaceThrough(temporary, path);
                else File.Move(temporary, path);
            }
            catch (FileStateChangedException) { throw; }
            catch { try { File.Delete(temporary); } catch (IOException) { } throw; }
        }

        private static string WriteTemporary(string path, byte[] bytes)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = Sibling(path, ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            }
            catch { try { File.Delete(temporary); } catch (IOException) { } throw; }
            return temporary;
        }

        public void CreateNew(string path, byte[] bytes)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("exists");
            var temporary = WriteTemporary(path, bytes);
            try { File.Move(temporary, path); } // fails when the target appeared meanwhile: File.Move never overwrites
            catch { try { File.Delete(temporary); } catch (IOException) { } throw; }
        }

        public void ReplaceExisting(string path, byte[] bytes)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("missing", path);
            var temporary = WriteTemporary(path, bytes);
            try { ReplaceThrough(temporary, path); }
            catch (FileStateChangedException) { throw; } // the temporary may be the only copy of the new bytes: ReplaceThrough decided what to keep
            catch { try { File.Delete(temporary); } catch (IOException) { } throw; }
        }

        private static string Stamp(string path)
        {
            try { if (!File.Exists(path)) return null; var info = new FileInfo(path); return info.Length + ":" + info.LastWriteTimeUtc.Ticks; }
            catch (IOException) { return "unreadable"; }
        }

        private static bool TryDelete(string path) { try { File.Delete(path); return true; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// File.Replace with a backup. ReplaceFileW can fail half way (ERROR_UNABLE_TO_MOVE_REPLACEMENT, 1176 and 1177) with the target
        /// already moved aside, so on any failure the target is examined: when it is gone the backup (or, failing that, the temporary file)
        /// is moved back; the temporary file is deleted only once the target is confirmed whole. A target that was touched at all raises
        /// <see cref="FileStateChangedException"/>; a target left exactly as it was rethrows the original failure.
        /// </summary>
        private void ReplaceThrough(string temporary, string path)
        {
            var before = Stamp(path);
            var backup = Sibling(path, ".bak");
            try { replace(temporary, path, backup); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                string outcome = null;
                if (!File.Exists(path))
                {
                    outcome = "target_missing";
                    try
                    {
                        if (File.Exists(backup)) { File.Move(backup, path); outcome = "restored_previous"; TryDelete(temporary); }
                        else if (File.Exists(temporary)) { File.Move(temporary, path); outcome = "restored_new"; }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                else if (Stamp(path) != before)
                {
                    outcome = "target_changed";
                    try { if (File.Exists(temporary) && SameBytes(File.ReadAllBytes(temporary), File.ReadAllBytes(path))) TryDelete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (outcome != null) throw new FileStateChangedException(outcome, error);
                TryDelete(temporary);
                if (File.Exists(backup)) TryDelete(backup);
                throw;
            }
            if (File.Exists(backup) && !TryDelete(backup)) ShipsSweeper.RegisterBackup(backup, path, DateTime.UtcNow);
        }

        public void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (UnauthorizedAccessException) { throw new IOException("delete_denied"); }
        }

        public void Copy(string from, string to, bool overwrite)
        {
            var directory = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.Copy(from, to, overwrite);
        }

        public IReadOnlyList<FileEntry> List(string directory, string suffix)
        {
            var result = new List<FileEntry>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return result;
            foreach (var path in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(path);
                if (suffix != null && !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(path);
                result.Add(new FileEntry(path, name, info.Length, info.LastWriteTimeUtc.Ticks));
            }
            return result;
        }

        public bool IsReparsePoint(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return false;
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException) { return true; } // unreadable attributes: treat as unsafe
            catch (UnauthorizedAccessException) { return true; }
        }
    }
}
