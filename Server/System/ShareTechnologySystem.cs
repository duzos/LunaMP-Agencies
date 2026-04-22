using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Server;

namespace Server.System
{
    public static class ShareTechnologySystem
    {
        public static void TechnologyReceived(ClientStructure client, ShareProgressTechnologyMsgData data)
        {
            var agency = AgencySystem.GetAgency(client.AgencyId);
            if (agency == null)
            {
                LunaLog.Warning($"[Agency] Dropping tech unlock from {client.PlayerName}: no agency assigned.");
                return;
            }

            LunaLog.Info($"[Agency] TechUnlock agency='{agency.Name}' player={client.PlayerName} node='{data.TechNode.Id}'");

            // Persist into this agency's R&D scenario, and bump the headline count.
            if (AgencyScenarioUpdater.AppendTechNodeBytes(agency.Id, data.TechNode.Data, data.TechNode.NumBytes))
            {
                AgencySystem.IncrementUnlockedTech(agency, 1);
            }

            // Relay only to the same agency's members. Other agencies must
            // not receive unlock events for tech they have not researched.
            MessageQueuer.RelayMessageToAgency<ShareProgressSrvMsg>(client, agency.Id, data);
        }
    }
}
