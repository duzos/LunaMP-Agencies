using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class LaunchSiteFlagPolicyTest
    {
        private static readonly Guid A = Guid.NewGuid();

        [TestMethod]
        public void ShowsOnlyWhenEverythingAllows()
        {
            Assert.IsTrue(LaunchSiteFlagPolicy.ShouldShow(true, true, true, A, false));
            Assert.IsFalse(LaunchSiteFlagPolicy.ShouldShow(false, true, true, A, false));
            Assert.IsFalse(LaunchSiteFlagPolicy.ShouldShow(true, false, true, A, false));
            Assert.IsFalse(LaunchSiteFlagPolicy.ShouldShow(true, true, false, A, false));
            Assert.IsFalse(LaunchSiteFlagPolicy.ShouldShow(true, true, true, Guid.Empty, false));
            Assert.IsFalse(LaunchSiteFlagPolicy.ShouldShow(true, true, true, A, true));
        }

        [TestMethod]
        public void HorizonBlocksTheFarSideOfTheBody()
        {
            var centre = new[] { 0d, 0, 0 };
            var camera = new[] { 2000d, 0, 0 };
            Assert.IsTrue(LaunchSiteFlagPolicy.AboveHorizon(new[] { 600d, 0, 0 }, centre, 600, camera));
            Assert.IsFalse(LaunchSiteFlagPolicy.AboveHorizon(new[] { -600d, 0, 0 }, centre, 600, camera));
            Assert.IsFalse(LaunchSiteFlagPolicy.AboveHorizon(new[] { -600.03, 0, 0 }, centre, 600, camera));
        }

        [TestMethod]
        public void HorizonKeepsNearSideSitesAndFromInside()
        {
            var centre = new[] { 0d, 0, 0 };
            Assert.IsTrue(LaunchSiteFlagPolicy.AboveHorizon(new[] { 301d, 520, 0 }, centre, 600, new[] { 2000d, 0, 0 }));
            Assert.IsFalse(LaunchSiteFlagPolicy.AboveHorizon(new[] { 0d, 601, 0 }, centre, 600, new[] { 2000d, 0, 0 }));
            Assert.IsTrue(LaunchSiteFlagPolicy.AboveHorizon(new[] { 0d, 601, 0 }, centre, 600, new[] { 0d, 700, 0 }));
            Assert.IsTrue(LaunchSiteFlagPolicy.AboveHorizon(new[] { -600d, 0, 0 }, centre, 600, new[] { 0d, 0, 0 }));
        }

        [TestMethod]
        public void BadInputIsVisible()
        {
            Assert.IsTrue(LaunchSiteFlagPolicy.AboveHorizon(null, new[] { 0d, 0, 0 }, 1, new[] { 0d, 0, 0 }));
            Assert.IsTrue(LaunchSiteFlagPolicy.AboveHorizon(new[] { 1d }, new[] { 0d, 0, 0 }, 1, new[] { 0d, 0, 0 }));
        }

        [TestMethod]
        public void KscRangeIsFiftyKilometres()
        {
            Assert.IsTrue(LaunchSiteFlagPolicy.WithinKscRange(49999d * 49999d));
            Assert.IsTrue(LaunchSiteFlagPolicy.WithinKscRange(50000d * 50000d));
            Assert.IsFalse(LaunchSiteFlagPolicy.WithinKscRange(50001d * 50001d));
            Assert.IsFalse(LaunchSiteFlagPolicy.WithinKscRange(-1));
        }
    }
}
