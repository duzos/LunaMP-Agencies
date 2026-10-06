using LmpCommon.Message.Data.ShareProgress;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerTest.Agency
{
    [TestClass]
    [DoNotParallelize]
    public class AgencyScenarioParityTest
    {
        private Guid agency;
        private Guid other;

        private AgencyTestScope _scope;

        [TestInitialize]
        public void Setup()
        {
            _scope = new AgencyTestScope();
            agency = Guid.NewGuid();
            other = Guid.NewGuid();
            AgencyScenarioStore.EnsureBaselineForAgency(agency, 0, 0, 0);
            AgencyScenarioStore.EnsureBaselineForAgency(other, 0, 0, 0);
        }

        [TestCleanup]
        public void Cleanup()
        {
            AgencyScenarioStore.RemoveAgency(agency);
            AgencyScenarioStore.RemoveAgency(other);
            _scope.Dispose();
        }

        [TestMethod]
        public async Task BracedRawUploadsApplyContractAndCrewMigrations()
        {
            await AgencyScenarioUpdater.RawConfigNodeInsertOrUpdate(agency, "Funding", "{\nname = Funding\nfunds = 123\n}");
            Assert.AreEqual("123", AgencyScenarioStore.GetOrNull(agency, "Funding").GetValue("funds").Value);
            await AgencyScenarioUpdater.RawConfigNodeInsertOrUpdate(agency, "ContractSystem", LegacyContracts);
            AssertCanonicalContracts();
            await AgencyScenarioUpdater.RawConfigNodeInsertOrUpdate(agency, "ProgressTracking", BracedProgress);
            AssertCrewDeduped();
        }

        [TestMethod]
        public void LoadingBracedAgencyScenariosMigratesArchivesAndDuplicateCrew()
        {
            var path = AgencyScenarioStore.AgencyScenariosPath(agency);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "ContractSystem.txt"), LegacyContracts);
            File.WriteAllText(Path.Combine(path, "ProgressTracking.txt"), BracedProgress);
            AgencyScenarioStore.LoadForAgency(agency);
            AssertCanonicalContracts();
            AssertCrewDeduped();
            AgencyScenarioStore.BackupAgency(agency);
            AgencyScenarioStore.RemoveAgency(agency);
            AgencyScenarioStore.LoadForAgency(agency);
            AssertCanonicalContracts();
            AssertCrewDeduped();
        }

        private const string LegacyContracts = "{\nname = ContractSystem\nCONTRACTS\n{\nCONTRACT\n{\nguid = one\nstate = Active\n}\n}\nCONTRACTS_FINISHED\n{\nCONTRACT\n{\nguid = one\nstate = Completed\n}\n}\n}";
        private const string BracedProgress = "{\nname = ProgressTracking\nProgress\n{\nFirstLaunch\n{\ncrew\n{\nitem = Jeb\nitem = Jeb\nitem = Val\n}\n}\n}\n}";

        private void AssertCanonicalContracts()
        {
            var scenario = AgencyScenarioStore.GetOrNull(agency, "ContractSystem");
            Assert.IsNull(scenario.GetNode("CONTRACTS_FINISHED"));
            var contract = scenario.GetNode("CONTRACTS").Value.GetAllNodes().Single();
            Assert.AreEqual("CONTRACT_FINISHED", contract.Name);
            Assert.AreEqual("Completed", contract.GetValue("state").Value);
        }

        private void AssertCrewDeduped()
        {
            var crew = AgencyScenarioStore.GetOrNull(agency, "ProgressTracking")
                .GetNode("Progress").Value.GetNode("FirstLaunch").Value.GetNode("crew").Value;
            CollectionAssert.AreEqual(new[] { "Jeb", "Val" }, crew.GetValues("item").Select(value => value.Value).ToArray());
        }

        [TestMethod]
        public void BracedTechAndScienceAreReadableAndRemainIsolated()
        {
            var tech = Encoding.UTF8.GetBytes("{\nid = basicRocketry\nstate = Available\n}\n");
            Assert.IsTrue(AgencyScenarioUpdater.AppendTechNodeBytes(agency, tech, tech.Length));
            Assert.IsFalse(AgencyScenarioUpdater.AppendTechNodeBytes(agency, tech, tech.Length));
            Assert.IsTrue(AgencyScenarioUpdater.AppendPartPurchase(agency, "basicRocketry", "engine"));
            Assert.IsFalse(AgencyScenarioUpdater.AppendPartPurchase(agency, "basicRocketry", "engine"));
            foreach (var amount in new[] { "0.5", "1.5" })
            {
                var subject = Encoding.UTF8.GetBytes("{\nid = temperature@Kerbin\nsci = " + amount + "\ncap = 2\n}\n");
                AgencyScenarioUpdater.WriteScienceSubject(agency, subject, subject.Length);
            }
            var rd = AgencyScenarioStore.GetOrNull(agency, "ResearchAndDevelopment");
            Assert.AreEqual("engine", rd.GetNodes("Tech").Single().Value.GetValue("part").Value);
            Assert.AreEqual("1.5", rd.GetNodes("Science").Single().Value.GetValue("sci").Value);
            Assert.AreEqual("2", rd.GetNodes("Science").Single().Value.GetValue("cap").Value);
            Assert.AreEqual(0, AgencyScenarioStore.GetOrNull(other, "ResearchAndDevelopment").GetAllNodes().Count);
        }

        [TestMethod]
        public void ContractBatchKeepsLatestStateUnderCanonicalParentAndFiltersOwnership()
        {
            var message = (ShareProgressContractsMsgData)Activator.CreateInstance(typeof(ShareProgressContractsMsgData), true);
            message.Contracts = new[] { Contract(agency, "one", "Active"), Contract(agency, "one", "Completed"), Contract(other, "foreign", "Active") };
            message.ContractCount = message.Contracts.Length;
            AgencyContractStore.WriteContracts(agency, message);
            var scenario = AgencyScenarioStore.GetOrNull(agency, "ContractSystem");
            var children = scenario.GetNode("CONTRACTS").Value.GetAllNodes();
            Assert.AreEqual(1, children.Count);
            Assert.AreEqual("CONTRACT_FINISHED", children.Single().Name);
            Assert.AreEqual("Completed", children.Single().GetValue("state").Value);
            Assert.IsNull(scenario.GetNode("CONTRACTS_FINISHED"));
        }

        [TestMethod]
        public void ForceCompletionUsesCanonicalArchivedContract()
        {
            var message = (ShareProgressContractsMsgData)Activator.CreateInstance(typeof(ShareProgressContractsMsgData), true);
            message.Contracts = new[] { Contract(agency, "one", "Active") };
            message.ContractCount = 1;
            AgencyContractStore.WriteContracts(agency, message);
            Assert.IsTrue(AgencyScenarioUpdater.ForceCompleteContract(agency, "one"));
            var contracts = AgencyScenarioStore.GetOrNull(agency, "ContractSystem").GetNode("CONTRACTS").Value;
            Assert.AreEqual(1, contracts.GetNodes("CONTRACT_FINISHED").Count);
            Assert.AreEqual(0, contracts.GetNodes("CONTRACT").Count);
        }

        private static ContractInfo Contract(Guid owner, string guid, string state)
        {
            var bytes = Encoding.UTF8.GetBytes("{\nguid = " + guid + "\nstate = " + state + "\n}\n");
            return new ContractInfo { OwningAgencyId = owner, Data = bytes, NumBytes = bytes.Length };
        }
    }
}
