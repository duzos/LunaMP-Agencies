using Lidgren.Network;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Generic server -> client feedback for a client-initiated agency or
    /// admin operation. Used to surface validation errors ("name taken",
    /// "not the owner", etc.) in the UI.
    /// </summary>
    public class AgencyReplyMsgData : AgencyBaseMsgData
    {
        internal AgencyReplyMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvReply;
        public override string ClassName { get; } = nameof(AgencyReplyMsgData);

        public bool Success;
        public string Message = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(Success);
            lidgrenMsg.Write(Message ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            Success = lidgrenMsg.ReadBoolean();
            Message = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize() => sizeof(bool) + 64;
    }
}
