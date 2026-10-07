using LmpAgenciesUpdater;
using LmpClient.Systems.SettingsSys;
using LmpClient.Windows.Update;
using LmpCommon.Agency;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine.Networking;

namespace LmpClient.Systems.AgenciesUpdate
{
    /// <summary>
    /// Finds new agencies releases, verifies them and stages the installer helper.
    /// KSP's Mono has no System.IO.Compression, so the zip is read with KSP's own Ionic.Zip and only the helper exe is extracted here.
    /// </summary>
    public static class AgenciesUpdateClient
    {
        private const string HelperEntry = "GameData/LunaMultiplayer/Updater/LmpAgenciesUpdater.exe";
        private const string UserAgent = "LunaMP-Agencies";

        private static int _staged;
        private static bool _busy;

        private static string Root => Path.Combine(MainSystem.KspPath, "LunaMultiplayer-update");

        public static IEnumerator CheckOnBoot(bool forced)
        {
            AgenciesLastResult last = null;
            try
            {
                CleanStaging();
                last = ReadAndClearLastResult();
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Agencies update housekeeping failed: {e.Message}");
            }

            string json;
            using (var www = NewGet(AgenciesBuild.LatestReleaseApi))
            {
                yield return www.SendWebRequest();
                if (www.isNetworkError || www.isHttpError)
                {
                    LunaLog.Log($"[LMP]: Could not check for agencies updates: {www.error}");
                    if (last != null) UpdateWindow.ShowStatusOnly(ResultText(last));
                    yield break;
                }
                json = www.downloadHandler.text;
            }

            AgenciesReleaseInfo info;
            try { info = AgenciesRelease.Parse(json); }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Could not parse agencies release: {e.Message}");
                info = null;
            }

            if (info == null)
            {
                if (last != null) UpdateWindow.ShowStatusOnly(ResultText(last));
                yield break;
            }

            var settings = SettingsSystem.CurrentSettings;
            var action = AgenciesUpdateDecision.Decide(AgenciesBuild.Number, info.Build, settings.AgenciesSkippedBuild,
                settings.AgenciesAutoUpdate, last, settings.AgenciesFailedBuild, forced);
            LunaLog.Log($"[LMP]: Agencies latest build {info.Build}, current {AgenciesBuild.Number}, action {action}");

            switch (action)
            {
                case AgenciesUpdateAction.Prompt:
                    UpdateWindow.Show(info, null);
                    break;
                case AgenciesUpdateAction.AutoDownload:
                    UpdateWindow.Show(info, null);
                    yield return DownloadAndStage(info);
                    break;
                case AgenciesUpdateAction.ShowFailure:
                    var failure = last != null && !string.IsNullOrEmpty(last.Message) && last.Build == info.Build
                        ? last.Message
                        : $"The previous install of agencies.{info.Build} did not take effect.";
                    UpdateWindow.Show(info, failure);
                    break;
            }

            if (last != null)
                UpdateWindow.Status = ResultText(last);
            if (last != null && action == AgenciesUpdateAction.None)
                UpdateWindow.ShowStatusOnly(ResultText(last));
        }

        /// <summary>Called on the main thread when the server says its agencies build differs from ours.</summary>
        public static void ShowServerReason(string reason)
        {
            UpdateWindow.ShowStatusOnly(reason);
        }

        public static void StartDownload(AgenciesReleaseInfo info)
        {
            if (_busy || info == null) return;
            MainSystem.Singleton.StartCoroutine(DownloadAndStage(info));
        }

        public static void Retry(AgenciesReleaseInfo info)
        {
            SettingsSystem.CurrentSettings.AgenciesFailedBuild = 0;
            SettingsSystem.SaveSettings();
            UpdateWindow.ClearFailure();
            StartDownload(info);
        }

        private static IEnumerator DownloadAndStage(AgenciesReleaseInfo info)
        {
            if (_staged == info.Build)
            {
                UpdateWindow.Status = $"agencies.{info.Build} will install when KSP closes.";
                yield break;
            }
            if (_busy) yield break;
            _busy = true;
            UpdateWindow.Status = $"Downloading agencies.{info.Build}...";

            try
            {
                var stagingDir = AgenciesUpdatePaths.StagingDir(Root, info.Build);
                var zipPath = Path.Combine(stagingDir, info.ClientZipName);
                Directory.CreateDirectory(stagingDir);

                string sums;
                using (var www = NewGet(info.SumsUrl))
                {
                    yield return www.SendWebRequest();
                    if (www.isNetworkError || www.isHttpError)
                    {
                        Fail($"Update download failed: {www.error}");
                        yield break;
                    }
                    sums = www.downloadHandler.text;
                }

                TryDelete(zipPath);
                using (var www = NewGet(info.ClientZipUrl))
                {
                    www.downloadHandler = new DownloadHandlerFile(zipPath);
                    yield return www.SendWebRequest();
                    if (www.isNetworkError || www.isHttpError)
                    {
                        Fail($"Update download failed: {www.error}");
                        TryDelete(zipPath);
                        yield break;
                    }
                }

                var expected = AgenciesRelease.ExpectedSha256(info, info.ClientZipName, sums);
                string actual;
                using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    actual = AgenciesRelease.Sha256Hex(fs);

                if (expected == null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(zipPath);
                    Fail("Update download failed verification.");
                    yield break;
                }

                var helperExe = Path.Combine(stagingDir, "LmpAgenciesUpdater.exe");
                if (!ExtractHelper(zipPath, helperExe))
                {
                    Fail("Invalid update package.");
                    yield break;
                }

                var current = Process.GetCurrentProcess();
                var options = new HelperOptions
                {
                    Mode = HelperMode.Client,
                    Zip = zipPath,
                    Sha256 = expected,
                    Target = Path.Combine(MainSystem.KspPath, "GameData"),
                    Extract = AgenciesUpdatePaths.ExtractDir(Root, info.Build),
                    Backup = AgenciesUpdatePaths.BackupDir(Root),
                    Result = AgenciesUpdatePaths.ResultPath(Root),
                    Pid = current.Id,
                    Build = info.Build,
                    WaitTimeoutSeconds = 0,
                    RelaunchArgs = HelperCommandLine.JoinArgs(Environment.GetCommandLineArgs().Skip(1)),
                    RelaunchDir = MainSystem.KspPath
                };
                if (SettingsSystem.CurrentSettings.AgenciesRelaunchAfterUpdate)
                    options.Relaunch = current.MainModule.FileName;

                Process.Start(new ProcessStartInfo(helperExe, HelperCommandLine.Build(options))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                _staged = info.Build;
                UpdateWindow.Status = $"agencies.{info.Build} will install when KSP closes.";
            }
            finally
            {
                _busy = false;
            }
        }

        private static bool ExtractHelper(string zipPath, string destination)
        {
            using (var zip = Ionic.Zip.ZipFile.Read(zipPath))
            {
                var entry = zip[HelperEntry];
                if (entry == null) return false;
                using (var fs = File.Create(destination))
                    entry.Extract(fs);
            }
            return true;
        }

        private static void Fail(string message)
        {
            LunaLog.LogError($"[LMP]: {message}");
            UpdateWindow.Status = message;
        }

        private static UnityWebRequest NewGet(string url)
        {
            var www = UnityWebRequest.Get(url);
            www.SetRequestHeader("User-Agent", UserAgent);
            www.redirectLimit = 5;
            return www;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        /// <summary>Deletes only numbered build directories; never last-result.txt or backup/.</summary>
        private static void CleanStaging()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0 || !name.All(c => c >= '0' && c <= '9')) continue;
                if (_staged > 0 && name == _staged.ToString()) continue;
                try { Directory.Delete(dir, true); }
                catch (Exception e) { LunaLog.LogWarning($"[LMP]: Could not clean {dir}: {e.Message}"); }
            }
        }

        private static AgenciesLastResult ReadAndClearLastResult()
        {
            var path = AgenciesUpdatePaths.ResultPath(Root);
            if (!File.Exists(path)) return null;

            AgenciesLastResult last = null;
            if (HelperCommandLine.TryReadResult(path, out var success, out var build, out var previous, out var message))
            {
                last = new AgenciesLastResult { Success = success, Build = build, PreviousBuild = previous, Message = message };
                LunaLog.Log($"[LMP]: Last agencies update result: {ResultText(last)}");
                if (!success || build != AgenciesBuild.Number)
                {
                    SettingsSystem.CurrentSettings.AgenciesFailedBuild = build;
                    SettingsSystem.SaveSettings();
                }
            }
            TryDelete(path);
            return last;
        }

        private static string ResultText(AgenciesLastResult last)
        {
            if (last.Success && last.Build == AgenciesBuild.Number) return $"Updated to agencies.{last.Build}";
            if (last.Success) return $"Update to agencies.{last.Build} did not take effect.";
            return string.IsNullOrEmpty(last.Message) ? $"Update to agencies.{last.Build} failed." : last.Message;
        }
    }
}
