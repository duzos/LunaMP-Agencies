using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LmpAgenciesInstaller
{
    public static class KspDiscovery
    {
        private static readonly Regex Pairs = new Regex("\"((?:\\\\.|[^\"\\\\])*)\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant);
        public static IEnumerable<KeyValuePair<string, string>> ReadPairs(string text)
        {
            foreach (Match match in Pairs.Matches(text ?? string.Empty))
                yield return new KeyValuePair<string, string>(Unescape(match.Groups[1].Value), Unescape(match.Groups[2].Value));
        }
        private static string Unescape(string text) => text.Replace("\\\\", "\\").Replace("\\\"", "\"");
        private static string ReadSmallFile(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllText(path) : string.Empty; }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
        }
        public static bool IsKspDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return Directory.Exists(Path.Combine(path, "GameData")) &&
                    (File.Exists(Path.Combine(path, "KSP_x64.exe")) && Directory.Exists(Path.Combine(path, "KSP_x64_Data")) ||
                     File.Exists(Path.Combine(path, "KSP.exe")) && Directory.Exists(Path.Combine(path, "KSP_Data")));
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
        public static string[] Detect(IEnumerable<string> steamRoots, IEnumerable<string> standaloneCandidates)
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in steamRoots.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                AddPath(libraries, root);
                foreach (var pair in ReadPairs(ReadSmallFile(Path.Combine(root, "steamapps", "libraryfolders.vdf"))))
                {
                    int index;
                    if (pair.Key == "path" || int.TryParse(pair.Key, out index))
                        if (Path.IsPathRooted(pair.Value)) AddPath(libraries, pair.Value);
                }
            }
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries)
            {
                var common = Path.Combine(library, "steamapps", "common");
                AddPath(candidates, Path.Combine(common, "Kerbal Space Program"));
                var name = ReadPairs(ReadSmallFile(Path.Combine(library, "steamapps", "appmanifest_220200.acf")))
                    .Where(p => p.Key == "installdir").Select(p => p.Value).FirstOrDefault();
                // Steam's installdir is one directory name, never a path outside common.
                if (!string.IsNullOrWhiteSpace(name) && name != "." && name != ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(new[] { '/', '\\' }) < 0)
                    AddPath(candidates, Path.Combine(common, name));
            }
            foreach (var path in standaloneCandidates) AddPath(candidates, path);
            return candidates.Where(IsKspDirectory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        private static void AddPath(HashSet<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { paths.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        public static string[] DetectInstalled()
        {
            var steam = new List<string>();
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = root.OpenSubKey(@"Software\Valve\Steam"))
                    { steam.Add(key?.GetValue("SteamPath") as string); steam.Add(key?.GetValue("InstallPath") as string); }
                }
                catch (Exception) { /* Registry detection is optional; Browse remains available. */ }
            }
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programs32 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            steam.Add(Path.Combine(programs, "Steam")); steam.Add(Path.Combine(programs32, "Steam"));
            var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return Detect(steam, new[] { Path.Combine(drive, "KSP"), Path.Combine(drive, "Games", "Kerbal Space Program"), Path.Combine(programs, "Kerbal Space Program"), Path.Combine(programs32, "Kerbal Space Program") });
        }
    }
}
