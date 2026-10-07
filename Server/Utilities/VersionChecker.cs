using LmpCommon.Agency;
using Server.Agency;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Threading.Tasks;

namespace Server.Utilities
{
    /// <summary>
    /// Log-only update check for servers that do not (or cannot) self-update: compares the agencies build with the latest
    /// release of the fork and says so in the log once an hour.
    /// </summary>
    public class VersionChecker
    {
        private static volatile AgenciesReleaseInfo _latestRelease;

        public static async Task RefreshLatestVersionAsync()
        {
            while (ServerContext.ServerRunning)
            {
                try
                {
                    _latestRelease = await AgenciesServerUpdater.FetchLatestAsync(AgenciesServerUpdater.HttpFetch);
                }
                catch (Exception e)
                {
                    // Keep whatever we knew before: a failed refresh is not a reason to forget a known update.
                    LunaLog.Debug($"Could not check for a new agencies release: {e.Message}");
                }

                //Sleep for 30 minutes...
                await Task.Delay(30 * 60 * 1000);
            }
        }

        public static async Task DisplayNewVersionMsgAsync()
        {
            while (ServerContext.ServerRunning)
            {
                var latest = _latestRelease;

                // No valid release known yet, skip the message and wait a few minutes first
                if (latest == null)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    continue;
                }

                if (latest.Build > AgenciesBuild.Number)
                {
                    var how = GeneralSettings.SettingsStore.AgencyAutoUpdate && OperatingSystem.IsWindows()
                        ? "AgencyAutoUpdate installs it the next time the server starts."
                        : $"Download it from {AgenciesBuild.ReleasesPage} (or set AgencyAutoUpdate to install it at startup on Windows).";
                    LunaLog.Info($"There is an agencies update available: agencies.{AgenciesBuild.Number} -> agencies.{latest.Build}. {how}");
                }

                // Repeat again in an hour
                await Task.Delay(TimeSpan.FromHours(1));
            }
        }
    }
}
