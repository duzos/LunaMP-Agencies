using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace KspControl.EditorModel
{
    /// <summary>One surface-attached placement: where it sits on its parent, and the support distance it was placed at.</summary>
    public sealed class SurfaceSite
    {
        public uint ParentCid { get; set; }
        public uint ChildCid { get; set; }
        public string PartId { get; set; }
        public string ChildPart { get; set; }
        /// <summary>Attach height in the parent's local frame, metres.</summary>
        public double Height { get; set; }
        /// <summary>Outward direction in the parent's frame, degrees in [0,360): 0 is +Z, 90 is +X.</summary>
        public double AngleDegrees { get; set; }
        /// <summary>The parent surface distance along the outward direction this instance was placed at, metres.</summary>
        public double RadiusUsed { get; set; }
    }

    /// <summary>What the live pass-1 load reported: per (parent craft id, height, angle) the parent's surface support, and per child part name its live srfAttachNode.</summary>
    public sealed class SurfaceMeasurements
    {
        public Dictionary<string, double> Radii { get; } = new Dictionary<string, double>(StringComparer.Ordinal);
        public Dictionary<string, SurfaceNodeDefinition> ChildNodes { get; } = new Dictionary<string, SurfaceNodeDefinition>(StringComparer.Ordinal);
        public void SetRadius(uint parentCid, double height, double angleDegrees, double radius) { Radii[SurfaceCalibration.Key(parentCid, height, angleDegrees)] = radius; }
    }

    /// <summary>
    /// P2.8 surface placement (plan R1-section 6.5). Surface positions are measured from the live parent instance, never from prefab bounds:
    /// pass 1 places surface subtrees at a provisional radius, the bridge measures each parent's support along the outward direction at the attach
    /// height (from its own mesh vertices) and each child's live srfAttachNode, and this module re-plans with those values (pass 2). Counterparts of a
    /// symmetric part (2..8) are fanned out about the parent axis by the planner, each measured along its own direction. Pure.
    /// </summary>
    public static class SurfaceCalibration
    {
        /// <summary>The pass-2 clearance gate: the child's live attach point may be at most 5 cm from the loaded parent surface, in height and along the outward direction.</summary>
        public const double ClearanceToleranceMetres = 0.05;
        public const double MinRadius = 0.01;
        public const double MaxRadius = 50;
        /// <summary>Half-height of the vertex band around the attach height.</summary>
        public const double BandMetres = 0.05;

        public static double NormaliseAngle(double degrees)
        {
            var a = degrees % 360.0; if (a < 0) a += 360.0;
            a = Math.Round(a, 6, MidpointRounding.AwayFromZero);
            return a >= 360.0 ? 0.0 : a;
        }

        public static string Key(uint parentCid, double height, double angleDegrees)
        {
            return parentCid.ToString(CultureInfo.InvariantCulture) + "@" + RotationMath.Number(Math.Round(height, 6, MidpointRounding.AwayFromZero))
                + "/" + RotationMath.Number(NormaliseAngle(angleDegrees));
        }

        /// <summary>Every surface-attached part of the layout, in layout order.</summary>
        public static List<SurfaceSite> Sites(StructuralLayout layout)
        {
            var sites = new List<SurfaceSite>();
            if (layout == null) return sites;
            foreach (var p in layout.Parts)
            {
                if (p.Kind != AttachKind.Surface || p.ParentIndex < 0 || p.Source.Surface == null) continue;
                sites.Add(new SurfaceSite
                {
                    ParentCid = layout.Parts[p.ParentIndex].Cid, ChildCid = p.Cid, PartId = p.Source.Id, ChildPart = p.Source.Part,
                    Height = p.Source.Surface.HeightOffset, AngleDegrees = p.SurfaceAngleDegrees, RadiusUsed = p.SurfaceRadius
                });
            }
            return sites;
        }

        /// <summary>True when the layout has any surface-attached part, so the two-pass flow applies.</summary>
        public static bool NeedsCalibration(StructuralLayout layout) { return layout != null && layout.Parts.Any(p => p.Kind == AttachKind.Surface); }

        /// <summary>The distinct (parent, height, angle) measurements pass 1 must provide.</summary>
        public static List<SurfaceSite> MeasurementSites(StructuralLayout layout)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return Sites(layout).Where(s => seen.Add(Key(s.ParentCid, s.Height, s.AngleDegrees))).ToList();
        }

        /// <summary>
        /// The parent's surface distance along the outward direction (angle 0 = +Z, 90 = +X) at <paramref name="height"/>, from mesh vertices already in
        /// the parent's local frame: the vertices within <see cref="BandMetres"/> of the height, or, when there are none, the band around the nearest
        /// vertex height. The support is the largest projection onto the direction, so a cylinder gives its radius at every angle and an offset plate its
        /// real extent. Null when there are no vertices or any is not finite.
        /// </summary>
        public static double? SupportAlong(IEnumerable<Vector> vertices, double height, double angleDegrees)
        {
            if (vertices == null || !Vector.Finite(height) || !Vector.Finite(angleDegrees)) return null;
            var list = new List<Vector>();
            foreach (var v in vertices) { if (!v.IsFinite) return null; list.Add(v); }
            if (list.Count == 0) return null;
            var centre = height;
            if (!list.Any(v => Math.Abs(v.Y - height) <= BandMetres)) centre = list.OrderBy(v => Math.Abs(v.Y - height)).First().Y;
            var a = angleDegrees * Math.PI / 180.0; var sx = Math.Sin(a); var cz = Math.Cos(a);
            double? best = null;
            foreach (var v in list)
            {
                if (Math.Abs(v.Y - centre) > BandMetres) continue;
                var s = v.X * sx + v.Z * cz;
                if (!best.HasValue || s > best.Value) best = s;
            }
            return best;
        }

        /// <summary>
        /// Pass 2: re-plans the graph with the measured supports and live child surface nodes. Issues carry code <c>surface_measurement_invalid</c> when a
        /// value is missing, not finite or out of range. The catalog passed in is not modified; hashes of it are unchanged.
        /// </summary>
        public static PlanResult Recalibrate(GraphDto graph, ConstructionCatalog catalog, PlannerOptions options, StructuralLayout provisional, SurfaceMeasurements measured)
        {
            var failed = new PlanResult();
            if (graph == null || catalog == null || provisional == null || measured == null) { failed.Issues.Add(new PlanIssue("surface_measurement_invalid", null, "missing input")); return failed; }
            foreach (var site in MeasurementSites(provisional))
            {
                double radius;
                if (!measured.Radii.TryGetValue(Key(site.ParentCid, site.Height, site.AngleDegrees), out radius) || !Vector.Finite(radius) || radius < MinRadius || radius > MaxRadius)
                    failed.Issues.Add(new PlanIssue("surface_measurement_invalid", site.PartId, "no usable parent surface at height " + RotationMath.Number(site.Height) + " angle " + RotationMath.Number(site.AngleDegrees)));
            }
            var live = new ConstructionCatalog();
            foreach (var part in catalog.Parts.Values)
            {
                SurfaceNodeDefinition node;
                if (part.SurfaceNode != null && measured.ChildNodes.TryGetValue(part.Name, out node))
                {
                    if (node == null || !node.Position.IsFinite || !node.Orientation.IsFinite || node.Orientation.LengthSquared < 1e-12)
                    { failed.Issues.Add(new PlanIssue("surface_measurement_invalid", null, "unusable live srfAttachNode on " + part.Name)); live.Add(part); continue; }
                    live.Add(WithSurfaceNode(part, node));
                }
                else live.Add(part);
            }
            if (failed.Issues.Count != 0) return failed;
            var radii = new Dictionary<string, double>(measured.Radii, StringComparer.Ordinal);
            var o = (options ?? new PlannerOptions()).Clone();
            o.SurfaceRadiusProvider = (cid, height, angle) => { double r; return radii.TryGetValue(Key(cid, height, angle), out r) ? (double?)r : null; };
            return CraftPlanner.Plan(graph, live, o);
        }

        /// <summary>
        /// The pass-2 gate. For each site the child's live attach point (in the parent's frame) must lie within 5 cm of the attach height and of the
        /// parent's loaded surface along the site's outward direction. Both come from the live loaded craft, so a placement that did not land on the
        /// surface fails; problems are listed as text.
        /// </summary>
        public static List<string> Clearance(IEnumerable<SurfaceSite> sites, Func<uint, double, double, double?> support, Func<uint, uint, Vector?> attachPoint)
        {
            var problems = new List<string>();
            foreach (var s in sites)
            {
                var surface = support(s.ParentCid, s.Height, s.AngleDegrees);
                var point = attachPoint(s.ParentCid, s.ChildCid);
                if (!surface.HasValue || !point.HasValue || !point.Value.IsFinite) { problems.Add("surface_unmeasured " + s.PartId); continue; }
                var a = s.AngleDegrees * Math.PI / 180.0;
                var along = point.Value.X * Math.Sin(a) + point.Value.Z * Math.Cos(a);
                var gap = Math.Abs(along - surface.Value);
                var rise = Math.Abs(point.Value.Y - s.Height);
                if (!(gap <= ClearanceToleranceMetres + 1e-9))
                    problems.Add("surface_clearance " + s.PartId + " off by " + gap.ToString("0.#####", CultureInfo.InvariantCulture) + " m");
                else if (!(rise <= ClearanceToleranceMetres + 1e-9))
                    problems.Add("surface_height " + s.PartId + " off by " + rise.ToString("0.#####", CultureInfo.InvariantCulture) + " m");
            }
            return problems;
        }

        private static ConstructionPart WithSurfaceNode(ConstructionPart p, SurfaceNodeDefinition node)
        {
            return new ConstructionPart
            {
                Name = p.Name, Category = p.Category, Buildable = p.Buildable, ConstructionSupport = p.ConstructionSupport,
                StackNodes = p.StackNodes, AttachRules = p.AttachRules, SurfaceNode = node
            };
        }
    }
}
