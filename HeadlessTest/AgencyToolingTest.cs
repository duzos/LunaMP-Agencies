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
public class AgencyToolingTest
{
    public TestContext TestContext { get; set; }
    [TestMethod]
    public async Task ToolingChargesAreIsolatedIdempotentAndLaunchReservationsRefundAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var bots = new[] { new BotClient("ToolsA", "tools-a-" + run), new BotClient("ToolsB", "tools-b-" + run) };
        var a = bots[0]; var b = bots[1];
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, s => { s.AgencyTooling = true; s.AgencyVesselOwnership = false; });
            var agencyA = (await a.ConnectAsync(server.Port, token)).MyAgencyId;
            var agencyB = (await b.ConnectAsync(server.Port, token)).MyAgencyId;
            var initialA = await a.WaitForAsync<EconomyStateBot>(s => s.State.Ready && s.State.AgencyId == agencyA, token);
            var initialB = await b.WaitForAsync<EconomyStateBot>(s => s.State.Ready && s.State.AgencyId == agencyB, token);
            var manifest = new ToolingManifest { Parts = new[] {
                new ToolingPart { Name = "probeCoreSphere", UnitCost = 100 },
                new ToolingPart { Name = "sensorThermometer", UnitCost = 300, IsScience = true } } };
            var tool = new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) };
            var toolReply = await CommandAsync(a, tool, token);
            Assert.IsTrue(toolReply.Success, toolReply.Reason);
            var tooled = await a.WaitForAsync<EconomyStateBot>(s => s.State.Revision > initialA.State.Revision && s.State.Designs.Any(d => d.Fingerprint == ToolingPolicy.Fingerprint(manifest)), token);
            Assert.AreEqual(initialA.State.Funds - 100 * ToolingDefaults.ToolingCost, tooled.State.Funds, .001);
            Assert.IsTrue((await CommandAsync(a, tool, token)).Success, "Retry must return the original receipt.");
            var quoteB = await CommandAsync(b, new EconomyCommand { Operation = EconomyOperation.Quote, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }, token);
            Assert.IsTrue(quoteB.Success, quoteB.Reason); Assert.IsFalse(quoteB.Quote.AlreadyTooled);
            Assert.AreEqual(100 * ToolingDefaults.UntooledLaunch + 300, quoteB.Quote.LaunchCost);
            var prepare = await CommandAsync(a, new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }, token);
            Assert.IsTrue(prepare.Success, prepare.Reason);
            Assert.AreEqual(310d, prepare.Quote.LaunchCost);
            var reserved = await a.WaitForAsync<EconomyStateBot>(s => s.State.Funds < tooled.State.Funds, token);
            Assert.AreEqual(tooled.State.Funds - 310, reserved.State.Funds, .001, "Duplicate tooling must not charge again.");
            var cancel = new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = prepare.LaunchId, LaunchToken = prepare.LaunchToken };
            Assert.IsTrue((await CommandAsync(a, cancel, token)).Success);
            var refunded = await a.WaitForAsync<EconomyStateBot>(s => s.State.Revision > reserved.State.Revision && s.State.Funds == tooled.State.Funds, token);
            Assert.IsTrue((await CommandAsync(a, cancel, token)).Success);
            var pending = await CommandAsync(a, new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }, token);
            Assert.IsTrue(pending.Success, pending.Reason);
            await a.DisconnectForReconnectAsync(token);
            await b.WaitForAsync<PlayerLeftSnapshot>(s => s.Player == a.Name, token);
            Assert.AreEqual(agencyA, (await a.ConnectAsync(server.Port, token)).MyAgencyId);
            var reconnected = await a.WaitForAsync<EconomyStateBot>(s => s.State.Ready, token);
            Assert.AreEqual(refunded.State.Funds, reconnected.State.Funds, .001);
            Assert.AreEqual(1, reconnected.State.Designs.Length);
            Assert.AreEqual(initialA.State.Funds, initialB.State.Funds);
            var launch = await CommandAsync(a, new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }, token);
            Assert.IsTrue(launch.Success, launch.Reason);
            var vessel = Guid.NewGuid();
            var proto = AgencyVesselOwnershipTest.FixtureVessel(vessel, "Paid science craft", 3301, 3302)
                .Replace("name = probeCoreSphere\nuid = 3302", "name = sensorThermometer\nuid = 3302");
            a.UploadPaidVessel(vessel, proto, launch.LaunchId, launch.LaunchToken, new[] { 0, 1 });
            var registered = await a.WaitForAsync<EconomyResultBot>(s => s.Result.RequestId == launch.LaunchId && s.Result.Operation == EconomyOperation.RegisterLaunch, token);
            Assert.IsTrue(registered.Result.Success, registered.Result.Reason);
            var recovery = new EconomyCommand { Operation = EconomyOperation.Recover, VesselId = vessel, RecoveryFactor = .5, VesselData = System.Text.Encoding.UTF8.GetBytes(proto),
                RecoveredParts = new[] { new RecoveryPart { FlightId = 3301, StockValue = 100 }, new RecoveryPart { FlightId = 3302, StockValue = 300 } } };
            var recovered = await CommandAsync(a, recovery, token);
            Assert.IsTrue(recovered.Success, recovered.Reason);
            var paidBack = await a.WaitForAsync<EconomyStateBot>(s => s.State.Revision >= recovered.Revision && s.State.Funds == refunded.State.Funds - 155, token);
            Assert.IsTrue((await CommandAsync(a, recovery, token)).Success, "Same operation must replay without another refund.");
            recovery.RequestId = Guid.NewGuid(); recovery.Sequence = 0; recovery.SessionId = Guid.Empty;
            Assert.IsFalse((await CommandAsync(a, recovery, token)).Success, "A fresh ID must not recover spent parts again.");
            await a.DisconnectForReconnectAsync(token);
            await b.WaitForAsync<PlayerLeftSnapshot>(s => s.Player == a.Name, token);
            await a.ConnectAsync(server.Port, token);
            var final = await a.WaitForAsync<EconomyStateBot>(s => s.State.Ready, token);
            Assert.AreEqual(paidBack.State.Funds, final.State.Funds, .001);
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

    private static async Task<EconomyResult> CommandAsync(BotClient bot, EconomyCommand command, CancellationToken token)
    {
        var request = bot.SendEconomy(command);
        return (await bot.WaitForAsync<EconomyResultBot>(s => s.Result.RequestId == request, token)).Result;
    }
}