using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.PlayerConnection;
using LmpCommon.Message.Server;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Plugin;
using Server.Server;
using System;
using System.Threading.Tasks;

namespace Server.System
{
    public partial class HandshakeSystem
    {
        /// <summary>
        /// How long a client refused for an agencies build mismatch stays connected, so that the reply reaches it before the connection closes.
        /// A test seam; production always uses 2 seconds.
        /// </summary>
        internal static Func<Task> MismatchDisconnectDelay = () => Task.Delay(2000);

        public void HandleHandshakeRequest(ClientStructure client, HandshakeRequestMsgData data)
        {
            //Defence in depth: the receive path already drops everything from a rejected client
            if (client.HandshakeRejected) return;

            var valid = CheckServerFull(client, out var reason);

            if (valid && !CheckAgenciesBuild(data.AgenciesBuild, out reason))
            {
                RejectAgenciesBuild(client, data, reason);
                return;
            }

            valid &= valid && CheckUsernameLength(client, data.PlayerName, out reason);
            valid &= valid && CheckUsernameCharacters(client, data.PlayerName, out reason);
            valid &= valid && CheckPlayerIsAlreadyConnected(client, data.PlayerName, out reason);
            valid &= valid && CheckUsernameIsReserved(client, data.PlayerName, out reason);
            valid &= valid && CheckPlayerIsBanned(client, data.UniqueIdentifier, out reason);

            if (!valid)
            {
                LunaLog.Normal($"Client {data.PlayerName} ({data.UniqueIdentifier}) failed to handshake: {reason}. Disconnecting");
                client.DisconnectClient = true;
                ClientConnectionHandler.DisconnectClient(client, reason);
            }
            else
            {
                client.PlayerName = data.PlayerName;
                client.UniqueIdentifier = data.UniqueIdentifier;
                client.KspVersion = string.IsNullOrWhiteSpace(data.KspVersion) ? "Unknown" : data.KspVersion;
                client.LmpVersion = $"{data.MajorVersion}.{data.MinorVersion}.{data.BuildVersion}";
                client.Authenticated = true;

                LmpPluginHandler.FireOnClientAuthenticated(client);

                // Resolve or create this player's agency so career state is
                // always keyed to exactly one agency. See AgencySystem for the
                // no-agency fallback policy (solo implicit agency).
                AgencySystem.AssignAgencyOnConnect(client);
                LunaLog.Info($"[Agency] Connect player={client.PlayerName}({client.UniqueIdentifier}) agency={client.AgencyId}");

                LunaLog.Normal($"Client {data.PlayerName} ({data.UniqueIdentifier}) handshake successful, LMP Version: {client.LmpVersion}, KSP Version: {client.KspVersion}");

                HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.HandshookSuccessfully, "success");

                // Push agency state before scenarios so the client knows its
                // identity before career data arrives.
                AgencyNetwork.SendSyncAllTo(client);

                // Push the vessel→agency map so the per-agency CommNet
                // filter has every existing vessel tagged before any
                // VesselProto messages start arriving.
                AgencyNetwork.SendVesselMapSyncTo(client);

                var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<PlayerConnectionJoinMsgData>();
                msgData.PlayerName = client.PlayerName;
                MessageQueuer.RelayMessage<PlayerConnectionSrvMsg>(client, msgData);

                LunaLog.Debug($"Online Players: {ServerContext.PlayerCount}, connected: {ClientRetriever.GetClients().Length}");
            }
        }

        /// <summary>
        /// Client and server must run exactly the same agencies build. Clients that predate the field send no build and read as 0.
        /// </summary>
        internal static bool CheckAgenciesBuild(int clientBuild, out string reason)
        {
            if (clientBuild == AgenciesBuild.Number)
            {
                reason = string.Empty;
                return true;
            }

            if (clientBuild > AgenciesBuild.Number)
            {
                reason = $"Agencies build mismatch: you have agencies.{clientBuild} but the server is on agencies.{AgenciesBuild.Number}; the server needs updating.";
            }
            else
            {
                var have = clientBuild == 0 ? "agencies.1 or older" : "agencies." + clientBuild;
                reason = $"Agencies build mismatch: server is on agencies.{AgenciesBuild.Number}, you have {have}. Update from {AgenciesBuild.ReleasesPage}.";
            }

            return false;
        }

        /// <summary>
        /// Refuses the handshake without disconnecting right away: an immediate disconnect would usually beat the reply to the client.
        /// The client is not authenticated, and <see cref="ClientStructure.HandshakeRejected"/> makes the receive path drop anything else it sends.
        /// <see cref="ClientStructure.DisconnectClient"/> is deliberately left alone, other systems set it on live clients.
        /// </summary>
        private static void RejectAgenciesBuild(ClientStructure client, HandshakeRequestMsgData data, string reason)
        {
            LunaLog.Normal($"Client {data.PlayerName} ({data.UniqueIdentifier}) failed to handshake: {reason} Disconnecting shortly");
            client.HandshakeRejected = true;
            HandshakeSystemSender.SendHandshakeReply(client, HandshakeReply.AgenciesBuildMismatch, reason);
            ScheduleMismatchDisconnect(client, reason);
        }

        /// <summary>
        /// Disconnects the client after <see cref="MismatchDisconnectDelay"/>, but only if it is still the live connection for its endpoint.
        /// </summary>
        internal static Task ScheduleMismatchDisconnect(ClientStructure client, string reason)
        {
            return MismatchDisconnectDelay().ContinueWith(_ =>
            {
                try
                {
                    if (client.ConnectionStatus == ConnectionStatus.Connected &&
                        ServerContext.Clients.TryGetValue(client.Endpoint, out var current) && ReferenceEquals(current, client))
                    {
                        ClientConnectionHandler.DisconnectClient(client, reason);
                    }
                }
                catch (Exception e)
                {
                    LunaLog.Error($"Error disconnecting a client with an agencies build mismatch: {e}");
                }
            });
        }
    }
}
