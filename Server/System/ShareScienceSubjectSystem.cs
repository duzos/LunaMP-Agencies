using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System.Scenario;

namespace Server.System
{
    public static class ShareScienceSubjectSystem
    {
        public static void ScienceSubjectReceived(ClientStructure client, ShareProgressScienceSubjectMsgData data)
        {
            if (GeneralSettings.SettingsStore.AgencyExperimentsPerAgency)
            {
                var agency = AgencySystem.GetAgency(client.AgencyId);
                if (agency == null)
                {
                    LunaLog.Warning($"[Agency] Dropping science subject from {client.PlayerName}: no agency assigned.");
                    return;
                }

                LunaLog.Info($"[Agency] ScienceSubjectUpdate agency='{agency.Name}' player={client.PlayerName} id={data.ScienceSubject.Id}");

                AgencyScenarioUpdater.WriteScienceSubject(agency.Id, data.ScienceSubject.Data, data.ScienceSubject.NumBytes);

                // Per-agency: only members of the same agency see each other's
                // subject progress — every other agency maintains independent
                // caps, so their players can still grind the same experiments.
                MessageQueuer.RelayMessageToAgency<ShareProgressSrvMsg>(client, agency.Id, data);
                return;
            }

            // Legacy global behaviour (default): all agencies share one set of
            // subject caps. First agency to max a subject spends the cap for
            // everyone else too.
            LunaLog.Debug($"Science experiment received: {data.ScienceSubject.Id}");
            MessageQueuer.RelayMessage<ShareProgressSrvMsg>(client, data);
            ScenarioDataUpdater.WriteScienceSubjectDataToFile(data.ScienceSubject);
        }
    }
}
