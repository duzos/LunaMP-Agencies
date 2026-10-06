using System;
using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
namespace LmpCommon.Message.Data.Agency
{
    public sealed class AgencyVisibilityResultMsgData : AgencyBaseMsgData
    {
        internal AgencyVisibilityResultMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvVisibilityResult;
        public override string ClassName => nameof(AgencyVisibilityResultMsgData);
        public Guid RequestId;
        public bool Success;
        public string Reason;
        internal override void InternalSerialize(NetOutgoingMessage message) { GuidUtil.Serialize(RequestId, message); message.Write(Success); message.Write(Reason ?? string.Empty); }
        internal override void InternalDeserialize(NetIncomingMessage message) { RequestId = GuidUtil.Deserialize(message); Success = message.ReadBoolean(); Reason = message.ReadString(); }
        internal override int InternalGetMessageSize() => 17 + Reason.GetByteCount();
    }
}
