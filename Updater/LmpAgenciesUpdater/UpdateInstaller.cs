// The install core of the agencies updater helper. It is compiled into the net48 helper exe and, as a linked file, into
// ServerTest (net10.0) so it can be tested. Keep it C# 7.3 and BCL only: the helper must not ship any extra DLL, so no
// Newtonsoft here. HelperCommandLine lives in its own file next to this one.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LmpAgenciesUpdater
{
    public sealed class InstallResult
    {
        public bool Success;
        public string Message;
        public int Build;
        public int PreviousBuild;
        /// <summary>True when the game or server was still running when the wait timed out.</summary>
        public bool TimedOut;
    }

    public static class UpdateInstaller
    {
        /// <summary>Top-level server entries the update never writes. A package that contains one is rejected.</summary>
        public static readonly string[] ServerProtected = { "Universe", "Config", "logs", "Plugins", "update-staging" };

        // Names handed to the fault injector, in the order they are reached. A test throws from one of them to simulate a
        // failure at that point and checks that everything was put back.
        public const string FaultClientOldMoved = "client-old-moved";
        public const string FaultClientNewInstalled = "client-new-installed";
        public const string FaultClientHarmonySwapped = "client-harmony-swapped";
        public const string FaultServerBackedUp = "server-backed-up";
        public const string FaultServerCopied = "server-copied";

        private const string InvalidPackage = "Invalid update package";
        private const string ClientModPath = "GameData/LunaMultiplayer/";
        private const string ClientHarmonyDll = "GameData/000_Harmony/0Harmony.dll";
        private const string MutexPrefix = "Global\\LmpAgenciesUpdater-";

        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static readonly Regex VersionObject = new Regex("\"VERSION\"\\s*:\\s*\\{([^}]*)\\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex VersionString = new Regex("\"VERSION\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex VersionPart = new Regex("\"(MAJOR|MINOR|PATCH|BUILD)\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LeadingDigits = new Regex("^\\s*v?(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A failure whose message is shown to the user as it is.</summary>
        private class InstallException : Exception
        {
            public InstallException(string message) : base(message) { }
        }

        /// <summary>The game or server was still running when the wait ran out.</summary>
        private sealed class TimedOutException : InstallException
        {
            public TimedOutException(string message) : base(message) { }
        }

        private sealed class State
        {
            public int Previous;
            public bool ExtractTouched;
        }

        /// <summary>
        /// Waits for the game or server to exit, verifies and extracts the package, validates its layout and swaps it in
        /// (with a backup and a rollback). Every outcome writes the result file. <paramref name="waitForExit"/> gets the pid
        /// and a timeout (null = wait forever) and returns false if the process was still running at the deadline.
        /// </summary>
        public static InstallResult Run(HelperOptions o, Func<int, TimeSpan?, bool> waitForExit, Action<string> faultInjector = null)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (waitForExit == null) throw new ArgumentNullException(nameof(waitForExit));

            var result = Execute(o, waitForExit, faultInjector);
            try
            {
                HelperCommandLine.WriteResult(o.Result, result.Success, result.Build, result.PreviousBuild, result.Message);
            }
            catch (Exception)
            {
                // The outcome is still returned to the caller; there is nowhere else to report a result file problem.
            }
            return result;
        }

        /// <summary>
        /// Takes the single-instance lock for one install target. False when another helper for the same target holds
        /// it. The caller releases it (on the same thread) with ReleaseMutex and Dispose.
        /// </summary>
        public static bool TryAcquire(string target, out Mutex mutex)
        {
            mutex = null;
            Mutex created;
            try
            {
                bool createdNew;
                created = new Mutex(true, MutexName(target), out createdNew);
                if (!createdNew)
                {
                    created.Dispose();
                    return false;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // the mutex exists and belongs to somebody we cannot open it from: somebody else is installing
                return false;
            }
            mutex = created;
            return true;
        }

        internal static string MutexName(string target)
        {
            var normalized = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(MutexPrefix);
                for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>The installed agencies build: client Target/LunaMultiplayer/Updater/build.txt, server Target/Updater/build.txt. Missing or unreadable is 0.</summary>
        internal static int InstalledBuild(HelperOptions o)
        {
            var path = o.Mode == HelperMode.Client
                ? Path.Combine(o.Target, "LunaMultiplayer", "Updater", "build.txt")
                : Path.Combine(o.Target, "Updater", "build.txt");
            return ReadBuildFile(path);
        }

        /// <summary>
        /// Whether the package's Harmony should replace the installed one: when nothing usable is installed, when either
        /// version is unknown, or when the package's is the same or newer.
        /// </summary>
        internal static bool ShouldReplaceHarmony(string installedDir, string payloadDir)
        {
            if (string.IsNullOrEmpty(installedDir) || !Directory.Exists(installedDir) || !File.Exists(Path.Combine(installedDir, "0Harmony.dll"))) return true;

            var installed = ReadHarmonyVersion(installedDir);
            var payload = ReadHarmonyVersion(payloadDir);
            if (installed == null || payload == null) return true;
            return payload >= installed;
        }

        // ------------------------------------------------------------------ the run

        private static InstallResult Execute(HelperOptions o, Func<int, TimeSpan?, bool> waitForExit, Action<string> faultInjector)
        {
            var state = new State();
            try
            {
                var message = Install(o, waitForExit, faultInjector, state);
                return new InstallResult { Success = true, Build = o.Build, PreviousBuild = state.Previous, Message = message };
            }
            catch (TimedOutException e)
            {
                var timedOut = Failed(o, state, e.Message);
                timedOut.TimedOut = true;
                return timedOut;
            }
            catch (InstallException e)
            {
                return Failed(o, state, e.Message);
            }
            catch (InvalidDataException)
            {
                return Failed(o, state, InvalidPackage + ".");
            }
            catch (Exception e)
            {
                return Failed(o, state, "Update failed: " + e.Message);
            }
        }

        private static InstallResult Failed(HelperOptions o, State state, string message)
        {
            if (state.ExtractTouched) TryDeleteDirectory(o.Extract);
            return new InstallResult { Success = false, Build = o.Build, PreviousBuild = state.Previous, Message = message };
        }

        /// <summary>The seven steps. Returns the success message; every rejection or failure is an exception.</summary>
        private static string Install(HelperOptions o, Func<int, TimeSpan?, bool> waitForExit, Action<string> faultInjector, State state)
        {
            // 1. wait for the game or server to release its files
            var exited = waitForExit(o.Pid, o.WaitTimeoutSeconds == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(o.WaitTimeoutSeconds));
            state.Previous = InstalledBuild(o);
            if (!exited) throw new TimedOutException("Timed out waiting for exit.");

            // 2. never install over the same or a newer build
            if (o.Build <= state.Previous)
                throw new InstallException("Refusing to install agencies." + o.Build + " over agencies." + state.Previous + ".");
            CheckPaths(o);

            // 3. one stream for hashing and extracting, so the bytes that were verified are the bytes that are installed
            using (var stream = new FileStream(o.Zip, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!string.Equals(Sha256Hex(stream), (o.Sha256 ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InstallException("Update package failed verification.");

                stream.Position = 0;
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                    ExtractArchive(archive, o.Extract, state);
            }

            // 4. the layout
            string note;
            if (o.Mode == HelperMode.Client)
            {
                ValidateClientLayout(o);
                note = SwapClient(o, faultInjector, state.Previous); // 5
            }
            else
            {
                ValidateServerLayout(o);
                note = SwapServer(o, faultInjector, state.Previous); // 6
            }

            // 7. done; a leftover scratch folder is not worth failing a finished install
            TryDeleteDirectory(o.Extract);
            return "Updated agencies." + state.Previous + " -> agencies." + o.Build + note;
        }

        // The helper deletes and refills the extract and backup folders, so refuse any combination where that could
        // reach the install itself or the package.
        private static void CheckPaths(HelperOptions o)
        {
            string target, extract, backup, zip;
            try
            {
                target = Normalize(o.Target);
                extract = Normalize(o.Extract);
                backup = Normalize(o.Backup);
                zip = Normalize(o.Zip);
            }
            catch (Exception)
            {
                throw new InstallException("Invalid helper paths.");
            }

            if (IsSameOrInside(target, extract) || IsSameOrInside(target, backup) ||
                IsSameOrInside(extract, backup) || IsSameOrInside(backup, extract) ||
                IsSameOrInside(zip, extract) || IsSameOrInside(zip, backup))
                throw new InstallException("Invalid helper paths.");
        }

        private static string Normalize(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>True when path is the folder itself or somewhere below it.</summary>
        private static bool IsSameOrInside(string path, string folder)
        {
            return string.Equals(path, folder, PathComparison) ||
                   path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);
        }

        private static string Sha256Hex(Stream stream)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------ extract and validate

        private static void ExtractArchive(ZipArchive archive, string extractDir, State state)
        {
            var root = Path.GetFullPath(extractDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // Check every entry before writing anything: a name that resolves outside the extract folder (zip-slip) rejects the package.
            var plan = new List<KeyValuePair<ZipArchiveEntry, string>>();
            foreach (var entry in archive.Entries)
            {
                string destination;
                try
                {
                    destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                }
                catch (Exception)
                {
                    throw new InstallException(InvalidPackage + ".");
                }
                if (!destination.StartsWith(root, PathComparison)) throw new InstallException(InvalidPackage + ".");
                plan.Add(new KeyValuePair<ZipArchiveEntry, string>(entry, destination));
            }

            state.ExtractTouched = true;
            DeleteDirectory(root);
            Directory.CreateDirectory(root);

            foreach (var item in plan)
            {
                var name = item.Key.FullName;
                if (name.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("\\", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(item.Value);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(item.Value));
                item.Key.ExtractToFile(item.Value, true);
            }
        }

        private static void ValidateClientLayout(HelperOptions o)
        {
            var files = ListFiles(o.Extract);
            RequireFile(files, ClientModPath + "Plugins/LmpClient.dll");
            RequireFile(files, ClientModPath + "Updater/LmpAgenciesUpdater.exe");
            RequireFile(files, ClientModPath + "Updater/build.txt");
            RequireBuild(o, Path.Combine(o.Extract, "GameData", "LunaMultiplayer", "Updater", "build.txt"));
            RequireFile(files, ClientHarmonyDll);

            // Harmony must only ever be GameData/000_Harmony: KSP's Harmony install checker refuses a second copy.
            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileName(file), "0Harmony.dll", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(file, ClientHarmonyDll, PathComparison))
                    throw new InstallException(InvalidPackage + ": 0Harmony.dll is only allowed in GameData/000_Harmony.");
            }
        }

        private static void ValidateServerLayout(HelperOptions o)
        {
            foreach (var entry in Directory.GetFileSystemEntries(o.Extract))
            {
                var name = Path.GetFileName(entry);
                foreach (var protectedName in ServerProtected)
                {
                    if (string.Equals(name, protectedName, StringComparison.OrdinalIgnoreCase))
                        throw new InstallException(InvalidPackage + ": it contains the protected entry " + name + ".");
                }
            }

            var files = ListFiles(o.Extract);
            RequireFile(files, "Server.dll");
            RequireFile(files, "Server.exe");
            RequireFile(files, "Updater/LmpAgenciesUpdater.exe");
            RequireFile(files, "Updater/build.txt");
            RequireBuild(o, Path.Combine(o.Extract, "Updater", "build.txt"));
        }

        private static void RequireFile(List<string> files, string relative)
        {
            if (!files.Exists(f => string.Equals(f, relative, PathComparison)))
                throw new InstallException(InvalidPackage + ": missing " + relative + ".");
        }

        private static void RequireBuild(HelperOptions o, string buildFile)
        {
            if (ReadBuildFile(buildFile) != o.Build)
                throw new InstallException(InvalidPackage + ": its build.txt does not say agencies." + o.Build + ".");
        }

        private static int ReadBuildFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                int build;
                return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out build) && build > 0 ? build : 0;
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        /// <summary>Every file below root, as sorted relative paths with '/' separators.</summary>
        private static List<string> ListFiles(string root)
        {
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var list = new List<string>();
            foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                list.Add(file.Substring(full.Length + 1).Replace('\\', '/'));
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // ------------------------------------------------------------------ client swap

        private static string SwapClient(HelperOptions o, Action<string> fault, int previous)
        {
            var live = Path.Combine(o.Target, "LunaMultiplayer");
            var liveHarmony = Path.Combine(o.Target, "000_Harmony");
            var backupMod = Path.Combine(o.Backup, "LunaMultiplayer");
            var backupHarmony = Path.Combine(o.Backup, "000_Harmony");
            var payloadMod = Path.Combine(o.Extract, "GameData", "LunaMultiplayer");
            var payloadHarmony = Path.Combine(o.Extract, "GameData", "000_Harmony");

            ResetDirectory(o.Backup);

            var oldMoved = false;
            var newPlaced = false;
            var harmonyOldMoved = false;
            var harmonyNewPlaced = false;
            var note = "";
            try
            {
                // a. the installed mod goes to the backup
                if (Directory.Exists(live))
                {
                    Directory.Move(live, backupMod);
                    oldMoved = true;
                }
                RunFault(fault, FaultClientOldMoved);

                // b. only these things survive from the old install: Data (replacing the payload's), Screenshots, missing Flags
                if (oldMoved) CarryOver(backupMod, payloadMod);

                // c. the new mod takes its place
                Directory.Move(payloadMod, live);
                newPlaced = true;
                RunFault(fault, FaultClientNewInstalled);

                // d. Harmony lives only in GameData/000_Harmony and only moves forward
                if (ShouldReplaceHarmony(liveHarmony, payloadHarmony))
                {
                    if (Directory.Exists(liveHarmony))
                    {
                        Directory.Move(liveHarmony, backupHarmony);
                        harmonyOldMoved = true;
                    }
                    Directory.Move(payloadHarmony, liveHarmony);
                    harmonyNewPlaced = true;
                }
                else
                {
                    note = " (kept installed Harmony)";
                }
                RunFault(fault, FaultClientHarmonySwapped);
            }
            catch (Exception e)
            {
                try
                {
                    if (harmonyOldMoved)
                    {
                        DeleteDirectory(liveHarmony);
                        Directory.Move(backupHarmony, liveHarmony);
                    }
                    else if (harmonyNewPlaced)
                    {
                        DeleteDirectory(liveHarmony);
                    }

                    // once the old mod is safely in the backup, whatever sits at the live path is ours (or nothing)
                    if (oldMoved || newPlaced) DeleteDirectory(live);
                    if (oldMoved) Directory.Move(backupMod, live);
                }
                catch (Exception rollback)
                {
                    throw new InstallException("Update failed (" + e.Message + ") and rolling back failed (" + rollback.Message +
                                               "); the previous files are in " + o.Backup + ".");
                }
                throw new InstallException("Update failed (" + e.Message + "); rolled back to agencies." + previous + ".");
            }
            return note;
        }

        private static void CarryOver(string oldMod, string newMod)
        {
            var oldData = Path.Combine(oldMod, "Data");
            if (Directory.Exists(oldData))
            {
                var newData = Path.Combine(newMod, "Data");
                DeleteDirectory(newData);
                CopyDirectory(oldData, newData, false);
            }

            var oldScreenshots = Path.Combine(oldMod, "Screenshots");
            if (Directory.Exists(oldScreenshots)) CopyDirectory(oldScreenshots, Path.Combine(newMod, "Screenshots"), false);

            var oldFlags = Path.Combine(oldMod, "Flags");
            if (Directory.Exists(oldFlags)) CopyDirectory(oldFlags, Path.Combine(newMod, "Flags"), true);
        }

        // ------------------------------------------------------------------ server swap

        private static string SwapServer(HelperOptions o, Action<string> fault, int previous)
        {
            var files = ListFiles(o.Extract);
            var backedUp = new List<string>();
            var added = new List<string>();
            var createdDirectories = new List<string>();

            ResetDirectory(o.Backup);
            try
            {
                // Only files the package replaces are backed up; the rest of the server folder is not ours.
                foreach (var relative in files)
                {
                    var livePath = Path.Combine(o.Target, relative);
                    if (File.Exists(livePath))
                    {
                        CopyFile(livePath, Path.Combine(o.Backup, relative));
                        backedUp.Add(relative);
                    }
                    else
                    {
                        added.Add(relative);
                    }
                }
                RunFault(fault, FaultServerBackedUp);

                foreach (var relative in files)
                {
                    var livePath = Path.Combine(o.Target, relative);
                    EnsureDirectory(Path.GetDirectoryName(livePath), createdDirectories);
                    File.Copy(Path.Combine(o.Extract, relative), livePath, true);
                }
                RunFault(fault, FaultServerCopied);
            }
            catch (Exception e)
            {
                try
                {
                    foreach (var relative in backedUp) CopyFile(Path.Combine(o.Backup, relative), Path.Combine(o.Target, relative));
                    foreach (var relative in added)
                    {
                        var livePath = Path.Combine(o.Target, relative);
                        if (File.Exists(livePath)) File.Delete(livePath);
                    }
                    for (var i = createdDirectories.Count - 1; i >= 0; i--)
                    {
                        try { Directory.Delete(createdDirectories[i], false); }
                        catch (IOException) { /* not empty or in use: leave it */ }
                    }
                }
                catch (Exception rollback)
                {
                    throw new InstallException("Update failed (" + e.Message + ") and rolling back failed (" + rollback.Message +
                                               "); the previous files are in " + o.Backup + ".");
                }
                throw new InstallException("Update failed (" + e.Message + "); rolled back to agencies." + previous + ".");
            }
            return "";
        }

        private static void EnsureDirectory(string path, List<string> created)
        {
            if (string.IsNullOrEmpty(path) || Directory.Exists(path)) return;
            EnsureDirectory(Path.GetDirectoryName(path), created);
            Directory.CreateDirectory(path);
            created.Add(path);
        }

        // ------------------------------------------------------------------ Harmony versions

        private static Version ReadHarmonyVersion(string dir)
        {
            try
            {
                var versionFile = Path.Combine(dir, "Harmony.version");
                if (File.Exists(versionFile))
                {
                    var fromFile = ParseVersionJson(File.ReadAllText(versionFile));
                    if (fromFile != null) return fromFile;
                }

                var dll = Path.Combine(dir, "0Harmony.dll");
                if (File.Exists(dll))
                {
                    var info = FileVersionInfo.GetVersionInfo(dll);
                    var fromDll = new Version(Math.Max(info.FileMajorPart, 0), Math.Max(info.FileMinorPart, 0), Math.Max(info.FileBuildPart, 0), Math.Max(info.FilePrivatePart, 0));
                    if (fromDll.Major != 0 || fromDll.Minor != 0 || fromDll.Build != 0 || fromDll.Revision != 0) return fromDll;
                }
            }
            catch (Exception)
            {
                // unreadable counts as unknown
            }
            return null;
        }

        /// <summary>
        /// The top-level VERSION of a KSP-AVC .version file: an object {MAJOR,MINOR,PATCH,BUILD} or a string like "2.2.1".
        /// Missing parts are 0. The key is matched with its quotes, so KSP_VERSION is never mistaken for it.
        /// </summary>
        private static Version ParseVersionJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            var obj = VersionObject.Match(json);
            if (obj.Success)
            {
                int major = 0, minor = 0, patch = 0, build = 0;
                var any = false;
                foreach (Match part in VersionPart.Matches(obj.Groups[1].Value))
                {
                    int value;
                    if (!int.TryParse(part.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return null;
                    any = true;
                    switch (part.Groups[1].Value.ToUpperInvariant())
                    {
                        case "MAJOR": major = value; break;
                        case "MINOR": minor = value; break;
                        case "PATCH": patch = value; break;
                        case "BUILD": build = value; break;
                    }
                }
                return any ? new Version(major, minor, patch, build) : null;
            }

            var str = VersionString.Match(json);
            if (str.Success)
            {
                var numbers = new int[4];
                var pieces = str.Groups[1].Value.Split('.');
                for (var i = 0; i < pieces.Length && i < 4; i++)
                {
                    var digits = LeadingDigits.Match(pieces[i]);
                    int value;
                    if (!digits.Success || !int.TryParse(digits.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return null;
                    numbers[i] = value;
                }
                return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
            }

            return null;
        }

        // ------------------------------------------------------------------ file helpers

        private static void RunFault(Action<string> fault, string stage)
        {
            if (fault != null) fault(stage);
        }

        private static void ResetDirectory(string path)
        {
            DeleteDirectory(path);
            Directory.CreateDirectory(path);
        }

        private static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                // a read-only file somewhere inside; clear the flag and try once more
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, true);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { DeleteDirectory(path); }
            catch (Exception) { /* scratch folder, best effort */ }
        }

        private static void CopyFile(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }

        /// <summary>Copies a folder tree. With onlyMissing, files that already exist at the destination are left alone.</summary>
        private static void CopyDirectory(string source, string destination, bool onlyMissing)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (onlyMissing && File.Exists(target)) continue;
                File.Copy(file, target, true);
            }
            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), onlyMissing);
        }
    }
}
