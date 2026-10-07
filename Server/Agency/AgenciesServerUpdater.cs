using LmpAgenciesUpdater;
using LmpCommon.Agency;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Agency
{
    /// <summary>
    /// Server side of the agencies auto-updater. At boot, before the server accepts anyone, an opted-in Windows server
    /// downloads the newest fork release, verifies it, and hands over to the update helper (taken from the verified zip),
    /// which waits for this process to exit, swaps the files and relaunches the server. Anything that goes wrong is logged
    /// and the server just carries on booting on the build it has.
    /// </summary>
    public static class AgenciesServerUpdater
    {
        private const string StagingFolderName = "update-staging";
        private const string FailedBuildFileName = "failed-build.txt";
        private const string HelperFileName = "LmpAgenciesUpdater.exe";
        private const string HelperEntryName = "Updater/" + HelperFileName;
        private const int HelperWaitTimeoutSeconds = 600;

        private const long MaxDownloadBytes = 256L * 1024 * 1024;
        private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan BodyTimeout = TimeSpan.FromMinutes(5);

        private static readonly Lazy<HttpClient> Client = new Lazy<HttpClient>(() =>
        {
            // GitHub redirects release downloads to its CDN; a few hops are fine and .NET refuses an https -> http redirect.
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
            {
                Timeout = HeadersTimeout
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LunaMP-Agencies");
            return client;
        });

        /// <summary>
        /// GETs a URL with HttpClient (User-Agent set, 30 s to get the response headers, 5 minutes for the body, 256 MB cap).
        /// Only the fork's release API and its release downloads can be fetched, and only over https.
        /// </summary>
        public static async Task<byte[]> HttpFetch(string url)
        {
            if (url == null || !IsFetchable(url))
                throw new ArgumentException("Refusing to fetch " + (url ?? "(null)") + ": only https URLs of the " + AgenciesBuild.Owner + "/" + AgenciesBuild.Repo + " releases are allowed.", nameof(url));

            using (var response = await Client.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                    throw new InvalidDataException("The response is larger than " + MaxDownloadBytes + " bytes.");

                using (var timeout = new CancellationTokenSource(BodyTimeout))
                using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        await buffer.WriteAsync(chunk, 0, read, timeout.Token).ConfigureAwait(false);
                        if (buffer.Length > MaxDownloadBytes)
                            throw new InvalidDataException("The response is larger than " + MaxDownloadBytes + " bytes.");
                    }
                    return buffer.ToArray();
                }
            }
        }

        private static bool IsFetchable(string url) =>
            string.Equals(url, AgenciesBuild.LatestReleaseApi, StringComparison.Ordinal) ||
            url.StartsWith(AgenciesBuild.DownloadPrefix, StringComparison.Ordinal);

        /// <summary>
        /// The latest stable fork release, or null when the latest release is not a valid agencies release (see
        /// <see cref="AgenciesRelease.Parse"/>). Fetch failures propagate to the caller.
        /// </summary>
        public static async Task<AgenciesReleaseInfo> FetchLatestAsync(Func<string, Task<byte[]>> fetch)
        {
            if (fetch == null) throw new ArgumentNullException(nameof(fetch));
            var body = await fetch(AgenciesBuild.LatestReleaseApi).ConfigureAwait(false);
            return AgenciesRelease.Parse(Encoding.UTF8.GetString(body ?? new byte[0]));
        }

        /// <summary>
        /// Boot-time update check. Returns true only when the update helper was started: the caller must then exit at once
        /// so the helper can replace the files and relaunch the server. Never throws; every failure is logged and means false.
        /// </summary>
        /// <param name="binariesDir">The folder holding Server.exe. Staging happens in its update-staging subfolder.</param>
        /// <param name="originalArgs">This process's command-line arguments, replayed when the helper relaunches the server.</param>
        /// <param name="workingDir">The working directory the relaunched server gets.</param>
        /// <param name="start">Starts the helper process (a seam for tests).</param>
        public static async Task<bool> TryUpdateAtBootAsync(Func<string, Task<byte[]>> fetch, string binariesDir, string[] originalArgs, string workingDir, Action<ProcessStartInfo> start, bool? isWindowsOverride = null, int? pidOverride = null)
        {
            try
            {
                return await CheckAndStageAsync(fetch, binariesDir, originalArgs, workingDir, start, isWindowsOverride ?? OperatingSystem.IsWindows(), pidOverride ?? Environment.ProcessId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                LunaLog.Warning($"Agencies update check failed, continuing to start: {e.Message}");
                return false;
            }
        }

        private static async Task<bool> CheckAndStageAsync(Func<string, Task<byte[]>> fetch, string binariesDir, string[] originalArgs, string workingDir, Action<ProcessStartInfo> start, bool isWindows, int pid)
        {
            if (fetch == null) throw new ArgumentNullException(nameof(fetch));
            if (start == null) throw new ArgumentNullException(nameof(start));
            if (string.IsNullOrEmpty(binariesDir)) throw new ArgumentException("A binaries directory is required.", nameof(binariesDir));

            // The apphost directory comes with a trailing separator; the helper gets it without one.
            var binaries = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binariesDir));
            var root = Path.Combine(binaries, StagingFolderName);

            // 1. Old build folders go; the backup, the last result and the failed build marker stay.
            CleanOldBuildFolders(root);

            // 2. What happened to the previous attempt, and what we remember about failed builds.
            var last = ConsumeLastResult(root);
            var failedBuild = ReadFailedBuild(root);

            // 3 + 4. The latest release. Non-Windows servers do this only to say what is available.
            AgenciesReleaseInfo latest;
            try
            {
                latest = await FetchLatestAsync(fetch).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                LunaLog.Warning($"Could not check for an agencies update: {e.Message}");
                return false;
            }

            if (latest == null)
            {
                LunaLog.Info("Agencies update check: the latest release is not a valid agencies release, nothing to install.");
                return false;
            }

            // A release newer than the failed build is a fresh chance; the old failure is forgotten.
            if (failedBuild != 0 && latest.Build > failedBuild)
            {
                DeleteQuietly(Path.Combine(root, FailedBuildFileName));
                failedBuild = 0;
            }

            if (!isWindows)
            {
                if (latest.Build > AgenciesBuild.Number)
                    LunaLog.Info($"Agencies update available: agencies.{latest.Build} (automatic updates only run on Windows, download it from {AgenciesBuild.ReleasesPage})");
                return false;
            }

            // 5. Should anything be installed?
            var action = AgenciesUpdateDecision.Decide(AgenciesBuild.Number, latest.Build, 0, GeneralSettings.SettingsStore.AgencyAutoUpdate, last, failedBuild, false);
            switch (action)
            {
                case AgenciesUpdateAction.None:
                    LunaLog.Debug($"Agencies update check: running agencies.{AgenciesBuild.Number}, latest release is agencies.{latest.Build}.");
                    return false;
                case AgenciesUpdateAction.ShowFailure:
                    LunaLog.Warning($"The last install of agencies.{latest.Build} failed or did not take effect; skipping agencies.{latest.Build} until a newer release.");
                    return false;
                case AgenciesUpdateAction.Prompt:
                    LunaLog.Info($"Agencies update available: agencies.{latest.Build} (set AgencyAutoUpdate to install at startup)");
                    return false;
            }

            // 6. Download and verify the server package.
            var staging = AgenciesUpdatePaths.StagingDir(root, latest.Build);
            Directory.CreateDirectory(staging);

            LunaLog.Info($"Downloading agencies.{latest.Build}...");
            var sumsBytes = await fetch(latest.SumsUrl).ConfigureAwait(false) ?? new byte[0];
            File.WriteAllBytes(Path.Combine(staging, AgenciesBuild.SumsAsset), sumsBytes);

            var expected = AgenciesRelease.ExpectedSha256(latest, latest.ServerZipName, Encoding.UTF8.GetString(sumsBytes));
            if (expected == null)
            {
                LunaLog.Error($"Not installing agencies.{latest.Build}: {AgenciesBuild.SumsAsset} has no checksum for {latest.ServerZipName}, or it disagrees with the release's own digest.");
                return false;
            }

            var zipPath = Path.Combine(staging, latest.ServerZipName);
            File.WriteAllBytes(zipPath, await fetch(latest.ServerZipUrl).ConfigureAwait(false) ?? new byte[0]);

            string actual;
            using (var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                actual = AgenciesRelease.Sha256Hex(zip);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuietly(zipPath);
                LunaLog.Error($"Not installing agencies.{latest.Build}: the downloaded package does not match its checksum.");
                return false;
            }

            // 7. Only the helper comes out of the verified zip; the helper unpacks the rest itself.
            var helper = Path.Combine(staging, HelperFileName);
            if (!TryExtractHelper(zipPath, helper))
            {
                LunaLog.Error($"Not installing agencies.{latest.Build}: the package does not contain {HelperEntryName}.");
                return false;
            }

            // 8 + 9. Hand over.
            var options = new HelperOptions
            {
                Mode = HelperMode.Server,
                Zip = zipPath,
                Sha256 = expected,
                Target = binaries,
                Extract = AgenciesUpdatePaths.ExtractDir(root, latest.Build),
                Backup = AgenciesUpdatePaths.BackupDir(root),
                Result = AgenciesUpdatePaths.ResultPath(root),
                Pid = pid,
                Build = latest.Build,
                WaitTimeoutSeconds = HelperWaitTimeoutSeconds,
                Relaunch = Path.Combine(binaries, "Server.exe"),
                RelaunchArgs = HelperCommandLine.JoinArgs(originalArgs),
                RelaunchDir = workingDir
            };

            try
            {
                start(new ProcessStartInfo(helper, HelperCommandLine.Build(options)) { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception e)
            {
                LunaLog.Error($"Could not start the update helper: {e.Message}");
                return false;
            }

            LunaLog.Info($"Installing agencies.{latest.Build}; the server restarts automatically.");
            return true;
        }

        /// <summary>Deletes numbered build folders except the running build's: its helper may still be exiting.</summary>
        private static void CleanOldBuildFolders(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;

                var running = AgenciesBuild.Number.ToString(CultureInfo.InvariantCulture);
                foreach (var directory in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(directory);
                    if (!IsBuildNumber(name) || name == running) continue;

                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch (Exception e)
                    {
                        LunaLog.Debug($"Could not remove old update folder {directory}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                LunaLog.Debug($"Could not clean the update staging folder: {e.Message}");
            }
        }

        private static bool IsBuildNumber(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var c in name)
                if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>
        /// Reads, logs and deletes the helper's result file. A failed install, or a "successful" one that is not the build
        /// now running, is remembered in failed-build.txt so a later boot will not try the same build again.
        /// </summary>
        private static AgenciesLastResult ConsumeLastResult(string root)
        {
            var path = AgenciesUpdatePaths.ResultPath(root);

            bool success;
            int build, previousBuild;
            string message;
            if (!HelperCommandLine.TryReadResult(path, out success, out build, out previousBuild, out message))
            {
                if (File.Exists(path))
                {
                    LunaLog.Warning("Ignoring an unreadable agencies update result file.");
                    DeleteQuietly(path);
                }
                return null;
            }

            if (success) LunaLog.Info($"Agencies update result: {message}");
            else LunaLog.Warning($"Agencies update failed: {message}");

            try
            {
                if (!success || build != AgenciesBuild.Number)
                {
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, FailedBuildFileName), build.ToString(CultureInfo.InvariantCulture));
                }
                File.Delete(path);
            }
            catch (Exception e)
            {
                // The result stays for the next boot; the decision below still sees it through the return value.
                LunaLog.Warning($"Could not record the agencies update result: {e.Message}");
            }

            return new AgenciesLastResult { Success = success, Build = build, PreviousBuild = previousBuild, Message = message };
        }

        private static int ReadFailedBuild(string root)
        {
            try
            {
                var path = Path.Combine(root, FailedBuildFileName);
                int build;
                if (File.Exists(path) &&
                    int.TryParse(File.ReadAllText(path).Trim().TrimStart('﻿'), NumberStyles.None, CultureInfo.InvariantCulture, out build) &&
                    build > 0)
                    return build;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return 0;
        }

        /// <summary>Extracts exactly Updater/LmpAgenciesUpdater.exe to the destination; false when the zip has no such entry.</summary>
        private static bool TryExtractHelper(string zipPath, string destination)
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!string.Equals(entry.FullName.Replace('\\', '/'), HelperEntryName, StringComparison.OrdinalIgnoreCase)) continue;
                    entry.ExtractToFile(destination, true);
                    return true;
                }
            }
            return false;
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e)
            {
                LunaLog.Debug($"Could not delete {path}: {e.Message}");
            }
        }
    }
}
