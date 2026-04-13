using LmpClient.Base;
using LmpClient.Extensions;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using LmpCommon;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Expansions;
using UniLinq;

namespace LmpClient.Systems.Scenario
{
    public class ScenarioSystem : MessageSystem<ScenarioSystem, ScenarioMessageSender, ScenarioMessageHandler>
    {
        #region Fields

        private ConcurrentDictionary<string, string> CheckData { get; } = new ConcurrentDictionary<string, string>();
        public ConcurrentQueue<ScenarioEntry> ScenarioQueue { get; private set; } = new ConcurrentQueue<ScenarioEntry>();

        // ReSharper disable once InconsistentNaming
        private static readonly ConcurrentDictionary<string, Type> _allScenarioTypesInAssemblies = new ConcurrentDictionary<string, Type>();
        private static ConcurrentDictionary<string, Type> AllScenarioTypesInAssemblies
        {
            get
            {
                if (!_allScenarioTypesInAssemblies.Any())
                {
                    var scenarioTypes = AssemblyLoader.loadedAssemblies
                        .SelectMany(a => a.assembly.GetLoadableTypes())
                        .Where(s => s.IsSubclassOf(typeof(ScenarioModule)) && !_allScenarioTypesInAssemblies.ContainsKey(s.Name));

                    foreach (var scenarioType in scenarioTypes)
                        _allScenarioTypesInAssemblies.TryAdd(scenarioType.Name, scenarioType);
                }

                return _allScenarioTypesInAssemblies;
            }
        }

        private static List<string> ScenarioName { get; } = new List<string>();
        private static List<byte[]> ScenarioData { get; } = new List<byte[]>();
        #endregion

        #region Base overrides

        public override string SystemName { get; } = nameof(ScenarioSystem);

        protected override bool ProcessMessagesInUnityThread => false;

        protected override void OnEnabled()
        {
            base.OnEnabled();
            //Run it every 30 seconds
            SetupRoutine(new RoutineDefinition(30000, RoutineExecution.Update, SendScenarioModules));
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();
            CheckData.Clear();
            ScenarioQueue = new ConcurrentQueue<ScenarioEntry>();
            AllScenarioTypesInAssemblies.Clear();
        }

        private static readonly List<Tuple<string, ConfigNode>> ScenariosConfigNodes = new List<Tuple<string, ConfigNode>>();

        #endregion

        #region Public methods

        public void LoadMissingScenarioDataIntoGame()
        {
            //ResourceScenario.Instance.Load();

            var validScenarios = KSPScenarioType.GetAllScenarioTypesInAssemblies()
                .Where(s => !HighLogic.CurrentGame.scenarios.Exists(psm => psm.moduleName == s.ModuleType.Name) 
                            && LoadModuleByGameMode(s)
                            && IsDlcScenarioInstalled(s.ModuleType.Name));

            foreach (var validScenario in validScenarios)
            {
                LunaLog.Log($"[LMP]: Creating new scenario module {validScenario.ModuleType.Name}");
                HighLogic.CurrentGame.AddProtoScenarioModule(validScenario.ModuleType, validScenario.ScenarioAttributes.TargetScenes);
            }
        }

        /// <summary>
        /// Check if the scenario has changed and sends it to the server
        /// </summary>
        public void SendScenarioModules()
        {
            if (Enabled)
            {
                try
                {
                    var modules = ScenarioRunner.GetLoadedModules().Where(s=> s != null);
                    ParseModulesToConfigNodes(modules);
                    TaskFactory.StartNew(SendModulesConfigNodes);
                }
                catch (Exception e)
                {
                    LunaLog.LogError($"Error while trying to send the scenario modules!. Details {e}");
                }
            }
        }

        /// <summary>
        /// This transforms the scenarioModule to a config node. We cannot do this in another thread as Lingoona 
        /// is called sometimes and that makes a hard crash
        /// </summary>
        private static void ParseModulesToConfigNodes(IEnumerable<ScenarioModule> modules)
        {
            ScenariosConfigNodes.Clear();
            foreach (var scenarioModule in modules)
            {
                var scenarioType = scenarioModule.GetType().Name;

                if (IgnoredScenarios.IgnoreSend.Contains(scenarioType))
                    continue;

                if (!IsScenarioModuleAllowed(scenarioType))
                    continue;

                var configNode = new ConfigNode();
                scenarioModule.Save(configNode);

                ScenariosConfigNodes.Add(new Tuple<string, ConfigNode>(scenarioType, configNode));
            }
        }

        /// <summary>
        /// Sends the parsed config nodes to the server after doing basic checks
        /// </summary>
        private void SendModulesConfigNodes()
        {
            ScenarioData.Clear();
            ScenarioName.Clear();

            foreach (var scenarioConfigNode in ScenariosConfigNodes)
            {
                var scenarioBytes = scenarioConfigNode.Item2.Serialize();
                var scenarioHash = Common.CalculateSha256Hash(scenarioBytes);

                if (scenarioBytes.Length == 0)
                {
                    LunaLog.Log($"[LMP]: Error writing scenario data for {scenarioConfigNode.Item1}");
                    continue;
                }

                //Data is the same since last time - Skip it.
                if (CheckData.ContainsKey(scenarioConfigNode.Item1) && CheckData[scenarioConfigNode.Item1] == scenarioHash) continue;

                CheckData[scenarioConfigNode.Item1] = scenarioHash;

                ScenarioName.Add(scenarioConfigNode.Item1);
                ScenarioData.Add(scenarioBytes);
            }

            if (ScenarioName.Any())
                MessageSender.SendScenarioModuleData(ScenarioName, ScenarioData);
        }

        public void LoadScenarioDataIntoGame()
        {
            while (ScenarioQueue.TryDequeue(out var scenarioEntry))
            {
                if (scenarioEntry == null)
                {
                    LunaLog.LogError("[LMP]: Skipping null scenario queue entry.");
                    WriteNullScenarioDebugLog(null);
                    continue;
                }

                if (scenarioEntry.ScenarioNode == null)
                {
                    LunaLog.LogError(
                        $"[LMP]: Skipping scenario '{scenarioEntry.ScenarioModule}' with null ConfigNode. See NullScenario.log in your KSP install folder.");
                    WriteNullScenarioDebugLog(scenarioEntry);
                    continue;
                }

                if (scenarioEntry.ScenarioModule == "ContractSystem")
                {
                    try
                    {
                        StripContractsWithMissingParts(scenarioEntry.ScenarioNode);
                    }
                    catch (Exception e)
                    {
                        LunaLog.LogError($"[ShareContracts]: Error while pre-filtering ContractSystem scenario data: {e.Message}. The scenario will be loaded as-is.");
                    }
                }


                ProtoScenarioModule psm;
                try
                {
                    psm = new ProtoScenarioModule(scenarioEntry.ScenarioNode);
                }
                catch (Exception e)
                {
                    LunaLog.LogError(
                        $"[LMP]: Failed to apply scenario '{scenarioEntry.ScenarioModule}' (ConfigNode could not be copied into ProtoScenarioModule). {e}");
                    continue;
                }

                if (IsScenarioModuleAllowed(psm.moduleName) && !IgnoredScenarios.IgnoreReceive.Contains(psm.moduleName))
                {
                    LunaLog.Log($"[LMP]: Loading {psm.moduleName} scenario data");
                    HighLogic.CurrentGame.scenarios.Add(psm);
                }
                else
                {
                    LunaLog.Log($"[LMP]: Skipping {psm.moduleName} scenario data in {SettingsSystem.ServerSettings.GameMode} mode");
                }
            }
        }

        /// <summary>
        /// Removes CONTRACT nodes from the ContractSystem scenario that reference part names not present
        /// in this client's install. Such contracts would throw an exception during ContractSystem.OnLoad()
        /// and display an error popup. They are silently dropped here instead, with a log warning.
        /// </summary>
        private static void StripContractsWithMissingParts(ConfigNode scenarioNode)
        {
            StripContractSectionWithMissingParts(scenarioNode, "CONTRACTS");
            StripContractSectionWithMissingParts(scenarioNode, "CONTRACTS_FINISHED");
        }

        private static void StripContractSectionWithMissingParts(ConfigNode scenarioNode, string sectionName)
        {
            var sectionNode = scenarioNode.GetNode(sectionName);
            if (sectionNode == null) return;

            var contractNodes = sectionNode.GetNodes("CONTRACT");
            sectionNode.ClearNodes();
            foreach (var contractNode in contractNodes)
            {
                var missingPart = FindMissingPartName(contractNode);
                if (missingPart == null)
                {
                    sectionNode.AddNode(contractNode);
                }
                else
                {
                    LunaLog.LogWarning($"[ShareContracts]: Dropping contract {contractNode.GetValue("guid")} ({contractNode.GetValue("type")}) from {sectionName} — references part '{missingPart}' which is not installed on this client.");
                }
            }
        }

        /// <summary>
        /// Recursively searches a contract ConfigNode for any "part = X" value where X is not a
        /// recognised part in PartLoader. Returns the first missing part name found, or null if all
        /// referenced parts are present.
        /// </summary>
        private static string FindMissingPartName(ConfigNode node)
        {
            foreach (ConfigNode.Value v in node.values)
            {
                if (v.name == "part" && PartLoader.getPartInfoByName(v.value) == null)
                    return v.value;
            }
            foreach (ConfigNode childNode in node.nodes)
            {
                var missing = FindMissingPartName(childNode);
                if (missing != null) return missing;
            }
            return null;
        }

        #endregion

        #region Private methods

        private static void WriteNullScenarioDebugLog(ScenarioEntry entry)
        {
            try
            {
                var path = Path.Combine(MainSystem.KspPath, "NullScenario.log");
                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
                if (entry == null)
                {
                    sb.AppendLine("ScenarioEntry: null");
                    sb.AppendLine();
                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                    return;
                }

                sb.AppendLine($"ScenarioModule: {entry.ScenarioModule ?? "(null)"}");
                sb.AppendLine($"ScenarioNode: {(entry.ScenarioNode == null ? "null" : "non-null (unexpected in this log)")}");
                sb.AppendLine($"RawNumBytes (from network): {entry.RawNumBytes}");
                if (entry.RawScenarioBytes != null && entry.RawScenarioBytes.Length > 0)
                {
                    sb.AppendLine($"RawScenarioBytes.Length: {entry.RawScenarioBytes.Length}");
                    sb.AppendLine();
                    sb.AppendLine("--- Payload as UTF-8 text (wire bytes before ConfigNode parse) ---");
                    sb.AppendLine(Encoding.UTF8.GetString(entry.RawScenarioBytes, 0, entry.RawScenarioBytes.Length));
                    sb.AppendLine();
                    sb.AppendLine("--- Payload as Base64 ---");
                    sb.AppendLine(Convert.ToBase64String(entry.RawScenarioBytes, 0, entry.RawScenarioBytes.Length));
                }
                else
                {
                    sb.AppendLine("RawScenarioBytes: (none — not captured or empty)");
                }

                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[LMP]: Failed to write NullScenario.log: {e.Message}");
            }
        }

        private static bool LoadModuleByGameMode(KSPScenarioType validScenario)
        {
            switch (HighLogic.CurrentGame.Mode)
            {
                case Game.Modes.CAREER:
                    return validScenario.ScenarioAttributes.HasCreateOption(ScenarioCreationOptions.AddToNewCareerGames);
                case Game.Modes.SCIENCE_SANDBOX:
                    return validScenario.ScenarioAttributes.HasCreateOption(ScenarioCreationOptions.AddToNewScienceSandboxGames);
                case Game.Modes.SANDBOX:
                    return validScenario.ScenarioAttributes.HasCreateOption(ScenarioCreationOptions.AddToNewSandboxGames);
            }
            return false;
        }

        private static bool IsDlcScenarioInstalled(string scenarioName)
        {
            if (scenarioName == "DeployedScience" && !ExpansionsLoader.IsExpansionInstalled("Serenity"))
                return false;

            return true;
        }

        private static bool IsScenarioModuleAllowed(string scenarioName)
        {
            if (string.IsNullOrEmpty(scenarioName)) return false;

            if (scenarioName == "DeployedScience" && !ExpansionsLoader.IsExpansionInstalled("Serenity"))
                return false;

            if (!IsDlcScenarioInstalled(scenarioName))
                return false;

            if (!AllScenarioTypesInAssemblies.ContainsKey(scenarioName)) return false; //Module missing

            var scenarioType = AllScenarioTypesInAssemblies[scenarioName];

            var scenarioAttributes = (KSPScenario[])scenarioType.GetCustomAttributes(typeof(KSPScenario), true);
            if (scenarioAttributes.Length > 0)
            {
                var attribute = scenarioAttributes[0];
                var protoAllowed = false;
                if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER)
                {
                    protoAllowed = attribute.HasCreateOption(ScenarioCreationOptions.AddToExistingCareerGames);
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToNewCareerGames);
                }
                if (HighLogic.CurrentGame.Mode == Game.Modes.SCIENCE_SANDBOX)
                {
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToExistingScienceSandboxGames);
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToNewScienceSandboxGames);
                }
                if (HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX)
                {
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToExistingSandboxGames);
                    protoAllowed |= attribute.HasCreateOption(ScenarioCreationOptions.AddToNewSandboxGames);
                }
                return protoAllowed;
            }

            //Scenario is not marked with KSPScenario - let's load it anyway.
            return true;
        }

        #endregion
    }
}
