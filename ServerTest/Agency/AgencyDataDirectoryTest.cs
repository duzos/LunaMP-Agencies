using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Context;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ServerTest.Agency
{
    [TestClass]
    [DoNotParallelize]
    public class AgencyDataDirectoryTest
    {
        [TestMethod]
        public async Task Persistence_AfterDataRootChanges_WritesAndReloadsOnlySelectedRoot()
        {
            using (var scope = new AgencyTestScope())
            {
                var rootA = Path.Combine(scope.Root, "A");
                var rootB = Path.Combine(scope.Root, "B");
                ServerContext.DataDirectory = rootA;
                // Evaluate every old cached path before changing only the data root.
                var agenciesA = AgencyStore.AgenciesPath;
                var mapA = AgencyVesselMap.MapFilePath;
                Assert.AreEqual(Path.Combine(rootA, "Universe", "Agencies"), agenciesA);
                Assert.AreEqual(Path.Combine(rootA, "Universe", "AgencyVesselMap.txt"), mapA);

                ServerContext.DataDirectory = rootB;
                var agency = new Server.Agency.Agency { Id = Guid.NewGuid(), Name = "Root B Agency", Funds = 12345 };
                var vessel = Guid.NewGuid();
                AgencyStore.PersistAgency(agency);
                AgencyScenarioStore.AddOrUpdate(agency.Id, "Funding", new ConfigNode("name = Funding\nfunds = 12345\n") { Name = "Funding" });
                AgencyScenarioStore.BackupAgency(agency.Id);
                AgencyKerbalStore.EnsureDefaultRoster(agency.Id);
                AgencyVesselMap.Set(vessel, agency.Id);
                await AgencyVesselMap.WaitForPendingWritesAsync();

                var agencyDirectory = Path.Combine(rootB, "Universe", "Agencies", agency.Id.ToString("N"));
                StringAssert.Contains(File.ReadAllText(Path.Combine(agencyDirectory, "meta.txt")), "Root B Agency");
                StringAssert.Contains(File.ReadAllText(Path.Combine(agencyDirectory, "Scenarios", "Funding.txt")), "12345");
                Assert.IsTrue(File.Exists(Path.Combine(agencyDirectory, "Kerbals", "Jebediah Kerman.txt")));
                var mapB = Path.Combine(rootB, "Universe", "AgencyVesselMap.txt");
                StringAssert.Contains(File.ReadAllText(mapB), vessel.ToString("N") + " = " + agency.Id.ToString("N"));
                Assert.IsFalse(Directory.Exists(rootA), "Evaluating paths must not create or write to the old root.");

                AgencyStore.Agencies.Clear();
                AgencyScenarioStore.RemoveAgency(agency.Id);
                // Load clears the map first, proving the lookup below comes from the selected file.
                AgencyVesselMap.Load();
                AgencyStore.LoadExistingAgencies();
                AgencyScenarioStore.LoadForAgency(agency.Id);
                Assert.AreEqual("Root B Agency", AgencyStore.Agencies[agency.Id].Name);
                Assert.AreEqual(12345d, AgencyStore.Agencies[agency.Id].Funds);
                Assert.AreEqual("12345", AgencyScenarioStore.GetOrNull(agency.Id, "Funding").GetValue("funds")?.Value);
                Assert.IsTrue(AgencyVesselMap.TryGetAgency(vessel, out var owner));
                Assert.AreEqual(agency.Id, owner);
            }
        }
    }
}
