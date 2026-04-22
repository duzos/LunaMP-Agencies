using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Context;
using System;
using System.IO;
using System.Linq;

namespace ServerTest.Agency
{
    [TestClass]
    public class AgencyStoreTest
    {
        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "LMPTestUniverse_" + Guid.NewGuid());
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            AgencyStore.AgenciesPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies");
            Directory.CreateDirectory(AgencyStore.AgenciesPath);
            AgencyStore.Agencies.Clear();
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, true);
        }

        [TestMethod]
        public void PersistAndReload_RoundTripsCoreFields()
        {
            var agency = new Server.Agency.Agency
            {
                Id = Guid.NewGuid(),
                Name = "Kerbin Dynamics",
                OwnerUniqueId = "uid-1",
                OwnerDisplayName = "Jeb",
                Funds = 123456.78,
                Science = 42.5f,
                Reputation = 7.25f,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                IsSolo = false,
                UnlockedTechCount = 3,
            };
            agency.Members.Add(new Server.Agency.Agency.Member { UniqueId = "uid-1", DisplayName = "Jeb" });
            agency.Members.Add(new Server.Agency.Agency.Member { UniqueId = "uid-2", DisplayName = "Bill" });

            AgencyStore.PersistAgency(agency);
            Assert.IsTrue(File.Exists(AgencyStore.MetaPath(agency.Id)), "Meta file should exist");

            AgencyStore.Agencies.Clear();
            AgencyStore.LoadExistingAgencies();

            Assert.IsTrue(AgencyStore.Agencies.ContainsKey(agency.Id), "Agency should be reloaded");
            var loaded = AgencyStore.Agencies[agency.Id];
            Assert.AreEqual("Kerbin Dynamics", loaded.Name);
            Assert.AreEqual("uid-1", loaded.OwnerUniqueId);
            Assert.AreEqual(123456.78, loaded.Funds, 1e-6);
            Assert.AreEqual(42.5f, loaded.Science);
            Assert.AreEqual(3, loaded.UnlockedTechCount);
            Assert.AreEqual(2, loaded.Members.Count);
            Assert.IsTrue(loaded.Members.Any(m => m.UniqueId == "uid-1"));
            Assert.IsTrue(loaded.Members.Any(m => m.UniqueId == "uid-2"));
        }

        [TestMethod]
        public void LoadExistingAgencies_SkipsCorruptMetaWithoutCrashing()
        {
            var badDir = Path.Combine(AgencyStore.AgenciesPath, "not-a-guid-dir");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(Path.Combine(badDir, "meta.txt"), "NOT VALID CONFIGNODE AT ALL <<<<<");

            AgencyStore.LoadExistingAgencies(); // should not throw

            Assert.AreEqual(0, AgencyStore.Agencies.Count, "Corrupt agency is skipped, not loaded");
        }
    }
}
