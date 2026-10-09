using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace ServerTest.Agency
{
    /// <summary>Plan 40 slice S1: building stock, launching from it, and every path that gives a unit back.</summary>
    [TestClass, DoNotParallelize]
    public class AgencyStockTest
    {
        internal const string Craft = "ship = Probe\ntype = VAB\nPART\n{\npart = probe_1\n}\n";
        internal static ToolingManifest Probe(params ToolingCargo[] cargo) => new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } }, Cargo = cargo };
        internal static string ProbeFingerprint => ToolingPolicy.Fingerprint(Probe());
        private static ToolingCargo Kit() => new ToolingCargo { Name = "kit", Count = 1, UnitCost = 40, ContainerPartIndex = 0 };

        internal static EconomyDocument Document() => (EconomyDocument)typeof(AgencyEconomyStore).GetField("_document", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        internal static EconomyResult Tool(Func<EconomyCommand, EconomyResult> run, ToolingManifest manifest, byte[] blueprint = null, string editor = "VAB", string name = "Probe")
            => run(new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest), DesignName = name, BlueprintData = blueprint ?? Array.Empty<byte>(), BlueprintEditor = editor });

        internal static StockQuote ServerQuote(Guid agency, string fp, int units)
        {
            var design = AgencyEconomyStore.Snapshot(agency).Designs.Single(d => d.Fingerprint == fp);
            var settings = GeneralSettings.SettingsStore;
            return StockPolicy.Quote(design, units, AgencyEconomyTest.Fixture.SettingsRates(), StockRates.Normalize(settings.StockMaxDiscount, settings.StockFullDiscountUnits));
        }

        internal static EconomyCommand BuildCommand(Guid agency, string fp, int units)
        {
            var career = GeneralSettings.SettingsStore.GameMode == GameMode.Career;
            var quote = ServerQuote(agency, fp, units);
            return new EconomyCommand { Operation = EconomyOperation.BuildStock, StockFingerprint = fp, StockUnits = units, ExpectedCharge = career ? quote.Total : 0 };
        }

        private static EconomyResult Build(AgencyTradeTest.Fixture f, int units, string fp = null) => f.Economy.Execute(BuildCommand(f.Economy.Client.AgencyId, fp ?? ProbeFingerprint, units));

        private static EconomyResult Prepare(Func<EconomyCommand, EconomyResult> run, ToolingManifest manifest, Guid lot, out Guid launch, Guid voucher = default(Guid))
        {
            launch = Guid.NewGuid();
            return run(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = launch, StockLotId = lot, VoucherId = voucher, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
        }

        private static Guid Register(ClientStructure client, EconomyResult prepared, Guid launch, uint uid)
        {
            var id = Guid.NewGuid();
            var raw = AgencyTradeTest.Proto(id, uid);
            var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
            message.VesselId = id; message.EconomyLaunchId = launch; message.EconomyLaunchToken = prepared.LaunchToken; message.EconomyManifestIndices = new[] { 0 };
            var registered = AgencyEconomyStore.Register(client, message, raw, new Server.System.Vessel.Classes.Vessel(raw));
            Assert.IsTrue(registered.Success, registered.Reason);
            return id;
        }

        private static DesignStockLot[] Lots(AgencyTradeTest.Fixture f) => f.Economy.Snapshot.Stock;
        private static int Units(AgencyTradeTest.Fixture f) => Lots(f).Sum(l => l.Units);
        private static double Funds(AgencyTradeTest.Fixture f) => f.Economy.Snapshot.Funds;
        private static int Slots(Guid agency) => AgencyEconomyStore.StockLotSlots(Document(), agency);

        private static AgencyTradeTest.Fixture Start()
        {
            var f = new AgencyTradeTest.Fixture();
            Assert.IsTrue(Tool(f.Economy.Execute, Probe()).Success);
            return f;
        }

        /// <summary>Moves units of the seller's stock into an open Stock offer directly in the live document, as a stock TradeCreate would.</summary>
        internal static StoredTradeOffer InjectEscrow(AgencyTradeTest.Fixture f, string fp, int units)
        {
            var document = Document();
            var seller = document.Agencies[f.Economy.Client.AgencyId];
            var escrow = StockPolicy.Take(seller.Stock, fp, units).ToList();
            var blueprint = Encoding.UTF8.GetBytes(Craft);
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(blueprint)).Replace("-", "").ToLowerInvariant();
            var offer = new TradeOffer
            {
                OfferId = Guid.NewGuid(), SellerAgencyId = f.Economy.Client.AgencyId, BuyerAgencyId = f.Buyer.AgencyId, Revision = 1, Status = TradeOfferStatus.Open,
                ExpiresUtcTicks = DateTime.UtcNow.AddHours(24).Ticks, DesignMode = TradeDesignMode.Stock, DesignFingerprint = fp, BlueprintName = "Probe", Editor = "VAB",
                StockUnits = units, StockPrepaidTotal = escrow.Sum(l => l.Units * l.PrepaidPerUnit), BuyerFunds = 1
            };
            var stored = new StoredTradeOffer
            {
                Offer = offer, SellerOwner = AgencyStore.Agencies[offer.SellerAgencyId].OwnerUniqueId, BuyerOwner = AgencyStore.Agencies[offer.BuyerAgencyId].OwnerUniqueId,
                Blueprint = blueprint, BlueprintHash = hash, Escrow = escrow
            };
            document.TradeOffers[offer.OfferId] = stored;
            return stored;
        }

        [DataTestMethod]
        [DataRow("before-document")]
        [DataRow("committed")]
        [DataRow("projections-written")]
        public void TooledBuildChargesExactlyTheVolumePriceAndFaultsLeaveNothingPartial(string faultBoundary)
        {
            using (var f = new AgencyTradeTest.Fixture())
            {
                var untooled = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.BuildStock, StockFingerprint = ProbeFingerprint, StockUnits = 10, ExpectedCharge = 70 });
                Assert.IsFalse(untooled.Success);
                StringAssert.Contains(untooled.Reason, "tooled");
                Assert.IsTrue(Tool(f.Economy.Execute, Probe()).Success);
                var before = Funds(f);
                var command = BuildCommand(f.Economy.Client.AgencyId, ProbeFingerprint, 10);
                // 30% off the 0.1x tooled launch: 100 x 0.07 = 7 per unit.
                Assert.AreEqual(70d, command.ExpectedCharge, 1e-9);
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == faultBoundary) throw new IOException("injected"); };
                var faulted = f.Economy.Execute(command);
                Assert.IsFalse(faulted.Success);
                AgencyEconomyStore.PersistenceCheckpoint = null;
                AgencyVesselMap.Load(); AgencyVesselMap.RecoverJournal(); AgencyEconomyStore.Load(); f.Refresh();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                if (faultBoundary == "before-document")
                {
                    Assert.AreEqual(before, Funds(f));
                    Assert.AreEqual(0, Lots(f).Length);
                    var built = Build(f, 10);
                    Assert.IsTrue(built.Success, built.Reason);
                    StringAssert.Contains(built.Reason, "Built 10");
                }
                Assert.AreEqual(before - 70, Funds(f), 1e-9);
                var lot = Lots(f).Single();
                Assert.AreEqual(10, lot.Units);
                Assert.AreEqual(7d, lot.PrepaidPerUnit, 1e-9);
                Assert.AreEqual(.07, lot.LaunchMultiplier, 1e-12);
                Assert.IsTrue(lot.FundsBuilt);
                Assert.AreEqual(f.Economy.Client.AgencyId, lot.BuilderAgencyId);
                Assert.AreEqual(Guid.Empty, lot.SourceAgencyId);
                Assert.AreEqual(10, f.Economy.Snapshot.StockHeldByFingerprint[ProbeFingerprint]);
            }
        }

        [TestMethod]
        public void ScienceIsNotDiscountedAndBuildingMoreNeverCostsLessInTotal()
        {
            using (var f = new AgencyTradeTest.Fixture())
            {
                var manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 }, new ToolingPart { Name = "lab", UnitCost = 50, IsScience = true } } };
                Assert.IsTrue(Tool(f.Economy.Execute, manifest).Success);
                var fp = ToolingPolicy.Fingerprint(manifest);
                var previous = 0d;
                for (var n = 1; n <= 12; n++)
                {
                    var total = ServerQuote(f.Economy.Client.AgencyId, fp, n).Total;
                    Assert.IsTrue(total > previous, "total(" + n + ") must exceed total(" + (n - 1) + ")");
                    previous = total;
                }
                var before = Funds(f);
                Assert.IsTrue(Build(f, 10, fp).Success);
                Assert.AreEqual(before - 10 * (50 + 7), Funds(f), 1e-9);
            }
        }

        [TestMethod]
        public void InvalidBuildsAreRejectedWithBalancesUnchanged()
        {
            using (var f = Start())
            {
                var agency = f.Economy.Client.AgencyId;
                var before = Funds(f);
                var stale = BuildCommand(agency, ProbeFingerprint, 10);
                stale.ExpectedCharge += 1;
                StringAssert.Contains(f.Economy.Execute(stale).Reason, "Price changed");
                foreach (var units in new[] { 0, 101 })
                    Assert.IsFalse(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.BuildStock, StockFingerprint = ProbeFingerprint, StockUnits = units }).Success);
                AgencyEconomyStore.SetBalance(agency, 5, null);
                var poor = f.Economy.Execute(BuildCommand(agency, ProbeFingerprint, 10));
                Assert.IsFalse(poor.Success);
                StringAssert.Contains(poor.Reason, "Insufficient funds");
                AgencyEconomyStore.SetBalance(agency, before, null);
                Assert.AreEqual(before, Funds(f));
                Assert.AreEqual(0, Lots(f).Length);

                // The 999 cap counts lot units, escrowed units and reserved (Prepared) units.
                for (var i = 0; i < 9; i++) Assert.IsTrue(Build(f, 100).Success);
                Assert.IsTrue(Build(f, 98).Success);
                InjectEscrow(f, ProbeFingerprint, 5);
                var prepared = Prepare(f.Economy.Execute, Probe(), Lots(f).Single().LotId, out _);
                Assert.IsTrue(prepared.Success, prepared.Reason);
                Assert.AreEqual(992, Units(f));
                Assert.AreEqual(998, f.Economy.Snapshot.StockHeldByFingerprint[ProbeFingerprint]);
                Assert.IsTrue(Build(f, 1).Success);
                var funds = Funds(f);
                var capped = Build(f, 1);
                Assert.IsFalse(capped.Success);
                StringAssert.Contains(capped.Reason, "999");
                Assert.AreEqual(funds, Funds(f));
            }
        }

        [TestMethod]
        public void BuildsOutsideCareerChargeNothingAndStoreNoPrepayment()
        {
            using (var f = Start())
            {
                GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
                var priced = new EconomyCommand { Operation = EconomyOperation.BuildStock, StockFingerprint = ProbeFingerprint, StockUnits = 10, ExpectedCharge = 70 };
                Assert.IsFalse(f.Economy.Execute(priced).Success, "Outside Career the client must expect a charge of 0.");
                var before = Funds(f);
                var built = Build(f, 10);
                Assert.IsTrue(built.Success, built.Reason);
                Assert.AreEqual(before, Funds(f));
                var lot = Lots(f).Single();
                Assert.AreEqual(0d, lot.PrepaidPerUnit);
                Assert.IsFalse(lot.FundsBuilt);
                Assert.AreEqual(.07, lot.LaunchMultiplier, 1e-12);
            }
        }

        [TestMethod]
        public void ReplayingABuildRequestBuildsOnce()
        {
            using (var f = Start())
            {
                var command = BuildCommand(f.Economy.Client.AgencyId, ProbeFingerprint, 4);
                Assert.IsTrue(f.Economy.Execute(command).Success);
                Assert.IsTrue(f.Economy.Execute(command).Success);
                Assert.AreEqual(4, Units(f));
            }
        }

        [TestMethod]
        public void StockLaunchChargesInventoryOnlyAndEveryReleasePathReturnsExactlyOneUnit()
        {
            using (var f = Start())
            {
                var now = DateTime.UtcNow;
                AgencyEconomyStore.UtcNow = () => now;
                Assert.IsTrue(Build(f, 10).Success);
                var lot = Lots(f).Single().LotId;
                var funds = Funds(f);
                var manifest = Probe(Kit());
                var prepared = Prepare(f.Economy.Execute, manifest, lot, out var launch);
                Assert.IsTrue(prepared.Success, prepared.Reason);
                Assert.AreEqual(40d, prepared.Quote.LaunchCost, 1e-9, "The prepaid 7 covers the parts; only the kit is charged.");
                Assert.AreEqual(funds - 40, Funds(f), 1e-9);
                Assert.AreEqual(9, Units(f));
                Assert.AreEqual(10, f.Economy.Snapshot.StockHeldByFingerprint[ProbeFingerprint], "A reserved unit still counts as held.");

                Assert.IsTrue(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = launch }).Success);
                Assert.AreEqual(10, Units(f));
                Assert.AreEqual(funds, Funds(f), 1e-9);
                Assert.AreEqual(lot, Lots(f).Single().LotId, "The unit merges back into its own row.");

                Assert.IsTrue(Prepare(f.Economy.Execute, manifest, lot, out _).Success);
                AgencyEconomyStore.CancelPending(f.Economy.Client);
                Assert.AreEqual(10, Units(f));
                f.Refresh();

                Assert.IsTrue(Prepare(f.Economy.Execute, manifest, lot, out _).Success);
                now = now.AddSeconds(61);
                AgencyEconomyStore.CancelPending();
                Assert.AreEqual(10, Units(f));

                Assert.IsTrue(Prepare(f.Economy.Execute, manifest, lot, out _).Success);
                AgencyEconomyStore.Load(); f.Refresh();
                Assert.AreEqual(10, Units(f));
                Assert.AreEqual(funds, Funds(f), 1e-9);

                var final = Prepare(f.Economy.Execute, manifest, lot, out var finalLaunch);
                var vessel = Register(f.Economy.Client, final, finalLaunch, 901);
                Assert.AreEqual(9, Units(f), "Register keeps the unit consumed.");
                Assert.AreEqual(.07, f.Economy.Snapshot.Vessels.Single(v => v.VesselId == vessel).Parts.Single().Multiplier, 1e-12);
                var beforeRecovery = Funds(f);
                var recovered = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Recover, VesselId = vessel, RecoveryFactor = 1, RecoveredParts = new[] { new RecoveryPart { FlightId = 901, StockValue = 1000 } }, RecoveredCargo = new[] { new ToolingCargo { Name = "kit", Count = 1, UnitCost = 40 } } });
                Assert.IsTrue(recovered.Success, recovered.Reason);
                Assert.AreEqual(47d, Funds(f) - beforeRecovery, 1e-9, "Recovery returns at most the 7 prepaid plus the 40 kit.");
            }
        }

        [TestMethod]
        public void BuildLaunchRecoverNeverAddsFunds()
        {
            using (var f = Start())
            {
                var start = Funds(f);
                Assert.IsTrue(Build(f, 10).Success);
                var prepared = Prepare(f.Economy.Execute, Probe(), Lots(f).Single().LotId, out var launch);
                var vessel = Register(f.Economy.Client, prepared, launch, 902);
                Assert.IsTrue(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Recover, VesselId = vessel, RecoveryFactor = 1, RecoveredParts = new[] { new RecoveryPart { FlightId = 902, StockValue = 100000 } } }).Success);
                Assert.IsTrue(Funds(f) <= start - 63 + 1e-9, "Nine units are still paid for; the recovered one returned at most its prepayment.");
            }
        }

        [TestMethod]
        public void RevertReturnsTheUnitAndTopUpButRevertLaunchKeepsItConsumed()
        {
            using (var f = Start())
            {
                Assert.IsTrue(Build(f, 3).Success);
                var lot = Lots(f).Single().LotId;
                var funds = Funds(f);
                var first = Prepare(f.Economy.Execute, Probe(Kit()), lot, out var launch);
                Register(f.Economy.Client, first, launch, 911);
                Assert.AreEqual(funds - 40, Funds(f), 1e-9);
                var reverted = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = launch });
                Assert.IsTrue(reverted.Success, reverted.Reason);
                Assert.AreEqual(3, Units(f));
                Assert.AreEqual(funds, Funds(f), 1e-9, "Only the 40 top-up comes back as funds; the unit comes back as a unit.");

                var second = Prepare(f.Economy.Execute, Probe(), lot, out var secondLaunch);
                Register(f.Economy.Client, second, secondLaunch, 912);
                var kept = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.RevertLaunch, LaunchId = secondLaunch });
                Assert.IsTrue(kept.Success, kept.Reason);
                Assert.AreEqual(2, Units(f));
            }
        }

        [TestMethod]
        public void InvalidStockPreparesAreRejected()
        {
            using (var f = Start())
            {
                Assert.IsTrue(Build(f, 1).Success);
                var lot = Lots(f).Single().LotId;
                var both = Prepare(f.Economy.Execute, Probe(), lot, out _, Guid.NewGuid());
                StringAssert.Contains(both.Reason, "either a voucher or stock");
                var foreign = Prepare(f.Buy, Probe(), lot, out _);
                StringAssert.Contains(foreign.Reason, "No stock left", "Another agency's lot is never found.");
                var other = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "other", UnitCost = 100 } } };
                StringAssert.Contains(Prepare(f.Economy.Execute, other, lot, out _).Reason, "does not match");
                Assert.AreEqual(1, Units(f));
                Assert.IsTrue(Prepare(f.Economy.Execute, Probe(), lot, out _).Success);
                Assert.AreEqual(0, Lots(f).Length, "A lot that reaches 0 is removed at once.");
                StringAssert.Contains(Prepare(f.Economy.Execute, Probe(), lot, out _).Reason, "No stock left");
            }
        }

        [TestMethod]
        public void TwoPilotsOfOneAgencyRaceForTheLastUnitAndOnlyOneWins()
        {
            using (var f = Start())
            {
                Assert.IsTrue(Build(f, 1).Success);
                var lot = Lots(f).Single().LotId;
                var second = (ClientStructure)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f.Economy.Client, null);
                typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(second, new ConcurrentQueue<IServerMessageBase>());
                second.UniqueIdentifier = "second-pilot"; second.PlayerName = "SecondPilot";
                ServerContext.Clients[new IPEndPoint(IPAddress.Loopback, 32353)] = second;
                AgencyStore.Agencies[f.Economy.Client.AgencyId].Members.Add(new Server.Agency.Agency.Member { UniqueId = second.UniqueIdentifier, DisplayName = second.PlayerName });
                AgencyEconomyStore.SendTo(second);
                var snapshot = second.SendMessageQueue.Select(m => m.Data).OfType<AgencyEconomySnapshotMsgData>().Last().Snapshot;
                var sequence = snapshot.LastSequence;
                EconomyResult RunSecond(EconomyCommand c) { c.RequestId = Guid.NewGuid(); c.SessionId = snapshot.SessionId; c.Sequence = ++sequence; return AgencyEconomyStore.Execute(second, c); }
                var a = Prepare(f.Economy.Execute, Probe(), lot, out _);
                var b = Prepare(RunSecond, Probe(), lot, out _);
                Assert.AreEqual(1, new[] { a, b }.Count(r => r.Success));
                StringAssert.Contains(b.Reason, "No stock left");
            }
        }

        [TestMethod]
        public void AnEmptiedLotIsRecreatedWithTheLaunchTermsOrMergedWithoutChangingItsId()
        {
            using (var f = Start())
            {
                Assert.IsTrue(Build(f, 1).Success);
                var original = Lots(f).Single();
                var prepared = Prepare(f.Economy.Execute, Probe(), original.LotId, out var launch);
                Assert.IsTrue(prepared.Success);
                Assert.AreEqual(0, Lots(f).Length);
                Assert.AreEqual(original.LotId, Document().Launches[launch].Stock.LotId);
                Assert.IsTrue(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = launch }).Success);
                var back = Lots(f).Single();
                Assert.AreNotEqual(original.LotId, back.LotId, "The returned unit is a fresh row.");
                Assert.IsTrue(StockPolicy.SameTerms(original, back));
                Assert.AreEqual(1, back.Units);

                // With a same-terms row present the unit merges and the row keeps its id.
                Assert.IsTrue(Prepare(f.Economy.Execute, Probe(), back.LotId, out var again).Success);
                Assert.IsTrue(Build(f, 1).Success);
                var target = Lots(f).Single();
                Assert.IsTrue(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = again }).Success);
                Assert.AreEqual(target.LotId, Lots(f).Single().LotId);
                Assert.AreEqual(2, Lots(f).Single().Units);
            }
        }

        [TestMethod]
        public void RevertAfterTheRemainingUnitsWereSoldReturnsAFreshRowWithTheLaunchTerms()
        {
            using (var f = Start())
            {
                Assert.IsTrue(Build(f, 3).Success);
                var lot = Lots(f).Single();
                var prepared = Prepare(f.Economy.Execute, Probe(Kit()), lot.LotId, out var launch);
                Register(f.Economy.Client, prepared, launch, 921);
                // The remaining two units leave with an accepted stock sale: the launch's source row is gone.
                var document = Document();
                var sold = StockPolicy.Take(document.Agencies[f.Economy.Client.AgencyId].Stock, ProbeFingerprint, 2);
                foreach (var row in sold) { row.LotId = Guid.NewGuid(); row.SourceAgencyId = f.Economy.Client.AgencyId; StockPolicy.Merge(AgencyEconomyStoreAgency(document, f.Buyer.AgencyId).Stock, row); }
                Assert.AreEqual(0, Lots(f).Length);
                var funds = Funds(f);
                var reverted = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = launch });
                Assert.IsTrue(reverted.Success, reverted.Reason);
                var back = Lots(f).Single();
                Assert.AreEqual(1, back.Units);
                Assert.AreNotEqual(lot.LotId, back.LotId);
                Assert.IsTrue(StockPolicy.SameTerms(lot, back));
                Assert.AreEqual(funds + 40, Funds(f), 1e-9);
            }
        }

        private static EconomyAgency AgencyEconomyStoreAgency(EconomyDocument document, Guid id)
        {
            if (!document.Agencies.TryGetValue(id, out var agency)) document.Agencies[id] = agency = new EconomyAgency { Funds = AgencyStore.Agencies[id].Funds, Science = AgencyStore.Agencies[id].Science };
            return agency;
        }

        [TestMethod]
        public void HeldCountIncludesRevertibleRegisteredStockLaunches()
        {
            using (var f = Start())
            {
                for (var i = 0; i < 9; i++) Assert.IsTrue(Build(f, 100).Success);
                Assert.IsTrue(Build(f, 99).Success);
                Assert.AreEqual(1, Lots(f).Length, "Equal terms merge.");
                var prepared = Prepare(f.Economy.Execute, Probe(), Lots(f).Single().LotId, out var launch);
                Register(f.Economy.Client, prepared, launch, 931);
                Assert.AreEqual(998, Units(f));
                Assert.AreEqual(999, f.Economy.Snapshot.StockHeldByFingerprint[ProbeFingerprint]);
                Assert.IsFalse(Build(f, 1).Success, "The registered launch can still be reverted, so its unit is held.");
            }
        }

        private static double Multiplier(int i) => .1 + .001 * (i + 1);

        /// <summary>Builds one distinct-term row (its own TooledLaunch multiplier) and returns it.</summary>
        private static DesignStockLot BuildRow(AgencyTradeTest.Fixture f, int i, int units)
        {
            var rates = AgencyEconomyTest.Fixture.SettingsRates();
            AgencyEconomyTest.Fixture.UseRates(new ToolingRates(rates.Tooling, Multiplier(i), rates.UntooledLaunch, rates.Combine));
            var before = Lots(f).Select(l => l.LotId).ToArray();
            var built = Build(f, units);
            Assert.IsTrue(built.Success, built.Reason);
            return Lots(f).Single(l => !before.Contains(l.LotId));
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void CombinedLotBoundRefusesGrowthButNeverRefusesAReturnOrARevert(bool fundsBuilt)
        {
            using (var f = Start())
            {
                var agency = f.Economy.Client.AgencyId;
                var now = DateTime.UtcNow;
                AgencyEconomyStore.UtcNow = () => now;
                // A registered launch whose source row is gone: its revert must not need a new row.
                if (!fundsBuilt) GeneralSettings.SettingsStore.GameMode = GameMode.Sandbox;
                var lone = BuildRow(f, 100, 1);
                GeneralSettings.SettingsStore.GameMode = GameMode.Career;
                var loneLaunched = Prepare(f.Economy.Execute, Probe(), lone.LotId, out var loneLaunch);
                Assert.IsTrue(loneLaunched.Success, loneLaunched.Reason);
                if (!fundsBuilt) Assert.AreEqual(100 * Multiplier(100), loneLaunched.Quote.LaunchCost, 1e-9, "A Sandbox-built unit launched in Career pays S + cargo + C x multiplier.");
                Register(f.Economy.Client, loneLaunched, loneLaunch, 941);

                for (var i = 0; i < 62; i++) BuildRow(f, i, 2);
                var escrowed = InjectEscrow(f, ProbeFingerprint, 2);
                Assert.AreEqual(1, escrowed.Escrow.Count);
                var partial = Lots(f).First(l => l.Units == 2);
                var reserved = Prepare(f.Economy.Execute, Probe(), partial.LotId, out var reservedLaunch);
                Assert.IsTrue(reserved.Success, reserved.Reason);
                BuildRow(f, 62, 2);
                Assert.AreEqual(64, Slots(agency));

                var rates = AgencyEconomyTest.Fixture.SettingsRates();
                AgencyEconomyTest.Fixture.UseRates(new ToolingRates(rates.Tooling, Multiplier(63), rates.UntooledLaunch, rates.Combine));
                var funds = Funds(f);
                StringAssert.Contains(Build(f, 1).Reason, "Too many stock batches");
                Assert.AreEqual(funds, Funds(f));
                var growing = Lots(f).First(l => l.Units == 2);
                StringAssert.Contains(Prepare(f.Economy.Execute, Probe(), growing.LotId, out _).Reason, "Too many stock batches");
                var emptying = Lots(f).Single(l => l.LotId == partial.LotId);
                Assert.AreEqual(1, emptying.Units);
                Assert.IsTrue(Prepare(f.Economy.Execute, Probe(), emptying.LotId, out _).Success, "An emptying Prepare moves the slot to the launch.");
                Assert.AreEqual(64, Slots(agency));

                // Returns never fail on the bound.
                AgencyEconomyStore.CancelPending(f.Economy.Client);
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.IsTrue(Slots(agency) <= 64);
                f.Refresh();
                Assert.IsTrue(Prepare(f.Economy.Execute, Probe(), Lots(f).First().LotId, out _).Success);
                now = now.AddSeconds(61);
                AgencyEconomyStore.CancelPending();
                Assert.IsTrue(Slots(agency) <= 64);
                Assert.IsTrue(Prepare(f.Economy.Execute, Probe(), Lots(f).First().LotId, out _).Success);
                AgencyEconomyStore.Load(); f.Refresh();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.IsTrue(Slots(agency) <= 64);
                while (Slots(agency) < 64) BuildRow(f, 63 + Slots(agency), 1);

                // Slots full and no row with the lone launch's terms: the revert credits the prepaid price instead of adding a row.
                var rows = Lots(f).Length;
                funds = Funds(f);
                var reverted = f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = loneLaunch });
                Assert.IsTrue(reverted.Success, reverted.Reason);
                Assert.AreEqual(rows, Lots(f).Length);
                Assert.AreEqual(funds + loneLaunched.Quote.LaunchCost + (fundsBuilt ? lone.PrepaidPerUnit : 0), Funds(f), 1e-9);
                Assert.AreEqual(64, Slots(agency));
            }
        }

        [TestMethod]
        public void RevertWithFullSlotsMergesIntoASameTermsRowAndCreditsNothing()
        {
            using (var f = Start())
            {
                var agency = f.Economy.Client.AgencyId;
                var kept = BuildRow(f, 100, 2);
                var launched = Prepare(f.Economy.Execute, Probe(), kept.LotId, out var launch);
                Register(f.Economy.Client, launched, launch, 951);
                for (var i = 0; i < 63; i++) BuildRow(f, i, 1);
                Assert.AreEqual(64, Slots(agency));
                var funds = Funds(f);
                Assert.IsTrue(f.Economy.Execute(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = launch }).Success);
                Assert.AreEqual(2, Lots(f).Single(l => l.LotId == kept.LotId).Units);
                Assert.AreEqual(funds + launched.Quote.LaunchCost, Funds(f), 1e-9);
            }
        }

        [TestMethod]
        public void SweepFailureIsLoggedOnceBacksOffAndNeverThrows()
        {
            using (var f = Start())
            {
                var now = DateTime.UtcNow;
                AgencyEconomyStore.UtcNow = () => now;
                Assert.IsTrue(Build(f, 2).Success);
                var prepared = Prepare(f.Economy.Execute, Probe(), Lots(f).Single().LotId, out var launch);
                Assert.IsTrue(prepared.Success);
                now = now.AddSeconds(61);
                var document = Document();
                var attempts = 0;
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "before-document") { attempts++; throw new IOException("disk full"); } };
                var logged = AgencyEconomyStore.MaintenanceErrorsLogged;
                AgencyEconomyStore.MaintenanceSweep();
                AgencyEconomyStore.CancelPending(f.Economy.Client);
                AgencyEconomyStore.CancelPending();
                AgencyEconomyStore.CancelPending();
                Assert.AreEqual(1, attempts, "After a failed Persist the sweep backs off.");
                Assert.AreEqual(logged + 1, AgencyEconomyStore.MaintenanceErrorsLogged);
                Assert.AreSame(document, Document());
                Assert.AreEqual(LaunchState.Prepared, Document().Launches[launch].State);
                Assert.IsTrue(AgencyEconomyStore.Ready);

                AgencyEconomyStore.PersistenceCheckpoint = null;
                now = now.AddSeconds(61);
                AgencyEconomyStore.MaintenanceSweep();
                Assert.IsTrue(!Document().Launches.TryGetValue(launch, out var swept) || swept.State == LaunchState.Cancelled);
                Assert.AreEqual(2, Units(f));
            }
        }

        private static JObject Written() => JObject.Parse(File.ReadAllText(AgencyEconomyStore.FilePath));

        [TestMethod]
        public void LegacyDocumentLoadsWithEmptyStockAndCorruptStockFailsClosed()
        {
            using (var f = Start())
            {
                Assert.IsTrue(Build(f, 3).Success);
                InjectEscrow(f, ProbeFingerprint, 1);
                Assert.IsTrue(Build(f, 1).Success, "Persists the injected escrow.");
                var good = Written();

                var legacy = (JObject)good.DeepClone();
                legacy["Version"] = 2;
                foreach (var agency in legacy["Agencies"].Children<JProperty>().Select(p => (JObject)p.Value)) { agency.Remove("Stock"); agency.Remove("Blueprints"); }
                foreach (var offer in legacy["TradeOffers"].Children<JProperty>().ToArray()) offer.Remove();
                File.WriteAllText(AgencyEconomyStore.FilePath, legacy.ToString());
                AgencyEconomyStore.Load(); f.Refresh();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.AreEqual(0, Lots(f).Length);
                Assert.IsTrue(Tool(f.Economy.Execute, new ToolingManifest { Parts = new[] { new ToolingPart { Name = "x", UnitCost = 1 } } }).Success);
                Assert.AreEqual(3, (int)Written()["Version"]);

                void Corrupt(Action<JObject> change)
                {
                    var json = (JObject)good.DeepClone();
                    change(json);
                    File.WriteAllText(AgencyEconomyStore.FilePath, json.ToString());
                    AgencyEconomyStore.Load();
                    Assert.IsFalse(AgencyEconomyStore.Ready);
                }
                JObject Lot(JObject json) => (JObject)json["Agencies"].Children<JProperty>().Select(p => p.Value["Stock"]).First(s => s.HasValues).First;
                JObject EscrowRow(JObject json) => (JObject)json["TradeOffers"].Children<JProperty>().Single().Value["Escrow"].First;
                Corrupt(json => Lot(json)["Units"] = 0);
                Corrupt(json => Lot(json)["Units"] = -1);
                Corrupt(json => EscrowRow(json)["LotId"] = Lot(json)["LotId"]);
                Corrupt(json => Lot(json)["PrepaidPerUnit"] = -1);
                Corrupt(json =>
                {
                    var stock = (JArray)json["Agencies"].Children<JProperty>().Select(p => p.Value["Stock"]).First(s => s.HasValues);
                    var template = (JObject)stock.First;
                    for (var i = 0; i < 64; i++) { var row = (JObject)template.DeepClone(); row["LotId"] = Guid.NewGuid(); row["LaunchMultiplier"] = 1 + i; stock.Add(row); }
                });
                File.WriteAllText(AgencyEconomyStore.FilePath, good.ToString());
                AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready);
            }
        }
    }
}
