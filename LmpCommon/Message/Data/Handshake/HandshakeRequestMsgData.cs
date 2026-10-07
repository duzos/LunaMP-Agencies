using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Handshake
{
    public class HandshakeRequestMsgData : HandshakeBaseMsgData
    {
        /// <inheritdoc />
        internal HandshakeRequestMsgData() { }
        public override HandshakeMessageType HandshakeMessageType => HandshakeMessageType.Request;

        public string PlayerName;
        public string UniqueIdentifier;
        public string KspVersion;

        /// <summary>
        /// The agencies build of the client (<see cref="LmpCommon.Agency.AgenciesBuild.Number"/>). Packets from clients that predate the field read as 0.
        /// </summary>
        public int AgenciesBuild;

        public override string ClassName { get; } = nameof(HandshakeRequestMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(PlayerName);
            lidgrenMsg.Write(UniqueIdentifier);
            lidgrenMsg.Write(KspVersion);
            lidgrenMsg.Write(AgenciesBuild);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            PlayerName = lidgrenMsg.ReadString();
            UniqueIdentifier = lidgrenMsg.ReadString();

            //  For backwards compatibility with v0.29.0, only continue reading if there are more bytes to read
            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
                KspVersion = lidgrenMsg.ReadString();

            //Message instances are pooled, so the field must be reset before the guarded read or an old-format packet would keep the previous build
            AgenciesBuild = 0;
            if (lidgrenMsg.LengthBits - lidgrenMsg.Position >= 32)
                AgenciesBuild = lidgrenMsg.ReadInt32();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + PlayerName.GetByteCount() + UniqueIdentifier.GetByteCount() + KspVersion.GetByteCount() + sizeof(int);
        }
    }
}
