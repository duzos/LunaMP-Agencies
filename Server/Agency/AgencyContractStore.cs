using LmpCommon.Message.Data.ShareProgress;
using LunaConfigNode.CfgNode;
using Server.Log;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Server.Agency
{
    /// <summary>
    /// Persists contract data into the owning agency's ContractSystem
    /// scenario ConfigNode. Mirrors the stock
    /// <c>ScenarioContractsDataUpdater.WriteContractDataToFile</c> logic but
    /// scoped per agency.
    /// </summary>
    public static class AgencyContractStore
    {
        private static readonly HashSet<string> FinishedContractStates = new HashSet<string>
        {
            "Completed", "Failed", "Cancelled", "DeadlineExpired", "Withdrawn"
        };

        public static void WriteContracts(Guid agencyId, ShareProgressContractsMsgData data)
        {
            if (data == null || data.ContractCount == 0) return;

            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ContractSystem"))
            {
                var cs = AgencyScenarioStore.GetOrNull(agencyId, "ContractSystem");
                if (cs == null) return;

                var contractsNode = cs.GetNode("CONTRACTS")?.Value;
                if (contractsNode == null) return;

                var finishedEntry = cs.GetNode("CONTRACTS_FINISHED");
                ConfigNode finishedNode;
                if (finishedEntry == null)
                {
                    finishedNode = new ConfigNode("") { Name = "CONTRACTS_FINISHED" };
                    cs.AddNode(finishedNode);
                }
                else
                {
                    finishedNode = finishedEntry.Value;
                }

                var existingActive = contractsNode.GetNodes("CONTRACT").Select(c => c.Value).ToArray();
                var existingFinished = finishedNode.GetNodes("CONTRACT").Select(c => c.Value).ToArray();

                for (var i = 0; i < data.ContractCount; i++)
                {
                    var info = data.Contracts[i];
                    if (info == null || info.OwningAgencyId != agencyId) continue;

                    ConfigNode contract;
                    try
                    {
                        contract = new ConfigNode(Encoding.UTF8.GetString(info.Data, 0, info.NumBytes)) { Name = "CONTRACT" };
                    }
                    catch (Exception e)
                    {
                        LunaLog.Error($"[Agency] Contract decode failed for {info.ContractGuid}: {e.Message}");
                        continue;
                    }

                    var guid = contract.GetValue("guid")?.Value;
                    var state = contract.GetValue("state")?.Value ?? string.Empty;

                    var inActive = existingActive.FirstOrDefault(n => n.GetValue("guid")?.Value == guid);
                    var inFinished = existingFinished.FirstOrDefault(n => n.GetValue("guid")?.Value == guid);

                    if (FinishedContractStates.Contains(state))
                    {
                        if (inActive != null) contractsNode.RemoveNode(inActive);

                        if (inFinished != null)
                            finishedNode.ReplaceNode(inFinished, contract);
                        else
                            finishedNode.AddNode(contract);
                    }
                    else
                    {
                        if (inActive != null)
                            contractsNode.ReplaceNode(inActive, contract);
                        else
                            contractsNode.AddNode(contract);
                    }
                }
            }
        }
    }
}
