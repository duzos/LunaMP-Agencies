using System;
using System.Linq;
using LmpClient;
using LmpClient.Network;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.ShareFunds;
using LmpClient.Systems.ShareScience;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass, DoNotParallelize]
    public class ToolingClientHandshakeTest
    {
        [TestInitialize]
        public void Initialize()
        {
            ToolingClient.Clear();
            ToolingClient.ApplyingBalance = false;
            NetworkSender.Sent.Clear();
            MainSystem.NetworkState = ClientState.Handshaking;
            SettingsSystem.ServerSettings = new TestSettings();
            AgencySystem.Singleton.MyAgencyId = Guid.NewGuid();
            Funding.Instance = new Funding();
            ResearchAndDevelopment.Instance = new ResearchAndDevelopment();
            ShareFundsSystem.Singleton.Applied = null;
            ShareFundsSystem.Singleton.OnApply = null;
            ShareScienceSystem.Singleton.Applied = null;
            ShareScienceSystem.Singleton.OnApply = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            ToolingClient.Clear();
            ToolingClient.ApplyingBalance = false;
            ShareFundsSystem.Singleton.OnApply = null;
            ShareScienceSystem.Singleton.OnApply = null;
            NetworkSender.Sent.Clear();
            Funding.Instance = null;
            ResearchAndDevelopment.Instance = null;
        }

        private static EconomySnapshot Snapshot(long revision = 8) => new EconomySnapshot
        {
            AgencyId = AgencySystem.Singleton.MyAgencyId, Ready = true,
            SessionId = Guid.NewGuid(), LastSequence = 41, Revision = revision,
            Funds = 12000, Science = 23
        };

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public void EarlySnapshot_SurvivesHandshake_AndHireSendsOneSequencedDelta(bool tooling, bool trade)
        {
            var initial = Snapshot();
            ToolingClient.Receive(initial);
            ToolingClient.Tick();
            Assert.IsFalse(ToolingClient.BalanceReady);
            Assert.IsNull(ShareFundsSystem.Singleton.Applied);
            Assert.AreEqual(0, NetworkSender.Sent.Count);

            SettingsSystem.ServerSettings.AgencyTooling = tooling;
            SettingsSystem.ServerSettings.AgencyTrade = trade;
            MainSystem.NetworkState = ClientState.SettingsSynced;
            ToolingClient.Tick();
            Assert.IsTrue(ToolingClient.BalanceReady);
            Assert.AreEqual(tooling, ToolingClient.Ready);
            Assert.AreEqual(initial.Funds, ShareFundsSystem.Singleton.Applied.Value);
            Assert.AreEqual((float)initial.Science, ShareScienceSystem.Singleton.Applied.Value);

            ToolingClient.SendDelta(-1500, 0);
            Assert.AreEqual(1, NetworkSender.Sent.Count);
            var command = ((AgencyEconomyCommandMsgData)NetworkSender.Sent.Single().Data).Command;
            Assert.AreEqual(EconomyOperation.Delta, command.Operation);
            Assert.AreEqual(initial.SessionId, command.SessionId);
            Assert.AreEqual(42L, command.Sequence);
            Assert.AreEqual(-1500d, command.FundsDelta);
            Assert.AreEqual(0d, command.ScienceDelta);
            Assert.AreNotEqual(Guid.Empty, command.RequestId);
        }

        [TestMethod]
        public void Clear_DiscardsPendingSnapshot_AndNextConnectionAcceptsFreshSession()
        {
            var old = Snapshot(100);
            ToolingClient.Receive(old);
            ToolingClient.Tick();
            ToolingClient.Clear();
            SettingsSystem.ServerSettings.AgencyTooling = true;
            MainSystem.NetworkState = ClientState.SettingsSynced;
            ToolingClient.Tick();
            ToolingClient.SendDelta(-1500, 0);
            Assert.IsFalse(ToolingClient.BalanceReady);
            Assert.AreEqual(0, NetworkSender.Sent.Count);
            var fresh = Snapshot(1);
            fresh.LastSequence = 0;
            ToolingClient.Receive(fresh);
            ToolingClient.Tick();
            ToolingClient.SendDelta(-1500, 0);
            var command = ((AgencyEconomyCommandMsgData)NetworkSender.Sent.Single().Data).Command;
            Assert.AreEqual(fresh.SessionId, command.SessionId);
            Assert.AreEqual(1L, command.Sequence);
        }

        [TestMethod]
        public void DisabledFeatures_DoNotApplyOrSendEconomyCommands()
        {
            ToolingClient.Receive(Snapshot());
            MainSystem.NetworkState = ClientState.SettingsSynced;
            ToolingClient.Tick();
            ToolingClient.SendDelta(-1500, 0);
            Assert.IsFalse(ToolingClient.BalanceReady);
            Assert.IsNull(ShareFundsSystem.Singleton.Applied);
            Assert.AreEqual(0, NetworkSender.Sent.Count);
        }

        [TestMethod]
        public void AuthoritativeBalanceApplication_SuppressesFundingAndScienceEchoes()
        {
            SettingsSystem.ServerSettings.AgencyTooling = true;
            MainSystem.NetworkState = ClientState.SettingsSynced;
            ShareFundsSystem.Singleton.OnApply = () => ToolingClient.SendDelta(12000, 0);
            ShareScienceSystem.Singleton.OnApply = () => ToolingClient.SendDelta(0, 23);
            ToolingClient.Receive(Snapshot());
            ToolingClient.Tick();
            ToolingClient.ApplyCachedBalance();
            Assert.AreEqual(0, NetworkSender.Sent.Count);
            Assert.IsFalse(ToolingClient.ApplyingBalance);
            ToolingClient.SendDelta(-1500, 0);
            Assert.AreEqual(1, NetworkSender.Sent.Count);
        }
    }
}
