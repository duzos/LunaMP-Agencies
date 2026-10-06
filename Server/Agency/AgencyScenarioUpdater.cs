using Server.Diagnostics;
using LunaConfigNode.CfgNode;
using Server.Log;
using Server.System.Scenario;
using System.Threading.Tasks;
using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Server.Agency
{
    /// <summary>
    /// Per-agency equivalent of <see cref="System.Scenario.ScenarioDataUpdater"/>.
    /// Writes career values into the agency's scenario ConfigNodes so the
    /// on-disk state stays consistent with in-memory broadcasts.
    /// </summary>
    public static class AgencyScenarioUpdater
    {
        /// <summary>
        /// Drop-in replacement for ScenarioDataUpdater.RawConfigNodeInsertOrUpdate
        /// that routes into the agency's scenario store rather than the global
        /// one. Used when a client uploads scenario state (on scene change,
        /// disconnect, etc.) so each agency's state stays independent.
        /// </summary>
        public static Task RawConfigNodeInsertOrUpdate(Guid agencyId, string moduleName, string scenarioAsConfigNodeText)
        {
            return Task.Run(() =>
            {
                try
                {
                    var node = ScenarioDataUpdater.ParseClientConfigNode(scenarioAsConfigNodeText, moduleName);
                    lock (AgencyVesselMap.TransactionGate)
                    lock (AgencyScenarioStore.SemaphoreFor(agencyId, moduleName))
                    {
                        if (AgencyEconomyStore.Enabled && AgencyEconomyStore.TryBalance(agencyId, out var funds, out var science))
                        {
                            if (moduleName == "Funding") node.UpdateValue("funds", funds.ToString(CultureInfo.InvariantCulture));
                            if (moduleName == "ResearchAndDevelopment") node.UpdateValue("sci", science.ToString(CultureInfo.InvariantCulture));
                        }
                        AgencyScenarioStore.AddOrUpdate(agencyId, moduleName, node);
                    }
                    PlaytestDiagnostics.Write("scenario.apply", () => $"agency={agencyId} module={moduleName} route=agency result=applied-in-memory");
                }
                catch (Exception e)
                {
                    PlaytestDiagnostics.Write("scenario.apply", () => $"agency={agencyId} module={moduleName} route=agency result=failed errorType={e.GetType().Name}");
                    LunaLog.Warning($"[Agency] Failed to upsert agency scenario {moduleName} for {agencyId}: {e.Message}");
                }
            });
        }

        public static void WriteFunds(Guid agencyId, double funds)
        {
            if (AgencyEconomyStore.Enabled && AgencyEconomyStore.TryBalance(agencyId, out var authoritativeFunds, out _)) funds = authoritativeFunds;
            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "Funding"))
            {
                var node = AgencyScenarioStore.GetOrNull(agencyId, "Funding");
                if (node == null) return;
                node.UpdateValue("funds", funds.ToString(CultureInfo.InvariantCulture));
            }
        }

        public static void WriteScience(Guid agencyId, float science)
        {
            if (AgencyEconomyStore.Enabled && AgencyEconomyStore.TryBalance(agencyId, out _, out var authoritativeScience)) science = (float)authoritativeScience;
            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ResearchAndDevelopment"))
            {
                var node = AgencyScenarioStore.GetOrNull(agencyId, "ResearchAndDevelopment");
                if (node == null) return;
                node.UpdateValue("sci", science.ToString(CultureInfo.InvariantCulture));
            }
        }

        public static void WriteReputation(Guid agencyId, float reputation)
        {
            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "Reputation"))
            {
                var node = AgencyScenarioStore.GetOrNull(agencyId, "Reputation");
                if (node == null) return;
                node.UpdateValue("rep", reputation.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Append the serialized tech node bytes to the agency's R&amp;D
        /// scenario if not already present. Returns true if a new Tech node
        /// was added.
        /// </summary>
        public static bool AppendTechNodeBytes(Guid agencyId, byte[] techNodeBytes, int numBytes)
        {
            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ResearchAndDevelopment"))
            {
                var rd = AgencyScenarioStore.GetOrNull(agencyId, "ResearchAndDevelopment");
                if (rd == null) return false;

                var incoming = ScenarioDataUpdater.ParseClientConfigNode(techNodeBytes, numBytes, "Tech");
                var incomingId = incoming.GetValue("id")?.Value;
                if (string.IsNullOrEmpty(incomingId)) return false;

                var existing = rd.GetNodes("Tech").Select(e => e.Value).FirstOrDefault(t => t.GetValue("id")?.Value == incomingId);
                if (existing != null)
                    return false;

                rd.AddNode(incoming);
                return true;
            }
        }

        public static bool ForceUnlockTech(Guid agencyId, string techId)
        {
            if (string.IsNullOrEmpty(techId)) return false;

            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ResearchAndDevelopment"))
            {
                var rd = AgencyScenarioStore.GetOrNull(agencyId, "ResearchAndDevelopment");
                if (rd == null) return false;

                var existing = rd.GetNodes("Tech").Select(e => e.Value).FirstOrDefault(t => t.GetValue("id")?.Value == techId);
                if (existing != null) return false;

                var techNodeText = $"id = {techId}\nstate = Available\ncost = 0\n";
                rd.AddNode(new ConfigNode(techNodeText) { Name = "Tech" });
                return true;
            }
        }

        /// <summary>
        /// Upserts a Science subject (per-experiment/biome cap record) into
        /// the agency's ResearchAndDevelopment scenario. Mirrors the global
        /// <c>ScenarioDataUpdater.WriteScienceSubjectDataToFile</c> behaviour.
        /// Called when the agency config flag <c>AgencyExperimentsPerAgency</c>
        /// is true.
        /// </summary>
        public static void WriteScienceSubject(Guid agencyId, byte[] subjectBytes, int numBytes)
        {
            if (subjectBytes == null || numBytes <= 0) return;

            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ResearchAndDevelopment"))
            {
                var rd = AgencyScenarioStore.GetOrNull(agencyId, "ResearchAndDevelopment");
                if (rd == null) return;

                var received = ScenarioDataUpdater.ParseClientConfigNode(subjectBytes, numBytes, "Science");
                if (received.IsEmpty() || string.IsNullOrEmpty(received.GetValue("id")?.Value)) return;
                received.Parent = rd;

                var existing = rd.GetNodes("Science").Select(v => v.Value)
                    .FirstOrDefault(n => n.GetValue("id")?.Value == received.GetValue("id")?.Value);

                if (existing != null) rd.ReplaceNode(existing, received);
                else rd.AddNode(received);
            }
        }

        public static bool AppendPartPurchase(Guid agencyId, string techId, string partName)
        {
            if (string.IsNullOrEmpty(techId) || string.IsNullOrEmpty(partName)) return false;

            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ResearchAndDevelopment"))
            {
                var rd = AgencyScenarioStore.GetOrNull(agencyId, "ResearchAndDevelopment");
                if (rd == null) return false;

                var tech = rd.GetNodes("Tech").Select(e => e.Value).FirstOrDefault(t => t.GetValue("id")?.Value == techId);
                if (tech == null) return false;

                var existingValues = tech.GetValues("part").Select(v => v.Value).ToArray();
                if (existingValues.Any(v => v == partName)) return false;

                tech.CreateValue(new CfgNodeValue<string, string>("part", partName));
                return true;
            }
        }

        public static bool ForceCompleteContract(Guid agencyId, string guid) => MoveContract(agencyId, guid, "Completed");
        public static bool ForceCancelContract(Guid agencyId, string guid) => MoveContract(agencyId, guid, "Cancelled");

        private static bool MoveContract(Guid agencyId, string guid, string newState)
        {
            if (string.IsNullOrEmpty(guid)) return false;

            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ContractSystem"))
            {
                var cs = AgencyScenarioStore.GetOrNull(agencyId, "ContractSystem");
                if (cs == null) return false;

                ScenarioDataUpdater.MigrateContractsScenario(cs);
                var contracts = cs.GetNode("CONTRACTS")?.Value;
                if (contracts == null) return false;

                var contract = contracts.GetNodes("CONTRACT").Select(e => e.Value).FirstOrDefault(c => c.GetValue("guid")?.Value == guid);
                if (contract == null) return false;

                contract.UpdateValue("state", newState);
                ScenarioDataUpdater.ApplyContractUpdates(cs, new[] { contract });
                LunaLog.Info($"[Agency] Contract {guid} state->{newState} in agency={agencyId}");
                return true;
            }
        }
    }
}
