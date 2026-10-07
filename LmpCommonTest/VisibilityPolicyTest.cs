using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class VisibilityPolicyTest
    {
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
        public void DetectionDebuffScalesAntennaOnlyAndInvalidSettingsUseOnePercent()
        {
            Assert.AreEqual(50000d, VisibilityPolicy.DetectionRadius(5000000, .01));
            foreach (var invalid in new[] { 0d, -1, 2, double.NaN, double.PositiveInfinity })
                Assert.AreEqual(50000d, VisibilityPolicy.DetectionRadius(5000000, invalid));
            var index = new VisibilitySensorIndex(new[] { new VisibilitySensor { Position = new VisibilityPoint(0, 0, 0), SensorRadius = VisibilityPolicy.DetectionRadius(5000000, .01) } });
            Assert.IsTrue(index.InSensorRange(new VisibilityPoint(50000, 0, 0)));
            Assert.IsFalse(index.InSensorRange(new VisibilityPoint(50000.01, 0, 0)));
            var weak = new VisibilitySensorIndex(new[] { new VisibilitySensor { Position = new VisibilityPoint(0, 0, 0), SensorRadius = VisibilityPolicy.DetectionRadius(1000, .01) } });
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
