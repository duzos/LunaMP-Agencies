using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencyRenameMsgData : AgencyBaseMsgData
    {
        internal AgencyRenameMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliRename;
        public override string ClassName { get; } = nameof(AgencyRenameMsgData);

        public Guid AgencyId;
        public string NewName = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(AgencyId.ToByteArray());
            lidgrenMsg.Write(NewName ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            AgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            NewName = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize() => 16 + 64;
    }
}
