using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Settings.Structures;
using System;
using System.IO;
using System.Linq;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class VesselOwnershipDeletionTest
    {
        [TestMethod]
        public void FailedLaunchMapDeletionPreservesAgencyAndCraftPermissions()
        {
            using (var scope = new AgencyTestScope())
            {
                var owner = AgencySystem.CreateAgency("Owner", "owner", "Owner").Agency.Id;
                var co = AgencySystem.CreateAgency("Co-owner", "co", "Co-owner").Agency.Id;
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var vessel = Guid.NewGuid();
                AgencyVesselMap.Set(vessel, owner);
                Assert.IsTrue(AgencyVesselMap.Mutate(vessel, owner, true, VesselOwnershipOperation.AddCoOwner, co, VesselDockingPolicy.Nobody).Success);
                Assert.IsTrue(AgencyLaunchSiteStore.Assign(owner, "LaunchPad").Success);
                File.WriteAllText(AgencyLaunchSiteStore.MapFilePath, "{broken");
                AgencyLaunchSiteStore.Load();
                var before = File.ReadAllText(AgencyVesselMap.OwnershipFilePath);
                var result = AgencySystem.DeleteAgency(owner, "admin", true, true);
                Assert.IsFalse(result.Success);
                Assert.IsNotNull(AgencySystem.GetAgency(owner));
                Assert.AreEqual(owner, AgencyVesselMap.Get(vessel).OwnerAgencyId);
                CollectionAssert.AreEqual(new[] { co }, AgencyVesselMap.Get(vessel).CoOwnerAgencyIds);
                Assert.AreEqual(before, File.ReadAllText(AgencyVesselMap.OwnershipFilePath));
            }
        }

        [TestMethod]
        public void DeletedAgencyIsEffectivelyOwnerlessWhenOptionalCleanupFailsAndAfterReload()
        {
            using (var scope = new AgencyTestScope())
            {
                var owner = AgencySystem.CreateAgency("Owner", "owner", "Owner").Agency.Id;
                var other = AgencySystem.CreateAgency("Other", "other", "Other").Agency.Id;
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var owned = Guid.NewGuid();
                var shared = Guid.NewGuid();
                AgencyVesselMap.Set(owned, owner);
                AgencyVesselMap.Set(shared, other);
                Assert.IsTrue(AgencyVesselMap.Mutate(shared, other, true, VesselOwnershipOperation.AddCoOwner, owner, VesselDockingPolicy.Nobody).Success);
                try
                {
                    AgencyVesselMap.PersistenceCheckpoint = _ => throw new IOException("Injected cleanup failure");
                    Assert.IsTrue(AgencySystem.DeleteAgency(owner, "admin", true, true).Success);
                }
                finally { AgencyVesselMap.PersistenceCheckpoint = null; }
                Assert.IsNull(AgencySystem.GetAgency(owner));
                Assert.AreEqual(Guid.Empty, AgencyVesselMap.Get(owned).OwnerAgencyId);
                Assert.AreEqual(0, AgencyVesselMap.Get(shared).CoOwnerAgencyIds.Length);
                Assert.IsFalse(AgencyVesselMap.Snapshot.ContainsKey(owned));
                AgencyVesselMap.Load();
                Assert.AreEqual(Guid.Empty, AgencyVesselMap.GetOwnershipSnapshot().Records.Single(r => r.VesselId == owned).OwnerAgencyId);
                Assert.IsTrue(AgencyVesselMap.Mutate(owned, other, false, VesselOwnershipOperation.Claim, Guid.Empty, VesselDockingPolicy.Nobody).Success);
                Assert.AreEqual(other, AgencyVesselMap.Get(owned).OwnerAgencyId);
            }
        }
    }
}
