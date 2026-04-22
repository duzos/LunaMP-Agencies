using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencyUpsertMsgData : AgencyBaseMsgData
    {
        internal AgencyUpsertMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvUpsert;
        public override string ClassName { get; } = nameof(AgencyUpsertMsgData);

        public AgencyInfo Agency = new AgencyInfo();

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            AgencyWireHelpers.WriteAgencyInfo(lidgrenMsg, Agency);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            Agency = AgencyWireHelpers.ReadAgencyInfo(lidgrenMsg);
        }

        internal override int InternalGetMessageSize() => 128;
    }
}
