using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Context;
using System;
using System.IO;

namespace ServerTest.Agency
{
    [TestClass]
    public class AgencyLeaderboardTest
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
        public void Leaderboard_PersistsAcrossRoundTrip()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Lifetime", "uid-1", "User");
            lock (a.Lock)
            {
                a.LifetimeFundsEarned = 1_234_567;
                a.LifetimeScienceGenerated = 250.5f;
                a.VesselsLaunched = 7;
                a.FirstAchievements["RecoverFromOrbit:Mun"] = DateTime.UtcNow.Ticks;
                a.CountedVesselIds.Add(Guid.NewGuid());
            }
            AgencyStore.PersistAgency(a);

            AgencyStore.Agencies.Clear();
            AgencyStore.LoadExistingAgencies();

            var loaded = AgencyStore.Agencies[a.Id];
            Assert.AreEqual(1_234_567, loaded.LifetimeFundsEarned, 1e-6);
            Assert.AreEqual(250.5f, loaded.LifetimeScienceGenerated, 1e-3);
            Assert.AreEqual(7, loaded.VesselsLaunched);
            Assert.AreEqual(1, loaded.FirstAchievements.Count);
            Assert.IsTrue(loaded.FirstAchievements.ContainsKey("RecoverFromOrbit:Mun"));
            Assert.AreEqual(1, loaded.CountedVesselIds.Count);
        }

        [TestMethod]
        public void AchievementRegistry_FirstClaimWins()
        {
            var (_, _, a1) = AgencySystem.CreateAgency("First", "uid-1", "User1");
            var (_, _, a2) = AgencySystem.CreateAgency("Second", "uid-2", "User2");

            AgencyAchievementRegistry.RebuildFromAgencies();

            Assert.IsTrue(AgencyAchievementRegistry.ClaimIfFirst(a1, "OrbitFirst:Kerbin"));
            Assert.IsFalse(AgencyAchievementRegistry.ClaimIfFirst(a2, "OrbitFirst:Kerbin"),
                "Second agency claiming the same key must not steal it.");

            Assert.IsTrue(a1.FirstAchievements.ContainsKey("OrbitFirst:Kerbin"));
            Assert.IsFalse(a2.FirstAchievements.ContainsKey("OrbitFirst:Kerbin"));
            Assert.AreEqual(1, AgencyAchievementRegistry.CountFor(a1.Id));
            Assert.AreEqual(0, AgencyAchievementRegistry.CountFor(a2.Id));
        }

        [TestMethod]
        public void AchievementRegistry_RebuildFromPersistedAgencies()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Loaded", "uid-1", "User");
            lock (a.Lock)
            {
                a.FirstAchievements["LandOnMun"] = 12345L;
                a.FirstAchievements["DockInOrbit"] = 67890L;
            }

            AgencyAchievementRegistry.RebuildFromAgencies();

            Assert.AreEqual(2, AgencyAchievementRegistry.CountFor(a.Id));

            // A subsequent claim of an already-held key by another agency
            // should still be rejected.
            var (_, _, b) = AgencySystem.CreateAgency("Other", "uid-2", "Other");
            Assert.IsFalse(AgencyAchievementRegistry.ClaimIfFirst(b, "LandOnMun"));
        }
    }
}
