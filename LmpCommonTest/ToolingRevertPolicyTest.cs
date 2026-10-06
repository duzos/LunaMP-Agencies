using System;
using System.Linq;
using LmpClient;
using LmpClient.Network;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass, DoNotParallelize]
    public class ToolingRevertPolicyTest
    {
        [TestInitialize] public void Setup()
        {
            ToolingClient.Clear(); NetworkSender.Sent.Clear();
            SettingsSystem.ServerSettings = new TestSettings();
            MainSystem.NetworkState = ClientState.Running;
            Funding.Instance = null; ResearchAndDevelopment.Instance = null;
            FlightGlobals.ActiveVessel = null;
        }
        [TestCleanup] public void Cleanup()
        {
            ToolingClient.Clear(); NetworkSender.Sent.Clear(); FlightGlobals.ActiveVessel = null;
            SettingsSystem.ServerSettings = new TestSettings(); MainSystem.NetworkState = ClientState.Disconnected;
        }
        [DataTestMethod]
        [DataRow(false, false)] [DataRow(false, true)] [DataRow(true, false)] [DataRow(true, true)]
        public void DisabledRevertStopsBothTargetsWithOrWithoutTooling(bool tooling, bool toLaunch)
        {
            SettingsSystem.ServerSettings.CanRevert = false;
            SettingsSystem.ServerSettings.AgencyTooling = tooling;
            Assert.IsFalse(ToolingClient.BeginRevert(EditorFacility.VAB, toLaunch));
            Assert.AreEqual(0, NetworkSender.Sent.Count);
            StringAssert.Contains(ToolingClient.LatestStatus, "disabled by the server");
        }
        [TestMethod] public void OfflineRevertIsUnchanged()
        {
            SettingsSystem.ServerSettings.CanRevert = false; MainSystem.NetworkState = ClientState.Disconnected;
            Assert.IsTrue(ToolingClient.BeginRevert(EditorFacility.VAB, false));
            Assert.IsTrue(ToolingClient.BeginRevert(EditorFacility.VAB, true));
        }
        [DataTestMethod] [DataRow(false)] [DataRow(true)]
        public void AllowedRevertQueuesTheCorrectAuthoritativeSettlement(bool toLaunch)
        {
            SettingsSystem.ServerSettings.AgencyTooling = true;
            var agency = Guid.NewGuid(); var vessel = Guid.NewGuid(); var launch = Guid.NewGuid();
            AgencySystem.Singleton.MyAgencyId = agency;
            ToolingClient.Receive(new EconomySnapshot { AgencyId = agency, Ready = true, SessionId = Guid.NewGuid(),
                Vessels = new[] { new PaidVesselRecord { VesselId = vessel, Parts = new[] { new PaidPart { LaunchId = launch } } } } });
            ToolingClient.Tick(); FlightGlobals.ActiveVessel = new Vessel { id = vessel };
            Assert.IsFalse(ToolingClient.BeginRevert(EditorFacility.VAB, toLaunch));
            var command = NetworkSender.Sent.Select(m => m.Data).OfType<AgencyEconomyCommandMsgData>().Single().Command;
            Assert.AreEqual(toLaunch ? EconomyOperation.RevertLaunch : EconomyOperation.Revert, command.Operation);
            Assert.AreEqual(launch, command.LaunchId);
        }
    }
}
