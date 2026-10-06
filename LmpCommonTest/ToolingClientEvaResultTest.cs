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
    public class ToolingClientEvaResultTest
    {
        [TestInitialize] public void Setup()
        {
            ToolingClient.Clear(); NetworkSender.Sent.Clear();
            SettingsSystem.ServerSettings = new TestSettings { AgencyTooling = true };
            MainSystem.NetworkState = ClientState.Running;
            MainSystem.Singleton.ForceQuit = false;
            Funding.Instance = null; ResearchAndDevelopment.Instance = null;
            FlightGlobals.ActiveVessel = null;
        }
        [TestCleanup] public void Cleanup()
        {
            ToolingClient.Clear(); NetworkSender.Sent.Clear(); FlightGlobals.ActiveVessel = null;
            SettingsSystem.ServerSettings = new TestSettings(); MainSystem.NetworkState = ClientState.Disconnected;
        }

        private static EconomyResult EvaRegistered(Guid requestId) => new EconomyResult { Operation = EconomyOperation.RegisterLaunch, Success = true, RequestId = requestId, VesselId = Guid.NewGuid() };

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void EvaRegistrationSuccessWhileIdleDoesNotDisconnect(bool withRequestId)
        {
            // An older server sends an empty request id; the current one sends the EVA vessel id. Neither is a launch or a revert.
            ToolingClient.Receive(EvaRegistered(withRequestId ? Guid.NewGuid() : Guid.Empty));
            ToolingClient.Tick();
            Assert.IsFalse(MainSystem.Singleton.ForceQuit);
        }

        [TestMethod]
        public void EvaRegistrationDoesNotCompleteAnInFlightRevert()
        {
            var agency = Guid.NewGuid(); var vessel = Guid.NewGuid(); var launch = Guid.NewGuid();
            AgencySystem.Singleton.MyAgencyId = agency;
            ToolingClient.Receive(new EconomySnapshot { AgencyId = agency, Ready = true, SessionId = Guid.NewGuid(),
                Vessels = new[] { new PaidVesselRecord { VesselId = vessel, Parts = new[] { new PaidPart { LaunchId = launch } } } } });
            ToolingClient.Tick(); FlightGlobals.ActiveVessel = new Vessel { id = vessel };
            Assert.IsFalse(ToolingClient.BeginRevert(EditorFacility.VAB, false));
            var command = NetworkSender.Sent.Select(m => m.Data).OfType<AgencyEconomyCommandMsgData>().Single().Command;
            ToolingClient.Receive(EvaRegistered(Guid.Empty));
            ToolingClient.Receive(EvaRegistered(Guid.NewGuid()));
            ToolingClient.Tick();
            // The revert is still pending: its own result is what completes it.
            ToolingClient.Receive(new EconomyResult { Operation = EconomyOperation.Revert, Success = true, RequestId = command.RequestId });
            var disconnect = Assert.ThrowsException<InvalidOperationException>(() => ToolingClient.Tick());
            StringAssert.Contains(disconnect.Message, "Revert completed");
        }
    }
}
