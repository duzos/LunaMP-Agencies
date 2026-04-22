using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;

namespace LmpClient.Systems.Chat
{
    public class ChatMessageSender : SubSystem<ChatSystem>, IMessageSender
    {
        public void SendMessage(IMessageData messageData)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<ChatCliMsg>(messageData)));
        }

        public void SendChatMsg(string text, bool relay = true)
        {
            SendChatMsg(text, ChatChannel.Global, relay);
        }

        public void SendChatMsg(string text, ChatChannel channel, bool relay = true)
        {
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<ChatMsgData>();
            msgData.From = SettingsSystem.CurrentSettings.PlayerName;
            msgData.Text = text;
            msgData.Relay = relay;
            msgData.Channel = channel;

            // For Agency channel the server always resolves the agency from the
            // sender, but we pass our cached id so the server-side trace is
            // informative when things go wrong.
            if (channel == ChatChannel.Agency)
            {
                msgData.AgencyId = LmpClient.Systems.Agency.AgencySystem.Singleton.MyAgencyId;
            }

            System.MessageSender.SendMessage(msgData);
        }
    }
}
