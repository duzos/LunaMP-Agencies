using LunaConfigNode.CfgNode;
using Server.Log;
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
        public static void RawConfigNodeInsertOrUpdate(Guid agencyId, string moduleName, string scenarioAsConfigNodeText)
        {
            global::System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var node = new ConfigNode(scenarioAsConfigNodeText) { Name = moduleName };
                    lock (AgencyScenarioStore.SemaphoreFor(agencyId, moduleName))
                    {
                        AgencyScenarioStore.AddOrUpdate(agencyId, moduleName, node);
                    }
                }
                catch (Exception e)
                {
                    LunaLog.Warning($"[Agency] Failed to upsert agency scenario {moduleName} for {agencyId}: {e.Message}");
                }
            });
        }

        public static void WriteFunds(Guid agencyId, double funds)
        {
            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "Funding"))
            {
                var node = AgencyScenarioStore.GetOrNull(agencyId, "Funding");
                if (node == null) return;
                node.UpdateValue("funds", funds.ToString(CultureInfo.InvariantCulture));
            }
        }

        public static void WriteScience(Guid agencyId, float science)
        {
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

                var incoming = new ConfigNode(Encoding.UTF8.GetString(techNodeBytes, 0, numBytes)) { Name = "Tech" };
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

                var received = new ConfigNode(Encoding.UTF8.GetString(subjectBytes, 0, numBytes)) { Parent = rd, Name = "Science" };
                if (received.IsEmpty()) return;

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

                // Append part via text-edit. LunaConfigNode does not expose
                // a value-add primitive on ConfigNode, so rebuild the Tech
                // node by appending the part line to its serialized form.
                var originalText = tech.ToString();
                var injected = InjectPartLine(originalText, partName);
                var rebuilt = new ConfigNode(injected);

                rd.ReplaceNode(tech, rebuilt);
                return true;
            }
        }

        /// <summary>
        /// Inserts a <c>part = &lt;name&gt;</c> line just before the closing
        /// brace of the serialized Tech node.
        /// </summary>
        private static string InjectPartLine(string nodeText, string partName)
        {
            if (string.IsNullOrEmpty(nodeText)) return nodeText;
            var lastBrace = nodeText.LastIndexOf('}');
            if (lastBrace < 0) return nodeText;
            return nodeText.Substring(0, lastBrace)
                   + "  part = " + partName + "\n"
                   + nodeText.Substring(lastBrace);
        }

        public static bool ForceCompleteContract(Guid agencyId, string guid) => MoveContract(agencyId, guid, "Completed", finished: true);
        public static bool ForceCancelContract(Guid agencyId, string guid) => MoveContract(agencyId, guid, "Cancelled", finished: true);

        private static bool MoveContract(Guid agencyId, string guid, string newState, bool finished)
        {
            if (string.IsNullOrEmpty(guid)) return false;

            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ContractSystem"))
            {
                var cs = AgencyScenarioStore.GetOrNull(agencyId, "ContractSystem");
                if (cs == null) return false;

                var contracts = cs.GetNode("CONTRACTS")?.Value;
                var finishedNode = cs.GetNode("CONTRACTS_FINISHED")?.Value;
                if (contracts == null || finishedNode == null) return false;

                var contract = contracts.GetNodes("CONTRACT").Select(e => e.Value).FirstOrDefault(c => c.GetValue("guid")?.Value == guid);
                if (contract == null) return false;

                contract.UpdateValue("state", newState);
                if (finished)
                {
                    contracts.RemoveNode(contract);
                    finishedNode.AddNode(contract);
                }
                LunaLog.Info($"[Agency] Contract {guid} state->{newState} in agency={agencyId}");
                return true;
            }
        }
    }
}
