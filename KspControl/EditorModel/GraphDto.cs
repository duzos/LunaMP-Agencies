using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace KspControl.EditorModel
{
    /// <summary>Surface placement request: height along the parent's Y axis and angle about it.</summary>
    public sealed class SurfaceDto
    {
        public double HeightOffset { get; set; }
        public double AngleDegrees { get; set; }
    }
    /// <summary>One part of the graph DTO v1. Free transforms are not accepted; the planner computes every transform.</summary>
    public sealed class GraphPartDto
    {
        public string Id { get; set; }
        public string Part { get; set; }
        public string Parent { get; set; }
        /// <summary>Attach node id on the parent (stack attachment).</summary>
        public string ParentNode { get; set; }
        /// <summary>Attach node id on this part (stack attachment).</summary>
        public string Node { get; set; }
        public SurfaceDto Surface { get; set; }
        public int? Symmetry { get; set; }
        /// <summary>KSP inverse-stage numbering; null leaves the choice to the staging rules.</summary>
        public int? Stage { get; set; }
        public List<string> Configuration { get; set; }
    }
    /// <summary>Graph DTO v1: {name, facility:"VAB", root, parts:[...]}.</summary>
    public sealed class GraphDto
    {
        public string Name { get; set; }
        public string Facility { get; set; }
        public string Root { get; set; }
        public List<GraphPartDto> Parts { get; set; } = new List<GraphPartDto>();
    }
    public sealed class PlanIssue
    {
        public string Code { get; }
        public string PartId { get; }
        public string Reason { get; }
        public PlanIssue(string code, string partId, string reason = null) { Code = code; PartId = partId; Reason = reason; }
        public override string ToString() { return Code + (PartId != null ? " [" + PartId + "]" : "") + (Reason != null ? ": " + Reason : ""); }
    }
    public static class GraphDtoLimits
    {
        public const int MaxParts = 250;
        public const int MaxNameLength = 64;
        public const int MaxIdLength = 64;
        public const int MaxPartNameLength = 64;
        public const int MaxNodeIdLength = 64;
        public const double MaxHeightOffset = 50;
        public const int MinSymmetry = 2;
        public const int MaxSymmetry = 8;
        public const int MinStage = -1;
        public const int MaxStage = 99;
    }
    /// <summary>Bounds and shape validation of the graph DTO. Catalog and geometry checks live in the planner.</summary>
    public static class GraphDtoValidator
    {
        private static readonly Regex HandleRegex = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly Regex NodeIdRegex = new Regex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);
        public static bool IsPartName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > GraphDtoLimits.MaxPartNameLength) return false;
            foreach (var c in name)
                if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '-'))) return false;
            return true;
        }
        public static List<PlanIssue> Validate(GraphDto graph)
        {
            var issues = new List<PlanIssue>();
            if (graph == null) { issues.Add(new PlanIssue("invalid_graph", null, "graph missing")); return issues; }
            if (graph.Parts == null || graph.Parts.Count == 0 || graph.Parts.Count > GraphDtoLimits.MaxParts)
            { issues.Add(new PlanIssue("part_count_out_of_range", null, "1.." + GraphDtoLimits.MaxParts + " parts required")); return issues; }
            if (string.IsNullOrWhiteSpace(graph.Name) || graph.Name.Length > GraphDtoLimits.MaxNameLength || HasControl(graph.Name) || graph.Name.Contains("//") || graph.Name.IndexOfAny(new[] { '{', '}' }) >= 0) issues.Add(new PlanIssue("invalid_craft_name", null));
            if (!string.Equals(graph.Facility, "VAB", StringComparison.Ordinal)) issues.Add(new PlanIssue("facility_mismatch", null, "only VAB is supported"));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in graph.Parts)
            {
                if (p == null || p.Id == null || !HandleRegex.IsMatch(p.Id)) { issues.Add(new PlanIssue("invalid_part_id", p == null ? null : p.Id)); continue; }
                if (!ids.Add(p.Id)) issues.Add(new PlanIssue("duplicate_part_id", p.Id));
                if (!IsPartName(p.Part)) issues.Add(new PlanIssue("invalid_part_name", p.Id));
                if (p.Configuration != null && p.Configuration.Count > 0) issues.Add(new PlanIssue("unsupported_configuration", p.Id, "no configuration adapters exist in P2"));
                if (p.Symmetry.HasValue && (p.Symmetry.Value < GraphDtoLimits.MinSymmetry || p.Symmetry.Value > GraphDtoLimits.MaxSymmetry)) issues.Add(new PlanIssue("invalid_symmetry", p.Id));
                if (p.Stage.HasValue && (p.Stage.Value < GraphDtoLimits.MinStage || p.Stage.Value > GraphDtoLimits.MaxStage)) issues.Add(new PlanIssue("invalid_stage", p.Id));
                if (p.Parent != null && !HandleRegex.IsMatch(p.Parent)) issues.Add(new PlanIssue("invalid_parent", p.Id));
                if (p.ParentNode != null && !NodeIdRegex.IsMatch(p.ParentNode)) issues.Add(new PlanIssue("invalid_node_id", p.Id));
                if (p.Node != null && !NodeIdRegex.IsMatch(p.Node)) issues.Add(new PlanIssue("invalid_node_id", p.Id));
                if (p.Surface != null)
                {
                    var s = p.Surface;
                    if (!Vector.Finite(s.HeightOffset) || Math.Abs(s.HeightOffset) > GraphDtoLimits.MaxHeightOffset
                        || !Vector.Finite(s.AngleDegrees) || s.AngleDegrees < 0 || s.AngleDegrees >= 360) issues.Add(new PlanIssue("invalid_surface", p.Id));
                    if (p.ParentNode != null || p.Node != null) issues.Add(new PlanIssue("invalid_attachment", p.Id, "surface and stack nodes are exclusive"));
                }
            }
            if (graph.Root == null || !ids.Contains(graph.Root)) issues.Add(new PlanIssue("missing_root", null));
            return issues;
        }
        private static bool HasControl(string s) { foreach (var c in s) if (char.IsControl(c)) return true; return false; }
    }
}
