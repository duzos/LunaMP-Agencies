using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server -> the requesting player and the agency owner.
    /// Indicates an outstanding join request was approved or rejected.
    /// </summary>
    public class AgencyJoinRequestResolvedMsgData : AgencyBaseMsgData
    {
        internal AgencyJoinRequestResolvedMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvJoinRequestResolved;
        public override string ClassName { get; } = nameof(AgencyJoinRequestResolvedMsgData);

        public Guid AgencyId;
        public string PlayerUniqueId = string.Empty;
        public bool Approved;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(AgencyId.ToByteArray());
            lidgrenMsg.Write(PlayerUniqueId ?? string.Empty);
            lidgrenMsg.Write(Approved);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            AgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            PlayerUniqueId = lidgrenMsg.ReadString();
            Approved = lidgrenMsg.ReadBoolean();
        }

        internal override int InternalGetMessageSize() => 16 + sizeof(bool);
    }
}
