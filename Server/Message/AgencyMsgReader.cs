using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Message.Base;
using Server.Settings.Structures;

namespace Server.Message
{
    public class AgencyMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var data = (AgencyBaseMsgData)message.Data;
            switch (data.AgencyMessageType)
            {
                case AgencyMessageType.CliCreate:
                    HandleCreate(client, (AgencyCreateMsgData)data);
                    break;
                case AgencyMessageType.CliRename:
                    HandleRename(client, (AgencyRenameMsgData)data);
                    break;
                case AgencyMessageType.CliJoinRequest:
                    HandleJoinRequest(client, (AgencyJoinRequestMsgData)data);
                    break;
                case AgencyMessageType.CliLeave:
                    HandleLeave(client, (AgencyLeaveMsgData)data);
                    break;
                case AgencyMessageType.CliApproveJoin:
                    HandleApprove(client, (AgencyApproveJoinMsgData)data);
                    break;
                case AgencyMessageType.CliRejectJoin:
                    HandleReject(client, (AgencyRejectJoinMsgData)data);
                    break;
                case AgencyMessageType.CliKickMember:
                    HandleKick(client, (AgencyKickMemberMsgData)data);
                    break;
                case AgencyMessageType.CliTransferOwner:
                    HandleTransfer(client, (AgencyTransferOwnerMsgData)data);
                    break;
                case AgencyMessageType.CliCancelJoinRequest:
                    HandleCancelJoinRequest(client, (AgencyCancelJoinRequestMsgData)data);
                    break;
                case AgencyMessageType.CliTransferResources:
                    HandleTransfer(client, (AgencyTransferResourcesMsgData)data);
                    break;
                case AgencyMessageType.CliAdminOp:
                    HandleAdminOp(client, (AgencyAdminOpMsgData)data);
                    break;
                default:
                    LunaLog.Warning($"[Agency] Unhandled Cli subtype {data.AgencyMessageType} from {client.PlayerName}");
                    break;
            }
        }

        private static void HandleCreate(ClientStructure client, AgencyCreateMsgData data)
        {
            var (ok, msg, _) = AgencySystem.CreateAgency(data.Name, client.UniqueIdentifier, client.PlayerName);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleRename(ClientStructure client, AgencyRenameMsgData data)
        {
            var (ok, msg) = AgencySystem.RenameAgency(data.AgencyId, client.UniqueIdentifier, data.NewName, isAdmin: false);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleJoinRequest(ClientStructure client, AgencyJoinRequestMsgData data)
        {
            var (ok, msg) = AgencySystem.PostJoinRequest(data.AgencyId, client.UniqueIdentifier, client.PlayerName);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleLeave(ClientStructure client, AgencyLeaveMsgData data)
        {
            var (ok, msg) = AgencySystem.LeaveAgency(client.UniqueIdentifier, client.PlayerName);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleApprove(ClientStructure client, AgencyApproveJoinMsgData data)
        {
            var (ok, msg) = AgencySystem.ResolveJoinRequest(data.AgencyId, client.UniqueIdentifier, data.PlayerUniqueId, approve: true, isAdmin: false);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleReject(ClientStructure client, AgencyRejectJoinMsgData data)
        {
            var (ok, msg) = AgencySystem.ResolveJoinRequest(data.AgencyId, client.UniqueIdentifier, data.PlayerUniqueId, approve: false, isAdmin: false);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleKick(ClientStructure client, AgencyKickMemberMsgData data)
        {
            var (ok, msg) = AgencySystem.KickMember(data.AgencyId, client.UniqueIdentifier, data.PlayerUniqueId, isAdmin: false);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleTransfer(ClientStructure client, AgencyTransferOwnerMsgData data)
        {
            var (ok, msg) = AgencySystem.TransferOwner(data.AgencyId, client.UniqueIdentifier, data.NewOwnerUniqueId, isAdmin: false);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleCancelJoinRequest(ClientStructure client, AgencyCancelJoinRequestMsgData data)
        {
            var (ok, msg) = AgencySystem.CancelJoinRequest(data.AgencyId, client.UniqueIdentifier);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleTransfer(ClientStructure client, AgencyTransferResourcesMsgData data)
        {
            // Resolve FromAgencyId defensively — if the client supplied Empty
            // or spoofed someone else's agency, fall back to the caller's real
            // membership so they can never pull from an agency they're not in.
            var source = data.FromAgencyId != global::System.Guid.Empty ? data.FromAgencyId : client.AgencyId;
            if (source != client.AgencyId)
            {
                AgencyNetwork.SendReply(client, false, "You can only send resources from your own agency.");
                return;
            }

            var (ok, msg) = AgencySystem.TransferResources(source, data.ToAgencyId, data.Kind, data.Amount, client.UniqueIdentifier, isAdmin: false);
            AgencyNetwork.SendReply(client, ok, msg);
        }

        private static void HandleAdminOp(ClientStructure client, AgencyAdminOpMsgData data)
        {
            if (!IsAuthenticatedAdmin(client, data.AdminPassword))
            {
                LunaLog.Warning($"[Agency] Rejected admin op {data.Op} from {client.PlayerName}: bad password");
                AgencyNetwork.SendReply(client, false, "Admin password rejected.");
                return;
            }

            var result = AdminOps.Dispatch(client, data);
            AgencyNetwork.SendReply(client, result.Success, result.Message);
        }

        private static bool IsAuthenticatedAdmin(ClientStructure client, string password)
        {
            var expected = GeneralSettings.SettingsStore.AdminPassword ?? string.Empty;
            if (string.IsNullOrEmpty(expected)) return false;
            return string.Equals(expected, password ?? string.Empty);
        }
    }
}
