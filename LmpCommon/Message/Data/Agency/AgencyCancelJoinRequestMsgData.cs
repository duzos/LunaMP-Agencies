using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencyCancelJoinRequestMsgData : AgencyBaseMsgData
    {
        internal AgencyCancelJoinRequestMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliCancelJoinRequest;
        public override string ClassName { get; } = nameof(AgencyCancelJoinRequestMsgData);

        public Guid AgencyId;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(AgencyId.ToByteArray());
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            AgencyId = new Guid(lidgrenMsg.ReadBytes(16));
        }

        internal override int InternalGetMessageSize() => 16;
    }
}
