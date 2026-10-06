using HeadlessTest.Infrastructure;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace HeadlessTest;
[TestClass]
[DoNotParallelize]
public class AgencyCommNetTest
{
    public TestContext TestContext { get; set; }
    [TestMethod]
    public async Task AgreementsPersistAndRejectForeignEditsWithoutOwnershipEnforcementAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var bots = new[] { new BotClient("RelayA", "relay-a-" + run), new BotClient("RelayB", "relay-b-" + run) };
        var a = bots[0]; var b = bots[1];
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, s => { s.AgencyCommNetPerAgency = true; s.AgencyCommNetOptIn = true; s.AgencyVesselOwnership = false; });
            var agencyA = (await a.ConnectAsync(server.Port, token)).MyAgencyId;
            var agencyB = (await b.ConnectAsync(server.Port, token)).MyAgencyId;
            var vesselA = Guid.NewGuid(); var vesselB = Guid.NewGuid();
            a.UploadVessel(vesselA, AgencyVesselOwnershipTest.FixtureVessel(vesselA, "Relay A", 1101));
            b.UploadVessel(vesselB, AgencyVesselOwnershipTest.FixtureVessel(vesselB, "Relay B", 2201));
            await a.WaitForAsync<CommNetMapSnapshot>(s => s.Ready && s.Endpoints.Any(e => e.VesselId == vesselA && e.OwnerAgencyId == agencyA) && s.Endpoints.Any(e => e.VesselId == vesselB && e.OwnerAgencyId == agencyB), token);
            var denied = a.ConfigureCommNet(CommNetOperation.SetAcceptAll, vesselB, enabled: true);
            Assert.IsFalse((await a.WaitForAsync<CommNetResultBot>(s => s.RequestId == denied, token)).Success);
            var requestA = a.ConfigureCommNet(CommNetOperation.SetTarget, vesselA, vesselB, true);
            Assert.IsTrue((await a.WaitForAsync<CommNetResultBot>(s => s.RequestId == requestA, token)).Success);
            var oneSided = await b.WaitForAsync<CommNetMapSnapshot>(s => s.Preferences.Any(p => p.Source.VesselId == vesselA && p.Targets.Any(t => t.VesselId == vesselB)), token);
            Assert.IsFalse(oneSided.Preferences.Any(p => p.Source.VesselId == vesselB && (p.AcceptAll || p.Targets.Any(t => t.VesselId == vesselA))));
            var requestB = b.ConfigureCommNet(CommNetOperation.SetAcceptAll, vesselB, enabled: true);
            Assert.IsTrue((await b.WaitForAsync<CommNetResultBot>(s => s.RequestId == requestB, token)).Success);
            await a.WaitForAsync<CommNetMapSnapshot>(s => s.Preferences.Any(p => p.Source.VesselId == vesselB && p.AcceptAll), token);
            await b.DisconnectForReconnectAsync(token);
            await a.WaitForAsync<PlayerLeftSnapshot>(s => s.Player == b.Name, token);
            Assert.AreEqual(agencyB, (await b.ConnectAsync(server.Port, token)).MyAgencyId);
            var restored = await b.WaitForAsync<CommNetMapSnapshot>(s => s.Ready && s.Preferences.Any(p => p.Source.VesselId == vesselB && p.AcceptAll), token);
            Assert.IsTrue(restored.Preferences.Any(p => p.Source.VesselId == vesselA && p.Targets.Any(t => t.VesselId == vesselB)));
            var revoke = a.ConfigureCommNet(CommNetOperation.SetTarget, vesselA, vesselB, false);
            Assert.IsTrue((await a.WaitForAsync<CommNetResultBot>(s => s.RequestId == revoke, token)).Success);
            await b.WaitForAsync<CommNetMapSnapshot>(s => s.Revision > restored.Revision && !s.Preferences.Any(p => p.Source.VesselId == vesselA && p.Targets.Any(t => t.VesselId == vesselB)), token);
            server.ThrowIfExited(); succeeded = true;
        }
        finally
        {
            var errors = new List<Exception>();
            foreach (var bot in bots)
            {
                try { await bot.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); TestContext.WriteLine("Bot cleanup failed: " + error); }
                try
                {
                    if (server != null) server.AttachTranscript(bot.Name, bot.Transcript);
                    else TestContext.WriteLine(bot.Transcript);
                }
                catch (Exception error) { errors.Add(error); TestContext.WriteLine("Transcript attachment failed: " + error); }
            }
            if (server != null)
            {
                try { await server.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); TestContext.WriteLine("Server cleanup failed: " + error); }
            }
            if (succeeded && errors.Count > 0) throw new AggregateException(errors);
        }
    }

}
