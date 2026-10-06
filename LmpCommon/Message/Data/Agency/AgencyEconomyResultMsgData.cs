using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
namespace LmpCommon.Message.Data.Agency
{
    public sealed class AgencyEconomyResultMsgData : AgencyBaseMsgData
    {
        internal AgencyEconomyResultMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvEconomyResult;
        public override string ClassName => nameof(AgencyEconomyResultMsgData);
        public EconomyResult Result = new EconomyResult();
        internal override void InternalSerialize(NetOutgoingMessage message) => AgencyEconomyWire.Write(message, Result);
        internal override void InternalDeserialize(NetIncomingMessage message)
        {
            Result = new EconomyResult();
            Result = AgencyEconomyWire.Read<EconomyResult>(message);
        }
        internal override int InternalGetMessageSize() => AgencyEconomyWire.Size(Result);
    }
}
