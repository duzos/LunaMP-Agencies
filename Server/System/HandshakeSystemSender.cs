using Server.Diagnostics;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Server;
using Server.Settings.Structures;

namespace Server.System
{
    public class HandshakeSystemSender
    {
        public static void SendHandshakeReply(ClientStructure client, HandshakeReply enumResponse, string reason)
        {
            PlaytestDiagnostics.Write("handshake.reply", () => $"{PlaytestDiagnostics.Client(client)} outcome={enumResponse}");
            var msgData = ServerContext.ServerMessageFactory.CreateNewMessageData<HandshakeReplyMsgData>();
            msgData.Response = enumResponse;
            msgData.Reason = reason;
            msgData.ServerAgenciesBuild = AgenciesBuild.Number;

            if (enumResponse == HandshakeReply.HandshookSuccessfully)
            {
                msgData.ModControl = GeneralSettings.SettingsStore.ModControl;
                msgData.ServerStartTime = TimeContext.StartTime.Ticks;

                if (GeneralSettings.SettingsStore.ModControl)
                {
                    msgData.ModFileData = FileHandler.ReadFileText(ServerContext.ModFilePath);
                }
            }

            MessageQueuer.SendToClient<HandshakeSrvMsg>(client, msgData);
        }
    }
}
