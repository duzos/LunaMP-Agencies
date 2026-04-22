using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Chat
{
    public class ChatMsgData : MessageData
    {
        /// <inheritdoc />
        internal ChatMsgData() { }

        public string From;
        public string Text;
        public bool Relay;

        /// <summary>
        /// Chat scope. Default Global preserves legacy wire behavior.
        /// When set to Agency the server routes only to online members of
        /// <see cref="AgencyId"/> (falls back to the sender's current agency
        /// when the supplied id is empty or not the sender's agency).
        /// </summary>
        public ChatChannel Channel = ChatChannel.Global;

        /// <summary>
        /// Agency id to target for Agency channel. Guid.Empty means
        /// "sender's own agency" (resolved server-side).
        /// </summary>
        public Guid AgencyId = Guid.Empty;

        public override string ClassName { get; } = nameof(ChatMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(From);
            lidgrenMsg.Write(Text);
            lidgrenMsg.Write(Relay);
            lidgrenMsg.Write((byte)Channel);
            lidgrenMsg.Write(AgencyId.ToByteArray());
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            From = lidgrenMsg.ReadString();
            Text = lidgrenMsg.ReadString();
            Relay = lidgrenMsg.ReadBoolean();
            Channel = (ChatChannel)lidgrenMsg.ReadByte();
            AgencyId = new Guid(lidgrenMsg.ReadBytes(16));
        }

        internal override int InternalGetMessageSize()
        {
            return From.GetByteCount() + Text.GetByteCount() + sizeof(bool) + sizeof(byte) + 16;
        }
    }
}
