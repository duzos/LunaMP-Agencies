using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyLaunchSiteSnapshotTest
    {
        [TestMethod]
        public void CopiesPooledEntriesAndIgnoresOlderValidRevision()
        {
            var owner = Guid.NewGuid();
            var entries = new[] { new LaunchSiteAssignment { SiteId = "Runway", AgencyId = owner } };
            var current = AgencyLaunchSiteSnapshot.Apply(null, true, 4, entries);
            entries[0].AgencyId = Guid.NewGuid(); entries[0].SiteId = "changed";
            Assert.AreEqual(owner, current.Assignments["Runway"]);
            Assert.AreSame(current, AgencyLaunchSiteSnapshot.Apply(current, true, 3, entries));
        }
        [TestMethod]
        public void MissingOrMalformedSnapshotRevokesPreviouslyGrantedAccess()
        {
            var entries = new[] { new LaunchSiteAssignment { SiteId = "Runway", AgencyId = Guid.NewGuid() } };
            var current = AgencyLaunchSiteSnapshot.Apply(null, true, 4, entries);
            var missing = AgencyLaunchSiteSnapshot.Apply(current, false, 5, entries);
            Assert.IsFalse(missing.Ready); Assert.AreEqual(0, missing.Assignments.Count);
            entries[0].SiteId = "bad\nsite";
            Assert.IsFalse(AgencyLaunchSiteSnapshot.Apply(current, true, 5, entries).Ready);
            entries[0].SiteId = "Runway";
            Assert.IsFalse(AgencyLaunchSiteSnapshot.Apply(current, true, 5, new[] { entries[0], entries[0] }).Ready);
            Assert.IsFalse(AgencyLaunchSiteSnapshot.Apply(current, true, 5, null).Ready);
        }
        [TestMethod]
        public void EmptyAuthoritativeMapIsReadyAndNewConnectionAcceptsLowerRevision()
        {
            var empty = AgencyLaunchSiteSnapshot.Apply(AgencyLaunchSiteSnapshot.Empty, true, 0, new LaunchSiteAssignment[0]);
            Assert.IsTrue(empty.Ready); Assert.AreEqual(0L, empty.Revision); Assert.AreEqual(0, empty.Assignments.Count);
        }
    }
}
