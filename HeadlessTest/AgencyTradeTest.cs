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
public class AgencyTradeTest
{
    public TestContext TestContext { get; set; }
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AtomicTradeTransfersTitleAndEntitlementAndSurvivesReconnectAsync(bool tooling)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var a = new BotClient("Seller", "seller-" + run); var b = new BotClient("Buyer", "buyer-" + run);
        var bots = new[] { a, b };
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, s => { s.AgencyTrade = true; s.AgencyTooling = tooling; s.AgencyVesselOwnership = false; });
            var agencyA = (await a.ConnectAsync(server.Port, token)).MyAgencyId;
            var agencyB = (await b.ConnectAsync(server.Port, token)).MyAgencyId;
            await a.WaitForAsync<EconomyStateBot>(s => s.State.Ready, token);
            var initialB = await b.WaitForAsync<EconomyStateBot>(s => s.State.Ready, token);
            var vessel = Guid.NewGuid();
            var raw = AgencyVesselOwnershipTest.FixtureVessel(vessel, "Trade probe", 4401);
            var manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probeCoreSphere", UnitCost = 100 } } };
            if (tooling)
            {
                Assert.IsTrue((await Command(a, new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }, token)).Success);
                var launch = await Command(a, new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) }, token);
                Assert.IsTrue(launch.Success, launch.Reason);
                a.UploadPaidVessel(vessel, raw, launch.LaunchId, launch.LaunchToken, new[] { 0 });
                var registered = await a.WaitForAsync<EconomyResultBot>(s => s.Result.RequestId == launch.LaunchId && s.Result.Operation == EconomyOperation.RegisterLaunch, token);
                Assert.IsTrue(registered.Result.Success, registered.Result.Reason);
            }
            else a.UploadVessel(vessel, raw);
            await b.WaitForAsync<VesselProtoSnapshot>(s => s.VesselId == vessel, token);
            var draft = new TradeCommand { OfferId = Guid.NewGuid(), BuyerAgencyId = agencyB, VesselId = vessel, SellerFunds = 100, BuyerFunds = 250 };
            if (tooling)
            {
                draft.DesignFingerprint = ToolingPolicy.Fingerprint(manifest); draft.BlueprintName = "Trade probe"; draft.Editor = "VAB";
                draft.BlueprintData = System.Text.Encoding.UTF8.GetBytes("ship = Trade probe\ntype = VAB\nPART\n{\npart = probeCoreSphere_1\n}\n");
            }
            var created = await Command(a, new EconomyCommand { Operation = EconomyOperation.TradeCreate, Trade = draft }, token);
            Assert.IsTrue(created.Success, created.Reason);
            var offerA = await a.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == created.TradeOfferId), token);
            var offerB = await b.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == created.TradeOfferId), token);
            var offer = offerB.State.Offers.Single(o => o.OfferId == created.TradeOfferId);
            var invalid = await Command(a, new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.OfferId, ExpectedRevision = offer.Revision } }, token);
            Assert.IsFalse(invalid.Success, "Only the addressed buyer may accept.");
            var accept = new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.OfferId, ExpectedRevision = offer.Revision } };
            var accepted = await Command(b, accept, token);
            Assert.IsTrue(accepted.Success, accepted.Reason);
            var finalB = await b.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == offer.OfferId && o.Status == TradeOfferStatus.Accepted), token);
            var finalA = await a.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == offer.OfferId && o.Status == TradeOfferStatus.Accepted), token);
            Assert.AreEqual(offerA.State.Funds + 150, finalA.State.Funds, .001);
            Assert.AreEqual(initialB.State.Funds - 150, finalB.State.Funds, .001);
            Assert.IsTrue(finalB.State.Entitlements.Any(e => e.Fingerprint == ToolingPolicy.Fingerprint(manifest)));
            Assert.AreEqual(tooling ? 1 : 0, finalB.State.Designs.Length, "Title-only purchase must not manufacture tooling.");
            await b.WaitForAsync<OwnershipMapSnapshot>(s => s.Records.Any(r => r.VesselId == vessel && r.OwnerAgencyId == agencyB), token);
            Assert.IsTrue((await Command(b, accept, token)).Success, "Receipt retry must be idempotent.");
            await b.DisconnectForReconnectAsync(token);
            await a.WaitForAsync<PlayerLeftSnapshot>(s => s.Player == b.Name, token);
            Assert.AreEqual(agencyB, (await b.ConnectAsync(server.Port, token)).MyAgencyId);
            var reconnected = await b.WaitForAsync<EconomyStateBot>(s => s.State.Ready, token);
            Assert.AreEqual(finalB.State.Funds, reconnected.State.Funds, .001);
            Assert.IsTrue(reconnected.State.Entitlements.Any(e => e.Fingerprint == ToolingPolicy.Fingerprint(manifest)));
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
    private static async Task<EconomyResult> Command(BotClient bot, EconomyCommand command, CancellationToken token)
    {
        var id = bot.SendEconomy(command);
        return (await bot.WaitForAsync<EconomyResultBot>(s => s.Result.RequestId == id, token)).Result;
    }
}
