using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareAchievementsSystem
    {
        public static void AchievementsReceived(ClientStructure client, ShareProgressAchievementsMsgData data)
        {
            LunaLog.Debug($"Achievements data received: {data.Id}");

            //send the achievements update to all other clients
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteAchievementDataToFile(data);

            // Leaderboard hook: claim "first to X" if this agency reports a
            // milestone no other agency has yet recorded. The achievement Id
            // is KSP's internal key, e.g. "RecoverFromOrbit:Mun" — using it
            // verbatim avoids any need to translate. Idempotent: the second
            // claim from the same agency is a no-op; claims from other
            // agencies after the holder don't displace it.
            var agency = AgencySystem.GetAgency(client.AgencyId);
            if (agency != null && !string.IsNullOrEmpty(data.Id))
            {
                AgencyAchievementRegistry.ClaimIfFirst(agency, data.Id);
            }
        }
    }
}
