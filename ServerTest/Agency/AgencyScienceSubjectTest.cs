using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Context;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerTest.Agency
{
    [TestClass]
    public class AgencyScienceSubjectTest
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
        public void WriteScienceSubject_UpsertsSubjectNodeIntoAgencyRD()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Kerbin Dynamics", "uid-1", "A");
            // Baseline R&D scenario was created by EnsureBaselineForAgency.

            // First write: inserts a brand-new Science subnode.
            var firstPayload = Encoding.UTF8.GetBytes("id = temperature@KerbinSrfLandedLaunchPad\ndsc = 1\nscv = 1\nsbv = 0.3\nsci = 0.5\ncap = 1.5\n");
            AgencyScenarioUpdater.WriteScienceSubject(a.Id, firstPayload, firstPayload.Length);

            var rd = AgencyScenarioStore.GetOrNull(a.Id, "ResearchAndDevelopment");
            Assert.IsNotNull(rd);
            var scienceNodes = rd.GetNodes("Science").Select(v => v.Value).ToArray();
            Assert.AreEqual(1, scienceNodes.Length);
            Assert.AreEqual("temperature@KerbinSrfLandedLaunchPad", scienceNodes[0].GetValue("id")?.Value);
            Assert.AreEqual("0.5", scienceNodes[0].GetValue("sci")?.Value);

            // Second write with same id replaces the existing node in place.
            var secondPayload = Encoding.UTF8.GetBytes("id = temperature@KerbinSrfLandedLaunchPad\ndsc = 1\nscv = 1\nsbv = 0.3\nsci = 1.2\ncap = 1.5\n");
            AgencyScenarioUpdater.WriteScienceSubject(a.Id, secondPayload, secondPayload.Length);

            scienceNodes = rd.GetNodes("Science").Select(v => v.Value).ToArray();
            Assert.AreEqual(1, scienceNodes.Length, "Duplicate id should replace, not append");
            Assert.AreEqual("1.2", scienceNodes[0].GetValue("sci")?.Value);
        }

        [TestMethod]
        public void WriteScienceSubject_IsolatedBetweenAgencies()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Alpha", "uid-a", "A");
            var (_, _, b) = AgencySystem.CreateAgency("Beta", "uid-b", "B");

            var payload = Encoding.UTF8.GetBytes("id = temperature@KerbinSrfLandedLaunchPad\nsci = 1.5\ncap = 1.5\n");
            AgencyScenarioUpdater.WriteScienceSubject(a.Id, payload, payload.Length);

            // A has the subject; B should have none.
            var rdA = AgencyScenarioStore.GetOrNull(a.Id, "ResearchAndDevelopment");
            var rdB = AgencyScenarioStore.GetOrNull(b.Id, "ResearchAndDevelopment");
            Assert.AreEqual(1, rdA.GetNodes("Science").Count);
            Assert.AreEqual(0, rdB.GetNodes("Science").Count);
        }
    }
}
