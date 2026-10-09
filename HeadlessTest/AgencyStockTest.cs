using HeadlessTest.Infrastructure;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessTest;

/// <summary>Design stock (plan 40) end to end over a real server process and the real wire.</summary>
[TestClass, DoNotParallelize]
public class AgencyStockTest
{
    public TestContext TestContext { get; set; }
    private const string Craft = "ship = Stock probe\ntype = VAB\nPART\n{\npart = probeCoreSphere_1\n}\n";

    private static ToolingManifest Manifest(double unitCost = 100) => new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probeCoreSphere", UnitCost = unitCost } } };

    [TestMethod]
    public async Task BuildSellLaunchRevertAndCancelKeepStockFundsAndBlueprintsConsistentAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var a = new BotClient("StockSeller", "stock-seller-" + run); var b = new BotClient("StockBuyer", "stock-buyer-" + run);
        var bots = new[] { a, b };
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, s => { s.AgencyTrade = true; s.AgencyTooling = true; s.AgencyVesselOwnership = false; });
            var agencyA = (await a.ConnectAsync(server.Port, token)).MyAgencyId;
            var agencyB = (await b.ConnectAsync(server.Port, token)).MyAgencyId;
            var initialA = await a.WaitForAsync<EconomyStateBot>(s => s.State.Ready, token);
            var initialB = await b.WaitForAsync<EconomyStateBot>(s => s.State.Ready, token);
            var manifest = Manifest();
            var hash = ToolingPolicy.ManifestHash(manifest);
            var fp = ToolingPolicy.Fingerprint(manifest);
            var blueprint = Encoding.UTF8.GetBytes(Craft);

            // A tools the design with its craft file.
            var tooled = await Command(a, new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = hash, DesignName = "Stock probe", BlueprintData = blueprint, BlueprintEditor = "VAB" }, token);
            Assert.IsTrue(tooled.Success, tooled.Reason);
            var afterTool = await a.WaitForAsync<EconomyStateBot>(s => s.State.Revision >= tooled.Revision && s.State.Designs.Any(d => d.Fingerprint == fp), token);
            Assert.AreEqual(initialA.State.Funds - 100 * ToolingDefaults.ToolingCost, afterTool.State.Funds, .001);
            Assert.AreEqual(fp, afterTool.State.DesignBlueprints.Single().Fingerprint, "The snapshot lists the saved craft.");

            // The build price follows the shared policy: only the tooled non-science share is discounted, and 10 units reaches the full discount.
            var quote = StockPolicy.Quote(afterTool.State.Designs.Single(), 10, ToolingRates.Default, StockRates.Default);
            Assert.IsTrue(quote.Success, quote.Reason);
            Assert.AreEqual(StockDefaults.MaxDiscount, quote.Discount, 1e-9);
            Assert.AreEqual(100 * ToolingDefaults.TooledLaunch * (1 - StockDefaults.MaxDiscount), quote.PrepaidPerUnit, 1e-9);
            Assert.AreEqual(70d, quote.Total, 1e-9);
            var stale = await Command(a, new EconomyCommand { Operation = EconomyOperation.BuildStock, StockFingerprint = fp, StockUnits = 10, ExpectedCharge = quote.Total + 1 }, token);
            Assert.IsFalse(stale.Success, "A mismatched client quote refuses the build.");
            var built = await Command(a, new EconomyCommand { Operation = EconomyOperation.BuildStock, StockFingerprint = fp, StockUnits = 10, ExpectedCharge = quote.Total }, token);
            Assert.IsTrue(built.Success, built.Reason);
            var stockedA = await a.WaitForAsync<EconomyStateBot>(s => s.State.Revision >= built.Revision && s.State.Stock.Any(), token);
            Assert.AreEqual(afterTool.State.Funds - quote.Total, stockedA.State.Funds, .001, "Funds are debited by the discounted total.");
            var lotA = stockedA.State.Stock.Single();
            Assert.AreEqual(10, lotA.Units); Assert.AreEqual(fp, lotA.Fingerprint); Assert.AreEqual(7d, lotA.PrepaidPerUnit, 1e-9);
            Assert.AreEqual(agencyA, lotA.BuilderAgencyId); Assert.AreEqual(Guid.Empty, lotA.SourceAgencyId); Assert.IsTrue(lotA.FundsBuilt);
            Assert.AreEqual(10, stockedA.State.StockHeldByFingerprint[fp]);

            // The blueprint round-trips exactly, and only for the tooling agency.
            var fetched = await Command(a, new EconomyCommand { Operation = EconomyOperation.FetchBlueprint, StockFingerprint = fp }, token);
            Assert.IsTrue(fetched.Success, fetched.Reason);
            CollectionAssert.AreEqual(blueprint, fetched.BlueprintData);
            Assert.AreEqual("VAB", fetched.BlueprintEditor);
            Assert.AreEqual(stockedA.State.DesignBlueprints.Single().Hash, fetched.BlueprintHash);
            Assert.IsFalse((await Command(b, new EconomyCommand { Operation = EconomyOperation.FetchBlueprint, StockFingerprint = fp }, token)).Success, "Another agency's craft is never returned.");

            // A sells 3 units to B; the offer escrows them while open.
            var draft = new TradeCommand { OfferId = Guid.NewGuid(), BuyerAgencyId = agencyB, DesignMode = TradeDesignMode.Stock, StockUnits = 3, SellerFunds = 0, BuyerFunds = 300,
                DesignFingerprint = fp, BlueprintName = "Stock probe", Editor = "VAB", BlueprintData = fetched.BlueprintData };
            var created = await Command(a, new EconomyCommand { Operation = EconomyOperation.TradeCreate, Trade = draft }, token);
            Assert.IsTrue(created.Success, created.Reason);
            var openA = await a.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == created.TradeOfferId) && s.State.Stock.Sum(l => l.Units) == 7, token);
            Assert.AreEqual(10, openA.State.StockHeldByFingerprint[fp], "Escrowed units still count as held.");
            var offer = (await b.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == created.TradeOfferId), token)).State.Offers.Single(o => o.OfferId == created.TradeOfferId);
            Assert.AreEqual(TradeDesignMode.Stock, offer.DesignMode); Assert.AreEqual(3, offer.StockUnits); Assert.AreEqual(21d, offer.StockPrepaidTotal, 1e-9);
            EconomyCommand Accept() => new EconomyCommand { Operation = EconomyOperation.TradeAccept, Trade = new TradeCommand { OfferId = offer.OfferId, ExpectedRevision = offer.Revision } };
            Assert.IsFalse((await Command(a, Accept(), token)).Success, "Only the addressed buyer may accept.");
            var accepted = await Command(b, Accept(), token);
            Assert.IsTrue(accepted.Success, accepted.Reason);
            var boughtB = await b.WaitForAsync<EconomyStateBot>(s => s.State.Revision >= accepted.Revision && s.State.Offers.Any(o => o.OfferId == offer.OfferId && o.Status == TradeOfferStatus.Accepted), token);
            var soldA = await a.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == offer.OfferId && o.Status == TradeOfferStatus.Accepted), token);
            Assert.AreEqual(stockedA.State.Funds + 300, soldA.State.Funds, .001, "Seller receives the trade price only.");
            Assert.AreEqual(initialB.State.Funds - 300, boughtB.State.Funds, .001);
            Assert.AreEqual(1, soldA.State.Designs.Length, "Seller tooling is untouched.");
            Assert.AreEqual(7, soldA.State.Stock.Sum(l => l.Units)); Assert.AreEqual(7, soldA.State.StockHeldByFingerprint[fp]);
            Assert.AreEqual(0, boughtB.State.Designs.Length, "Buyer receives no tooling.");
            var lotB = boughtB.State.Stock.Single();
            Assert.AreEqual(3, lotB.Units); Assert.AreEqual(7d, lotB.PrepaidPerUnit, 1e-9); Assert.AreEqual(agencyA, lotB.BuilderAgencyId); Assert.AreEqual(agencyA, lotB.SourceAgencyId);
            Assert.AreNotEqual(lotA.LotId, lotB.LotId, "The buyer's lot gets a fresh id.");
            Assert.AreEqual(3, boughtB.State.StockHeldByFingerprint[fp]);
            var stockEntitlement = boughtB.State.Entitlements.Single();
            Assert.AreEqual(TradeEntitlementKind.StockDesign, stockEntitlement.Kind); Assert.AreEqual(fp, stockEntitlement.Fingerprint);
            CollectionAssert.AreEqual(blueprint, stockEntitlement.BlueprintData);

            // Cancelling another open offer returns its escrow to the seller.
            var second = new TradeCommand { OfferId = Guid.NewGuid(), BuyerAgencyId = agencyB, DesignMode = TradeDesignMode.Stock, StockUnits = 2, BuyerFunds = 50, DesignFingerprint = fp, BlueprintName = "Stock probe", Editor = "VAB", BlueprintData = blueprint };
            var createdSecond = await Command(a, new EconomyCommand { Operation = EconomyOperation.TradeCreate, Trade = second }, token);
            Assert.IsTrue(createdSecond.Success, createdSecond.Reason);
            var escrowed = await a.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == createdSecond.TradeOfferId && o.Status == TradeOfferStatus.Open) && s.State.Stock.Sum(l => l.Units) == 5, token);
            Assert.AreEqual(7, escrowed.State.StockHeldByFingerprint[fp]);
            var secondOffer = escrowed.State.Offers.Single(o => o.OfferId == createdSecond.TradeOfferId);
            var cancelled = await Command(a, new EconomyCommand { Operation = EconomyOperation.TradeCancel, Trade = new TradeCommand { OfferId = secondOffer.OfferId, ExpectedRevision = secondOffer.Revision } }, token);
            Assert.IsTrue(cancelled.Success, cancelled.Reason);
            var returned = await a.WaitForAsync<EconomyStateBot>(s => s.State.Offers.Any(o => o.OfferId == secondOffer.OfferId && o.Status == TradeOfferStatus.Cancelled) && s.State.Stock.Sum(l => l.Units) == 7, token);
            Assert.AreEqual(7, returned.State.StockHeldByFingerprint[fp]);
            Assert.AreEqual(soldA.State.Funds, returned.State.Funds, .001, "Cancelling charges nothing.");

            // B prepares a launch on a stock unit, drops, and the unit comes back.
            var lotId = lotB.LotId;
            var dropped = await Command(b, new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), StockLotId = lotId, Manifest = manifest, ManifestHash = hash }, token);
            Assert.IsTrue(dropped.Success, dropped.Reason);
            Assert.AreEqual(0d, dropped.Quote.LaunchCost, .001, "The prepayment covers the whole launch.");
            await b.WaitForAsync<EconomyStateBot>(s => s.State.Revision >= dropped.Revision && s.State.Stock.Sum(l => l.Units) == 2, token);
            await b.DisconnectForReconnectAsync(token);
            await a.WaitForAsync<PlayerLeftSnapshot>(s => s.Player == b.Name, token);
            Assert.AreEqual(agencyB, (await b.ConnectAsync(server.Port, token)).MyAgencyId);
            var back = await b.WaitForAsync<EconomyStateBot>(s => s.State.Ready && s.State.AgencyId == agencyB, token);
            Assert.AreEqual(3, back.State.Stock.Sum(l => l.Units), "A dropped reservation gives the unit back.");
            Assert.AreEqual(lotId, back.State.Stock.Single().LotId, "A same-terms return merges without changing the lot id.");
            Assert.AreEqual(boughtB.State.Funds, back.State.Funds, .001);

            // A real launch from stock registers and consumes the unit.
            var launch = await Command(b, new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), StockLotId = lotId, Manifest = manifest, ManifestHash = hash }, token);
            Assert.IsTrue(launch.Success, launch.Reason);
            Assert.AreEqual(0d, launch.Quote.LaunchCost, .001);
            var vessel = Guid.NewGuid();
            b.UploadPaidVessel(vessel, AgencyVesselOwnershipTest.FixtureVessel(vessel, "Stock probe", 4601), launch.LaunchId, launch.LaunchToken, new[] { 0 }, tradeEntitlement: lotId);
            var registered = await b.WaitForAsync<EconomyResultBot>(s => s.Result.RequestId == launch.LaunchId && s.Result.Operation == EconomyOperation.RegisterLaunch, token);
            Assert.IsTrue(registered.Result.Success, registered.Result.Reason);
            var launched = await b.WaitForAsync<EconomyStateBot>(s => s.State.Stock.Sum(l => l.Units) == 2 && s.State.Launches.Any(l => l.LaunchId == launch.LaunchId && l.State == LaunchState.Registered), token);
            Assert.AreEqual(back.State.Funds, launched.State.Funds, .001, "Charge is zero when the prepayment covers the launch.");
            Assert.AreEqual(3, launched.State.StockHeldByFingerprint[fp], "A revertible stock launch still counts as held.");

            // A launch priced above the prepayment charges only the top-up, and reverting refunds it and returns the unit.
            var dear = Manifest(150);
            var topUp = await Command(b, new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), StockLotId = lotId, Manifest = dear, ManifestHash = ToolingPolicy.ManifestHash(dear) }, token);
            Assert.IsTrue(topUp.Success, topUp.Reason);
            Assert.AreEqual(150 * 0.07 - 7, topUp.Quote.LaunchCost, 1e-6);
            var vessel2 = Guid.NewGuid();
            b.UploadPaidVessel(vessel2, AgencyVesselOwnershipTest.FixtureVessel(vessel2, "Stock probe 2", 4701), topUp.LaunchId, topUp.LaunchToken, new[] { 0 }, tradeEntitlement: lotId);
            Assert.IsTrue((await b.WaitForAsync<EconomyResultBot>(s => s.Result.RequestId == topUp.LaunchId && s.Result.Operation == EconomyOperation.RegisterLaunch, token)).Result.Success);
            var charged = await b.WaitForAsync<EconomyStateBot>(s => s.State.Stock.Sum(l => l.Units) == 1 && s.State.Launches.Any(l => l.LaunchId == topUp.LaunchId && l.State == LaunchState.Registered), token);
            Assert.AreEqual(launched.State.Funds - topUp.Quote.LaunchCost, charged.State.Funds, .001);
            var reverted = await Command(b, new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = topUp.LaunchId }, token);
            Assert.IsTrue(reverted.Success, reverted.Reason);
            var afterRevert = await b.WaitForAsync<EconomyStateBot>(s => s.State.Revision >= reverted.Revision && s.State.Stock.Sum(l => l.Units) == 2, token);
            Assert.AreEqual(launched.State.Funds, afterRevert.State.Funds, .001, "Revert refunds the charge.");
            Assert.AreEqual(lotId, afterRevert.State.Stock.Single().LotId);
            Assert.AreEqual(3, afterRevert.State.StockHeldByFingerprint[fp]);

            // The seller's view never moved during B's launches, and B holds no tooling.
            var finalA = await a.WaitForAsync<EconomyStateBot>(s => s.State.Ready && s.State.Revision >= returned.State.Revision, token);
            Assert.AreEqual(soldA.State.Funds, finalA.State.Funds, .001);
            Assert.AreEqual(7, finalA.State.Stock.Sum(l => l.Units)); Assert.AreEqual(7, finalA.State.StockHeldByFingerprint[fp]);
            Assert.AreEqual(1, finalA.State.Designs.Length); Assert.AreEqual(1, finalA.State.DesignBlueprints.Length);
            Assert.AreEqual(0, afterRevert.State.Designs.Length);
            server.ThrowIfExited(); succeeded = true;
        }
        finally { await CleanupAsync(bots, server, succeeded); }
    }

    [TestMethod]
    public async Task StockBuildsAndLaunchesWorkWithTradeSwitchedOffAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var a = new BotClient("StockSolo", "stock-solo-" + run);
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, s => { s.AgencyTrade = false; s.AgencyTooling = true; s.AgencyVesselOwnership = false; });
            await a.ConnectAsync(server.Port, token);
            var initial = await a.WaitForAsync<EconomyStateBot>(s => s.State.Ready, token);
            var manifest = Manifest(); var hash = ToolingPolicy.ManifestHash(manifest); var fp = ToolingPolicy.Fingerprint(manifest);
            var tooled = await Command(a, new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = hash }, token);
            Assert.IsTrue(tooled.Success, tooled.Reason);
            var afterTool = await a.WaitForAsync<EconomyStateBot>(s => s.State.Revision >= tooled.Revision && s.State.Designs.Any(), token);
            Assert.AreEqual(0, afterTool.State.DesignBlueprints.Length, "A Tool without craft bytes saves no blueprint.");
            var built = await Command(a, new EconomyCommand { Operation = EconomyOperation.BuildStock, StockFingerprint = fp, StockUnits = 5, ExpectedCharge = 5 * 100 * ToolingDefaults.TooledLaunch * (1 - StockDefaults.MaxDiscount * 4 / 9) }, token);
            Assert.IsTrue(built.Success, built.Reason);
            var stocked = await a.WaitForAsync<EconomyStateBot>(s => s.State.Revision >= built.Revision && s.State.Stock.Any(), token);
            var lot = stocked.State.Stock.Single();
            Assert.AreEqual(5, lot.Units);
            Assert.AreEqual(afterTool.State.Funds - 5 * lot.PrepaidPerUnit, stocked.State.Funds, .001);
            var launch = await Command(a, new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), StockLotId = lot.LotId, Manifest = manifest, ManifestHash = hash }, token);
            Assert.IsTrue(launch.Success, launch.Reason);
            var vessel = Guid.NewGuid();
            a.UploadPaidVessel(vessel, AgencyVesselOwnershipTest.FixtureVessel(vessel, "Solo stock probe", 4801), launch.LaunchId, launch.LaunchToken, new[] { 0 }, tradeEntitlement: lot.LotId);
            var registered = await a.WaitForAsync<EconomyResultBot>(s => s.Result.RequestId == launch.LaunchId && s.Result.Operation == EconomyOperation.RegisterLaunch, token);
            Assert.IsTrue(registered.Result.Success, registered.Result.Reason);
            var final = await a.WaitForAsync<EconomyStateBot>(s => s.State.Stock.Sum(l => l.Units) == 4, token);
            Assert.AreEqual(5, final.State.StockHeldByFingerprint[fp]);
            server.ThrowIfExited(); succeeded = true;
        }
        finally { await CleanupAsync(new[] { a }, server, succeeded); }
    }

    private async Task CleanupAsync(BotClient[] bots, ServerProcess server, bool succeeded)
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

    private static async Task<EconomyResult> Command(BotClient bot, EconomyCommand command, CancellationToken token)
    {
        var id = bot.SendEconomy(command);
        return (await bot.WaitForAsync<EconomyResultBot>(s => s.Result.RequestId == id, token)).Result;
    }
}
