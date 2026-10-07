using System;
using System.Globalization;
namespace KspControl.EditorModel
{
    /// <summary>Small quaternion and vector helpers on top of <see cref="Vector"/> and <see cref="Rotation"/>. Pure, double precision.</summary>
    public static class RotationMath
    {
        public static readonly Rotation Identity = new Rotation(0, 0, 0, 1);
        public static Vector Add(Vector a, Vector b) { return new Vector(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vector Sub(Vector a, Vector b) { return new Vector(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vector Neg(Vector a) { return new Vector(-a.X, -a.Y, -a.Z); }
        public static double Dot(Vector a, Vector b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static Vector Cross(Vector a, Vector b) { return new Vector(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
        public static double Length(Vector a) { return Math.Sqrt(a.LengthSquared); }
        public static Vector Normalize(Vector a)
        {
            var l = Length(a);
            if (!(l > 1e-12)) throw new ArgumentException("zero_length_vector");
            return new Vector(a.X / l, a.Y / l, a.Z / l);
        }
        public static Rotation Multiply(Rotation a, Rotation b)
        {
            return Normalize(new Rotation(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z));
        }
        public static Rotation Conjugate(Rotation q) { return new Rotation(-q.X, -q.Y, -q.Z, q.W); }
        public static Rotation Normalize(Rotation q)
        {
            var l = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (!(l > 1e-12)) throw new ArgumentException("zero_length_rotation");
            return new Rotation(q.X / l, q.Y / l, q.Z / l, q.W / l);
        }
        /// <summary>Rotation of <paramref name="degrees"/> about +Y, same convention as Unity's Quaternion.AngleAxis(deg, up).</summary>
        public static Rotation AboutY(double degrees)
        {
            var half = degrees * Math.PI / 360.0;
            return new Rotation(0, Math.Sin(half), 0, Math.Cos(half));
        }
        public static Vector Rotate(Vector v, Rotation q) { return AttachmentGeometry.Rotate(v, Normalize(q)); }
        public static Vector InverseRotate(Vector v, Rotation q) { return AttachmentGeometry.Rotate(v, Conjugate(Normalize(q))); }
        /// <summary>Shortest-arc rotation taking direction <paramref name="from"/> onto <paramref name="to"/>.</summary>
        public static Rotation FromTo(Vector from, Vector to)
        {
            var a = Normalize(from); var b = Normalize(to);
            var d = Dot(a, b);
            if (d > 1 - 1e-12) return Identity;
            if (d < -1 + 1e-12)
            {
                var axis = Cross(a, new Vector(0, 1, 0));
                if (axis.LengthSquared < 1e-12) axis = Cross(a, new Vector(0, 0, 1));
                axis = Normalize(axis);
                return new Rotation(axis.X, axis.Y, axis.Z, 0);
            }
            var c = Cross(a, b);
            return Normalize(new Rotation(c.X, c.Y, c.Z, 1 + d));
        }
        /// <summary>Angle in radians between two unit rotations, in [0, pi].</summary>
        public static double AngleBetween(Rotation a, Rotation b)
        {
            var na = Normalize(a); var nb = Normalize(b);
            var dot = Math.Abs(na.X * nb.X + na.Y * nb.Y + na.Z * nb.Z + na.W * nb.W);
            if (dot > 1) dot = 1;
            return 2 * Math.Acos(dot);
        }
        // KSP writes single-precision values in round-trip form.
        public static string Number(double value)
        {
            var f = (float)value;
            if (f == 0) f = 0; // normalise -0
            return f.ToString("R", CultureInfo.InvariantCulture);
        }
        public static string Format(Vector v) { return Number(v.X) + "," + Number(v.Y) + "," + Number(v.Z); }
        public static string FormatPipe(Vector v) { return Number(v.X) + "|" + Number(v.Y) + "|" + Number(v.Z); }
        public static string Format(Rotation q) { return Number(q.X) + "," + Number(q.Y) + "," + Number(q.Z) + "," + Number(q.W); }
    }
}
