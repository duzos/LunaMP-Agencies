using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.System;

namespace Server.Agency
{
    public static class AgencyIdentitySystem
    {
        internal static Action<Agency> PersistenceCheckpoint;
        public static bool Supports(ClientStructure client) => client != null && client.Authenticated && client.AgencyIdentityProtocol == AgencyIdentityDefaults.ProtocolVersion;

        public static void SendSnapshot(ClientStructure client)
        {
            if (!Supports(client)) return;
            // Each message is bounded; clients merge by revision, so chunks need no replacement semantics.
            var agencies = AgencyStore.Agencies.Values.ToArray();
            for (int offset = 0; offset < agencies.Length; offset += AgencyIdentityDefaults.MaxIdentities)
            {
                var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyIdentitySnapshotMsgData>();
                data.Identities = agencies.Skip(offset).Take(AgencyIdentityDefaults.MaxIdentities).Select(a => a.ToIdentity()).ToArray();
                MessageQueuer.SendToClient<AgencySrvMsg>(client, data);
            }
        }

        public static void Handle(ClientStructure client, AgencySetIdentityMsgData command)
        {
            var result = Set(client, command);
            AgencyNetwork.SendReply(client, result.Success, result.Message);
        }

        internal static (bool Success, string Message) Set(ClientStructure client, AgencySetIdentityMsgData command)
        {
            if (!Supports(client) || command == null) return (false, "Agency customization is unavailable.");
            lock (AgencyLaunchSiteStore.MutationLock) return SetUnderRemovalGate(client, command);
        }

        private static (bool Success, string Message) SetUnderRemovalGate(ClientStructure client, AgencySetIdentityMsgData command)
        {
            if (!AgencyStore.Agencies.TryGetValue(command.AgencyId, out var agency)) return (false, "Agency not found.");
            lock (agency.Lock)
            {
                if (!client.Authenticated || client.ConnectionStatus != ConnectionStatus.Connected || !ServerContext.Clients.Values.Any(c => ReferenceEquals(c, client)) || client.AgencyId != agency.Id || !agency.HasMember(client.UniqueIdentifier) || agency.OwnerUniqueId != client.UniqueIdentifier)
                    return (false, "Only the current agency owner can customize it.");
                if (command.ExpectedRevision != agency.IdentityRevision || agency.IdentityRevision == long.MaxValue)
                    return (false, "Agency appearance changed; refresh and try again.");
                if (!AgencyIdentityDefaults.IsSafeFlagUrl(command.FlagUrl)) return (false, "Invalid flag path.");
                try { if (!ValidateFlag(command.FlagUrl, command.FlagSha256)) return (false, "Select a stock or already synchronized flag; conflicting flag files cannot be used."); }
                catch (Exception e) { LunaLog.Warning("[Agency] Flag validation failed: " + e.Message); return (false, "Unable to verify flag."); }
                var before = agency.ToIdentity();
                agency.HasColour = command.HasColour; agency.Red = command.Red; agency.Green = command.Green; agency.Blue = command.Blue;
                agency.FlagUrl = command.FlagUrl; agency.IdentityRevision++;
                try { PersistenceCheckpoint?.Invoke(agency); AgencyStore.PersistAgency(agency); }
                catch (Exception e)
                {
                    agency.HasColour = before.HasColour; agency.Red = before.Red; agency.Green = before.Green; agency.Blue = before.Blue;
                    agency.FlagUrl = before.FlagUrl; agency.IdentityRevision = before.Revision;
                    LunaLog.Error("[Agency] Appearance persistence failed: " + e.Message);
                    return (false, "Unable to save agency appearance.");
                }
                Broadcast(agency);
                return (true, "Agency appearance saved.");
            }
        }

        internal static bool ValidateFlag(string url, string expectedHash)
        {
            if (!AgencyIdentityDefaults.IsSafeFlagUrl(url)) return false;
            if (AgencyIdentityDefaults.IsStockFlag(url)) return true;
            if (expectedHash == null || expectedHash.Length != 64 || expectedHash.Any(c => !Uri.IsHexDigit(c)) || !Directory.Exists(FlagSystem.FlagPath)) return false;
            var filename = url.Replace('/', '$') + ".png";
            var found = false;
            foreach (var path in Directory.EnumerateFiles(FlagSystem.FlagPath, "*.png", SearchOption.AllDirectories).Where(p => string.Equals(Path.GetFileName(p), filename, StringComparison.Ordinal)))
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length <= 0 || stream.Length > 1000000) return false;
                    using (var sha = SHA256.Create())
                        if (!string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
                }
                found = true;
            }
            return found;
        }

        private static void Broadcast(Agency agency)
        {
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyIdentityUpsertMsgData>();
            data.Identity = agency.ToIdentity();
            foreach (var client in ServerContext.Clients.Values.Where(Supports)) MessageQueuer.SendToClient<AgencySrvMsg>(client, data);
        }
    }
}
