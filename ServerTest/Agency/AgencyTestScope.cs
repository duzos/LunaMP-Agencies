using LunaConfigNode.CfgNode;
using Server.Agency;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ServerTest.Agency
{
    // Agency tests mutate process-wide server state and must use DoNotParallelize.
    internal sealed class AgencyTestScope : IDisposable
    {
        private readonly string _previousDataDirectory;
        private readonly string _previousScenariosPath;
        private readonly LmpCommon.Enums.GameMode _previousGameMode;
        private readonly bool _previousScansatPerAgency;
        private readonly bool _previousKerbalsPerAgency;
        private readonly bool _previousZeroStartingKerbals;
        private readonly bool _previousLaunchSites;
        private readonly bool _previousOwnership;
        private readonly List<Action> _restore = new List<Action>();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LMPAgencyTest_" + Guid.NewGuid().ToString("N"));

        public AgencyTestScope()
        {
            // Initialize the process-wide logger against the original root before switching
            // to fixture data. Its persistent writer must never own a temporary test file.
            // Reading the public property preserves any already-open writer and settings.
            _ = LunaLog.LogFilename;
            AgencyVesselMap.WaitForPendingWritesAsync().GetAwaiter().GetResult();
            _previousDataDirectory = ServerContext.DataDirectory;
            _previousScenariosPath = ScenarioSystem.ScenariosPath;
            _previousGameMode = GeneralSettings.SettingsStore.GameMode;
            _previousScansatPerAgency = GeneralSettings.SettingsStore.AgencyScansatPerAgency;
            _previousKerbalsPerAgency = GeneralSettings.SettingsStore.AgencyKerbalsPerAgency;
            _previousZeroStartingKerbals = GeneralSettings.SettingsStore.AgencyZeroStartingKerbals;
            _previousOwnership = GeneralSettings.SettingsStore.AgencyVesselOwnership;
            GeneralSettings.SettingsStore.AgencyVesselOwnership = false;
            _previousLaunchSites = GeneralSettings.SettingsStore.AgencyLaunchSitesPerAgency;
            SaveAndReplacePrivateField(typeof(AgencyLaunchSiteStore), "_assignments", new Dictionary<string, Guid>(StringComparer.Ordinal));
            SaveAndReplacePrivateField(typeof(AgencyLaunchSiteStore), "_revision", 0L);
            SaveAndReplacePrivateField(typeof(AgencyLaunchSiteStore), "_loadError", null);
            SaveAndClear(AgencyStore.Agencies);
            SaveAndClear(PrivateDictionary<string, Guid>(typeof(AgencyAchievementRegistry), "Holders"));
            SaveAndClear(ScenarioStoreSystem.CurrentScenarios);
            SaveAndClear(PrivateDictionary<Guid, ConcurrentDictionary<string, ConfigNode>>(typeof(AgencyScenarioStore), "Store"));
            SaveAndClear(PrivateDictionary<string, object>(typeof(AgencyScenarioStore), "Semaphores"));
            SaveAndReplacePrivateField(typeof(AgencyVesselMap), "_document", new OwnershipDocument());
            SaveAndReplacePrivateField(typeof(AgencyVesselMap), "_loadError", null);
            ServerContext.DataDirectory = Root;
            ScenarioSystem.ScenariosPath = Path.Combine(ServerContext.UniverseDirectory, "Scenarios");
            Directory.CreateDirectory(ServerContext.AgenciesDirectory);
        }

        private void SaveAndReplacePrivateField(Type type, string name, object replacement)
        {
            var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            var saved = field.GetValue(null);
            field.SetValue(null, replacement);
            _restore.Add(() => field.SetValue(null, saved));
        }

        private static ConcurrentDictionary<TKey, TValue> PrivateDictionary<TKey, TValue>(Type type, string name)
            => (ConcurrentDictionary<TKey, TValue>)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        private void SaveAndClear<TKey, TValue>(ConcurrentDictionary<TKey, TValue> store)
        {
            var saved = store.ToArray();
            store.Clear();
            _restore.Add(() =>
            {
                store.Clear();
                foreach (var pair in saved) store[pair.Key] = pair.Value;
            });
        }

        public void Dispose()
        {
            // Never let queued writes reach a restored root or a deleted fixture directory.
            AgencyVesselMap.WaitForPendingWritesAsync().GetAwaiter().GetResult();
            foreach (var restore in _restore) restore();
            ServerContext.DataDirectory = _previousDataDirectory;
            ScenarioSystem.ScenariosPath = _previousScenariosPath;
            GeneralSettings.SettingsStore.GameMode = _previousGameMode;
            GeneralSettings.SettingsStore.AgencyScansatPerAgency = _previousScansatPerAgency;
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = _previousKerbalsPerAgency;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = _previousZeroStartingKerbals;
            GeneralSettings.SettingsStore.AgencyLaunchSitesPerAgency = _previousLaunchSites;
            GeneralSettings.SettingsStore.AgencyVesselOwnership = _previousOwnership;
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
