using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    public abstract class AgencyBaseMsgData : MessageData
    {
        internal AgencyBaseMsgData() { }

        public override ushort SubType => (ushort)(int)AgencyMessageType;

        public virtual AgencyMessageType AgencyMessageType => throw new NotImplementedException();

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
        }

        internal override int InternalGetMessageSize()
        {
            return 0;
        }
    }
}
