using HeadlessTest.Infrastructure;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessTest;
[TestClass, DoNotParallelize]
public class AgencyVisibilityTest
{
    public TestContext TestContext { get; set; }
    [TestMethod]
    public async Task VisibilitySharingIsAuthorizedStampedAndPersistsWithoutOwnershipEnforcementAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token; var run = Guid.NewGuid().ToString("N");
        var a = new BotClient("VisibleA", "visibility-a-" + run); var b = new BotClient("VisibleB", "visibility-b-" + run);
        var bots = new[] { a, b }; ServerProcess server = null; var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, s => { s.AgencyHideCraft = true; s.AgencyVesselOwnership = false; });
            var agencyA = (await a.ConnectAsync(server.Port, token)).MyAgencyId;
            var agencyB = (await b.ConnectAsync(server.Port, token)).MyAgencyId;
            await a.WaitForAsync<VisibilityStateBot>(s => s.State.Ready, token);
            await b.WaitForAsync<VisibilityStateBot>(s => s.State.Ready, token);
            var vessel = Guid.NewGuid(); a.UploadVessel(vessel, AgencyVesselOwnershipTest.FixtureVessel(vessel, "Private craft", 5501));
            // Client-side hiding must not filter the actual vessel stream.
            await b.WaitForAsync<VesselProtoSnapshot>(s => s.VesselId == vessel, token);
            var initial = await a.WaitForAsync<VisibilityStateBot>(s => s.State.Endpoints.Any(e => e.VesselId == vessel && e.OwnerAgencyId == agencyA), token);
            var endpoint = initial.State.Endpoints.Single(e => e.VesselId == vessel);
            var request = b.SetVisibility(VisibilityOperation.SetCraftOverride, agencyA, vessel, rule: VisibilityOverride.Allow, ownershipRevision: endpoint.OwnershipRevision);
            Assert.IsFalse((await b.WaitForAsync<VisibilityResultBot>(s => s.RequestId == request, token)).Success);
            request = a.SetVisibility(VisibilityOperation.SetAgencyShare, agencyB, enabled: true);
            var result = await a.WaitForAsync<VisibilityResultBot>(s => s.RequestId == request, token); Assert.IsTrue(result.Success, result.Reason);
            await b.WaitForAsync<VisibilityStateBot>(s => s.State.AgencyGrants.Any(g => g.OwnerAgencyId == agencyA && g.TargetAgencyId == agencyB), token);
            request = a.SetVisibility(VisibilityOperation.SetCraftOverride, agencyB, vessel, rule: VisibilityOverride.Deny, ownershipRevision: endpoint.OwnershipRevision);
            result = await a.WaitForAsync<VisibilityResultBot>(s => s.RequestId == request, token); Assert.IsTrue(result.Success, result.Reason);
            var denied = await b.WaitForAsync<VisibilityStateBot>(s => s.State.CraftOverrides.Any(r => r.Source.VesselId == vessel && r.TargetAgencyId == agencyB && r.Rule == VisibilityOverride.Deny), token);
            await b.DisconnectForReconnectAsync(token); await a.WaitForAsync<PlayerLeftSnapshot>(s => s.Player == b.Name, token);
            await b.ConnectAsync(server.Port, token);
            var reload = await b.WaitForAsync<VisibilityStateBot>(s => s.State.Ready, token);
            Assert.IsTrue(reload.State.CraftOverrides.Any(r => r.Source.VesselId == vessel && r.Rule == VisibilityOverride.Deny));
            Assert.IsTrue(reload.State.AgencyGrants.Any(g => g.OwnerAgencyId == agencyA && g.TargetAgencyId == agencyB));
            request = a.SetVisibility(VisibilityOperation.SetCraftOverride, agencyB, vessel, rule: VisibilityOverride.Inherit, ownershipRevision: endpoint.OwnershipRevision);
            result = await a.WaitForAsync<VisibilityResultBot>(s => s.RequestId == request, token); Assert.IsTrue(result.Success, result.Reason);
            request = a.SetVisibility(VisibilityOperation.SetAgencyShare, agencyB, enabled: false);
            result = await a.WaitForAsync<VisibilityResultBot>(s => s.RequestId == request, token); Assert.IsTrue(result.Success, result.Reason);
            await b.WaitForAsync<VisibilityStateBot>(s => s.State.Revision > denied.State.Revision && !s.State.AgencyGrants.Any(g => g.OwnerAgencyId == agencyA) && !s.State.CraftOverrides.Any(r => r.Source.VesselId == vessel), token);
            server.ThrowIfExited(); succeeded = true;
        }
        finally
        {
            var errors = new List<Exception>();
            foreach (var bot in bots)
            {
                try { await bot.DisposeAsync(); } catch (Exception e) { errors.Add(e); TestContext.WriteLine(e.ToString()); }
                try { if (server != null) server.AttachTranscript(bot.Name, bot.Transcript); } catch (Exception e) { errors.Add(e); }
            }
            if (server != null) try { await server.DisposeAsync(); } catch (Exception e) { errors.Add(e); }
            if (succeeded && errors.Count > 0) throw new AggregateException(errors);
        }
    }
}
