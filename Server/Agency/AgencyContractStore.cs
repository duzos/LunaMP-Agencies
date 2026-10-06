using LmpCommon.Message.Data.ShareProgress;
using LunaConfigNode.CfgNode;
using Server.Log;
using Server.System.Scenario;
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
        public static void WriteContracts(Guid agencyId, ShareProgressContractsMsgData data)
        {
            if (data == null || data.ContractCount == 0) return;

            lock (AgencyScenarioStore.SemaphoreFor(agencyId, "ContractSystem"))
            {
                var cs = AgencyScenarioStore.GetOrNull(agencyId, "ContractSystem");
                if (cs == null) return;

                var incoming = new List<ConfigNode>();
                for (var i = 0; i < data.ContractCount; i++)
                {
                    var info = data.Contracts[i];
                    if (info == null || info.OwningAgencyId != agencyId) continue;
                    try
                    {
                        incoming.Add(ScenarioDataUpdater.ParseClientConfigNode(info.Data, info.NumBytes, "CONTRACT"));
                    }
                    catch (Exception e)
                    {
                        LunaLog.Error($"[Agency] Contract decode failed for {info.ContractGuid}: {e.Message}");
                    }
                }
                ScenarioDataUpdater.ApplyContractUpdates(cs, incoming);
            }
        }
    }
}
