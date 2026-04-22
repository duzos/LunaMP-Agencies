using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server -> agency owner. Delivered when a new join request is posted
    /// for an agency the recipient owns.
    /// </summary>
    public class AgencyJoinRequestPostedMsgData : AgencyBaseMsgData
    {
        internal AgencyJoinRequestPostedMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvJoinRequestPosted;
        public override string ClassName { get; } = nameof(AgencyJoinRequestPostedMsgData);

        public JoinRequestInfo Request = new JoinRequestInfo();

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            AgencyWireHelpers.WriteJoinRequestInfo(lidgrenMsg, Request);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            Request = AgencyWireHelpers.ReadJoinRequestInfo(lidgrenMsg);
        }

        internal override int InternalGetMessageSize() => 64;
    }
}
