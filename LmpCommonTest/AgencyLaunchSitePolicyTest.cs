using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyLaunchSitePolicyTest
    {
        [DataTestMethod]
        [DataRow(null, false)]
        [DataRow("", false)]
        [DataRow("   ", false)]
        [DataRow("LaunchPad", true)]
        [DataRow("Custom = Site ", true)]
        [DataRow("line\nbreak", false)]
        public void ExactIdValidation(string site, bool valid) => Assert.AreEqual(valid, AgencyLaunchSitePolicy.IsValidSiteId(site));

        [TestMethod]
        public void EnforcesLengthBound()
        {
            Assert.IsTrue(AgencyLaunchSitePolicy.IsValidSiteId(new string('x', AgencyLaunchSitePolicy.MaxSiteIdLength)));
            Assert.IsFalse(AgencyLaunchSitePolicy.IsValidSiteId(new string('x', AgencyLaunchSitePolicy.MaxSiteIdLength + 1)));
        }

        [TestMethod]
        public void EnabledPolicyRequiresReadyOwnExactAssignment_NoKscFallback()
        {
            var mine = Guid.NewGuid();
            var map = new Dictionary<string, Guid> { ["LaunchPad"] = mine, ["Runway"] = Guid.NewGuid() };
            Assert.IsTrue(AgencyLaunchSitePolicy.CanLaunch(false, false, Guid.Empty, null, null));
            Assert.IsFalse(AgencyLaunchSitePolicy.CanLaunch(true, false, mine, "LaunchPad", map));
            Assert.IsFalse(AgencyLaunchSitePolicy.CanLaunch(true, true, Guid.Empty, "LaunchPad", map));
            Assert.IsFalse(AgencyLaunchSitePolicy.CanLaunch(true, true, mine, "Runway", map));
            Assert.IsFalse(AgencyLaunchSitePolicy.CanLaunch(true, true, mine, "launchpad", map));
            Assert.IsFalse(AgencyLaunchSitePolicy.CanLaunch(true, true, mine, "Unassigned", map));
            Assert.IsFalse(AgencyLaunchSitePolicy.CanLaunch(true, true, mine, null, map));
            Assert.IsTrue(AgencyLaunchSitePolicy.CanLaunch(true, true, mine, "LaunchPad", map));
        }
    }
}
