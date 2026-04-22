using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Client request to transfer Funds or Science from the player's current
    /// agency to another agency. Server validates membership of the source
    /// agency (or admin) and sufficient resources before applying.
    /// </summary>
    public class AgencyTransferResourcesMsgData : AgencyBaseMsgData
    {
        internal AgencyTransferResourcesMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliTransferResources;
        public override string ClassName { get; } = nameof(AgencyTransferResourcesMsgData);

        public Guid FromAgencyId;
        public Guid ToAgencyId;
        public ResourceKind Kind;
        public double Amount;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(FromAgencyId.ToByteArray());
            lidgrenMsg.Write(ToAgencyId.ToByteArray());
            lidgrenMsg.Write((byte)Kind);
            lidgrenMsg.Write(Amount);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            FromAgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            ToAgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            Kind = (ResourceKind)lidgrenMsg.ReadByte();
            Amount = lidgrenMsg.ReadDouble();
        }

        internal override int InternalGetMessageSize() => 16 + 16 + sizeof(byte) + sizeof(double);
    }
}
