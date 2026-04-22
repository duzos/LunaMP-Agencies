using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencyKickMemberMsgData : AgencyBaseMsgData
    {
        internal AgencyKickMemberMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliKickMember;
        public override string ClassName { get; } = nameof(AgencyKickMemberMsgData);

        public Guid AgencyId;
        public string PlayerUniqueId = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(AgencyId.ToByteArray());
            lidgrenMsg.Write(PlayerUniqueId ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            AgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            PlayerUniqueId = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize() => 16 + 64;
    }
}
