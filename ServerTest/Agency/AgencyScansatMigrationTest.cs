using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.IO;

namespace ServerTest.Agency
{
    [TestClass]
    public class AgencyScansatMigrationTest
    {
        private string _scenariosPath;

        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "LMPTestUniverse_" + Guid.NewGuid());
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            AgencyStore.AgenciesPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies");
            Directory.CreateDirectory(AgencyStore.AgenciesPath);
            _scenariosPath = ScenarioSystem.ScenariosPath = Path.Combine(ServerContext.UniverseDirectory, "Scenarios");
            Directory.CreateDirectory(_scenariosPath);

            AgencyStore.Agencies.Clear();
            GeneralSettings.SettingsStore.AgencyScansatPerAgency = true;
        }

        [TestCleanup]
        public void Cleanup()
        {
            GeneralSettings.SettingsStore.AgencyScansatPerAgency = false;
            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, true);
        }

        [TestMethod]
        public void Migration_SeedsOnlyOldestAgency()
        {
            // Three agencies, created at different times.
            var (_, _, oldest) = AgencySystem.CreateAgency("Oldest", "uid-old", "Old");
            oldest.CreatedUtcTicks = 100;

            System.Threading.Thread.Sleep(2);
            var (_, _, mid) = AgencySystem.CreateAgency("Middle", "uid-mid", "Mid");
            mid.CreatedUtcTicks = 200;

            System.Threading.Thread.Sleep(2);
            var (_, _, newest) = AgencySystem.CreateAgency("Newest", "uid-new", "New");
            newest.CreatedUtcTicks = 300;

            // Seed a global SCANcontroller scenario to migrate.
            File.WriteAllText(Path.Combine(_scenariosPath, "SCANcontroller.txt"),
                "name = SCANcontroller\nstorageUpgraded = True\nbiomeMapEnabled = True\n");

            AgencyScansatMigration.RunIfNeeded();

            var oldestPath = Path.Combine(AgencyScenarioStore.AgencyScenariosPath(oldest.Id), "SCANcontroller.txt");
            var midPath = Path.Combine(AgencyScenarioStore.AgencyScenariosPath(mid.Id), "SCANcontroller.txt");
            var newestPath = Path.Combine(AgencyScenarioStore.AgencyScenariosPath(newest.Id), "SCANcontroller.txt");

            Assert.IsTrue(File.Exists(oldestPath), "Oldest agency should inherit the global SCANcontroller");
            Assert.IsFalse(File.Exists(midPath), "Mid agency should NOT inherit");
            Assert.IsFalse(File.Exists(newestPath), "Newest agency should NOT inherit");

            // Migration is idempotent — second run is a no-op.
            File.WriteAllText(oldestPath, "DIFFERENT CONTENT");
            AgencyScansatMigration.RunIfNeeded();
            Assert.AreEqual("DIFFERENT CONTENT", File.ReadAllText(oldestPath),
                "Migration must not overwrite existing agency files on re-run.");
        }
    }
}
