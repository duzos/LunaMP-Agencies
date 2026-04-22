using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Client.Base;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Client
{
    public class AgencyCliMsg : CliMsgBase<AgencyBaseMsgData>
    {
        internal AgencyCliMsg() { }

        public override string ClassName { get; } = nameof(AgencyCliMsg);

        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)AgencyMessageType.CliCreate] = typeof(AgencyCreateMsgData),
            [(ushort)AgencyMessageType.CliRename] = typeof(AgencyRenameMsgData),
            [(ushort)AgencyMessageType.CliJoinRequest] = typeof(AgencyJoinRequestMsgData),
            [(ushort)AgencyMessageType.CliLeave] = typeof(AgencyLeaveMsgData),
            [(ushort)AgencyMessageType.CliApproveJoin] = typeof(AgencyApproveJoinMsgData),
            [(ushort)AgencyMessageType.CliRejectJoin] = typeof(AgencyRejectJoinMsgData),
            [(ushort)AgencyMessageType.CliKickMember] = typeof(AgencyKickMemberMsgData),
            [(ushort)AgencyMessageType.CliTransferOwner] = typeof(AgencyTransferOwnerMsgData),
            [(ushort)AgencyMessageType.CliCancelJoinRequest] = typeof(AgencyCancelJoinRequestMsgData),
            [(ushort)AgencyMessageType.CliTransferResources] = typeof(AgencyTransferResourcesMsgData),
            [(ushort)AgencyMessageType.CliAdminOp] = typeof(AgencyAdminOpMsgData),
        };

        public override ClientMessageType MessageType => ClientMessageType.Agency;

        protected override int DefaultChannel => 22;

        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
