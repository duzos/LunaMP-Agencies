using LunaConfigNode.CfgNode;
using Server.Log;
using Server.System;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Server.Agency
{
    /// <summary>
    /// Per-agency scenario storage. Mirrors <see cref="ScenarioStoreSystem"/>
    /// but keys by agency id. Each agency owns its own copy of the relevant
    /// career scenario modules (Funding, Reputation, ResearchAndDevelopment,
    /// ContractSystem, etc.).
    ///
    /// Files live under:
    ///   Universe/Agencies/&lt;guid&gt;/Scenarios/&lt;Module&gt;.txt
    ///
    /// Non-career scenarios (DeployedScience, CommNetScenario, etc.) stay in
    /// the global <see cref="ScenarioStoreSystem"/> — agencies only need to
    /// fork the modules that legitimately differ between groups of players.
    /// </summary>
    public static class AgencyScenarioStore
    {
        public const string ScenarioFileFormat = ".txt";

        private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, ConfigNode>> Store
            = new ConcurrentDictionary<Guid, ConcurrentDictionary<string, ConfigNode>>();

        private static readonly ConcurrentDictionary<string, object> Semaphores
            = new ConcurrentDictionary<string, object>();

        public static string AgencyScenariosPath(Guid agencyId)
            => Path.Combine(AgencyStore.AgencyDirectory(agencyId), "Scenarios");

        public static ConcurrentDictionary<string, ConfigNode> GetOrCreateDict(Guid agencyId)
        {
            return Store.GetOrAdd(agencyId, _ => new ConcurrentDictionary<string, ConfigNode>());
        }

        public static bool TryGet(Guid agencyId, string module, out ConfigNode scenario)
        {
            scenario = null;
            if (!Store.TryGetValue(agencyId, out var dict)) return false;
            return dict.TryGetValue(module, out scenario);
        }

        public static ConfigNode GetOrNull(Guid agencyId, string module)
        {
            return TryGet(agencyId, module, out var sc) ? sc : null;
        }

        public static void AddOrUpdate(Guid agencyId, string module, ConfigNode node)
        {
            var dict = GetOrCreateDict(agencyId);
            dict.AddOrUpdate(module, node, (_, __) => node);
        }

        public static object SemaphoreFor(Guid agencyId, string module)
        {
            var key = agencyId.ToString("N") + "/" + module;
            return Semaphores.GetOrAdd(key, _ => new object());
        }

        public static void LoadAllExisting()
        {
            foreach (var agencyId in AgencyStore.Agencies.Keys)
            {
                LoadForAgency(agencyId);
            }
        }

        public static void LoadForAgency(Guid agencyId)
        {
            var path = AgencyScenariosPath(agencyId);
            if (!FileHandler.FolderExists(path))
                FileHandler.FolderCreate(path);

            var dict = GetOrCreateDict(agencyId);
            foreach (var file in Directory.GetFiles(path).Where(f => Path.GetExtension(f) == ScenarioFileFormat))
            {
                try
                {
                    var key = Path.GetFileNameWithoutExtension(file);
                    dict[key] = new ConfigNode(FileHandler.ReadFileText(file));
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[Agency] Failed to load scenario {file}: {e}");
                }
            }
        }

        public static void BackupAll()
        {
            foreach (var pair in Store)
                BackupAgency(pair.Key);
        }

        public static void BackupAgency(Guid agencyId)
        {
            if (!Store.TryGetValue(agencyId, out var dict)) return;

            var path = AgencyScenariosPath(agencyId);
            if (!FileHandler.FolderExists(path))
                FileHandler.FolderCreate(path);

            foreach (var scenario in dict.ToArray())
            {
                lock (SemaphoreFor(agencyId, scenario.Key))
                {
                    FileHandler.WriteToFile(Path.Combine(path, $"{scenario.Key}{ScenarioFileFormat}"), scenario.Value.ToString());
                }
            }
        }

        public static void EnsureBaselineForAgency(Guid agencyId, double startingFunds, float startingScience, float startingReputation)
        {
            var inv = global::System.Globalization.CultureInfo.InvariantCulture;

            if (!TryGet(agencyId, "Funding", out _))
            {
                // Body only; Name is set separately — the ConfigNode(string)
                // constructor doesn't parse the outer "<Name> { ... }" wrapper.
                var text = "name = Funding\nscene = 7, 8, 5, 6, 9\n"
                           + $"funds = {startingFunds.ToString(inv)}\n";
                AddOrUpdate(agencyId, "Funding", new ConfigNode(text) { Name = "Funding" });
            }

            if (!TryGet(agencyId, "ResearchAndDevelopment", out _))
            {
                var text = "name = ResearchAndDevelopment\nscene = 5, 6, 7, 8, 9\n"
                           + $"sci = {startingScience.ToString(inv)}\n";
                AddOrUpdate(agencyId, "ResearchAndDevelopment", new ConfigNode(text) { Name = "ResearchAndDevelopment" });
            }

            if (!TryGet(agencyId, "Reputation", out _))
            {
                var text = "name = Reputation\nscene = 5, 6, 7, 8, 9\n"
                           + $"rep = {startingReputation.ToString(inv)}\n";
                AddOrUpdate(agencyId, "Reputation", new ConfigNode(text) { Name = "Reputation" });
            }

            if (!TryGet(agencyId, "ContractSystem", out _))
            {
                var text = "name = ContractSystem\nscene = 7, 8, 5, 6, 9\nupdate = 0\n"
                           + "CONTRACTS\n{\n}\n"
                           + "CONTRACTS_FINISHED\n{\n}\n";
                AddOrUpdate(agencyId, "ContractSystem", new ConfigNode(text) { Name = "ContractSystem" });
            }
        }

        public static void RemoveAgency(Guid agencyId)
        {
            Store.TryRemove(agencyId, out _);
        }
    }
}
