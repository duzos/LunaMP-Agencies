using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Settings.Definition;
using Server.Settings.Structures;
using System;
using System.IO;
using System.Linq;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class AgencyToolingRatesTest
    {
        // probe 100, a science part 300 and one 25-funds inventory item inside the probe.
        private static ToolingManifest Manifest() => new ToolingManifest
        {
            Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 }, new ToolingPart { Name = "science", UnitCost = 300, IsScience = true } },
            Cargo = new[] { new ToolingCargo { Name = "cargo", Count = 1, UnitCost = 25, ContainerPartIndex = 0 } }
        };
        private static EconomyCommand Command(EconomyOperation operation, Guid launchId = default(Guid))
        {
            var manifest = Manifest();
            return new EconomyCommand { Operation = operation, LaunchId = launchId, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) };
        }
        private static Guid Launch(AgencyEconomyTest.Fixture fixture)
        {
            var launchId = Guid.NewGuid();
            var prepared = fixture.Execute(Command(EconomyOperation.PrepareLaunch, launchId));
            Assert.IsTrue(prepared.Success, prepared.Reason);
            var id = Guid.NewGuid(); var raw = AgencyEconomyTest.Proto(id);
            var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
            message.VesselId = id; message.EconomyLaunchId = launchId; message.EconomyLaunchToken = prepared.LaunchToken; message.EconomyManifestIndices = new[] { 0, 1 };
            var registered = AgencyEconomyStore.Register(fixture.Client, message, raw, new Server.System.Vessel.Classes.Vessel(raw));
            Assert.IsTrue(registered.Success, registered.Reason);
            return id;
        }
        private static EconomyCommand Recover(Guid vessel, double factor, double probeStock) => new EconomyCommand
        {
            Operation = EconomyOperation.Recover, VesselId = vessel, RecoveryFactor = factor,
            RecoveredParts = new[] { new RecoveryPart { FlightId = 701, StockValue = probeStock }, new RecoveryPart { FlightId = 702, StockValue = 300 } },
            RecoveredCargo = new[] { new ToolingCargo { Name = "cargo", Count = 1, UnitCost = 25, ContainerFlightId = 701 } }
        };

        [TestMethod]
        public void ServerSettingsDefaultToTheShippedRates()
        {
            var settings = new GeneralSettingsDefinition();
            Assert.AreEqual(ToolingDefaults.ToolingCost, settings.ToolingCostMultiplier); Assert.AreEqual(ToolingDefaults.TooledLaunch, settings.TooledLaunchMultiplier);
            Assert.AreEqual(ToolingDefaults.UntooledLaunch, settings.UntooledLaunchMultiplier); Assert.AreEqual(ToolingDefaults.Combine, settings.ToolingCombineMultiplier);
        }

        [TestMethod]
        public void ConfigWrittenBeforeTheUntooledSettingKeepsItsValuesAndGainsTheDefault()
        {
            var path = Path.GetTempFileName();
            try
            {
                LunaXmlSerializer.WriteToXmlFile(new GeneralSettingsDefinition { ToolingCostMultiplier = 10 }, path);
                var written = File.ReadAllText(path);
                StringAssert.Contains(written, "<UntooledLaunchMultiplier>2</UntooledLaunchMultiplier>");
                var old = string.Join("\n", written.Split('\n').Where(l => !l.Contains("UntooledLaunchMultiplier") && !l.Contains("Launch price multiplier for a craft")));
                File.WriteAllText(path, old);
                var loaded = (GeneralSettingsDefinition)LunaXmlSerializer.ReadXmlFromPath(typeof(GeneralSettingsDefinition), path);
                Assert.AreEqual(10d, loaded.ToolingCostMultiplier, "an operator's configured value is not overwritten");
                Assert.AreEqual(ToolingDefaults.UntooledLaunch, loaded.UntooledLaunchMultiplier);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void UntooledLaunchChargesTheUntooledMultiplierOnPartsOnlyAndRecordsItAsPaid()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                AgencyEconomyTest.Fixture.UseRates(ToolingRates.Default);
                var id = Launch(fixture);
                // 100 x 2 for the probe, science 300 and inventory 25 at face value.
                Assert.AreEqual(50000d - (100 * ToolingDefaults.UntooledLaunch + 300 + 25), fixture.Snapshot.Funds);
                var parts = fixture.Snapshot.Vessels.Single(v => v.VesselId == id).Parts.ToDictionary(p => p.FlightId);
                Assert.AreEqual(ToolingDefaults.UntooledLaunch, parts[701].Multiplier); Assert.AreEqual(100 * ToolingDefaults.UntooledLaunch, parts[701].MaximumRefund);
                Assert.AreEqual(1d, parts[702].Multiplier); Assert.AreEqual(300d, parts[702].MaximumRefund);
            }
        }

        [TestMethod]
        public void TooledLaunchChargesTheTooledMultiplierAndRecordsIt()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                AgencyEconomyTest.Fixture.UseRates(ToolingRates.Default);
                Assert.IsTrue(fixture.Execute(Command(EconomyOperation.Tool)).Success);
                Assert.AreEqual(50000d - 100 * ToolingDefaults.ToolingCost, fixture.Snapshot.Funds);
                var id = Launch(fixture);
                Assert.AreEqual(50000d - 100 * ToolingDefaults.ToolingCost - (100 * ToolingDefaults.TooledLaunch + 300 + 25), fixture.Snapshot.Funds, 1e-9);
                var parts = fixture.Snapshot.Vessels.Single(v => v.VesselId == id).Parts.ToDictionary(p => p.FlightId);
                Assert.AreEqual(ToolingDefaults.TooledLaunch, parts[701].Multiplier); Assert.AreEqual(1d, parts[702].Multiplier);
            }
        }

        [TestMethod]
        public void RecoveryAfterAnUntooledLaunchNeverRefundsMoreThanWasPaid()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                AgencyEconomyTest.Fixture.UseRates(ToolingRates.Default);
                var id = Launch(fixture);
                var paid = 50000d - fixture.Snapshot.Funds;
                // A stock price above the one paid for (client says 1000, launch paid 100 x 2) is capped at the paid amount.
                var recovery = fixture.Execute(Recover(id, 1, 1000));
                Assert.IsTrue(recovery.Success, recovery.Reason);
                Assert.AreEqual(50000d, fixture.Snapshot.Funds, 1e-9, "full recovery returns exactly what was paid, never more (paid " + paid + ")");
            }
        }

        [TestMethod]
        public void PartialRecoveryAfterAnUntooledLaunchScalesWithTheFactorAndStaysUnderThePaidAmount()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                AgencyEconomyTest.Fixture.UseRates(ToolingRates.Default);
                var id = Launch(fixture);
                var afterLaunch = fixture.Snapshot.Funds;
                var recovery = fixture.Execute(Recover(id, .5, 100));
                Assert.IsTrue(recovery.Success, recovery.Reason);
                // probe 100 x .5 x 2 (cap 200), science 300 x .5, inventory 25 x .5
                Assert.AreEqual(afterLaunch + 100 + 150 + 12.5, fixture.Snapshot.Funds, 1e-9);
                Assert.IsTrue(fixture.Snapshot.Funds < 50000d);
            }
        }

        [TestMethod]
        public void NonFiniteUntooledMultiplierKeepsTheEconomyClosedUntilItIsFixed()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var settings = GeneralSettings.SettingsStore; var old = settings.UntooledLaunchMultiplier;
                try
                {
                    settings.UntooledLaunchMultiplier = double.NaN; AgencyEconomyStore.Load();
                    Assert.IsFalse(AgencyEconomyStore.Ready);
                    settings.UntooledLaunchMultiplier = old; AgencyEconomyStore.Load();
                    Assert.IsTrue(AgencyEconomyStore.Ready);
                }
                finally { settings.UntooledLaunchMultiplier = old; }
            }
        }
    }
}
