using System;
using System.Collections.Generic;
using System.Linq;

namespace LmpCommon.Agency
{
    public enum VisibilityOverride : byte { Inherit, Allow, Deny }
    public static class VisibilityPolicy
    {
        public const double PhysicsFloor = 2500;
        public const double DefaultDetectionRangeMultiplier = .01;
        public static double NormalizeDetectionMultiplier(double value) =>
            VisibilityPoint.Finite(value) && value > 0 && value <= 1 ? value : DefaultDetectionRangeMultiplier;
        public static double DetectionRadius(double antennaRange, double multiplier) =>
            VisibilityPoint.Finite(antennaRange) && antennaRange > 0 ? antennaRange * NormalizeDetectionMultiplier(multiplier) : 0;
        public static bool CanSee(Guid viewer, Guid owner, bool ready, bool agencyShared,
            VisibilityOverride rule, bool inPhysics, bool inSensor)
        {
            if (viewer != Guid.Empty && viewer == owner || inPhysics) return true;
            if (!ready || viewer == Guid.Empty) return false;
            return inSensor || rule == VisibilityOverride.Allow || rule == VisibilityOverride.Inherit && agencyShared;
        }
    }

    public struct VisibilityPoint
    {
        public readonly double X, Y, Z;
        public VisibilityPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
        internal double Axis(int axis) => axis == 0 ? X : axis == 1 ? Y : Z;
        internal bool Valid => Finite(X) && Finite(Y) && Finite(Z);
        // Well beyond KSP coordinates while ensuring squared distances cannot overflow.
        internal static bool Finite(double value) => !double.IsNaN(value) && Math.Abs(value) <= 1e100;
    }
    public sealed class VisibilitySensor
    {
        public VisibilityPoint Position;
        public double SensorRadius, PhysicsRadius;
    }

    public struct VisibilitySphere
    {
        public readonly VisibilityPoint Center;
        public readonly double Radius;
        public VisibilitySphere(VisibilityPoint center, double radius) { Center = center; Radius = radius; }
    }

    public static class VisibilityLineOfSight
    {
        /// <summary>Solid body spheres only; tangency and an outward ray from the surface are clear.</summary>
        public static bool IsClear(VisibilityPoint start, VisibilityPoint end, IEnumerable<VisibilitySphere> bodies)
        {
            if (!start.Valid || !end.Valid || bodies == null) return false;
            var dx = end.X - start.X; var dy = end.Y - start.Y; var dz = end.Z - start.Z;
            var lengthSquared = dx * dx + dy * dy + dz * dz;
            foreach (var body in bodies)
            {
                if (!body.Center.Valid || !VisibilityPoint.Finite(body.Radius) || body.Radius <= 0) return false;
                var x = start.X - body.Center.X; var y = start.Y - body.Center.Y; var z = start.Z - body.Center.Z;
                var t = lengthSquared == 0 ? 0 : Math.Max(0, Math.Min(1, -(x * dx + y * dy + z * dz) / lengthSquared));
                x += t * dx; y += t * dy; z += t * dz;
                // Coordinates are bounded at 1e100, so these products cannot overflow.
                // A tiny relative tolerance avoids roundoff hiding a surface detector looking outward.
                if (x * x + y * y + z * z < body.Radius * body.Radius * (1 - 1e-12)) return false;
            }
            return true;
        }
    }

    /// <summary>Immutable balanced spatial tree; renderer hooks use cached decisions, not this index directly.</summary>
    public sealed class VisibilitySensorIndex
    {
        private sealed class Node
        {
            internal VisibilitySensor Sensor;
            internal Node Left, Right;
            internal double MinX, MinY, MinZ, MaxX, MaxY, MaxZ, MaxSensor, MaxPhysics;
        }
        private readonly Node root;
        public VisibilitySensorIndex(IEnumerable<VisibilitySensor> sensors)
        {
            var copy = (sensors ?? Array.Empty<VisibilitySensor>()).Where(s => s != null && s.Position.Valid)
                .Select(s => new VisibilitySensor { Position = s.Position, SensorRadius = Radius(s.SensorRadius),
                    PhysicsRadius = Math.Max(VisibilityPolicy.PhysicsFloor, Radius(s.PhysicsRadius)) }).ToArray();
            root = Build(copy, 0, copy.Length, 0);
        }
        private static double Radius(double value) => VisibilityPoint.Finite(value) && value > 0 ? value : 0;
        private static Node Build(VisibilitySensor[] sensors, int start, int length, int axis)
        {
            if (length == 0) return null;
            Array.Sort(sensors, start, length, Comparer<VisibilitySensor>.Create((a, b) => a.Position.Axis(axis).CompareTo(b.Position.Axis(axis))));
            var middle = start + length / 2;
            var sensor = sensors[middle]; var p = sensor.Position;
            var node = new Node { Sensor = sensor, MinX = p.X, MaxX = p.X, MinY = p.Y, MaxY = p.Y, MinZ = p.Z, MaxZ = p.Z,
                MaxSensor = sensor.SensorRadius, MaxPhysics = sensor.PhysicsRadius,
                Left = Build(sensors, start, middle - start, (axis + 1) % 3),
                Right = Build(sensors, middle + 1, start + length - middle - 1, (axis + 1) % 3) };
            Include(node, node.Left); Include(node, node.Right); return node;
        }
        private static void Include(Node node, Node child)
        {
            if (child == null) return;
            node.MinX = Math.Min(node.MinX, child.MinX); node.MaxX = Math.Max(node.MaxX, child.MaxX);
            node.MinY = Math.Min(node.MinY, child.MinY); node.MaxY = Math.Max(node.MaxY, child.MaxY);
            node.MinZ = Math.Min(node.MinZ, child.MinZ); node.MaxZ = Math.Max(node.MaxZ, child.MaxZ);
            node.MaxSensor = Math.Max(node.MaxSensor, child.MaxSensor); node.MaxPhysics = Math.Max(node.MaxPhysics, child.MaxPhysics);
        }
        public bool InSensorRange(VisibilityPoint target, Func<VisibilityPoint, VisibilityPoint, bool> lineOfSight = null) =>
            target.Valid && Query(root, target, false, 0, lineOfSight);
        public bool InPhysicsRange(VisibilityPoint target, double targetPhysicsRadius = VisibilityPolicy.PhysicsFloor) =>
            target.Valid && Query(root, target, true, Math.Max(VisibilityPolicy.PhysicsFloor, Radius(targetPhysicsRadius)), null);
        private static double Outside(double value, double min, double max) => value < min ? min - value : value > max ? value - max : 0;
        private static bool Query(Node node, VisibilityPoint target, bool physics, double floor, Func<VisibilityPoint, VisibilityPoint, bool> lineOfSight)
        {
            if (node == null) return false;
            var radius = physics ? Math.Max(node.MaxPhysics, floor) : node.MaxSensor;
            if (radius <= 0) return false;
            var dx = Outside(target.X, node.MinX, node.MaxX); var dy = Outside(target.Y, node.MinY, node.MaxY); var dz = Outside(target.Z, node.MinZ, node.MaxZ);
            if (dx * dx + dy * dy + dz * dz > radius * radius) return false;
            var position = node.Sensor.Position;
            radius = physics ? Math.Max(node.Sensor.PhysicsRadius, floor) : node.Sensor.SensorRadius;
            dx = target.X - position.X; dy = target.Y - position.Y; dz = target.Z - position.Z;
            if (radius > 0 && dx * dx + dy * dy + dz * dz <= radius * radius &&
                (lineOfSight == null || lineOfSight(position, target))) return true;
            return Query(node.Left, target, physics, floor, lineOfSight) || Query(node.Right, target, physics, floor, lineOfSight);
        }
    }
}
