using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class NameplatePolicyTest
    {
        private static readonly Guid Mine = Guid.NewGuid(), Rival = Guid.NewGuid();
        private const double Km = 2.5;
        private static double Sq(double metres) => metres * metres;

        [TestMethod]
        public void RivalVisibleCraftInRangeShows()
        {
            Assert.IsTrue(NameplatePolicy.ShouldShow(true, Rival, Mine, false, true, VesselTypeCode.Ship, Sq(1000), Km));
            Assert.IsTrue(NameplatePolicy.ShouldShow(true, Rival, Mine, false, true, VesselTypeCode.Eva, Sq(2500), Km), "range edge is inclusive and EVA is allowed");
            Assert.IsTrue(NameplatePolicy.ShouldShow(true, Rival, Guid.Empty, false, true, VesselTypeCode.Probe, 0, Km), "an agency-less viewer still sees agency craft");
        }

        [TestMethod]
        public void ClassifiedCraftNeverShow()
        {
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Rival, Mine, false, false, VesselTypeCode.Ship, Sq(10), Km));
            Assert.IsTrue(NameplatePolicy.IsCandidate(true, Rival, Mine, false, VesselTypeCode.Ship, Sq(10), Km), "candidate check excludes visibility on purpose");
        }

        [TestMethod]
        public void OwnUnownedAndActiveCraftDoNotShow()
        {
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Mine, Mine, false, true, VesselTypeCode.Ship, Sq(10), Km));
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Guid.Empty, Mine, false, true, VesselTypeCode.Ship, Sq(10), Km));
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Guid.Empty, Guid.Empty, false, true, VesselTypeCode.Ship, Sq(10), Km));
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Rival, Mine, true, true, VesselTypeCode.Ship, 0, Km));
        }

        [TestMethod]
        public void FeatureOffAndOutOfRangeDoNotShow()
        {
            Assert.IsFalse(NameplatePolicy.ShouldShow(false, Rival, Mine, false, true, VesselTypeCode.Ship, Sq(10), Km));
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Rival, Mine, false, true, VesselTypeCode.Ship, Sq(2500.5), Km));
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Rival, Mine, false, true, VesselTypeCode.Ship, double.NaN, Km));
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Rival, Mine, false, true, VesselTypeCode.Ship, 0, 0));
            Assert.IsFalse(NameplatePolicy.ShouldShow(true, Rival, Mine, false, true, VesselTypeCode.Ship, 0, double.NaN));
        }

        [TestMethod]
        public void NoiseTypesAreExcluded()
        {
            foreach (var type in new[] { VesselTypeCode.Debris, VesselTypeCode.Flag, VesselTypeCode.SpaceObject, VesselTypeCode.Unknown,
                         VesselTypeCode.DroppedPart, VesselTypeCode.DeployedSciencePart, VesselTypeCode.DeployedGroundPart, (VesselTypeCode)99 })
                Assert.IsFalse(NameplatePolicy.ShouldShow(true, Rival, Mine, false, true, type, 0, Km), type.ToString());
            foreach (var type in new[] { VesselTypeCode.Probe, VesselTypeCode.Relay, VesselTypeCode.Rover, VesselTypeCode.Lander, VesselTypeCode.Ship,
                         VesselTypeCode.Plane, VesselTypeCode.Station, VesselTypeCode.Base, VesselTypeCode.Eva, VesselTypeCode.DeployedScienceController })
                Assert.IsTrue(NameplatePolicy.ShouldShow(true, Rival, Mine, false, true, type, 0, Km), type.ToString());
        }

        [TestMethod]
        public void TypeCodesMatchKspVesselTypeOrder()
        {
            // KSP VesselType declaration order (Assembly-CSharp), which the client casts from.
            Assert.AreEqual(0, (int)VesselTypeCode.Debris);
            Assert.AreEqual(2, (int)VesselTypeCode.Unknown);
            Assert.AreEqual(11, (int)VesselTypeCode.Eva);
            Assert.AreEqual(12, (int)VesselTypeCode.Flag);
            Assert.AreEqual(16, (int)VesselTypeCode.DeployedGroundPart);
        }

        [TestMethod]
        public void AlphaIsFullThenFades()
        {
            Assert.AreEqual(1f, NameplatePolicy.Alpha(0, 2500));
            Assert.AreEqual(1f, NameplatePolicy.Alpha(1750, 2500));
            Assert.AreEqual(0.625f, NameplatePolicy.Alpha(2125, 2500), 1e-4);
            Assert.AreEqual(0.25f, NameplatePolicy.Alpha(2500, 2500), 1e-6);
            Assert.AreEqual(0.25f, NameplatePolicy.Alpha(9000, 2500), 1e-6);
            Assert.AreEqual(0.25f, NameplatePolicy.Alpha(10, 0), 1e-6);
            Assert.AreEqual(0.25f, NameplatePolicy.Alpha(double.NaN, 2500), 1e-6);
            Assert.IsTrue(NameplatePolicy.Alpha(2000, 2500) > NameplatePolicy.Alpha(2400, 2500));
        }

        [TestMethod]
        public void InsertionKeepsTheNearestClampCount()
        {
            const int n = NameplatePolicy.ClampCount;
            var distances = new double[n];
            var ids = new int[n];
            var count = 0;
            // 40 candidates in a scrambled order; the nearest 16 (distances 0..15) must survive, ascending.
            for (var k = 0; k < 40; k++)
            {
                var id = (k * 17) % 40;
                double d = id;
                var slot = NameplatePolicy.InsertSlot(distances, count, d);
                if (slot < 0) continue;
                NameplatePolicy.ShiftRight(distances, count, slot);
                NameplatePolicy.ShiftRight(ids, count, slot);
                distances[slot] = d;
                ids[slot] = id;
                if (count < n) count++;
            }
            Assert.AreEqual(n, count);
            for (var i = 0; i < n; i++)
            {
                Assert.AreEqual(i, ids[i]);
                Assert.AreEqual(i, distances[i]);
            }
        }

        [TestMethod]
        public void InsertionRejectsFartherWhenFullAndAppendsWhenNot()
        {
            var distances = new double[] { 1, 2, 3 };
            Assert.AreEqual(-1, NameplatePolicy.InsertSlot(distances, 3, 3));
            Assert.AreEqual(-1, NameplatePolicy.InsertSlot(distances, 3, 9));
            Assert.AreEqual(2, NameplatePolicy.InsertSlot(distances, 3, 2.5));
            Assert.AreEqual(0, NameplatePolicy.InsertSlot(distances, 3, 0));
            Assert.AreEqual(2, NameplatePolicy.InsertSlot(distances, 2, 7), "appends after the filled prefix");
            Assert.AreEqual(0, NameplatePolicy.InsertSlot(distances, 0, 7));
        }
    }
}
