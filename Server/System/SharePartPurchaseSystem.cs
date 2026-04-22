using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Server;

namespace Server.System
{
    public class SharePartPurchaseSystem
    {
        public static void PurchaseReceived(ClientStructure client, ShareProgressPartPurchaseMsgData data)
        {
            var agency = AgencySystem.GetAgency(client.AgencyId);
            if (agency == null)
            {
                LunaLog.Warning($"[Agency] Dropping part purchase from {client.PlayerName}: no agency assigned.");
                return;
            }

            LunaLog.Info($"[Agency] PartPurchase agency='{agency.Name}' player={client.PlayerName} tech='{data.TechId}' part='{data.PartName}'");

            AgencyScenarioUpdater.AppendPartPurchase(agency.Id, data.TechId, data.PartName);

            MessageQueuer.RelayMessageToAgency<ShareProgressSrvMsg>(client, agency.Id, data);
        }
    }
}
