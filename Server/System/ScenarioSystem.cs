using LmpCommon.Enums;
using LmpCommon.Message.Data.Scenario;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Properties;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Scenario;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.System
{
    public class ScenarioSystem
    {
        public const string ScenarioFileFormat = ".txt";
        public static string ScenariosPath = Path.Combine(ServerContext.UniverseDirectory, "Scenarios");

        public static bool GenerateDefaultScenarios()
        {
            var scenarioFilesCreated =
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ROCScenario.txt"), Resources.ROCScenario) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "DeployedScience.txt"), Resources.DeployedScience) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "CommNetScenario.txt"), Resources.CommNetScenario) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "PartUpgradeManager.txt"), Resources.PartUpgradeManager) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ProgressTracking.txt"), Resources.ProgressTracking) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ResourceScenario.txt"), Resources.ResourceScenario) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioAchievements.txt"), Resources.ScenarioAchievements) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioDestructibles.txt"), Resources.ScenarioDestructibles) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "SentinelScenario.txt"), Resources.SentinelScenario) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "VesselRecovery.txt"), Resources.VesselRecovery) &&
            FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioNewGameIntro.txt"), Resources.ScenarioNewGameIntro);

            if (GeneralSettings.SettingsStore.GameMode != GameMode.Sandbox)
            {
                scenarioFilesCreated &= FileHandler.CreateFile(Path.Combine(ScenariosPath, "ResearchAndDevelopment.txt"), Resources.ResearchAndDevelopment);
            }
            else
            {
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "ResearchAndDevelopment.txt"));
            }

            if (GeneralSettings.SettingsStore.GameMode == GameMode.Career)
            {
                scenarioFilesCreated &=
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "ContractSystem.txt"), Resources.ContractSystem) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "Funding.txt"), Resources.Funding) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "Reputation.txt"), Resources.Reputation) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioContractEvents.txt"), Resources.ScenarioContractEvents) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "ScenarioUpgradeableFacilities.txt"), Resources.ScenarioUpgradeableFacilities) &&
                FileHandler.CreateFile(Path.Combine(ScenariosPath, "StrategySystem.txt"), Resources.StrategySystem);
            }
            else
            {
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "ContractSystem.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "Funding.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "Reputation.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "ScenarioContractEvents.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "ScenarioUpgradeableFacilities.txt"));
                FileHandler.FileDelete(Path.Combine(ScenariosPath, "StrategySystem.txt"));
            }

            return scenarioFilesCreated;
        }

        /// <summary>
        /// Modules that are stored per-agency. For a career-mode client, these
        /// come from the client's agency; all other modules come from the
        /// global store.
        /// </summary>
        /// <summary>
        /// Modules whose state is mutated through ShareProgress* delta
        /// messages — client uploads of these must be dropped to avoid
        /// stale local KSP state clobbering the server.
        /// </summary>
        private static readonly HashSet<string> DeltaManagedModules = new HashSet<string>(AgencyMigration.DeltaManagedModules);

        public static void SendScenarioModules(ClientStructure client)
        {
            // Deduplicate module names: take the agency's copy when available,
            // fall back to the global copy otherwise.
            var agencyDict = client.AgencyId != global::System.Guid.Empty
                ? AgencyScenarioStore.GetOrCreateDict(client.AgencyId)
                : null;

            // Re-read the active set on every send so config flags toggled
            // mid-server-life take effect on the next client connect.
            var agencyScoped = AgencyMigration.GetActiveAgencyScopedModules();

            var names = new HashSet<string>(ScenarioStoreSystem.CurrentScenarios.Keys);
            if (agencyDict != null)
            {
                foreach (var k in agencyDict.Keys) names.Add(k);
            }

            var list = new List<ScenarioInfo>(names.Count);
            foreach (var moduleName in names)
            {
                string text = null;

                if (agencyDict != null && agencyScoped.Contains(moduleName)
                    && agencyDict.TryGetValue(moduleName, out var agencyNode) && agencyNode != null)
                {
                    text = agencyNode.ToString();
                }
                else
                {
                    text = ScenarioStoreSystem.GetScenarioInConfigNodeFormat(moduleName);
                }

                if (string.IsNullOrEmpty(text)) continue;

                var serializedData = Encoding.UTF8.GetBytes(text);
                list.Add(new ScenarioInfo
                {
                    Data = serializedData,
                    NumBytes = serializedData.Length,
                    Module = Path.GetFileNameWithoutExtension(moduleName),
                });
            }

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<ScenarioDataMsgData>();
            msgData.ScenariosData = list.ToArray();
            msgData.ScenarioCount = list.Count;

            LunaLog.Info($"[Agency] Sending {list.Count} scenario modules to {client.PlayerName} agency={client.AgencyId}");
            MessageQueuer.SendToClient<ScenarioSrvMsg>(client, msgData);

            // KSP's scenario-load path is additive: HighLogic.CurrentGame.scenarios.Add
            // on the client does not displace any existing module KSP loaded from
            // persistent.sfs, so Funding.Instance can keep a stale local value after
            // reconnect. Push the agency's career state as ShareProgress* deltas in
            // addition — the client handler calls Funding.Instance.SetFunds(...) which
            // is authoritative regardless of what the scenario loader produced.
            var agency = AgencySystem.GetAgency(client.AgencyId);
            if (agency != null)
            {
                AgencyFanout.PushCareerToClient(client, agency);
                LunaLog.Info($"[Agency] Pushed career-state delta to {client.PlayerName}: funds={agency.Funds} sci={agency.Science} rep={agency.Reputation}");
            }
        }


        public static void ParseReceivedScenarioData(ClientStructure client, ScenarioBaseMsgData messageData)
        {
            var data = (ScenarioDataMsgData)messageData;
            LunaLog.Debug($"Saving {data.ScenarioCount} scenario modules from {client.PlayerName}");

            // The client runs a 30s periodic routine that re-uploads every
            // loaded ScenarioModule. With the per-agency model, blindly
            // accepting agency-scoped modules lets stale client state (for
            // example an uninitialised Funding.Instance reading as 0) clobber
            // the authoritative server copy the next time the player is in a
            // different agency than their cached KSP state reflects.
            //
            // For agency-scoped modules the incremental deltas already arrive
            // via the ShareProgress* messages, so we drop the uploads.
            // Re-read the active snapshot set so config flag toggles take
            // effect mid-life. Computed once per upload, not per module.
            var snapshotManaged = AgencyMigration.GetActiveSnapshotManagedModules();

            for (var i = 0; i < data.ScenarioCount; i++)
            {
                var moduleName = data.ScenariosData[i].Module;

                // Delta-managed: server is authoritative via ShareProgress*
                // messages. Drop the client's snapshot.
                if (DeltaManagedModules.Contains(moduleName))
                {
                    LunaLog.Debug($"[Agency] Ignoring scenario upload for delta-managed module '{moduleName}' from {client.PlayerName}");
                    continue;
                }

                var scenarioAsConfigNode = Encoding.UTF8.GetString(data.ScenariosData[i].Data, 0, data.ScenariosData[i].NumBytes);

                // Snapshot-managed: no delta channel, so route the upload
                // into the player's agency scenario store. Preserves
                // SCANsat coverage / strategy state / facility upgrades
                // per agency.
                if (snapshotManaged.Contains(moduleName) && client.AgencyId != global::System.Guid.Empty)
                {
                    LunaLog.Debug($"[Agency] Storing scenario upload '{moduleName}' from {client.PlayerName} into agency {client.AgencyId}");
                    AgencyScenarioUpdater.RawConfigNodeInsertOrUpdate(client.AgencyId, moduleName, scenarioAsConfigNode);
                    continue;
                }

                ScenarioDataUpdater.RawConfigNodeInsertOrUpdate(moduleName, scenarioAsConfigNode);
            }
        }
    }
}
