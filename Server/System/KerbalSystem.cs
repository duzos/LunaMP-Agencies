using LmpCommon.Message.Data.Kerbal;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Properties;
using Server.Server;
using Server.Settings.Structures;
using System.IO;
using System.Linq;

namespace Server.System
{
    public class KerbalSystem
    {
        public static readonly string KerbalsPath = Path.Combine(ServerContext.UniverseDirectory, "Kerbals");

        public static void GenerateDefaultKerbals()
        {
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Jebediah Kerman.txt"), Resources.Jebediah_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Bill Kerman.txt"), Resources.Bill_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Bob Kerman.txt"), Resources.Bob_Kerman);
            FileHandler.CreateFile(Path.Combine(KerbalsPath, "Valentina Kerman.txt"), Resources.Valentina_Kerman);
        }

        public static void HandleKerbalProto(ClientStructure client, KerbalProtoMsgData data)
        {
            if (GeneralSettings.SettingsStore.AgencyKerbalsPerAgency && client.AgencyId != global::System.Guid.Empty)
            {
                // Write into the agency's private kerbal folder and only relay
                // to members of that agency. Ensures agency rosters stay
                // isolated — recruits, promotions, and deaths don't cross over.
                var dir = AgencyKerbalStore.KerbalsPath(client.AgencyId);
                if (!FileHandler.FolderExists(dir))
                    FileHandler.FolderCreate(dir);

                var path = AgencyKerbalStore.KerbalPath(client.AgencyId, data.Kerbal.KerbalName);
                FileHandler.WriteToFile(path, data.Kerbal.KerbalData, data.Kerbal.NumBytes);
                LunaLog.Debug($"[Agency] Saved kerbal '{data.Kerbal.KerbalName}' for agency {client.AgencyId} (from {client.PlayerName})");

                MessageQueuer.RelayMessageToAgency<KerbalSrvMsg>(client, client.AgencyId, data);
                return;
            }

            LunaLog.Debug($"Saving kerbal {data.Kerbal.KerbalName} from {client.PlayerName}");
            var globalPath = Path.Combine(KerbalsPath, $"{data.Kerbal.KerbalName}.txt");
            FileHandler.WriteToFile(globalPath, data.Kerbal.KerbalData, data.Kerbal.NumBytes);

            MessageQueuer.RelayMessage<KerbalSrvMsg>(client, data);
        }

        public static void HandleKerbalsRequest(ClientStructure client)
        {
            string[] kerbalFiles;

            if (GeneralSettings.SettingsStore.AgencyKerbalsPerAgency && client.AgencyId != global::System.Guid.Empty)
            {
                // Seed default roster on first access so new agencies (or
                // agencies from before the flag flipped) aren't empty.
                AgencyKerbalStore.EnsureDefaultRoster(client.AgencyId);
                kerbalFiles = FileHandler.GetFilesInPath(AgencyKerbalStore.KerbalsPath(client.AgencyId));
                LunaLog.Debug($"[Agency] Sending {client.PlayerName} {kerbalFiles.Length} kerbals from agency {client.AgencyId}");
            }
            else
            {
                kerbalFiles = FileHandler.GetFilesInPath(KerbalsPath);
                LunaLog.Debug($"Sending {client.PlayerName} {kerbalFiles.Length} kerbals...");
            }

            var kerbalsData = kerbalFiles.Select(k =>
            {
                var kerbalData = FileHandler.ReadFile(k);
                return new KerbalInfo
                {
                    KerbalData = kerbalData,
                    NumBytes = kerbalData.Length,
                    KerbalName = Path.GetFileNameWithoutExtension(k)
                };
            });

            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<KerbalReplyMsgData>();
            msgData.Kerbals = kerbalsData.ToArray();
            msgData.KerbalsCount = msgData.Kerbals.Length;

            MessageQueuer.SendToClient<KerbalSrvMsg>(client, msgData);
        }

        public static void HandleKerbalRemove(ClientStructure client, KerbalRemoveMsgData message)
        {
            var kerbalToRemove = message.KerbalName;

            if (GeneralSettings.SettingsStore.AgencyKerbalsPerAgency && client.AgencyId != global::System.Guid.Empty)
            {
                LunaLog.Debug($"[Agency] Removing kerbal {kerbalToRemove} from agency {client.AgencyId} (by {client.PlayerName})");
                FileHandler.FileDelete(AgencyKerbalStore.KerbalPath(client.AgencyId, kerbalToRemove));
                MessageQueuer.RelayMessageToAgency<KerbalSrvMsg>(client, client.AgencyId, message);
                return;
            }

            LunaLog.Debug($"Removing kerbal {kerbalToRemove} from {client.PlayerName}");
            FileHandler.FileDelete(Path.Combine(KerbalsPath, $"{kerbalToRemove}.txt"));
            MessageQueuer.RelayMessage<KerbalSrvMsg>(client, message);
        }
    }
}
