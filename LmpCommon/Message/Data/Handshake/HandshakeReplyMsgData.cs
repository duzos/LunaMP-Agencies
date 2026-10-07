using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Handshake
{
    public class HandshakeReplyMsgData : HandshakeBaseMsgData
    {
        /// <inheritdoc />
        internal HandshakeReplyMsgData() { }
        public override HandshakeMessageType HandshakeMessageType => HandshakeMessageType.Reply;

        public HandshakeReply Response;
        public string Reason;
        public bool ModControl;
        public long ServerStartTime;
        public string ModFileData;

        /// <summary>
        /// The agencies build of the server (<see cref="LmpCommon.Agency.AgenciesBuild.Number"/>). Packets from servers that predate the field read as 0.
        /// </summary>
        public int ServerAgenciesBuild;
        public int AgencyIdentityProtocol;

        public override string ClassName { get; } = nameof(HandshakeReplyMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write((int)Response);
            lidgrenMsg.Write(Reason);

            lidgrenMsg.Write(ModControl);
            lidgrenMsg.WritePadBits();

            lidgrenMsg.Write(ServerStartTime);
            lidgrenMsg.Write(ModFileData);
            lidgrenMsg.Write(ServerAgenciesBuild);
            if (AgencyIdentityProtocol != 0) lidgrenMsg.Write(AgencyIdentityProtocol);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            Response = (HandshakeReply)lidgrenMsg.ReadInt32();
            Reason = lidgrenMsg.ReadString();

            ModControl = lidgrenMsg.ReadBoolean();
            lidgrenMsg.SkipPadBits();

            ServerStartTime = lidgrenMsg.ReadInt64();

            ModFileData = lidgrenMsg.ReadString();

            //Message instances are pooled, so the field must be reset before the guarded read or an old-format packet would keep the previous build
            ServerAgenciesBuild = 0;
            if (lidgrenMsg.LengthBits - lidgrenMsg.Position >= 32)
                ServerAgenciesBuild = lidgrenMsg.ReadInt32();
            AgencyIdentityProtocol = 0;
            if (lidgrenMsg.LengthBits - lidgrenMsg.Position >= 32)
                AgencyIdentityProtocol = lidgrenMsg.ReadInt32();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(HandshakeReply) + Reason.GetByteCount() + sizeof(byte) //We write pad bits so it's size of byte
                + sizeof(long) + ModFileData.GetByteCount() + sizeof(int) * 2;
        }
    }
}
