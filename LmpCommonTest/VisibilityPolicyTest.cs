using System;
using System.Linq;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class VisibilityPolicyTest
    {
        private static VisibilitySensorIndex Index(params VisibilitySensor[] sensors) => new VisibilitySensorIndex(sensors);
        private static VisibilitySensor Craft(double x, double radius) => new VisibilitySensor { Position = new VisibilityPoint(x, 0, 0), SensorRadius = radius };
        private static bool Sees(VisibilitySensor[] all, VisibilitySensor[] active, bool targetActive, double targetX, Func<VisibilityPoint, VisibilityPoint, bool> los = null) =>
            VisibilityPolicy.Detects(Index(all), Index(active), targetActive, new VisibilityPoint(targetX, 0, 0), los);

        [TestMethod]
        public void RadarPowerIsTheSumAndRadiusIsLinearInPower()
        {
            Assert.AreEqual(5000000d + 500000, VisibilityPolicy.SumPower(new[] { 5000000d, 500000 }));
            Assert.AreEqual(2 * VisibilityPolicy.SumPower(new[] { 5000d }), VisibilityPolicy.SumPower(new[] { 5000d, 5000 }));
            Assert.AreEqual(5000000d, VisibilityPolicy.SumPower(Enumerable.Repeat(500000d, 10)), "Ten Communotron 16 equal one HG-5.");
            var single = VisibilityPolicy.RadarRadius(5000, .2, 3);
            Assert.AreEqual(3000d, single, 1e-9);
            Assert.AreEqual(2 * single, VisibilityPolicy.RadarRadius(10000, .2, 3), 1e-9);
            Assert.AreEqual(300000d, VisibilityPolicy.RadarRadius(500000, .2, 3), 1e-6);
            Assert.AreEqual(3000000d, VisibilityPolicy.RadarRadius(5000000, .2, 3), 1e-5);
            Assert.AreEqual(0d, VisibilityPolicy.RadarRadius(0, .2, 3));
        }
        [TestMethod]
        public void PowerSumIgnoresBadValuesAndRadiusIsCapped()
        {
            Assert.AreEqual(10d, VisibilityPolicy.SumPower(new[] { 10d, double.NaN, double.PositiveInfinity, -5, 1e101 }));
            Assert.AreEqual(0d, VisibilityPolicy.SumPower(null));
            Assert.AreEqual(1e100, VisibilityPolicy.SumPower(new[] { 1e100, 1e100, 1e100 }));
            Assert.AreEqual(1e100, VisibilityPolicy.RadarRadius(1e100, 1, 100));
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1d })
                Assert.AreEqual(0d, VisibilityPolicy.RadarRadius(bad, .2, 3));
            Assert.AreEqual(3000d, VisibilityPolicy.RadarRadius(5000, double.NaN, 3), 1e-9, "Invalid detection multiplier falls back to the default.");
            Assert.AreEqual(3000d, VisibilityPolicy.RadarRadius(5000, .2, double.NaN), 1e-9, "Invalid active multiplier falls back to the default.");
        }
        [TestMethod]
        public void InvalidDetectionMultipliersUseTheDefault()
        {
            Assert.AreEqual(.2, VisibilityPolicy.DefaultDetectionRangeMultiplier);
            foreach (var invalid in new[] { 0d, -1, 2, double.NaN, double.PositiveInfinity })
                Assert.AreEqual(.2, VisibilityPolicy.NormalizeDetectionMultiplier(invalid));
            Assert.AreEqual(1d, VisibilityPolicy.NormalizeDetectionMultiplier(1));
        }
        [TestMethod]
        public void PassivePassiveNeverDetectsAndActiveSeesPassive()
        {
            var passive = new[] { Craft(0, 50000) };
            Assert.IsFalse(Sees(passive, new VisibilitySensor[0], false, 10), "Passive/passive cannot detect, even when very close and powerful.");
            var active = new[] { Craft(0, 50000) };
            Assert.IsTrue(Sees(active, active, false, 50000), "Active sees a passive craft at its own boundary.");
            Assert.IsFalse(Sees(active, active, false, 50000.1));
            Assert.IsTrue(VisibilityPolicy.CanDetect(true, false)); Assert.IsTrue(VisibilityPolicy.CanDetect(false, true));
            Assert.IsTrue(VisibilityPolicy.CanDetect(true, true)); Assert.IsFalse(VisibilityPolicy.CanDetect(false, false));
        }
        [TestMethod]
        public void PassiveHearsActiveTargetOnlyWithinItsOwnRadius()
        {
            // Regression: a probe core (3 km) must not hear an active HG-5 (3,000 km) at the HG-5's range; it hears it only inside its own 3 km.
            var probe = new[] { Craft(0, VisibilityPolicy.RadarRadius(5000, .2, 3)) };
            Assert.IsTrue(Sees(probe, new VisibilitySensor[0], true, 3000));
            Assert.IsFalse(Sees(probe, new VisibilitySensor[0], true, 3000.1));
            Assert.IsFalse(Sees(probe, new VisibilitySensor[0], true, 150000), "No cap at the target's emission radius any more.");
            var hg5 = new[] { Craft(0, VisibilityPolicy.RadarRadius(5000000, .2, 3)) };
            Assert.IsTrue(Sees(hg5, new VisibilitySensor[0], true, 150000), "A powerful passive listener hears an active target within its own range.");
        }
        [TestMethod]
        public void ZeroPowerOwnCraftStillGivesPhysicsRangeButNeverSensesAtRange()
        {
            var silent = new[] { new VisibilitySensor { Position = new VisibilityPoint(0, 0, 0) } };
            var all = Index(silent);
            Assert.IsTrue(all.InPhysicsRange(new VisibilityPoint(2500, 0, 0)));
            Assert.IsFalse(VisibilityPolicy.Detects(all, Index(), true, new VisibilityPoint(100, 0, 0)), "Zero power has no radar radius, even toward an active target.");
            Assert.IsFalse(VisibilityPolicy.Detects(all, Index(), false, new VisibilityPoint(100, 0, 0)));
        }
        [TestMethod]
        public void SightLineAppliesToBothIndexPaths()
        {
            var bodies = new[] { new VisibilitySphere(new VisibilityPoint(0, 0, 0), 10) };
            Func<VisibilityPoint, VisibilityPoint, bool> los = (a, b) => VisibilityLineOfSight.IsClear(a, b, bodies);
            var sensors = new[] { Craft(-20, 100) };
            Assert.IsFalse(Sees(sensors, sensors, true, 20, los), "Active target behind a planet is hidden from the all-sensor path.");
            Assert.IsFalse(Sees(sensors, sensors, false, 20, los), "Passive target behind a planet is hidden from the active path.");
            Assert.IsTrue(Sees(sensors, sensors, true, 20), "Clear without bodies.");
            Assert.IsTrue(Sees(sensors, sensors, false, 20));
        }
        [TestMethod]
        public void SearchExpandsTreeBoundsAndContinuesPastOccludedObserver()
        {
            var sensors = new[] { Craft(-20, 50), Craft(-15, 50), Craft(25, 50) };
            var bodies = new[] { new VisibilitySphere(new VisibilityPoint(0, 0, 0), 10) };
            Assert.IsTrue(Sees(sensors, sensors, true, 20, (a, b) => VisibilityLineOfSight.IsClear(a, b, bodies)), "The unblocked observer on the far side reveals it.");
            Assert.IsFalse(Sees(new[] { Craft(-20, 50), Craft(-15, 50) }, sensors, true, 20, (a, b) => VisibilityLineOfSight.IsClear(a, b, bodies)));
        }
        [TestMethod]
        public void SolidBodiesBlockFarSideButNotSurfaceOutwardOrTangency()
        {
            var bodies = new[] { new VisibilitySphere(new VisibilityPoint(0, 0, 0), 10) };
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(-20, 0, 0), new VisibilityPoint(20, 0, 0), bodies));
            Assert.IsTrue(VisibilityLineOfSight.IsClear(new VisibilityPoint(10, 0, 0), new VisibilityPoint(20, 0, 0), bodies));
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(10, 0, 0), new VisibilityPoint(-20, 0, 0), bodies));
            Assert.IsTrue(VisibilityLineOfSight.IsClear(new VisibilityPoint(-20, 10, 0), new VisibilityPoint(20, 10, 0), bodies));
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(-20, 9, 0), new VisibilityPoint(20, 9, 0), bodies));
            Assert.IsTrue(VisibilityLineOfSight.IsClear(new VisibilityPoint(20, 0, 0), new VisibilityPoint(20, 0, 0), bodies));
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(0, 0, 0), new VisibilityPoint(0, 0, 0), bodies));
        }

        [TestMethod]
        public void AnyUnblockedDetectorCanRevealAndPhysicsIgnoresBodies()
        {
            var index = new VisibilitySensorIndex(new[] {
                new VisibilitySensor { Position = new VisibilityPoint(-20, 0, 0), SensorRadius = 100 },
                new VisibilitySensor { Position = new VisibilityPoint(-15, 0, 0), SensorRadius = 100 },
                new VisibilitySensor { Position = new VisibilityPoint(25, 0, 0), SensorRadius = 100 } });
            var bodies = new[] { new VisibilitySphere(new VisibilityPoint(0, 0, 0), 10) };
            var evaluated = 0;
            Assert.IsTrue(index.InSensorRange(new VisibilityPoint(20, 0, 0), (a,b) => { evaluated++; return VisibilityLineOfSight.IsClear(a,b,bodies); }));
            Assert.IsTrue(evaluated >= 2, "Search must continue past the blocked middle detector.");
            var blocked = new VisibilitySensorIndex(new[] { new VisibilitySensor { Position = new VisibilityPoint(-20, 0, 0), SensorRadius = 100 } });
            Assert.IsFalse(blocked.InSensorRange(new VisibilityPoint(20, 0, 0), (a,b) => VisibilityLineOfSight.IsClear(a,b,bodies)));
            Assert.IsTrue(blocked.InPhysicsRange(new VisibilityPoint(20, 0, 0)));
        }

        [TestMethod]
        public void MultipleBodiesAndUpdatedTranslatedSnapshotsRemainConsistent()
        {
            var bodies = new[] { new VisibilitySphere(new VisibilityPoint(1e12, 0, 0), 10), new VisibilitySphere(new VisibilityPoint(1e12 + 50, 0, 0), 5) };
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(1e12 + 20, 0, 0), new VisibilityPoint(1e12 + 80, 0, 0), bodies));
            Assert.IsTrue(VisibilityLineOfSight.IsClear(new VisibilityPoint(1e12 + 20, 20, 0), new VisibilityPoint(1e12 + 80, 20, 0), bodies));
            bodies[1] = new VisibilitySphere(new VisibilityPoint(1e12 + 50, 30, 0), 5);
            Assert.IsTrue(VisibilityLineOfSight.IsClear(new VisibilityPoint(1e12 + 20, 0, 0), new VisibilityPoint(1e12 + 80, 0, 0), bodies));
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(-1e100, 0, 0), new VisibilityPoint(1e100, 0, 0), new[] { new VisibilitySphere(new VisibilityPoint(0, 0, 0), 1e99) }));
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(double.NaN, 0, 0), new VisibilityPoint(0, 0, 0), bodies));
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(0, 0, 0), new VisibilityPoint(20, 0, 0), null));
            Assert.IsFalse(VisibilityLineOfSight.IsClear(new VisibilityPoint(0, 0, 0), new VisibilityPoint(20, 0, 0), new[] { new VisibilitySphere(new VisibilityPoint(0, 0, 0), double.PositiveInfinity) }));
        }

        [TestMethod]
        public void RadarRadiusDrivesIndexBoundaryAndWeakCraftStayBelowPhysicsFloor()
        {
            var index = new VisibilitySensorIndex(new[] { new VisibilitySensor { Position = new VisibilityPoint(0, 0, 0), SensorRadius = VisibilityPolicy.RadarRadius(5000000, .01, 3) } });
            Assert.IsTrue(index.InSensorRange(new VisibilityPoint(150000, 0, 0)));
            Assert.IsFalse(index.InSensorRange(new VisibilityPoint(150000.01, 0, 0)));
            var weak = new VisibilitySensorIndex(new[] { new VisibilitySensor { Position = new VisibilityPoint(0, 0, 0), SensorRadius = VisibilityPolicy.RadarRadius(1000, .01, 3) } });
            Assert.IsTrue(weak.InPhysicsRange(new VisibilityPoint(2500, 0, 0)));
            Assert.IsFalse(weak.InSensorRange(new VisibilityPoint(2500, 0, 0)));
        }
        [TestMethod]
        public void OwnCraftAndPhysicsContactSurviveUnreadySharingState()
        {
            var mine = Guid.NewGuid(); var enemy = Guid.NewGuid();
            Assert.IsTrue(VisibilityPolicy.CanSee(mine, mine, false, false, VisibilityOverride.Deny, false, false));
            Assert.IsTrue(VisibilityPolicy.CanSee(mine, enemy, false, false, VisibilityOverride.Deny, true, false));
            Assert.IsFalse(VisibilityPolicy.CanSee(mine, enemy, false, true, VisibilityOverride.Allow, false, true));
            Assert.IsFalse(VisibilityPolicy.CanSee(Guid.Empty, Guid.Empty, false, false, VisibilityOverride.Inherit, false, false));
        }
        [TestMethod]
        public void CraftOverridesAgencySharingButCannotSuppressSensorReveal()
        {
            var mine = Guid.NewGuid(); var enemy = Guid.NewGuid();
            Assert.IsTrue(VisibilityPolicy.CanSee(mine, enemy, true, true, VisibilityOverride.Inherit, false, false));
            Assert.IsFalse(VisibilityPolicy.CanSee(mine, enemy, true, true, VisibilityOverride.Deny, false, false));
            Assert.IsTrue(VisibilityPolicy.CanSee(mine, enemy, true, false, VisibilityOverride.Allow, false, false));
            Assert.IsTrue(VisibilityPolicy.CanSee(mine, enemy, true, false, VisibilityOverride.Deny, false, true));
        }
        [TestMethod]
        public void SpatialIndexHonorsExactBoundaryAndNoAntennaPhysicsOnly()
        {
            var index = new VisibilitySensorIndex(new[] { new VisibilitySensor { Position = new VisibilityPoint(100, 200, 300), SensorRadius = 10000, PhysicsRadius = 2500 } });
            Assert.IsTrue(index.InSensorRange(new VisibilityPoint(10100, 200, 300)));
            Assert.IsFalse(index.InSensorRange(new VisibilityPoint(10100.01, 200, 300)));
            var none = new VisibilitySensorIndex(new[] { new VisibilitySensor { Position = new VisibilityPoint(0, 0, 0) } });
            Assert.IsFalse(none.InSensorRange(new VisibilityPoint(0, 0, 0)));
            Assert.IsTrue(none.InPhysicsRange(new VisibilityPoint(2500, 0, 0)));
            Assert.IsFalse(none.InPhysicsRange(new VisibilityPoint(2500.1, 0, 0)));
            Assert.IsTrue(none.InPhysicsRange(new VisibilityPoint(5000, 0, 0), 5000));
        }
        [TestMethod]
        public void IndexMatchesBruteForceAcrossSeparatedObserversAndIsImmutable()
        {
            var sensors = new VisibilitySensor[40];
            for (var i = 0; i < sensors.Length; i++) sensors[i] = new VisibilitySensor { Position = new VisibilityPoint(i * 30000, i % 3 * 5000, 0), SensorRadius = 4000 + i * 200, PhysicsRadius = 3000 };
            var index = new VisibilitySensorIndex(sensors);
            for (var i = 0; i < 300; i++)
            {
                var point = new VisibilityPoint(i * 4000, 5000, 0);
                var expected = false;
                foreach (var sensor in sensors)
                {
                    var dx = point.X - sensor.Position.X; var dy = point.Y - sensor.Position.Y;
                    expected |= dx * dx + dy * dy <= sensor.SensorRadius * sensor.SensorRadius;
                }
                Assert.AreEqual(expected, index.InSensorRange(point), "query " + i);
            }
            sensors[0].SensorRadius = 1e10;
            Assert.IsFalse(index.InSensorRange(new VisibilityPoint(-100000, 0, 0)));
        }
        [TestMethod]
        public void InvalidCoordinatesAndPowerCannotRevealForeignCraft()
        {
            var index = new VisibilitySensorIndex(new[] { new VisibilitySensor { Position = new VisibilityPoint(double.NaN, 0, 0), SensorRadius = 1e10 }, new VisibilitySensor { Position = new VisibilityPoint(0, 0, 0), SensorRadius = double.PositiveInfinity } });
            Assert.IsFalse(index.InSensorRange(new VisibilityPoint(100, 0, 0)));
            Assert.IsFalse(index.InPhysicsRange(new VisibilityPoint(double.NaN, 0, 0)));
            Assert.IsFalse(new VisibilitySensorIndex(Array.Empty<VisibilitySensor>()).InPhysicsRange(new VisibilityPoint(0, 0, 0)));
        }
    }
}
