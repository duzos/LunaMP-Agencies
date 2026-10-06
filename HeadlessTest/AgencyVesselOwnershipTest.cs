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
public class AgencyVesselOwnershipTest
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task ForcedControlCoOwnerRevocationHandoverAndDockConsentAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var bots = new[] { new BotClient("OwnerA", "owner-a-" + run), new BotClient("OwnerB", "owner-b-" + run) };
        var a = bots[0]; var b = bots[1];
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, settings => settings.AgencyVesselOwnership = true);
            var agencyA = (await a.ConnectAsync(server.Port, token)).MyAgencyId;
            var agencyB = (await b.ConnectAsync(server.Port, token)).MyAgencyId;
            var vesselA = Guid.NewGuid();
            var vesselB = Guid.NewGuid();
            a.UploadVessel(vesselA, FixtureVessel(vesselA, "Agency A craft", 1001));
            await b.WaitForAsync<VesselProtoSnapshot>(s => s.VesselId == vesselA, token);
            await a.WaitForAsync<OwnershipMapSnapshot>(s => s.Ready && s.Records.Any(r => r.VesselId == vesselA && r.OwnerAgencyId == agencyA), token);
            b.UploadVessel(vesselB, FixtureVessel(vesselB, "Agency B craft", 2001));
            await a.WaitForAsync<VesselProtoSnapshot>(s => s.VesselId == vesselB, token);
            await b.WaitForAsync<OwnershipMapSnapshot>(s => s.Ready && s.Records.Any(r => r.VesselId == vesselB && r.OwnerAgencyId == agencyB), token);

            a.AcquireControl(vesselA, true);
            await a.WaitForAsync<ControlSnapshot>(s => s.VesselId == vesselA && s.Player == a.Name && s.Granted, token);
            b.AcquireControl(vesselA, true);
            await b.WaitForAsync<ControlSnapshot>(s => s.VesselId == vesselA && s.Player == b.Name && !s.Granted && s.Reason != "released", token);
            await CommandAsync(a, VesselOwnershipOperation.AddCoOwner, vesselA, agencyB, token);
            b.AcquireControl(vesselA, true);
            await b.WaitForAsync<ControlSnapshot>(s => s.VesselId == vesselA && s.Player == b.Name && s.Granted, token);
            await CommandAsync(a, VesselOwnershipOperation.RemoveCoOwner, vesselA, agencyB, token);
            await b.WaitForAsync<ControlSnapshot>(s => s.VesselId == vesselA && s.Player == b.Name && !s.Granted && s.Reason == "released", token);
            b.AcquireControl(vesselA, true);
            await b.WaitForAsync<ControlSnapshot>(s => s.VesselId == vesselA && s.Player == b.Name && !s.Granted && s.Reason != "released", token);

            await CommandAsync(a, VesselOwnershipOperation.Transfer, vesselA, agencyB, token);
            a.AcquireControl(vesselA, true);
            await a.WaitForAsync<ControlSnapshot>(s => s.VesselId == vesselA && s.Player == a.Name && !s.Granted && s.Reason != "released", token);
            await b.DisconnectForReconnectAsync(token);
            await a.WaitForAsync<PlayerLeftSnapshot>(s => s.Player == b.Name, token);
            Assert.AreEqual(agencyB, (await b.ConnectAsync(server.Port, token)).MyAgencyId);
            b.AcquireControl(vesselA, true);
            await b.WaitForAsync<ControlSnapshot>(s => s.VesselId == vesselA && s.Player == b.Name && s.Granted, token);

            var visitingVessel = Guid.NewGuid();
            a.UploadVessel(visitingVessel, FixtureVessel(visitingVessel, "Visiting craft", 3001));
            await b.WaitForAsync<VesselProtoSnapshot>(s => s.VesselId == visitingVessel, token);
            await a.WaitForAsync<OwnershipMapSnapshot>(s => s.Ready && s.Records.Any(r => r.VesselId == visitingVessel && r.OwnerAgencyId == agencyA), token);
            a.AcquireControl(visitingVessel, true);
            await a.WaitForAsync<ControlSnapshot>(s => s.VesselId == visitingVessel && s.Player == a.Name && s.Granted, token);
            var declined = a.RequestDock(visitingVessel, vesselB);
            await b.WaitForAsync<DockStatusSnapshot>(s => s.RequestId == declined && s.Status == DockConsentStatus.Pending, token);
            b.AnswerDock(declined, false);
            await a.WaitForAsync<DockStatusSnapshot>(s => s.RequestId == declined && s.Status == DockConsentStatus.Denied, token);
            var approved = a.RequestDock(visitingVessel, vesselB);
            await b.WaitForAsync<DockStatusSnapshot>(s => s.RequestId == approved && s.Status == DockConsentStatus.Pending, token);
            b.AnswerDock(approved, true);
            await a.WaitForAsync<DockStatusSnapshot>(s => s.RequestId == approved && s.Status == DockConsentStatus.Granted, token);

            // An ownership edit invalidates the outstanding one-off permission.
            await CommandAsync(b, VesselOwnershipOperation.SetDockingPolicy, vesselB, Guid.Empty, token, VesselDockingPolicy.Anyone);
            await a.WaitForAsync<DockStatusSnapshot>(s => s.RequestId == approved && (s.Status == DockConsentStatus.Cancelled || s.Status == DockConsentStatus.Denied), token);
            await b.DisconnectForReconnectAsync(token);
            await a.WaitForAsync<PlayerLeftSnapshot>(s => s.Player == b.Name, token);
            var offline = a.RequestDock(visitingVessel, vesselB);
            var offlineGrant = await a.WaitForAsync<DockStatusSnapshot>(s => s.RequestId == offline && s.Status == DockConsentStatus.Granted, token);
            var operation = Guid.NewGuid();
            var merged = FixtureVessel(vesselB, "Docked assembly", 2001, 3001);
            a.CompleteDock(operation, offlineGrant.GrantId, vesselB, 2001, visitingVessel, 3001, merged);
            await a.WaitForAsync<DockStatusSnapshot>(s => s.OperationId == operation && s.Status == DockConsentStatus.Completed, token);
            // A repeated completion must return the durable receipt rather than consume permission again.
            a.CompleteDock(operation, offlineGrant.GrantId, vesselB, 2001, visitingVessel, 3001, merged);
            await a.WaitForAsync<DockStatusSnapshot>(s => s.OperationId == operation && s.Status == DockConsentStatus.Completed, token);
            Assert.AreEqual(agencyB, (await b.ConnectAsync(server.Port, token)).MyAgencyId);
            b.RequestVessels();
            var authoritativeMerged = await b.WaitForAsync<VesselProtoSnapshot>(s => s.VesselId == vesselB, token);
            StringAssert.Contains(authoritativeMerged.Text, "uid = 2001");
            StringAssert.Contains(authoritativeMerged.Text, "uid = 3001");
            server.ThrowIfExited();
            succeeded = true;
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

    private static async Task CommandAsync(BotClient bot, VesselOwnershipOperation operation, Guid vessel, Guid agency,
        CancellationToken token, VesselDockingPolicy policy = VesselDockingPolicy.Nobody)
    {
        var request = bot.OwnershipCommand(operation, vessel, agency, policy);
        var reply = await bot.WaitForAsync<OwnershipResultSnapshot>(s => s.RequestId == request, token);
        Assert.IsTrue(reply.Success, reply.Reason);
    }

    internal static string FixtureVessel(Guid id, string name, params uint[] parts)
    {
        var result = $"pid = {id:N}\nname = {name}\nroot = 0\ntype = Ship\nsit = ORBITING\nORBIT\n{{\nbody = Kerbin\n}}\n";
        foreach (var uid in parts) result += $"PART\n{{\nname = probeCoreSphere\nuid = {uid}\nparent = 0\n}}\n";
        foreach (var node in new[] { "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }) result += node + "\n{\n}\n";
        return result;
    }
}
