using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Diagnostics;
using System;
using System.Linq;
using System.Threading;

namespace LmpClient.Diagnostics
{
    /// <summary>Opt-in bounded trace. Unity snapshots are deferred to MainSystem.Update.</summary>
    internal static class PlaytestDiagnostics
    {
        private static readonly DiagnosticTrace Trace = new DiagnosticTrace(LunaLog.Log);
        private static int _snapshotRequested;
        private static int _lastScene = -1;
        public static bool Enabled => Trace.Enabled;

        // Called explicitly after KspPath/settings are ready, never from a settings initializer.
        public static void Configure(bool enabled)
        {
            if (!enabled) Write("client.diagnostics.disabled", () => "enabled=false");
            Trace.Enabled = enabled;
            if (!enabled) return;
            Write("client.diagnostics.enabled", () =>
            {
                var assembly = typeof(MainSystem).Assembly;
                return $"version={assembly.GetName().Version} mvid={assembly.ManifestModule.ModuleVersionId} assembly={assembly.Location} ksp={MainSystem.KspPath} log=KSP.log";
            });
            RequestSnapshot();
        }

        public static void Write(string eventName, Func<string> details, bool traffic = false)
            => Trace.Write(eventName, details, traffic);

        public static void RequestSnapshot()
        {
            if (Enabled) Interlocked.Exchange(ref _snapshotRequested, 1);
        }

        public static void PumpUnitySnapshot()
        {
            if (!Enabled || !MainSystem.IsUnityThread) return;
            try
            {
                // Keep Unity reads on the main thread; failures must never break Update.
                if (Interlocked.Exchange(ref _snapshotRequested, 0) == 0 && _lastScene == (int)HighLogic.LoadedScene) return;
                Write("client.snapshot", () =>
                {
                    _lastScene = (int)HighLogic.LoadedScene;
                    var agency = AgencySystem.Singleton.GetMyAgency();
                    var flags = SettingsSystem.ServerSettings;
                    return $"scene={HighLogic.LoadedScene} network={MainSystem.NetworkState} agency={AgencySystem.Singleton.MyAgencyId} knownAgencies={AgencySystem.Singleton.KnownAgencies.Count} " +
                           $"members={agency?.MemberUniqueIds?.Length ?? 0} agencyFunds={agency?.Funds} agencyScience={agency?.Science} agencyReputation={agency?.Reputation} " +
                           $"roster={HighLogic.CurrentGame?.CrewRoster?.Crew?.Count()} gameMode={flags.GameMode} experiments={flags.AgencyExperimentsPerAgency} kerbals={flags.AgencyKerbalsPerAgency} scansat={flags.AgencyScansatPerAgency} contracts={flags.AgencyContractsPoolPerAgency} commnet={flags.AgencyCommNetPerAgency}";
                });
            }
            catch { /* Diagnostics must not interrupt the Unity update loop. */ }
        }
    }
}
