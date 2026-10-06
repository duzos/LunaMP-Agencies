using LmpCommon.Diagnostics;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using System;

namespace Server.Diagnostics
{
    public static class PlaytestDiagnostics
    {
        private static DiagnosticTrace _trace = new DiagnosticTrace(LunaLog.Normal);
        public static bool Enabled => _trace.Enabled;

        public static void Configure(bool commandLineEnabled)
        {
            _trace.Enabled = commandLineEnabled || DebugSettings.SettingsStore.VerboseDiagnostics;
            Snapshot("startup.settings");
        }

        public static void Write(string name, Func<string> details, bool traffic = false)
            => _trace.Write(name, details, traffic);

        public static string Client(ClientStructure client)
            => $"player={client?.PlayerName} agency={client?.AgencyId} authenticated={client?.Authenticated} connection={client?.ConnectionStatus}";

        public static void AgencySnapshot(ClientStructure client)
        {
            Write("agency.state", () =>
            {
                var agency = AgencySystem.GetAgency(client.AgencyId);
                if (agency == null) return $"{Client(client)} state=missing";
                double funds;
                float science, reputation;
                int members, tech;
                lock (agency.Lock)
                {
                    funds = agency.Funds;
                    science = agency.Science;
                    reputation = agency.Reputation;
                    members = agency.Members.Count;
                    tech = agency.UnlockedTechCount;
                }
                return $"{Client(client)} members={members} funds={funds} science={science} reputation={reputation} tech={tech}";
            });
        }

        public static void Snapshot(string name)
        {
            Write(name, () =>
            {
                var assembly = typeof(PlaytestDiagnostics).Assembly;
                var settings = GeneralSettings.SettingsStore;
                return $"version={assembly.GetName().Version} mvid={assembly.ManifestModule.ModuleVersionId} data={ServerContext.DataDirectory} universe={ServerContext.UniverseDirectory} agenciesPath={AgencyStore.AgenciesPath} agencies={AgencyStore.Agencies.Count} clients={ServerContext.Clients.Count} gameMode={settings.GameMode} experiments={settings.AgencyExperimentsPerAgency} kerbals={settings.AgencyKerbalsPerAgency} zeroStartingKerbals={settings.AgencyZeroStartingKerbals} scansat={settings.AgencyScansatPerAgency} contracts={settings.AgencyContractsPoolPerAgency} commnet={settings.AgencyCommNetPerAgency} launchSites={settings.AgencyLaunchSitesPerAgency}";
            });
        }
    }
}
