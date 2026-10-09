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
        public void StockPassNeverClaimsKkSites()
        {
            // KK catalog known: a KK site (hidden or not) is left to the KK pass.
            Assert.IsFalse(LaunchSiteFlagPolicy.AllowNonKkMark("HiddenBase", true, true, true, false));
            Assert.IsTrue(LaunchSiteFlagPolicy.AllowNonKkMark("Desert_Launch_Site", true, true, false, true));
            Assert.IsTrue(LaunchSiteFlagPolicy.AllowNonKkMark("OtherModFacility", true, true, false, false));
            // KK pad/runway entries never block the stock KSC sites.
            Assert.IsTrue(LaunchSiteFlagPolicy.AllowNonKkMark("LaunchPad", true, true, true, false));
            Assert.IsTrue(LaunchSiteFlagPolicy.AllowNonKkMark("Runway", true, false, true, false));
        }

        [TestMethod]
        public void UnknownKkCatalogFailsClosed()
        {
            Assert.IsFalse(LaunchSiteFlagPolicy.AllowNonKkMark("HiddenBase", true, false, false, false));
            Assert.IsTrue(LaunchSiteFlagPolicy.AllowNonKkMark("Woomerang_Launch_Site", true, false, false, true));
            Assert.IsTrue(LaunchSiteFlagPolicy.AllowNonKkMark("LaunchPad", true, false, false, false));
            Assert.IsFalse(LaunchSiteFlagPolicy.AllowNonKkMark(null, false, false, false, false));
            Assert.IsFalse(LaunchSiteFlagPolicy.AllowNonKkMark("", false, false, false, false));
        }

        [TestMethod]
        public void WithoutKkEverySiteIsStock()
        {
            Assert.IsTrue(LaunchSiteFlagPolicy.AllowNonKkMark("AnyFacility", false, false, false, false));
            Assert.IsTrue(LaunchSiteFlagPolicy.IsStockKscSite("LaunchPad"));
            Assert.IsTrue(LaunchSiteFlagPolicy.IsStockKscSite("Runway"));
            Assert.IsFalse(LaunchSiteFlagPolicy.IsStockKscSite("launchpad"));
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
