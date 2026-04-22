using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System;
using System.Collections.Concurrent;

namespace LmpClient.Systems.Chat
{
    public class ChatMessageHandler : SubSystem<ChatSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg.Data is ChatMsgData msgData)) return;

            // Tag agency messages inline so the operator can tell scope apart
            // even without a separate tab. Global stays identical to the
            // pre-agency behavior for backwards-compatibility.
            var prefix = msgData.Channel == ChatChannel.Agency ? "[Agency] " : string.Empty;
            System.NewChatMessages.Enqueue(new Tuple<string, string, string>(
                msgData.From,
                msgData.Text,
                $"{prefix}{msgData.From}: {msgData.Text}"));
        }
    }
}
