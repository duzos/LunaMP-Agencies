using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Command.Command;
using Server.Context;
using Server.Settings.Definition;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Serialization;

namespace ServerTest.Agency
{
    [TestClass]
    [DoNotParallelize]
    public class AgencyLaunchSiteStoreTest
    {
        private AgencyTestScope _scope;
        private Guid _a, _b;
        [TestInitialize]
        public void Setup()
        {
            _scope = new AgencyTestScope();
            _a = AgencySystem.CreateAgency("Agency Alpha", "alpha", "Alpha").Agency.Id;
            _b = AgencySystem.CreateAgency("Agency Beta", "beta", "Beta").Agency.Id;
        }
        [TestCleanup]
        public void Cleanup() => _scope.Dispose();

        [TestMethod]
        public void AssignmentMovesExclusivelyAndStaleUnassignCannotRemoveNewOwner()
        {
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(_a, "LaunchPad").Success);
            var first = AgencyLaunchSiteStore.GetSnapshot();
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(_b, "LaunchPad").Success);
            Assert.IsTrue(AgencyLaunchSiteStore.Revision > first.Revision);
            Assert.AreEqual(_a, first.Assignments["LaunchPad"], "Snapshots must not mutate after publication.");
            Assert.AreEqual(_b, AgencyLaunchSiteStore.GetSnapshot().Assignments["LaunchPad"]);
            Assert.IsFalse(AgencyLaunchSiteStore.Unassign(_a, "LaunchPad").Success);
            Assert.IsTrue(AgencyLaunchSiteStore.Unassign(_b, "LaunchPad").Success);
            Assert.AreEqual(0, AgencyLaunchSiteStore.GetSnapshot().Assignments.Count);
        }

        [TestMethod]
        public void ValidationRejectsUnknownAgenciesAndInvalidIds()
        {
            Assert.IsFalse(AgencyLaunchSiteStore.Assign(Guid.NewGuid(), "Runway").Success);
            foreach (var site in new[] { "", " ", "bad\nline", new string('x', AgencyLaunchSitePolicy.MaxSiteIdLength + 1) })
                Assert.IsFalse(AgencyLaunchSiteStore.Assign(_a, site).Success);
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(_a, "Custom = Site").Success);
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(_b, "custom = site").Success);
            Assert.AreEqual(2, AgencyLaunchSiteStore.GetSnapshot().Assignments.Count);
        }

        [TestMethod]
        public void JsonReloadAndDataRootSwitchUseOnlySelectedRoot()
        {
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(_a, "Custom = Site").Success);
            var originalPath = AgencyLaunchSiteStore.MapFilePath;
            var original = File.ReadAllText(originalPath);
            AgencyLaunchSiteStore.Load();
            Assert.AreEqual(_a, AgencyLaunchSiteStore.GetSnapshot().Assignments["Custom = Site"]);
            ServerContext.DataDirectory = Path.Combine(_scope.Root, "other-root");
            AgencyLaunchSiteStore.Load();
            Assert.AreEqual(0, AgencyLaunchSiteStore.GetSnapshot().Assignments.Count);
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(_b, "Runway").Success);
            Assert.IsTrue(File.Exists(AgencyLaunchSiteStore.MapFilePath));
            Assert.AreEqual(original, File.ReadAllText(originalPath));
        }

        [TestMethod]
        public void CorruptFileBlocksMutationRatherThanOverwritingEvidence()
        {
            File.WriteAllText(AgencyLaunchSiteStore.MapFilePath, "{broken");
            AgencyLaunchSiteStore.Load();
            Assert.IsFalse(AgencyLaunchSiteStore.Assign(_a, "Runway").Success);
            Assert.AreEqual("{broken", File.ReadAllText(AgencyLaunchSiteStore.MapFilePath));
            Assert.AreEqual(0, AgencyLaunchSiteStore.GetSnapshot().Assignments.Count);
        }

        [TestMethod]
        public void PersistenceFailureDoesNotCommitRevisionOrDeleteAgency()
        {
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(_a, "LaunchPad").Success);
            var revision = AgencyLaunchSiteStore.Revision;
            File.Delete(AgencyLaunchSiteStore.MapFilePath);
            Directory.CreateDirectory(AgencyLaunchSiteStore.MapFilePath);
            Assert.IsFalse(AgencyLaunchSiteStore.Assign(_b, "LaunchPad").Success);
            Assert.AreEqual(revision, AgencyLaunchSiteStore.Revision);
            Assert.AreEqual(_a, AgencyLaunchSiteStore.GetSnapshot().Assignments["LaunchPad"]);
            Assert.IsFalse(AgencySystem.DeleteAgency(_a, "console", true, true).Success);
            Assert.IsTrue(AgencyStore.Agencies.ContainsKey(_a));
        }

        [TestMethod]
        public void LoadRejectsOrphansAndInvalidEntriesWithoutGrantingFallback()
        {
            File.WriteAllText(AgencyLaunchSiteStore.MapFilePath, JsonSerializer.Serialize(new[] {
                new LaunchSiteAssignment { SiteId = "LaunchPad", AgencyId = _a },
                new LaunchSiteAssignment { SiteId = "Orphan", AgencyId = Guid.NewGuid() },
                new LaunchSiteAssignment { SiteId = "bad\nline", AgencyId = _b } }));
            AgencyLaunchSiteStore.Load();
            Assert.AreEqual(1, AgencyLaunchSiteStore.GetSnapshot().Assignments.Count);
            Assert.AreEqual(_a, AgencyLaunchSiteStore.GetSnapshot().Assignments["LaunchPad"]);
        }

        [TestMethod]
        public void ExplicitAndImplicitAgencyDeletionCleanSavedAssignments()
        {
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(_a, "LaunchPad").Success);
            Assert.IsTrue(AgencySystem.DeleteAgency(_a, "console", true, true).Success);
            Assert.AreEqual(0, AgencyLaunchSiteStore.GetSnapshot().Assignments.Count);
            var solo = AgencySystem.EnsureSoloAgency("solo-owner", "Solo pilot");
            Assert.IsTrue(AgencyLaunchSiteStore.Assign(solo.Id, "Runway").Success);
            Assert.IsTrue(AgencySystem.CreateAgency("New Agency", "solo-owner", "Pilot").Success);
            Assert.IsFalse(AgencyStore.Agencies.ContainsKey(solo.Id));
            AgencyLaunchSiteStore.Load();
            Assert.AreEqual(0, AgencyLaunchSiteStore.GetSnapshot().Assignments.Count);
        }

        [TestMethod]
        public void CommandsSupportQuotedNamesAndRejectAmbiguousIds()
        {
            Assert.IsTrue(new AssignLaunchSiteCommand().Execute("\"Agency Alpha\" \"Custom Site\""));
            Assert.AreEqual(_a, AgencyLaunchSiteStore.GetSnapshot().Assignments["Custom Site"]);
            Assert.IsTrue(new ListLaunchSitesCommand().Execute("\"Agency Alpha\""));
            Assert.IsFalse(new UnassignLaunchSiteCommand().Execute("\"Agency Beta\" \"Custom Site\""));
            Assert.IsTrue(new UnassignLaunchSiteCommand().Execute("\"Agency Alpha\" \"Custom Site\""));
            Assert.IsFalse(LaunchSiteCommandArguments.TryParse("\"unterminated", out _));
            var id1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
            var id2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
            AgencyStore.Agencies[id1] = new Server.Agency.Agency { Id = id1, Name = "One" };
            AgencyStore.Agencies[id2] = new Server.Agency.Agency { Id = id2, Name = "Two" };
            Assert.IsFalse(AgencyCmdHelpers.TryResolveAgency("aaaaaaaa", out _));
        }

        [TestMethod]
        public void OldSettingsDefaultOff()
        {
            var serializer = new XmlSerializer(typeof(GeneralSettingsDefinition));
            using var reader = new StringReader("<GeneralSettingsDefinition/>");
            Assert.IsFalse(((GeneralSettingsDefinition)serializer.Deserialize(reader)).AgencyLaunchSitesPerAgency);
        }
    }
}
