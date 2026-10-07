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
        /// Creates <paramref name="path"/> only if nothing is there: writes <c>&lt;path&gt;.kspcontrol.&lt;guid&gt;.tmp</c>, flushes it and moves it into place.
        /// Throws <see cref="IOException"/> ("exists") when the target exists, leaving it untouched and the temporary file removed.
        /// </summary>
        void CreateNew(string path, byte[] bytes);
        /// <summary>Replaces an existing file through a temporary sibling and <c>File.Replace</c>. Throws <see cref="FileNotFoundException"/> when it is missing; never creates it.</summary>
        void ReplaceExisting(string path, byte[] bytes);
        /// <summary>Deletes the file. Does nothing when it does not exist.</summary>
        void Delete(string path);
        void Copy(string from, string to, bool overwrite);
        /// <summary>The files directly in a directory whose name ends with <paramref name="suffix"/>, or an empty list when it does not exist.</summary>
        IReadOnlyList<FileEntry> List(string directory, string suffix);
        /// <summary>True when the path itself is a reparse point (symbolic link or junction). False for a missing path.</summary>
        bool IsReparsePoint(string path);
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
        public bool Exists(string path) { return File.Exists(path); }
        public byte[] ReadAllBytes(string path) { return File.ReadAllBytes(path); }

        public void WriteAtomic(string path, byte[] bytes)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch { try { File.Delete(temporary); } catch (IOException) { } throw; }
        }

        private static string TemporarySibling(string path) { return path + ".kspcontrol." + Guid.NewGuid().ToString("N") + ".tmp"; }

        private static string WriteTemporary(string path, byte[] bytes)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = TemporarySibling(path);
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
            try { File.Replace(temporary, path, null); }
            catch { try { File.Delete(temporary); } catch (IOException) { } throw; }
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
