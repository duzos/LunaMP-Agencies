using System;

namespace LmpCommon.Agency
{
    /// <summary>Pure rules for drawing agency flags over launch sites (plan 41 S4). No Unity types.</summary>
    public static class LaunchSiteFlagPolicy
    {
        public const double KscRangeMetres = 50000;

        /// <summary>
        /// A site gets a flag only when the feature and per-agency sites are on, assignments have arrived, the
        /// site is assigned and it is not a hidden KK site. Site assignments are public to every client today;
        /// if secret sites are ever added, filter them here.
        /// </summary>
        public static bool ShouldShow(bool featureOn, bool perAgencySites, bool ready, Guid assigned, bool kkHidden) =>
            featureOn && perAgencySites && ready && assigned != Guid.Empty && !kkHidden;

        /// <summary>
        /// True when the site is not hidden behind the body sphere as seen from the camera (ray-sphere test of the
        /// segment camera to site). Each point is {x, y, z}. A degenerate input is treated as visible.
        /// </summary>
        public static bool AboveHorizon(double[] site, double[] bodyCentre, double bodyRadius, double[] camera)
        {
            if (site == null || bodyCentre == null || camera == null || site.Length != 3 || bodyCentre.Length != 3 || camera.Length != 3) return true;
            double dx = site[0] - camera[0], dy = site[1] - camera[1], dz = site[2] - camera[2];
            double fx = camera[0] - bodyCentre[0], fy = camera[1] - bodyCentre[1], fz = camera[2] - bodyCentre[2];
            var a = dx * dx + dy * dy + dz * dz;
            if (a <= 0) return true;
            var c = fx * fx + fy * fy + fz * fz - bodyRadius * bodyRadius;
            if (c < 0) return true; // camera inside the body: nothing sensible to hide
            var b = 2 * (fx * dx + fy * dy + fz * dz);
            var disc = b * b - 4 * a * c;
            if (disc < 0) return true;
            var t = (-b - Math.Sqrt(disc)) / (2 * a);
            return !(t > 0 && t < 1);
        }

        /// <summary>Whether a squared distance in metres is within the KSC-scene draw range.</summary>
        public static bool WithinKscRange(double distanceSquared, double rangeMetres = KscRangeMetres) =>
            distanceSquared >= 0 && distanceSquared <= rangeMetres * rangeMetres;
    }
}
