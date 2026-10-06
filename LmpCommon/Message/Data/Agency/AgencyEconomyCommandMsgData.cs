using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
namespace LmpCommon.Message.Data.Agency
{
    public sealed class AgencyEconomyCommandMsgData : AgencyBaseMsgData
    {
        internal AgencyEconomyCommandMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliEconomyCommand;
        public override string ClassName => nameof(AgencyEconomyCommandMsgData);
        public EconomyCommand Command = new EconomyCommand();
        internal override void InternalSerialize(NetOutgoingMessage message) => AgencyEconomyWire.Write(message, Command);
        internal override void InternalDeserialize(NetIncomingMessage message)
        {
            Command = new EconomyCommand();
            Command = AgencyEconomyWire.Read<EconomyCommand>(message);
        }
        internal override int InternalGetMessageSize() => AgencyEconomyWire.Size(Command);
    }
}
