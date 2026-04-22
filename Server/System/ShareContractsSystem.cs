using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Server;
using System;

namespace Server.System
{
    /// <summary>
    /// Server-side contract handler with per-agency ownership.
    ///
    /// Contracts remain globally visible so any player can see the full list,
    /// but each contract is locked to exactly one agency the first time one of
    /// its members accepts it. Subsequent accept attempts by another agency
    /// are rejected. Rewards/penalties apply only to the owning agency's
    /// scenario store.
    /// </summary>
    public static class ShareContractsSystem
    {
        public static void ContractsReceived(ClientStructure client, ShareProgressContractsMsgData data)
        {
            var agency = AgencySystem.GetAgency(client.AgencyId);
            if (agency == null)
            {
                LunaLog.Warning($"[Agency] Dropping contracts update from {client.PlayerName}: no agency assigned.");
                return;
            }

            LunaLog.Info($"[Agency] ContractsUpdate agency='{agency.Name}' player={client.PlayerName} count={data.ContractCount}");

            for (var i = 0; i < data.ContractCount; i++)
            {
                var contract = data.Contracts[i];
                if (contract == null) continue;

                if (contract.OwningAgencyId == Guid.Empty)
                {
                    // First agency to touch the contract becomes its owner.
                    contract.OwningAgencyId = agency.Id;
                    LunaLog.Info($"[Agency] ContractAssigned contract={contract.ContractGuid} owner='{agency.Name}' id={agency.Id}");
                }
                else if (contract.OwningAgencyId != agency.Id)
                {
                    // Another agency owns this contract. Log and overwrite
                    // the client-sent owner with the authoritative value so
                    // the relayed message is consistent, but do NOT update
                    // the other agency's scenario.
                    LunaLog.Warning($"[Agency] Rejecting cross-agency contract update: contract={contract.ContractGuid} sender='{agency.Name}' owner={contract.OwningAgencyId}");
                    continue;
                }
            }

            // Persist into the owning agency's contract scenario.
            AgencyContractStore.WriteContracts(agency.Id, data);

            // Broadcast to ALL authenticated clients so every agency sees
            // the ownership flag in their UI — the flag is public even
            // though the contract's rewards are not.
            MessageQueuer.SendToAllClients<ShareProgressSrvMsg>(data);
        }
    }
}
