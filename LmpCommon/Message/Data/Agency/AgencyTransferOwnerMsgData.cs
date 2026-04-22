using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencyTransferOwnerMsgData : AgencyBaseMsgData
    {
        internal AgencyTransferOwnerMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliTransferOwner;
        public override string ClassName { get; } = nameof(AgencyTransferOwnerMsgData);

        public Guid AgencyId;
        public string NewOwnerUniqueId = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(AgencyId.ToByteArray());
            lidgrenMsg.Write(NewOwnerUniqueId ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            AgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            NewOwnerUniqueId = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize() => 16 + 64;
    }
}
