using Microsoft.VisualStudio.TestTools.UnitTesting;
using LunaConfigNode.CfgNode;
using Server.Agency;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.IO;
using System.Linq;

namespace ServerTest.Agency
{
    [TestClass]
    public class AgencyMigrationTest
    {
        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "LMPTestUniverse_" + Guid.NewGuid());
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            AgencyStore.AgenciesPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies");
            Directory.CreateDirectory(AgencyStore.AgenciesPath);
            AgencyStore.Agencies.Clear();
            ScenarioStoreSystem.CurrentScenarios.Clear();

            GeneralSettings.SettingsStore.GameMode = LmpCommon.Enums.GameMode.Career;
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, true);
        }

        [TestMethod]
        public void Migration_CreatesDefaultAgencyFromGlobalScenarios()
        {
            // Simulate an existing scenario loaded globally.
            var fundingText = "Funding\n{\n  name = Funding\n  funds = 999999\n}\n";
            ScenarioStoreSystem.CurrentScenarios["Funding"] = new ConfigNode(fundingText);

            AgencyMigration.RunIfNeeded();

            Assert.AreEqual(1, AgencyStore.Agencies.Count, "Default agency created");
            var agency = AgencyStore.Agencies.Values.First();
            Assert.AreEqual(AgencyMigration.DefaultAgencyName, agency.Name);
            Assert.IsTrue(AgencyScenarioStore.TryGet(agency.Id, "Funding", out var funding));
            Assert.IsNotNull(funding);
        }

        [TestMethod]
        public void Migration_IsIdempotent()
        {
            AgencyMigration.RunIfNeeded();
            var firstCount = AgencyStore.Agencies.Count;

            AgencyMigration.RunIfNeeded();
            var secondCount = AgencyStore.Agencies.Count;

            Assert.AreEqual(firstCount, secondCount);
        }

        [TestMethod]
        public void Migration_CopiesFundsAndScienceOntoAgencyEntity()
        {
            // Regression: previously the Agency entity was left at the
            // gameplay defaults (e.g. funds=25000), so the UI showed 25k
            // even though the scenario file had the real migrated value.
            ScenarioStoreSystem.CurrentScenarios["Funding"] = new ConfigNode(
                "name = Funding\nfunds = 961545.3483122353\n") { Name = "Funding" };
            ScenarioStoreSystem.CurrentScenarios["ResearchAndDevelopment"] = new ConfigNode(
                "name = ResearchAndDevelopment\nsci = 349.19482\n") { Name = "ResearchAndDevelopment" };
            ScenarioStoreSystem.CurrentScenarios["Reputation"] = new ConfigNode(
                "name = Reputation\nrep = 42\n") { Name = "Reputation" };

            AgencyMigration.RunIfNeeded();

            var agency = AgencyStore.Agencies.Values.First();
            Assert.AreEqual(961545.3483122353, agency.Funds, 1e-6, "Agency entity funds must reflect the migrated scenario");
            Assert.AreEqual(349.19482f, agency.Science, 1e-3f, "Agency entity science must reflect the migrated scenario");
            Assert.AreEqual(42f, agency.Reputation, 1e-6f, "Agency entity reputation must reflect the migrated scenario");
        }

        [TestMethod]
        public void SyncEntityFromScenarios_HealsDriftedEntityValues()
        {
            // Simulate a server migrated with the earlier buggy version:
            // agency exists with wrong (default) entity values but the
            // per-agency Funding scenario has the real value on disk.
            var agency = new Server.Agency.Agency
            {
                Id = Guid.NewGuid(),
                Name = "Drifted",
                OwnerUniqueId = "x",
                OwnerDisplayName = "x",
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                Funds = 25000,
                Science = 0,
                Reputation = 0,
            };
            AgencyStore.Agencies[agency.Id] = agency;
            AgencyScenarioStore.AddOrUpdate(agency.Id, "Funding",
                new ConfigNode("funds = 961545.3483122353\n") { Name = "Funding" });
            AgencyScenarioStore.AddOrUpdate(agency.Id, "ResearchAndDevelopment",
                new ConfigNode("sci = 349.19482\n") { Name = "ResearchAndDevelopment" });

            AgencyMigration.SyncEntityFromScenarios(agency);

            Assert.AreEqual(961545.3483122353, agency.Funds, 1e-6);
            Assert.AreEqual(349.19482f, agency.Science, 1e-3f);
        }
    }
}
