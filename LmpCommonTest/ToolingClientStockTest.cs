using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    /// <summary>Plan 40 slice S3: the pure stock and blueprint state of the linked production ToolingClient.</summary>
    [TestClass, DoNotParallelize]
    public class ToolingClientStockTest
    {
        private static readonly ToolingManifest Manifest = new ToolingManifest
        {
            Parts = new[]
            {
                new ToolingPart { Name = "fuelTank", UnitCost = 1000 },
                new ToolingPart { Name = "fuelTank", UnitCost = 1000 },
                new ToolingPart { Name = "probeCore", UnitCost = 500, IsScience = true }
            }
        };
        private static readonly string Fp = ToolingPolicy.Fingerprint(Manifest);
        private static readonly ToolingManifest Other = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "wing", UnitCost = 200 } } };
        private static readonly byte[] CraftBytes = Encoding.UTF8.GetBytes("ship = Test craft\ntype = SPH\n");
        private Guid agency;

        [TestInitialize]
        public void Setup()
        {
            ToolingClient.Clear(); ToolingClient.UseStock = true; ToolingClient.ResetBlueprintHooks();
            NetworkSender.Sent.Clear();
            SettingsSystem.ServerSettings = new TestSettings { AgencyTooling = true, GameMode = GameMode.Career };
            MainSystem.NetworkState = ClientState.Running;
            MainSystem.Singleton.ForceQuit = false;
            agency = Guid.NewGuid(); AgencySystem.Singleton.MyAgencyId = agency;
            Funding.Instance = null; ResearchAndDevelopment.Instance = null;
            HighLogic.LoadedScene = GameScenes.SPACECENTER; HighLogic.LoadedSceneIsEditor = false;
            EditorLogic.fetch = null; ToolingManifestBuilder.BuildHook = null;
        }

        [TestCleanup]
        public void Cleanup()
        {
            ToolingClient.Clear(); ToolingClient.UseStock = true; ToolingClient.ResetBlueprintHooks();
            NetworkSender.Sent.Clear(); EditorLogic.fetch = null; ToolingManifestBuilder.BuildHook = null;
            HighLogic.LoadedScene = GameScenes.SPACECENTER; HighLogic.LoadedSceneIsEditor = false;
            SettingsSystem.ServerSettings = new TestSettings(); MainSystem.NetworkState = ClientState.Disconnected;
        }

        private EconomySnapshot Snapshot(params DesignStockLot[] lots) => new EconomySnapshot
        {
            AgencyId = agency, Ready = true, SessionId = Guid.NewGuid(), Revision = 1, Funds = 1000000,
            Designs = new[] { new ToolingDesign { Fingerprint = Fp, Manifest = Manifest, ToolingBasis = 2000, Name = "Lifter" } },
            Stock = lots
        };

        private static DesignStockLot Lot(int units, double prepaid = 2640, double multiplier = 0.07, long ticks = 1, string fingerprint = null) => new DesignStockLot
        {
            LotId = Guid.NewGuid(), Fingerprint = fingerprint ?? Fp, Units = units, PrepaidPerUnit = prepaid, LaunchMultiplier = multiplier, FundsBuilt = true, CreatedUtcTicks = ticks
        };

        private static void Apply(EconomySnapshot snapshot) { ToolingClient.Receive(snapshot); ToolingClient.Tick(); }

        private static EconomyCommand[] Commands() => NetworkSender.Sent.Select(m => m.Data).OfType<AgencyEconomyCommandMsgData>().Select(d => d.Command).ToArray();

        private static string Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private void OpenEditor(ToolingManifest manifest, int parts = 3)
        {
            HighLogic.LoadedScene = GameScenes.EDITOR; HighLogic.LoadedSceneIsEditor = true;
            var ship = new ShipConstruct { shipName = "Lifter" };
            for (var i = 0; i < parts; i++) ship.parts.Add(new Part());
            EditorLogic.fetch = new EditorLogic { ship = ship };
            ToolingManifestBuilder.BuildHook = _ => manifest;
        }

        private static void SetPending(Guid lot, string fingerprint)
        {
            var type = typeof(ToolingClient).GetNestedType("PendingLaunch", BindingFlags.NonPublic);
            var pending = Activator.CreateInstance(type, true);
            type.GetField("StockLot", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pending, lot);
            type.GetField("StockFingerprint", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pending, fingerprint);
            type.GetField("Scene", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pending, HighLogic.LoadedScene);
            type.GetField("Deadline", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pending, DateTime.UtcNow.AddMinutes(1));
            typeof(ToolingClient).GetField("pending", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, pending);
        }

        [TestMethod]
        public void StockUnits_ReadsServerHeldCount_OfferedUnitsCountsOnlyOpenOutgoingStockOffers()
        {
            var snapshot = Snapshot(Lot(4), Lot(3, ticks: 2), Lot(9, fingerprint: ToolingPolicy.Fingerprint(Other)));
            snapshot.StockHeldByFingerprint[Fp] = 12; // 7 in rows, plus escrow and revertible launches the server counts
            snapshot.Offers = new[]
            {
                new TradeOffer { SellerAgencyId = agency, Status = TradeOfferStatus.Open, DesignMode = TradeDesignMode.Stock, DesignFingerprint = Fp, StockUnits = 2 },
                new TradeOffer { SellerAgencyId = agency, Status = TradeOfferStatus.Open, DesignMode = TradeDesignMode.Stock, DesignFingerprint = Fp, StockUnits = 1 },
                new TradeOffer { SellerAgencyId = agency, Status = TradeOfferStatus.Declined, DesignMode = TradeDesignMode.Stock, DesignFingerprint = Fp, StockUnits = 5 },
                new TradeOffer { SellerAgencyId = Guid.NewGuid(), BuyerAgencyId = agency, Status = TradeOfferStatus.Open, DesignMode = TradeDesignMode.Stock, DesignFingerprint = Fp, StockUnits = 7 },
                new TradeOffer { SellerAgencyId = agency, Status = TradeOfferStatus.Open, DesignMode = TradeDesignMode.SingleLaunch, DesignFingerprint = Fp, StockUnits = 0 }
            };
            Apply(snapshot);
            Assert.AreEqual(12, ToolingClient.StockUnits(Fp));
            Assert.AreEqual(7, ToolingClient.AvailableStockUnits(Fp));
            Assert.AreEqual(9, ToolingClient.StockUnits(ToolingPolicy.Fingerprint(Other)), "Without a held entry the rows are counted.");
            Assert.AreEqual(3, ToolingClient.OfferedUnits(Fp));
            Assert.AreEqual(3, ToolingClient.GetStockSnapshot().Count);
            Assert.AreEqual("Lifter", ToolingClient.GetDesignsSnapshot().Single().Name);
            ToolingClient.GetStockSnapshot()[0].Units = 500;
            Assert.AreEqual(7, ToolingClient.AvailableStockUnits(Fp), "Snapshots are copies.");
        }

        [TestMethod]
        public void BuildStock_SendsTheLiveQuoteTotalInCareer()
        {
            Apply(Snapshot());
            var quote = ToolingClient.QuoteBuild(Fp, 10);
            var expected = StockPolicy.Quote(Snapshot().Designs[0], 10, ToolingClient.Rates(), ToolingClient.StockRates());
            Assert.IsTrue(quote.Success);
            Assert.AreEqual(expected.Total, quote.Total, 1e-9);
            Assert.AreEqual(0.30, quote.Discount, 1e-12);
            Assert.AreNotEqual(Guid.Empty, ToolingClient.BuildStock(Fp, 10, quote.Total));
            var command = Commands().Single();
            Assert.AreEqual(EconomyOperation.BuildStock, command.Operation);
            Assert.AreEqual(Fp, command.StockFingerprint);
            Assert.AreEqual(10, command.StockUnits);
            Assert.AreEqual(quote.Total, command.ExpectedCharge, 1e-9);
            Assert.IsTrue(ToolingClient.BuildPending);
            Assert.AreEqual(Guid.Empty, ToolingClient.BuildStock(Fp, 1, ToolingClient.QuoteBuild(Fp, 1).Total), "One build at a time.");
            ToolingClient.Receive(new EconomyResult { Operation = EconomyOperation.BuildStock, RequestId = command.RequestId, Success = true, Reason = "Built 10." });
            ToolingClient.Tick();
            Assert.IsFalse(ToolingClient.BuildPending);
            Assert.AreEqual("Built 10.", ToolingClient.LatestStatus);
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void BuildStock_SendsZeroOutsideCareer(bool passQuotedTotal)
        {
            SettingsSystem.ServerSettings.GameMode = GameMode.Sandbox;
            Apply(Snapshot());
            var quote = ToolingClient.QuoteBuild(Fp, 5);
            Assert.AreNotEqual(Guid.Empty, ToolingClient.BuildStock(Fp, 5, passQuotedTotal ? quote.Total : 0));
            Assert.AreEqual(0d, Commands().Single().ExpectedCharge);
        }

        [TestMethod]
        public void BuildStock_RefusesAStalePriceAndTheHeldCap()
        {
            var snapshot = Snapshot(Lot(998));
            snapshot.StockHeldByFingerprint[Fp] = 999; // one of the 999 is a Registered (revertible) stock launch
            Apply(snapshot);
            var quote = ToolingClient.QuoteBuild(Fp, 1);
            Assert.AreEqual(Guid.Empty, ToolingClient.BuildStock(Fp, 1, quote.Total));
            StringAssert.Contains(ToolingClient.LatestStatus, "999");
            snapshot = Snapshot(); snapshot.Revision = 2;
            Apply(snapshot);
            Assert.AreEqual(Guid.Empty, ToolingClient.BuildStock(Fp, 2, quote.Total));
            StringAssert.Contains(ToolingClient.LatestStatus, "Price changed");
            Assert.AreEqual(Guid.Empty, ToolingClient.BuildStock(ToolingPolicy.Fingerprint(Other), 1, 0), "Only a tooled design can be built.");
            Assert.AreEqual(0, Commands().Length);
        }

        [TestMethod]
        public void EditorQuote_AppliesTheCheapestLot_AndUseStockOffRestoresNormalPricing()
        {
            var cheap = Lot(3, prepaid: 600, multiplier: 0.07, ticks: 5); // charge 40
            var dearer = Lot(2, prepaid: 600, multiplier: 0.1, ticks: 1); // charge 100, older
            Apply(Snapshot(dearer, cheap));
            OpenEditor(Manifest);
            ToolingClient.Tick();
            var standard = ToolingClient.StandardQuote(Manifest);
            Assert.IsTrue(standard.AlreadyTooled);
            Assert.AreEqual(cheap.LotId, ToolingClient.EditorStock.LotId);
            Assert.AreEqual(StockPolicy.LaunchCharge(standard, cheap), ToolingClient.EditorQuote.LaunchCost, 1e-9);
            Assert.IsTrue(ToolingClient.EditorQuote.LaunchCost < standard.LaunchCost);

            ToolingClient.UseStock = false;
            ToolingClient.Tick();
            Assert.IsNull(ToolingClient.EditorStock);
            Assert.AreEqual(standard.LaunchCost, ToolingClient.EditorQuote.LaunchCost, 1e-9);
            Assert.IsNull(ToolingClient.SelectStock(standard));
        }

        [TestMethod]
        public void SelectStock_PrefersAOneUnitLotWhenSlotsAreFull()
        {
            var lots = Enumerable.Range(0, StockDefaults.MaxLots - 1).Select(i => Lot(5, prepaid: 2600 + i, ticks: i)).ToList();
            var single = Lot(1, prepaid: 2690, ticks: 999);
            lots.Add(single);
            Apply(Snapshot(lots.ToArray()));
            Assert.IsTrue(ToolingClient.StockSlotsFull);
            var standard = ToolingClient.StandardQuote(Manifest);
            Assert.AreEqual(single.LotId, ToolingClient.SelectStock(standard).LotId, "Only emptying a row keeps the slot count unchanged.");

            lots.Remove(single); lots.Add(Lot(2, prepaid: 2690, ticks: 999));
            var full = Snapshot(lots.ToArray()); full.Revision = 2;
            Apply(full);
            Assert.IsNull(ToolingClient.SelectStock(standard), "No 1-unit lot: no stock applies while slots are full.");

            lots.RemoveAt(0);
            var free = Snapshot(lots.ToArray()); free.Revision = 3;
            Apply(free);
            Assert.AreEqual(2600 + 1, ToolingClient.SelectStock(standard).PrepaidPerUnit, 1e-9, "With a free slot any lot qualifies again (equal charges: the oldest).");
        }

        [TestMethod]
        public void HasStockResearch_NeedsAHeldUnitBeforeAnyPricing()
        {
            Apply(Snapshot(Lot(1, fingerprint: ToolingPolicy.Fingerprint(Other))));
            var calls = ToolingClient.StandardQuoteCalls;
            Assert.IsFalse(ToolingClient.HasStockResearch(Manifest));
            Assert.AreEqual(calls, ToolingClient.StandardQuoteCalls, "No held unit of this design: StandardQuote is never computed.");

            var held = Snapshot(Lot(2)); held.Revision = 2;
            Apply(held);
            Assert.IsTrue(ToolingClient.HasStockResearch(Manifest));
            ToolingClient.UseStock = false;
            Assert.IsFalse(ToolingClient.HasStockResearch(Manifest));
            ToolingClient.UseStock = true;
            SettingsSystem.ServerSettings.AgencyTooling = false;
            Assert.IsFalse(ToolingClient.HasStockResearch(Manifest), "Stock research needs tooling.");
        }

        [TestMethod]
        public void HasStockResearch_PendingLaunchCountsByLotIdAfterTheRowIsPruned()
        {
            var lot = Lot(1);
            Apply(Snapshot(lot));
            SetPending(lot.LotId, Fp);
            var pruned = Snapshot(); pruned.Revision = 2; // Prepare emptied and removed the row
            Apply(pruned);
            Assert.IsTrue(ToolingClient.HasStockResearch(Manifest));
            Assert.IsFalse(ToolingClient.HasStockResearch(Other));
            SetPending(Guid.Empty, null);
            Assert.IsFalse(ToolingClient.HasStockResearch(Manifest), "A pending launch without stock never unlocks research.");
        }

        [TestMethod]
        public void EditorBlueprintNeedsSave_IsSetOnlyFromTick()
        {
            var snapshot = Snapshot();
            snapshot.DesignBlueprints = new[] { new ToolingBlueprintInfo { Fingerprint = Fp, Name = "Lifter", Editor = "VAB", Hash = "saved", Bytes = 10 } };
            Apply(snapshot);
            OpenEditor(Manifest);
            ToolingClient.TestEditorHash = "edited";
            Assert.IsFalse(ToolingClient.EditorBlueprintNeedsSave);
            Assert.AreEqual(0, ToolingClient.TestEditorHashCalls, "Nothing captures the craft outside Tick.");
            ToolingClient.Tick();
            Assert.IsTrue(ToolingClient.EditorBlueprintNeedsSave);

            ToolingClient.RequestQuoteRefresh(); ToolingClient.Tick();
            Assert.AreEqual(1, ToolingClient.TestEditorHashCalls, "Unchanged craft and saved hash: no recapture, however many ticks pass.");
            ToolingClient.RequestQuoteRefresh(); ToolingClient.Tick();
            Assert.AreEqual(1, ToolingClient.TestEditorHashCalls, "There is no timer.");

            ToolingClient.MarkEditorCraftModified();
            ToolingClient.RequestQuoteRefresh(); ToolingClient.Tick();
            Assert.AreEqual(2, ToolingClient.TestEditorHashCalls, "onEditorShipModified triggers one recapture.");
            ToolingClient.RequestQuoteRefresh(); ToolingClient.Tick();
            Assert.AreEqual(2, ToolingClient.TestEditorHashCalls);

            var saved = Snapshot(); saved.Revision = 2;
            saved.DesignBlueprints = new[] { new ToolingBlueprintInfo { Fingerprint = Fp, Name = "Lifter", Editor = "VAB", Hash = "edited", Bytes = 10 } };
            Apply(saved);
            ToolingClient.RequestQuoteRefresh(); ToolingClient.Tick();
            Assert.IsFalse(ToolingClient.EditorBlueprintNeedsSave, "A new saved hash is rechecked at once.");

            ToolingManifestBuilder.BuildHook = _ => Other; // untooled craft
            ToolingClient.RequestQuoteRefresh(); ToolingClient.Tick();
            Assert.IsFalse(ToolingClient.EditorBlueprintNeedsSave);
        }

        [TestMethod]
        public void EditorBlueprintNeedsSave_TrueWhenNoBlueprintIsSaved()
        {
            Apply(Snapshot());
            OpenEditor(Manifest);
            ToolingClient.TestEditorHash = "craft";
            ToolingClient.Tick();
            Assert.IsTrue(ToolingClient.EditorBlueprintNeedsSave);
        }

        [TestMethod]
        public void OversizedCraft_HidesSaveAndReportsTheLimit()
        {
            Apply(Snapshot());
            OpenEditor(Manifest);
            ToolingClient.TestEditorHash = "craft";
            ToolingClient.TestEditorBytes = ToolingLimits.MaxToolingBlueprintBytes + 1;
            ToolingClient.Tick();
            Assert.IsFalse(ToolingClient.EditorBlueprintNeedsSave, "No Save craft to tooling button for a craft the server would refuse.");
            Assert.AreEqual(ToolingLimits.MaxToolingBlueprintBytes + 1, ToolingClient.EditorBlueprintOversizeBytes);
            StringAssert.Contains(ToolingClient.BlueprintTooLargeText(ToolingClient.EditorBlueprintOversizeBytes), (ToolingLimits.MaxToolingBlueprintBytes / 1024) + " KB");

            ToolingClient.TestCaptureBytes = new byte[ToolingLimits.MaxToolingBlueprintBytes + 1];
            Assert.AreEqual(Guid.Empty, ToolingClient.SaveBlueprintToTooling());
            StringAssert.Contains(ToolingClient.LatestStatus, "limited to");
            Assert.AreEqual(0, Commands().Length);

            ToolingClient.TestEditorBytes = 0;
            ToolingClient.MarkEditorCraftModified(); ToolingClient.RequestQuoteRefresh(); ToolingClient.Tick();
            Assert.AreEqual(0, ToolingClient.EditorBlueprintOversizeBytes);
            Assert.IsTrue(ToolingClient.EditorBlueprintNeedsSave);
        }

        [TestMethod]
        public void SaveBlueprintToTooling_OneAtATimeUntilTheResultOrTheDeadline()
        {
            Apply(Snapshot());
            OpenEditor(Manifest);
            ToolingClient.Tick();
            ToolingClient.TestCaptureBytes = CraftBytes;
            var request = ToolingClient.SaveBlueprintToTooling();
            Assert.AreNotEqual(Guid.Empty, request);
            Assert.IsTrue(ToolingClient.SaveBlueprintPending);
            Assert.AreEqual(Guid.Empty, ToolingClient.SaveBlueprintToTooling(), "A second click while saving sends nothing.");
            Assert.AreEqual(1, Commands().Length);
            ToolingClient.Receive(new EconomyResult { Operation = EconomyOperation.Tool, RequestId = request, Success = true, Reason = "Saved." });
            ToolingClient.Tick();
            Assert.IsFalse(ToolingClient.SaveBlueprintPending);

            Assert.AreNotEqual(Guid.Empty, ToolingClient.SaveBlueprintToTooling());
            SetDeadline("saveDeadline", DateTime.UtcNow.AddSeconds(-1));
            ToolingClient.Tick();
            Assert.IsFalse(ToolingClient.SaveBlueprintPending, "A save with no answer frees the button at its deadline.");
        }

        private static void SetDeadline(string field, DateTime value) =>
            typeof(ToolingClient).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);

        [TestMethod]
        public void BuildPending_ClearsAtItsDeadline()
        {
            Apply(Snapshot());
            Assert.AreNotEqual(Guid.Empty, ToolingClient.BuildStock(Fp, 2, ToolingClient.QuoteBuild(Fp, 2).Total));
            ToolingClient.Tick();
            Assert.IsTrue(ToolingClient.BuildPending, "Still within the deadline.");
            SetDeadline("buildDeadline", DateTime.UtcNow.AddSeconds(-1));
            ToolingClient.Tick();
            Assert.IsFalse(ToolingClient.BuildPending);
            StringAssert.Contains(ToolingClient.LatestStatus, "did not arrive");
            Assert.AreNotEqual(Guid.Empty, ToolingClient.BuildStock(Fp, 1, ToolingClient.QuoteBuild(Fp, 1).Total), "A new build is allowed again.");
            var late = Commands().First();
            ToolingClient.Receive(new EconomyResult { Operation = EconomyOperation.BuildStock, RequestId = late.RequestId, Success = true, Reason = "Built 2." });
            ToolingClient.Tick();
            Assert.IsTrue(ToolingClient.BuildPending, "The late result of the timed-out build does not clear the new one.");
        }

        [TestMethod]
        public void SaveBlueprintToTooling_SendsAToolWithTheCraftOnlyForATooledDesign()
        {
            Apply(Snapshot());
            OpenEditor(Other);
            ToolingClient.Tick();
            ToolingClient.TestCaptureBytes = CraftBytes;
            Assert.AreEqual(Guid.Empty, ToolingClient.SaveBlueprintToTooling(), "Never buys tooling.");
            Assert.AreEqual(0, Commands().Length);

            ToolingManifestBuilder.BuildHook = _ => Manifest;
            ToolingClient.RequestQuoteRefresh(); ToolingClient.Tick();
            Assert.AreNotEqual(Guid.Empty, ToolingClient.SaveBlueprintToTooling());
            var command = Commands().Single();
            Assert.AreEqual(EconomyOperation.Tool, command.Operation);
            CollectionAssert.AreEqual(CraftBytes, command.BlueprintData);
            Assert.AreEqual("VAB", command.BlueprintEditor);
            Assert.AreEqual("Test craft", command.DesignName);
        }

        [TestMethod]
        public void PurchaseTooling_StillBuysWhenTheCraftCannotBeCaptured()
        {
            Apply(Snapshot());
            OpenEditor(Other);
            ToolingClient.Tick();
            Assert.AreNotEqual(Guid.Empty, ToolingClient.PurchaseTooling());
            var command = Commands().Single();
            Assert.AreEqual(EconomyOperation.Tool, command.Operation);
            Assert.AreEqual(0, command.BlueprintData.Length);
            Assert.IsNull(command.BlueprintEditor);
            Assert.AreEqual("Lifter", command.DesignName, "Falls back to the editor ship name.");
        }

        private EconomySnapshot WithBlueprint(long revision = 1)
        {
            var snapshot = Snapshot(); snapshot.Revision = revision;
            snapshot.DesignBlueprints = new[] { new ToolingBlueprintInfo { Fingerprint = Fp, Name = "Lifter", Editor = "SPH", Hash = Hex(CraftBytes), Bytes = CraftBytes.Length } };
            return snapshot;
        }

        private static EconomyResult Fetched(Guid request, byte[] bytes = null, string hash = null) => new EconomyResult
        {
            Operation = EconomyOperation.FetchBlueprint, RequestId = request, Success = true, BlueprintData = bytes ?? CraftBytes, BlueprintEditor = "SPH",
            BlueprintName = "Lifter", BlueprintHash = hash ?? Hex(CraftBytes)
        };

        [TestMethod]
        public void LoadTooledDesign_RefusesWithoutPreconditions()
        {
            Assert.AreEqual(DesignLoadState.Refused, ToolingClient.LoadTooledDesign(Fp, false), "Not ready.");
            Apply(Snapshot());
            Assert.AreEqual(DesignLoadState.Refused, ToolingClient.LoadTooledDesign(Fp, false));
            StringAssert.Contains(ToolingClient.LoadStatus, "Save craft to tooling");
            Apply(WithBlueprint(2));
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            Assert.AreEqual(DesignLoadState.Refused, ToolingClient.LoadTooledDesign(Fp, true));
            StringAssert.Contains(ToolingClient.LoadStatus, "Space Center");
            Assert.AreEqual(0, Commands().Length);
        }

        [TestMethod]
        public void LoadTooledDesign_AsksBeforeReplacingAnEditorCraft()
        {
            Apply(WithBlueprint());
            OpenEditor(Other);
            Assert.AreEqual(DesignLoadState.NeedsConfirm, ToolingClient.LoadTooledDesign(Fp, false));
            Assert.AreEqual(0, Commands().Count(c => c.Operation == EconomyOperation.FetchBlueprint));
            Assert.AreEqual(DesignLoadState.Fetching, ToolingClient.LoadTooledDesign(Fp, true));

            ToolingClient.Clear(); NetworkSender.Sent.Clear();
            Apply(WithBlueprint());
            OpenEditor(Other, parts: 0);
            Assert.AreEqual(DesignLoadState.Fetching, ToolingClient.LoadTooledDesign(Fp, false), "An empty editor needs no confirm.");
        }

        [TestMethod]
        public void LoadTooledDesign_FetchesValidatesWritesLoadsAndCaches()
        {
            Apply(WithBlueprint());
            ToolingClient.TestBlueprintFingerprint = Fp;
            Assert.AreEqual(DesignLoadState.Fetching, ToolingClient.LoadTooledDesign(Fp, false));
            var fetch = Commands().Single();
            Assert.AreEqual(EconomyOperation.FetchBlueprint, fetch.Operation);
            Assert.AreEqual(Fp, fetch.StockFingerprint);
            Assert.AreEqual(DesignLoadState.Fetching, ToolingClient.LoadTooledDesign(Fp, false), "A repeat click while fetching sends nothing.");
            Assert.AreEqual(1, Commands().Length);

            ToolingClient.Receive(Fetched(fetch.RequestId));
            ToolingClient.Tick();
            Assert.AreEqual(DesignLoadState.Loaded, ToolingClient.LoadState);
            Assert.AreEqual(1, ToolingClient.TestWrittenFiles.Count);
            Assert.AreEqual("SPH", ToolingClient.TestLoads.Single().Item2);
            Assert.AreEqual(ToolingClient.TestWrittenFiles[0], ToolingClient.TestLoads.Single().Item1);

            Assert.AreEqual(DesignLoadState.Loaded, ToolingClient.LoadTooledDesign(Fp, false));
            Assert.AreEqual(1, Commands().Length, "The second load uses the session cache.");
            Assert.AreEqual(2, ToolingClient.TestLoads.Count);
        }

        [TestMethod]
        public void LoadTooledDesign_RejectsAHashMismatchWithoutLoading()
        {
            Apply(WithBlueprint());
            ToolingClient.TestBlueprintFingerprint = Fp;
            ToolingClient.LoadTooledDesign(Fp, false);
            var tampered = Encoding.UTF8.GetBytes("ship = Other\n");
            ToolingClient.Receive(Fetched(Commands().Single().RequestId, tampered, Hex(tampered)));
            ToolingClient.Tick();
            Assert.AreEqual(DesignLoadState.Failed, ToolingClient.LoadState);
            StringAssert.Contains(ToolingClient.LoadStatus, "failed validation");
            Assert.AreEqual(0, ToolingClient.TestLoads.Count);
        }

        [TestMethod]
        public void LoadTooledDesign_RejectsAPartListMismatchWithoutLoading()
        {
            Apply(WithBlueprint());
            ToolingClient.TestBlueprintFingerprint = ToolingPolicy.Fingerprint(Other);
            ToolingClient.LoadTooledDesign(Fp, false);
            ToolingClient.Receive(Fetched(Commands().Single().RequestId));
            ToolingClient.Tick();
            Assert.AreEqual(DesignLoadState.Failed, ToolingClient.LoadState);
            Assert.AreEqual(0, ToolingClient.TestLoads.Count);
        }

        [TestMethod]
        public void LoadTooledDesign_RefusesMissingPartsWithoutLoading()
        {
            Apply(WithBlueprint());
            ToolingClient.TestBlueprintFingerprint = Fp;
            ToolingClient.TestMissingParts = new[] { "modTank", "modEngine" };
            ToolingClient.LoadTooledDesign(Fp, false);
            ToolingClient.Receive(Fetched(Commands().Single().RequestId));
            ToolingClient.Tick();
            Assert.AreEqual(DesignLoadState.Refused, ToolingClient.LoadState);
            StringAssert.Contains(ToolingClient.LoadStatus, "Missing parts: modTank, modEngine");
            Assert.AreEqual(0, ToolingClient.TestWrittenFiles.Count);
            Assert.AreEqual(0, ToolingClient.TestLoads.Count);
        }

        [TestMethod]
        public void LateFetch_AsksAgainWhenTheSceneChanged()
        {
            Apply(WithBlueprint());
            ToolingClient.TestBlueprintFingerprint = Fp;
            Assert.AreEqual(DesignLoadState.Fetching, ToolingClient.LoadTooledDesign(Fp, false), "Space Center: no craft to replace.");
            var fetch = Commands().Single();
            OpenEditor(Other); // the player entered the VAB and built something while the fetch was in flight
            ToolingClient.Receive(Fetched(fetch.RequestId));
            ToolingClient.Tick();
            Assert.AreEqual(DesignLoadState.NeedsConfirm, ToolingClient.LoadState);
            Assert.AreEqual(Fp, ToolingClient.LoadingFingerprint);
            Assert.AreEqual(0, ToolingClient.TestLoads.Count, "The editor craft is never replaced without a confirm.");

            Assert.AreEqual(DesignLoadState.Loaded, ToolingClient.LoadTooledDesign(Fp, true));
            Assert.AreEqual(1, ToolingClient.TestLoads.Count);
            Assert.AreEqual(1, Commands().Length, "Confirming uses the cached bytes.");
        }

        [TestMethod]
        public void LateFetch_AsksWhenAnUnconfirmedEditorGotACraft_AndLoadsWhenConfirmed()
        {
            Apply(WithBlueprint());
            ToolingClient.TestBlueprintFingerprint = Fp;
            OpenEditor(Other, parts: 0);
            Assert.AreEqual(DesignLoadState.Fetching, ToolingClient.LoadTooledDesign(Fp, false), "An empty editor needs no confirm.");
            EditorLogic.fetch.ship.parts.Add(new Part());
            ToolingClient.Receive(Fetched(Commands().Single().RequestId));
            ToolingClient.Tick();
            Assert.AreEqual(DesignLoadState.NeedsConfirm, ToolingClient.LoadState);
            Assert.AreEqual(0, ToolingClient.TestLoads.Count);
            ToolingClient.CancelLoadConfirm();
            Assert.AreEqual(DesignLoadState.Idle, ToolingClient.LoadState);

            ToolingClient.Clear(); NetworkSender.Sent.Clear(); ToolingClient.TestLoads.Clear();
            Apply(WithBlueprint());
            OpenEditor(Other);
            Assert.AreEqual(DesignLoadState.Fetching, ToolingClient.LoadTooledDesign(Fp, true));
            ToolingClient.Receive(Fetched(Commands().Single().RequestId));
            ToolingClient.Tick();
            Assert.AreEqual(DesignLoadState.Loaded, ToolingClient.LoadState, "Confirmed in the same scene: loads.");
            Assert.AreEqual(1, ToolingClient.TestLoads.Count);
        }

        [TestMethod]
        public void LoadTooledDesign_ReportsAFailedFetch()
        {
            Apply(WithBlueprint());
            ToolingClient.LoadTooledDesign(Fp, false);
            ToolingClient.Receive(new EconomyResult { Operation = EconomyOperation.FetchBlueprint, RequestId = Commands().Single().RequestId, Success = false, Reason = "Saved craft unavailable; save it to tooling again" });
            var before = ToolingClient.LatestStatus;
            ToolingClient.Tick();
            Assert.AreEqual(DesignLoadState.Failed, ToolingClient.LoadState);
            StringAssert.Contains(ToolingClient.LoadStatus, "save it to tooling again");
            Assert.AreEqual(before, ToolingClient.LatestStatus, "A fetch never changes the economy status line.");
        }
    }
}
