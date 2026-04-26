using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Server;

namespace Server.System
{
    public static class ShareFundsSystem
    {
        public static void FundsReceived(ClientStructure client, ShareProgressFundsMsgData data)
        {
            var agency = AgencySystem.GetAgency(client.AgencyId);
            if (agency == null)
            {
                LunaLog.Warning($"[Agency] Dropping funds update from {client.PlayerName}: no agency assigned.");
                return;
            }

            LunaLog.Info($"[Agency] FundsUpdate agency='{agency.Name}' player={client.PlayerName} before={agency.Funds} after={data.Funds} reason={data.Reason}");

            // Leaderboard: only positive deltas count toward lifetime earned.
            // Spending money on parts / contracts shouldn't reduce the
            // historical total, but receiving rewards / transfers / recoveries
            // should accumulate. Computed BEFORE we update agency.Funds.
            var delta = data.Funds - agency.Funds;
            if (delta > 0)
            {
                lock (agency.Lock) agency.LifetimeFundsEarned += delta;
            }

            // Persist to this agency's Funding scenario, not the global one.
            AgencyScenarioUpdater.WriteFunds(agency.Id, data.Funds);

            // Reflect the headline value on the Agency entity so admin UI
            // and join-time sync stay accurate without re-reading scenarios.
            AgencySystem.SetAgencyFunds(agency, data.Funds, data.Reason ?? "funds-update");

            // Relay to other members of the same agency only.
            MessageQueuer.RelayMessageToAgency<ShareProgressSrvMsg>(client, agency.Id, data);
        }
    }
}
