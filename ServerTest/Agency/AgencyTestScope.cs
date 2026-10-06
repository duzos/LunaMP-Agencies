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
            SaveAndClear(AgencyStore.Agencies);
            SaveAndClear(PrivateDictionary<string, Guid>(typeof(AgencyAchievementRegistry), "Holders"));
            SaveAndClear(ScenarioStoreSystem.CurrentScenarios);
            SaveAndClear(PrivateDictionary<Guid, ConcurrentDictionary<string, ConfigNode>>(typeof(AgencyScenarioStore), "Store"));
            SaveAndClear(PrivateDictionary<string, object>(typeof(AgencyScenarioStore), "Semaphores"));
            SaveAndClear(PrivateDictionary<Guid, Guid>(typeof(AgencyVesselMap), "_map"));
            ServerContext.DataDirectory = Root;
            ScenarioSystem.ScenariosPath = Path.Combine(ServerContext.UniverseDirectory, "Scenarios");
            Directory.CreateDirectory(ServerContext.AgenciesDirectory);
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
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
