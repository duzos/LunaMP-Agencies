using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Context;
using Server.Settings.Definition;
using Server.Settings.Structures;
using Server.System;
using System;
using System.IO;
using System.Xml.Serialization;

namespace ServerTest.Agency
{
    [TestClass]
    [DoNotParallelize]
    public class AgencyKerbalStoreTest
    {
        private AgencyTestScope _scope;
        [TestInitialize]
        public void Setup() => _scope = new AgencyTestScope();
        [TestCleanup]
        public void Cleanup() => _scope.Dispose();

        private static Server.Agency.Agency Create(bool solo)
        {
            var identity = Guid.NewGuid().ToString("N");
            if (solo) return AgencySystem.EnsureSoloAgency(identity, "Pilot");
            var result = AgencySystem.CreateAgency("Agency " + identity, identity, "Pilot");
            Assert.IsTrue(result.Success, result.Message);
            return result.Agency;
        }

        [DataTestMethod]
        [DataRow(true, true, 0)]
        [DataRow(false, true, 0)]
        [DataRow(true, false, 4)]
        [DataRow(false, false, 4)]
        public void CreationInitializesSoloAndNamedRosters(bool solo, bool zero, int expectedCount)
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = zero;
            var agency = Create(solo);
            var path = AgencyKerbalStore.KerbalsPath(agency.Id);
            Assert.IsTrue(Directory.Exists(path));
            Assert.AreEqual(expectedCount, Directory.GetFiles(path).Length);
            AgencyKerbalStore.EnsureDefaultRoster(agency.Id);
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = !zero;
            AgencyKerbalStore.EnsureDefaultRoster(agency.Id);
            Assert.AreEqual(expectedCount, Directory.GetFiles(path).Length, "Later access and flag toggles must not reseed initialized rosters.");
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ZeroFlagWithoutPerAgencyModeDoesNotCreatePrivateRoster(bool solo)
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = false;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = true;
            var agency = Create(solo);
            Assert.IsFalse(Directory.Exists(AgencyKerbalStore.KerbalsPath(agency.Id)));
        }

        [TestMethod]
        public void ExistingCustomAndEmptyRostersRemainAuthoritative()
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = false;
            var custom = Guid.NewGuid();
            var empty = Guid.NewGuid();
            Directory.CreateDirectory(AgencyKerbalStore.KerbalsPath(custom));
            Directory.CreateDirectory(AgencyKerbalStore.KerbalsPath(empty));
            var recruit = AgencyKerbalStore.KerbalPath(custom, "Custom Recruit");
            File.WriteAllText(recruit, "name = Custom Recruit\ntrait = Engineer\n");
            AgencyKerbalStore.EnsureDefaultRoster(custom);
            AgencyKerbalStore.EnsureDefaultRoster(empty);
            Assert.AreEqual(1, Directory.GetFiles(AgencyKerbalStore.KerbalsPath(custom)).Length);
            Assert.AreEqual("name = Custom Recruit\ntrait = Engineer\n", File.ReadAllText(recruit));
            Assert.AreEqual(0, Directory.GetFiles(AgencyKerbalStore.KerbalsPath(empty)).Length);
        }

        [TestMethod]
        public void MissingLegacyRosterReceivesDefaultsEvenWhenZeroIsEnabled()
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = false;
            var legacy = Create(false);
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = true;
            AgencyKerbalStore.EnsureDefaultRoster(legacy.Id);
            var path = AgencyKerbalStore.KerbalsPath(legacy.Id);
            CollectionAssert.AreEquivalent(new[] { "Jebediah Kerman.txt", "Bill Kerman.txt", "Bob Kerman.txt", "Valentina Kerman.txt" }, Array.ConvertAll(Directory.GetFiles(path), Path.GetFileName));
        }

        [TestMethod]
        public void GlobalMigrationPreservesCrewAndDoesNotFillInitializedEmptyAgency()
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = false;
            var legacy = Create(false);
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = true;
            var empty = Create(true);
            Directory.CreateDirectory(KerbalSystem.KerbalsPath);
            var globalFile = Path.Combine(KerbalSystem.KerbalsPath, "Veteran.txt");
            File.WriteAllText(globalFile, "name = Veteran\nexperience = 15\n");
            AgencyKerbalStore.MigrateGlobalKerbalsIfNeeded();
            AgencyKerbalStore.EnsureDefaultRoster(legacy.Id);
            Assert.AreEqual("name = Veteran\nexperience = 15\n", File.ReadAllText(AgencyKerbalStore.KerbalPath(legacy.Id, "Veteran")));
            Assert.AreEqual(1, Directory.GetFiles(AgencyKerbalStore.KerbalsPath(legacy.Id)).Length);
            Assert.AreEqual(0, Directory.GetFiles(AgencyKerbalStore.KerbalsPath(empty.Id)).Length);
            Assert.IsTrue(File.Exists(globalFile), "Migration must not remove global crew.");
        }

        private static string MarkerPath => Path.Combine(ServerContext.AgenciesDirectory, ".kerbals-migrated");

        private static string WriteGlobalKerbal()
        {
            Directory.CreateDirectory(KerbalSystem.KerbalsPath);
            var globalFile = Path.Combine(KerbalSystem.KerbalsPath, "Veteran.txt");
            File.WriteAllText(globalFile, "name = Veteran\nexperience = 15\n");
            return globalFile;
        }

        [TestMethod]
        public void MigrationWritesMarkerWhenThereIsNoGlobalFolder()
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            Assert.IsFalse(Directory.Exists(KerbalSystem.KerbalsPath));
            Assert.IsFalse(File.Exists(MarkerPath));
            AgencyKerbalStore.MigrateGlobalKerbalsIfNeeded();
            Assert.IsTrue(File.Exists(MarkerPath));
            Assert.IsTrue(AgencyKerbalStore.GlobalMigrationCompleted);
        }

        [TestMethod]
        public void MigrationWritesMarkerWhenTheGlobalFolderIsEmpty()
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            Directory.CreateDirectory(KerbalSystem.KerbalsPath);
            AgencyKerbalStore.MigrateGlobalKerbalsIfNeeded();
            Assert.IsTrue(File.Exists(MarkerPath));
        }

        [TestMethod]
        public void FirstEverMigrationCopiesGlobalRosterIntoLegacyAgenciesAndWritesMarker()
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = false;
            var legacy = Create(false);
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = true;
            WriteGlobalKerbal();
            Assert.IsFalse(File.Exists(MarkerPath));
            AgencyKerbalStore.MigrateGlobalKerbalsIfNeeded();
            Assert.IsTrue(File.Exists(MarkerPath));
            Assert.AreEqual("name = Veteran\nexperience = 15\n", File.ReadAllText(AgencyKerbalStore.KerbalPath(legacy.Id, "Veteran")));
            Assert.AreEqual(1, Directory.GetFiles(AgencyKerbalStore.KerbalsPath(legacy.Id)).Length);
        }

        [DataTestMethod]
        [DataRow(true, 0)]
        [DataRow(false, 4)]
        public void AfterMigrationBootInitializesFolderlessAgencyAsNewInsteadOfCopyingGlobalRoster(bool zero, int expectedCount)
        {
            // First correct boot: the universe is migrated and the marker written.
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = zero;
            var established = Create(false);
            var custom = AgencyKerbalStore.KerbalPath(established.Id, "Custom Recruit");
            File.WriteAllText(custom, "name = Custom Recruit\n");
            WriteGlobalKerbal();
            AgencyKerbalStore.MigrateGlobalKerbalsIfNeeded();
            Assert.IsTrue(File.Exists(MarkerPath));

            // A boot with per-agency kerbals off creates an agency with no Kerbals folder.
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = false;
            var late = Create(false);
            Assert.IsFalse(Directory.Exists(AgencyKerbalStore.KerbalsPath(late.Id)));

            // The next correct boot must not treat it as a legacy agency.
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            AgencyKerbalStore.MigrateGlobalKerbalsIfNeeded();
            var lateDir = AgencyKerbalStore.KerbalsPath(late.Id);
            Assert.IsTrue(Directory.Exists(lateDir));
            Assert.AreEqual(expectedCount, Directory.GetFiles(lateDir).Length);
            Assert.IsFalse(File.Exists(AgencyKerbalStore.KerbalPath(late.Id, "Veteran")), "The global roster must not be copied after the migration marker exists.");
            Assert.AreEqual("name = Custom Recruit\n", File.ReadAllText(custom), "Existing rosters stay untouched.");
        }

        [DataTestMethod]
        [DataRow(true, 0)]
        [DataRow(false, 4)]
        public void AfterMigrationEnsureDefaultRosterInitializesFolderlessAgencyAsNew(bool zero, int expectedCount)
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = false;
            var late = Create(false);
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = zero;
            File.WriteAllText(MarkerPath, "migrated");
            AgencyKerbalStore.EnsureDefaultRoster(late.Id);
            Assert.AreEqual(expectedCount, Directory.GetFiles(AgencyKerbalStore.KerbalsPath(late.Id)).Length);
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = !zero;
            AgencyKerbalStore.EnsureDefaultRoster(late.Id);
            Assert.AreEqual(expectedCount, Directory.GetFiles(AgencyKerbalStore.KerbalsPath(late.Id)).Length, "An initialized roster must not be reseeded.");
        }

        [TestMethod]
        public void SavedRecruitSurvivesAgencyReloadAndRepeatedAccess()
        {
            GeneralSettings.SettingsStore.AgencyKerbalsPerAgency = true;
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = true;
            var agency = Create(true);
            var recruit = AgencyKerbalStore.KerbalPath(agency.Id, "Hired Pilot");
            File.WriteAllText(recruit, "name = Hired Pilot\ntrait = Pilot\n");
            AgencyStore.Agencies.Clear();
            AgencyStore.LoadExistingAgencies();
            Assert.IsTrue(AgencyStore.Agencies.ContainsKey(agency.Id));
            GeneralSettings.SettingsStore.AgencyZeroStartingKerbals = false;
            AgencyKerbalStore.EnsureDefaultRoster(agency.Id);
            AgencyKerbalStore.EnsureDefaultRoster(agency.Id);
            Assert.AreEqual(1, Directory.GetFiles(AgencyKerbalStore.KerbalsPath(agency.Id)).Length);
            Assert.AreEqual("name = Hired Pilot\ntrait = Pilot\n", File.ReadAllText(recruit));
        }

        [TestMethod]
        public void OldSettingsRemainDefaultOffAndOptInRoundTrips()
        {
            Assert.IsFalse(new GeneralSettingsDefinition().AgencyZeroStartingKerbals);
            var serializer = new XmlSerializer(typeof(GeneralSettingsDefinition));
            using (var reader = new StringReader("<GeneralSettingsDefinition><AgencyKerbalsPerAgency>true</AgencyKerbalsPerAgency></GeneralSettingsDefinition>"))
                Assert.IsFalse(((GeneralSettingsDefinition)serializer.Deserialize(reader)).AgencyZeroStartingKerbals);
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, new GeneralSettingsDefinition { AgencyZeroStartingKerbals = true });
                using (var reader = new StringReader(writer.ToString()))
                    Assert.IsTrue(((GeneralSettingsDefinition)serializer.Deserialize(reader)).AgencyZeroStartingKerbals);
            }
        }
    }
}
