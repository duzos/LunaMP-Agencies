using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencyDeleteMsgData : AgencyBaseMsgData
    {
        internal AgencyDeleteMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvDelete;
        public override string ClassName { get; } = nameof(AgencyDeleteMsgData);

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
