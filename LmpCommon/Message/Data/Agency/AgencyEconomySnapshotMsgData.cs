using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
namespace LmpCommon.Message.Data.Agency
{
    public sealed class AgencyEconomySnapshotMsgData : AgencyBaseMsgData
    {
        internal AgencyEconomySnapshotMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvEconomySnapshot;
        public override string ClassName => nameof(AgencyEconomySnapshotMsgData);
        public EconomySnapshot Snapshot = new EconomySnapshot();
        internal override void InternalSerialize(NetOutgoingMessage message) => AgencyEconomyWire.Write(message, Snapshot);
        internal override void InternalDeserialize(NetIncomingMessage message)
        {
            Snapshot = new EconomySnapshot();
            Snapshot = AgencyEconomyWire.Read<EconomySnapshot>(message);
        }
        internal override int InternalGetMessageSize() => AgencyEconomyWire.Size(Snapshot);
    }
}
