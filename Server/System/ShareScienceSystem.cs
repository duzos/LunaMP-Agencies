using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Server;

namespace Server.System
{
    public static class ShareScienceSystem
    {
        public static void ScienceReceived(ClientStructure client, ShareProgressScienceMsgData data)
        {
            var agency = AgencySystem.GetAgency(client.AgencyId);
            if (agency == null)
            {
                LunaLog.Warning($"[Agency] Dropping science update from {client.PlayerName}: no agency assigned.");
                return;
            }

            LunaLog.Info($"[Agency] ScienceUpdate agency='{agency.Name}' player={client.PlayerName} before={agency.Science} after={data.Science} reason={data.Reason}");

            AgencyScenarioUpdater.WriteScience(agency.Id, data.Science);
            AgencySystem.SetAgencyScience(agency, data.Science, data.Reason ?? "science-update");

            MessageQueuer.RelayMessageToAgency<ShareProgressSrvMsg>(client, agency.Id, data);
        }
    }
}
