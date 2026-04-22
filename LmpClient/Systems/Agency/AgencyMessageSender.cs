using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System;

namespace LmpClient.Systems.Agency
{
    public class AgencyMessageSender : SubSystem<AgencySystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<AgencyCliMsg>(msg)));
        }

        public void SendCreate(string name)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyCreateMsgData>();
            d.Name = name ?? string.Empty;
            SendMessage(d);
        }

        public void SendRename(Guid agencyId, string newName)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyRenameMsgData>();
            d.AgencyId = agencyId;
            d.NewName = newName ?? string.Empty;
            SendMessage(d);
        }

        public void SendJoinRequest(Guid agencyId)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyJoinRequestMsgData>();
            d.AgencyId = agencyId;
            SendMessage(d);
        }

        public void SendLeave()
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyLeaveMsgData>();
            SendMessage(d);
        }

        public void SendApproveJoin(Guid agencyId, string playerUniqueId)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyApproveJoinMsgData>();
            d.AgencyId = agencyId;
            d.PlayerUniqueId = playerUniqueId ?? string.Empty;
            SendMessage(d);
        }

        public void SendRejectJoin(Guid agencyId, string playerUniqueId)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyRejectJoinMsgData>();
            d.AgencyId = agencyId;
            d.PlayerUniqueId = playerUniqueId ?? string.Empty;
            SendMessage(d);
        }

        public void SendKick(Guid agencyId, string playerUniqueId)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyKickMemberMsgData>();
            d.AgencyId = agencyId;
            d.PlayerUniqueId = playerUniqueId ?? string.Empty;
            SendMessage(d);
        }

        public void SendTransferOwner(Guid agencyId, string newOwnerUniqueId)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyTransferOwnerMsgData>();
            d.AgencyId = agencyId;
            d.NewOwnerUniqueId = newOwnerUniqueId ?? string.Empty;
            SendMessage(d);
        }

        public void SendCancelJoinRequest(Guid agencyId)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyCancelJoinRequestMsgData>();
            d.AgencyId = agencyId;
            SendMessage(d);
        }

        public void SendTransferResources(Guid fromAgencyId, Guid toAgencyId, ResourceKind kind, double amount)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyTransferResourcesMsgData>();
            d.FromAgencyId = fromAgencyId;
            d.ToAgencyId = toAgencyId;
            d.Kind = kind;
            d.Amount = amount;
            SendMessage(d);
        }

        public void SendAdminOp(string adminPassword, AgencyAdminOp op, Guid targetAgency, string stringArg, double numericArg)
        {
            var d = NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyAdminOpMsgData>();
            d.AdminPassword = adminPassword ?? string.Empty;
            d.Op = op;
            d.TargetAgencyId = targetAgency;
            d.StringArg = stringArg ?? string.Empty;
            d.NumericArg = numericArg;
            SendMessage(d);
        }
    }
}
