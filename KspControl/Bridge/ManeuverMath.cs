using System;

namespace KspControl.Bridge
{
    /// <summary>Stock-orbit arithmetic behind the two planning helpers. Pure doubles, no game types. All results are estimates for near-circular orbits.</summary>
    internal static class ManeuverMath
    {
        internal sealed class HohmannPlan
        {
            /// <summary>Seconds from now to the transfer burn.</summary>
            public double WaitSeconds { get; set; }
            /// <summary>Prograde delta-v of the departure burn, m/s (positive outward).</summary>
            public double DepartureDeltaV { get; set; }
            /// <summary>The phase angle (radians, target ahead of the vessel) that gives an encounter at the end of the transfer.</summary>
            public double RequiredPhaseRadians { get; set; }
            public double TransferSeconds { get; set; }
        }

        /// <summary>Prograde delta-v to circularize at the apoapsis: circular speed there minus the speed on the current orbit (vis-viva).</summary>
        public static double CircularizeAtApoapsis(double mu, double apoapsisRadius, double semiMajorAxis)
        {
            if (!(mu > 0) || !(apoapsisRadius > 0) || !(semiMajorAxis > 0)) throw new ArgumentException("invalid_orbit");
            var speedNow = Math.Sqrt(mu * (2.0 / apoapsisRadius - 1.0 / semiMajorAxis));
            return Math.Sqrt(mu / apoapsisRadius) - speedNow;
        }

        /// <summary>
        /// Bi-impulsive transfer between two near-circular, coplanar orbits about one body. <paramref name="phaseNowRadians"/> is the angle by which the target
        /// leads the vessel (any value; reduced modulo 2 pi). Returns the wait until the departure burn, the burn and the lead angle it assumes.
        /// </summary>
        public static HohmannPlan Hohmann(double mu, double r1, double r2, double phaseNowRadians)
        {
            if (!(mu > 0) || !(r1 > 0) || !(r2 > 0) || Math.Abs(r1 - r2) < 1.0) throw new ArgumentException("invalid_orbit");
            var transferAxis = (r1 + r2) / 2.0;
            var transferSeconds = Math.PI * Math.Sqrt(transferAxis * transferAxis * transferAxis / mu);
            var w1 = Math.Sqrt(mu / (r1 * r1 * r1)); var w2 = Math.Sqrt(mu / (r2 * r2 * r2));
            var required = Math.PI - w2 * transferSeconds;
            // The target's lead changes at (w2 - w1): wait until it equals the required lead.
            var rate = w2 - w1; var delta = required - phaseNowRadians;
            if (rate < 0) { rate = -rate; delta = -delta; }
            var wait = Mod(delta, 2 * Math.PI) / rate;
            var dv = Math.Sqrt(mu / r1) * (Math.Sqrt(2 * r2 / (r1 + r2)) - 1.0);
            return new HohmannPlan { WaitSeconds = wait, DepartureDeltaV = dv, RequiredPhaseRadians = required, TransferSeconds = transferSeconds };
        }

        public static double Mod(double value, double modulus)
        {
            var r = value % modulus;
            return r < 0 ? r + modulus : r;
        }
    }
}
