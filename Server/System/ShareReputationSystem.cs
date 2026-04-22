using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Server;

namespace Server.System
{
    public static class ShareReputationSystem
    {
        public static void ReputationReceived(ClientStructure client, ShareProgressReputationMsgData data)
        {
            var agency = AgencySystem.GetAgency(client.AgencyId);
            if (agency == null)
            {
                LunaLog.Warning($"[Agency] Dropping reputation update from {client.PlayerName}: no agency assigned.");
                return;
            }

            LunaLog.Info($"[Agency] ReputationUpdate agency='{agency.Name}' player={client.PlayerName} before={agency.Reputation} after={data.Reputation} reason={data.Reason}");

            AgencyScenarioUpdater.WriteReputation(agency.Id, data.Reputation);
            AgencySystem.SetAgencyReputation(agency, data.Reputation, data.Reason ?? "reputation-update");

            MessageQueuer.RelayMessageToAgency<ShareProgressSrvMsg>(client, agency.Id, data);
        }
    }
}
