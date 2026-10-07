using System;
using System.Diagnostics;
using System.IO;

namespace LmpAgenciesInstaller
{
    public static class InstallerSafety
    {
        public static bool IsInside(string path, string folder)
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        public static void ValidateTarget(string root, string installerPath = null)
        {
            if (!KspDiscovery.IsKspDirectory(root)) throw new InvalidOperationException("Choose a KSP folder containing KSP_x64.exe (or KSP.exe), its Data folder, and GameData.");
            for (var current = new DirectoryInfo(Path.GetFullPath(root)); current != null; current = current.Parent) RejectLink(current.FullName);
            var gameData = Path.Combine(root, "GameData"); RejectLink(gameData);
            foreach (var name in new[] { "LunaMultiplayer", "000_Harmony" })
            {
                var path = Path.Combine(gameData, name);
                if (installerPath != null && IsInside(installerPath, path)) throw new InvalidOperationException("Move this installer outside GameData before running it.");
                CheckTree(path);
            }
        }
        public static void CheckTree(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return;
            RejectLink(path);
            if (!Directory.Exists(path)) return;
            foreach (var entry in Directory.GetFileSystemEntries(path)) CheckTree(entry);
        }
        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Installation through a linked folder is unsupported: " + path);
        }
        // A KSP process whose executable cannot be read must fail closed.
        public static bool BlocksTarget(string executable, string root) => executable == null || IsInside(executable, root);
        public static void RequireGameClosed(string root)
        {
            foreach (var name in new[] { "KSP", "KSP_x64" })
            foreach (var process in Process.GetProcessesByName(name))
            using (process)
            {
                string executable;
                try { if (process.HasExited) continue; executable = process.MainModule?.FileName; }
                catch (InvalidOperationException) { continue; }
                catch (Exception) { executable = null; }
                if (BlocksTarget(executable, root)) throw new InvalidOperationException("Close KSP before installing. A running KSP process is using this installation or could not be inspected.");
            }
        }
    }
}
