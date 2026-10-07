using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpClient.Systems.SettingsSys;
using LmpClient.Utilities;
using LmpCommon;
using LmpCommon.Agency;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Interface;

namespace LmpClient.Systems.Handshake
{
    public class HandshakeMessageSender : SubSystem<HandshakeSystem>, IMessageSender
    {
        public void SendMessage(IMessageData msg)
        {
            TaskFactory.StartNew(() => NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<HandshakeCliMsg>(msg)));
        }

        public void SendHandshakeRequest()
        {
            LmpClient.Systems.Agency.AgencyIdentityClient.BeginSession();
            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<HandshakeRequestMsgData>();
            msgData.PlayerName = SettingsSystem.CurrentSettings.PlayerName;
            msgData.UniqueIdentifier = MainSystem.UniqueIdentifier;
            msgData.KspVersion = $"{CompatibilityChecker.KspVersion}";
            msgData.AgenciesBuild = AgenciesBuild.Number;
            msgData.AgencyIdentityProtocol = 1;

            SendMessage(msgData);
        }
    }
}
