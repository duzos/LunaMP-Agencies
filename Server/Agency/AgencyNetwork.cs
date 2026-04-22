using LmpCommon.Agency;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Server;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Agency
{
    /// <summary>
    /// Helpers for sending agency-related server messages and for routing
    /// to online members of a specific agency. Wraps <see cref="MessageQueuer"/>
    /// so that agency dispatch has one well-known entrypoint.
    /// </summary>
    public static class AgencyNetwork
    {
        /// <summary>
        /// Sends the same message data to every online client currently a
        /// member of <paramref name="agencyId"/>. The exceptClient parameter
        /// mirrors the MessageQueuer pattern (pass null to include the sender).
        /// </summary>
        public static void SendToAgencyMembers<T>(ClientStructure exceptClient, Guid agencyId, IMessageData data)
            where T : class, IServerMessageBase
        {
            if (data == null || agencyId == Guid.Empty) return;

            foreach (var client in GetOnlineAgencyMembers(agencyId))
            {
                if (exceptClient != null && Equals(client, exceptClient)) continue;
                MessageQueuer.SendToClient<T>(client, data);
            }
        }

        public static IEnumerable<ClientStructure> GetOnlineAgencyMembers(Guid agencyId)
        {
            if (agencyId == Guid.Empty) return Array.Empty<ClientStructure>();
            return ServerContext.Clients.Values.Where(c => c.Authenticated && c.AgencyId == agencyId);
        }

        public static ClientStructure GetOnlinePlayerByUniqueId(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return null;
            return ServerContext.Clients.Values.FirstOrDefault(c =>
                c.Authenticated && string.Equals(c.UniqueIdentifier, uniqueId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Broadcasts an AgencyUpsertMsgData for the given agency to every
        /// authenticated client. The full list of agencies is public metadata
        /// so every client can see the list in the browse UI.
        /// </summary>
        public static void BroadcastUpsert(Agency agency)
        {
            if (agency == null) return;
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyUpsertMsgData>();
            data.Agency = agency.ToInfo();
            MessageQueuer.SendToAllClients<AgencySrvMsg>(data);
        }

        public static void BroadcastDelete(Guid agencyId)
        {
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyDeleteMsgData>();
            data.AgencyId = agencyId;
            MessageQueuer.SendToAllClients<AgencySrvMsg>(data);
        }

        public static void SendSyncAllTo(ClientStructure client)
        {
            if (client == null) return;
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencySyncAllMsgData>();
            data.MyAgencyId = client.AgencyId;
            data.Agencies = AgencyStore.Agencies.Values.Select(a => a.ToInfo()).ToArray();
            MessageQueuer.SendToClient<AgencySrvMsg>(client, data);
        }

        public static void SendReply(ClientStructure client, bool success, string message)
        {
            if (client == null) return;
            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyReplyMsgData>();
            data.Success = success;
            data.Message = message ?? string.Empty;
            MessageQueuer.SendToClient<AgencySrvMsg>(client, data);
        }

        public static void SendJoinRequestPostedToOwner(Agency agency, JoinRequestInfo request)
        {
            if (agency == null || request == null) return;
            var owner = GetOnlinePlayerByUniqueId(agency.OwnerUniqueId);
            if (owner == null) return;

            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyJoinRequestPostedMsgData>();
            data.Request = request;
            MessageQueuer.SendToClient<AgencySrvMsg>(owner, data);
        }

        /// <summary>
        /// Disconnects the player with a human-readable reason so their KSP
        /// auto-reconnects against the new agency state. Needed because the
        /// stock KSP scenario modules cache state aggressively — a clean
        /// reconnect is the only way to pick up a fresh tech tree / contract
        /// list / career headline numbers.
        /// </summary>
        public static void KickForAgencyChange(string playerUniqueId, string reason)
        {
            var client = GetOnlinePlayerByUniqueId(playerUniqueId);
            if (client == null) return;

            global::Server.Log.LunaLog.Info($"[Agency] Force-disconnecting {client.PlayerName} ({playerUniqueId}): {reason}");
            MessageQueuer.SendConnectionEnd(client, reason);
        }

        public static void SendJoinRequestResolvedToPlayer(string playerUniqueId, Guid agencyId, bool approved)
        {
            var player = GetOnlinePlayerByUniqueId(playerUniqueId);
            if (player == null) return;

            var data = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyJoinRequestResolvedMsgData>();
            data.AgencyId = agencyId;
            data.PlayerUniqueId = playerUniqueId;
            data.Approved = approved;
            MessageQueuer.SendToClient<AgencySrvMsg>(player, data);
        }
    }
}
