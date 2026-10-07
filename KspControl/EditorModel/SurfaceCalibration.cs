using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace KspControl.EditorModel
{
    /// <summary>One surface-attached placement: where it sits on its parent, and the radius it was placed at.</summary>
    public sealed class SurfaceSite
    {
        public uint ParentCid { get; set; }
        public uint ChildCid { get; set; }
        public string PartId { get; set; }
        public string ChildPart { get; set; }
        /// <summary>Attach height in the parent's local frame, metres.</summary>
        public double Height { get; set; }
        /// <summary>The parent surface radius this instance was placed at, metres.</summary>
        public double RadiusUsed { get; set; }
    }

    /// <summary>What the live pass-1 load reported: per (parent craft id, height) the parent surface radius, and per child part name its live srfAttachNode.</summary>
    public sealed class SurfaceMeasurements
    {
        public Dictionary<string, double> Radii { get; } = new Dictionary<string, double>(StringComparer.Ordinal);
        public Dictionary<string, SurfaceNodeDefinition> ChildNodes { get; } = new Dictionary<string, SurfaceNodeDefinition>(StringComparer.Ordinal);
        public void SetRadius(uint parentCid, double height, double radius) { Radii[SurfaceCalibration.Key(parentCid, height)] = radius; }
    }

    /// <summary>
    /// P2.8 surface placement (plan R1-section 6.5). Surface positions are measured from the live parent instance, never from prefab bounds:
    /// pass 1 places surface subtrees at a provisional radius, the bridge measures each parent's renderer-bounds radius at the attach height and
    /// each child's live srfAttachNode, and this module re-plans with those values (pass 2). Counterparts of a symmetric part (2..8) are
    /// recomputed by the planner's own fan-out about the parent axis, so they stay identical to the single-part placement. Pure.
    /// </summary>
    public static class SurfaceCalibration
    {
        /// <summary>The pass-2 clearance gate: the loaded parent's surface may differ from the radius the child was placed at by at most 5 cm.</summary>
        public const double ClearanceToleranceMetres = 0.05;
        public const double MinRadius = 0.01;
        public const double MaxRadius = 50;
        private const double HeightSlack = 1e-3;

        public static string Key(uint parentCid, double height)
        {
            return parentCid.ToString(CultureInfo.InvariantCulture) + "@" + RotationMath.Number(Math.Round(height, 6, MidpointRounding.AwayFromZero));
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
                    Height = p.Source.Surface.HeightOffset, RadiusUsed = p.SurfaceRadius
                });
            }
            return sites;
        }

        /// <summary>True when the layout has any surface-attached part, so the two-pass flow applies.</summary>
        public static bool NeedsCalibration(StructuralLayout layout) { return layout != null && layout.Parts.Any(p => p.Kind == AttachKind.Surface); }

        /// <summary>The distinct (parent, height) measurements pass 1 must provide.</summary>
        public static List<SurfaceSite> MeasurementSites(StructuralLayout layout)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return Sites(layout).Where(s => seen.Add(Key(s.ParentCid, s.Height))).ToList();
        }

        /// <summary>
        /// Radius of the parent at <paramref name="height"/> from renderer boxes already expressed in the parent's local frame (eight corners each).
        /// A box counts when the height lies inside its vertical extent; the radius is the largest horizontal extent among those boxes, so for a
        /// part whose axis is local Y it is the cylinder radius. Null when no box spans the height or any corner is not finite.
        /// </summary>
        public static double? RadiusAtHeight(IEnumerable<Vector[]> boxes, double height)
        {
            if (boxes == null || !Vector.Finite(height)) return null;
            double? best = null;
            foreach (var corners in boxes)
            {
                if (corners == null || corners.Length == 0) continue;
                double minY = double.MaxValue, maxY = double.MinValue, extent = 0;
                foreach (var c in corners)
                {
                    if (!c.IsFinite) return null;
                    minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
                    extent = Math.Max(extent, Math.Max(Math.Abs(c.X), Math.Abs(c.Z)));
                }
                if (height < minY - HeightSlack || height > maxY + HeightSlack) continue;
                if (!best.HasValue || extent > best.Value) best = extent;
            }
            return best;
        }

        /// <summary>
        /// Pass 2: re-plans the graph with the measured radii and live child surface nodes. Issues carry code <c>surface_measurement_invalid</c> when a
        /// value is missing, not finite or out of range. The catalog passed in is not modified; hashes of it are unchanged.
        /// </summary>
        public static PlanResult Recalibrate(GraphDto graph, ConstructionCatalog catalog, PlannerOptions options, StructuralLayout provisional, SurfaceMeasurements measured)
        {
            var failed = new PlanResult();
            if (graph == null || catalog == null || provisional == null || measured == null) { failed.Issues.Add(new PlanIssue("surface_measurement_invalid", null, "missing input")); return failed; }
            foreach (var site in MeasurementSites(provisional))
            {
                double radius;
                if (!measured.Radii.TryGetValue(Key(site.ParentCid, site.Height), out radius) || !Vector.Finite(radius) || radius < MinRadius || radius > MaxRadius)
                    failed.Issues.Add(new PlanIssue("surface_measurement_invalid", site.PartId, "no usable parent radius at height " + RotationMath.Number(site.Height)));
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
            o.SurfaceRadiusProvider = (cid, height) => { double r; return radii.TryGetValue(Key(cid, height), out r) ? (double?)r : null; };
            return CraftPlanner.Plan(graph, live, o);
        }

        /// <summary>Problems, as text, where the loaded parent surface is farther than the clearance tolerance from the radius used (or could not be measured).</summary>
        public static List<string> Clearance(IEnumerable<SurfaceSite> sites, Func<uint, double, double?> measure)
        {
            var problems = new List<string>();
            foreach (var s in sites)
            {
                var r = measure(s.ParentCid, s.Height);
                if (!r.HasValue) { problems.Add("surface_unmeasured " + s.PartId); continue; }
                var gap = Math.Abs(r.Value - s.RadiusUsed);
                if (!(gap <= ClearanceToleranceMetres + 1e-9))
                    problems.Add("surface_clearance " + s.PartId + " off by " + gap.ToString("0.#####", CultureInfo.InvariantCulture) + " m");
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
