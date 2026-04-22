using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.Message.Types;
using Server.Agency;
using Server.Client;
using Server.Log;
using Server.Message.Base;
using Server.Server;
using System;

namespace Server.Message
{
    public class ChatMsgReader : ReaderBase
    {
        public override void HandleMessage(ClientStructure client, IClientMessageBase message)
        {
            var messageData = (ChatMsgData)message.Data;
            if (messageData.From != client.PlayerName) return;

            if (!messageData.Relay)
            {
                LunaLog.Warning($"{messageData.From}: {messageData.Text}");
                return;
            }

            switch (messageData.Channel)
            {
                case ChatChannel.Agency:
                    {
                        var agencyId = messageData.AgencyId != Guid.Empty ? messageData.AgencyId : client.AgencyId;
                        // Harden: ignore agency spoofing from the client. A
                        // player can only chat into their own agency.
                        if (agencyId != client.AgencyId)
                        {
                            LunaLog.Warning($"[Agency][Chat] Rejecting agency chat from {client.PlayerName}: targeted agency {agencyId} but belongs to {client.AgencyId}");
                            return;
                        }

                        var agency = AgencySystem.GetAgency(agencyId);
                        var label = agency?.Name ?? agencyId.ToString();
                        messageData.AgencyId = agencyId;

                        // Deliver to all online members, including the sender,
                        // so the client can use the authoritative copy as
                        // visible confirmation rather than echoing locally.
                        MessageQueuer.SendMessageToAgency<ChatSrvMsg>(agencyId, messageData);
                        LunaLog.ChatMessage($"[{label}] {messageData.From}: {messageData.Text}");
                        break;
                    }

                case ChatChannel.Global:
                default:
                    messageData.Channel = ChatChannel.Global;
                    messageData.AgencyId = Guid.Empty;
                    MessageQueuer.SendToAllClients<ChatSrvMsg>(messageData);
                    LunaLog.ChatMessage($"{messageData.From}: {messageData.Text}");
                    break;
            }
        }
    }
}
