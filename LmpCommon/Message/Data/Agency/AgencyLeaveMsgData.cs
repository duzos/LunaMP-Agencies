using Lidgren.Network;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Request to leave the player's current agency. Body is empty —
    /// the server always uses the caller's own agency membership.
    /// </summary>
    public class AgencyLeaveMsgData : AgencyBaseMsgData
    {
        internal AgencyLeaveMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliLeave;
        public override string ClassName { get; } = nameof(AgencyLeaveMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg) { }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg) { }

        internal override int InternalGetMessageSize() => 0;
    }
}
