using Lidgren.Network;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencyCreateMsgData : AgencyBaseMsgData
    {
        internal AgencyCreateMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliCreate;
        public override string ClassName { get; } = nameof(AgencyCreateMsgData);

        public string Name = string.Empty;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(Name ?? string.Empty);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            Name = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize() => 64;
    }
}
