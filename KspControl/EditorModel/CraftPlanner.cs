using System;
using System.Collections.Generic;
using System.Linq;
namespace KspControl.EditorModel
{
    public sealed class PlannerOptions
    {
        public uint FirstCraftId { get; set; } = 100000;
        /// <summary>Supplies persistentId values (header first, then one per part). Null means a sequential counter from 1.</summary>
        public Func<uint> PersistentIdGenerator { get; set; }
        public string Version { get; set; } = "1.12.5";
        public string Description { get; set; } = "";
        public string MissionFlag { get; set; } = "Squad/Flags/default";
        public string VesselType { get; set; } = "Debris";
        /// <summary>Copied verbatim into the header when not null (admission header of a live SaveShip()).</summary>
        public string ModVersions { get; set; }
        /// <summary>Vector-less attN form, pending spike S0e.</summary>
        public bool VectorLessAttN { get; set; }
        /// <summary>Provisional surface radius in metres. Bridge-side calibration replaces it later (R1-section 6.5).</summary>
        public double SurfaceRadius { get; set; } = 0.625;
        public double RootHeight { get; set; } = 15;
    }
    public enum AttachKind { Root, Stack, Surface }
    /// <summary>One placed part instance (a symmetric graph part yields several).</summary>
    public sealed class LayoutPart
    {
        public GraphPartDto Source { get; set; }
        public ConstructionPart Definition { get; set; }
        public int Index { get; set; }
        public int ParentIndex { get; set; } = -1;
        public int InstanceIndex { get; set; }
        public int InstanceCount { get; set; } = 1;
        public AttachKind Kind { get; set; }
        public string ParentNodeId { get; set; }
        public string NodeId { get; set; }
        public Vector Position { get; set; }
        public Rotation Rotation { get; set; } = RotationMath.Identity;
        public Vector AttPos0 { get; set; }
        public Rotation AttRot0 { get; set; } = RotationMath.Identity;
        public uint Cid { get; set; }
    }
    public sealed class StructuralLayout
    {
        public List<LayoutPart> Parts { get; } = new List<LayoutPart>();
        /// <summary>Counterpart groups as part indices, one list per symmetric graph part.</summary>
        public List<List<int>> SymmetryGroups { get; } = new List<List<int>>();
    }
    public sealed class PlanResult
    {
        public List<PlanIssue> Issues { get; } = new List<PlanIssue>();
        public StructuralLayout Layout { get; set; }
        public string Topology { get; set; }
        public StructuralCraft Craft { get; set; }
        public bool Ok { get { return Issues.Count == 0 && Craft != null; } }
    }
    public static class CraftPlanner
    {
        /// <summary>Full plan: layout, topology classification, staging rules, structural craft. Craft is null when any issue exists.</summary>
        public static PlanResult Plan(GraphDto graph, ConstructionCatalog catalog, PlannerOptions options = null)
        {
            options = options ?? new PlannerOptions();
            var result = new PlanResult();
            var layout = Layout(graph, catalog, options, result.Issues);
            result.Layout = layout;
            if (layout == null || result.Issues.Count != 0) return result;
            string reason;
            var kind = TopologyClassifier.Classify(layout, out reason);
            result.Topology = kind == TopologyKind.Unsupported ? null : kind.ToString();
            if (kind == TopologyKind.Unsupported) { result.Issues.Add(new PlanIssue("unsupported_staging_topology", null, reason)); return result; }
            var rule = StagingRules.For(kind);
            if (rule == null) { result.Issues.Add(new PlanIssue("unsupported_staging_topology", null, kind + ": twin pending")); return result; }
            List<StagingFields> staging;
            if (!rule.TryApply(layout, out staging, out reason)) { result.Issues.Add(new PlanIssue("unsupported_staging_topology", null, reason)); return result; }
            result.Craft = Assemble(graph, layout, staging, options);
            return result;
        }
        /// <summary>Placement only: instances, transforms, symmetry groups. Staging is separate because it needs a twin-verified topology.</summary>
        public static StructuralLayout Layout(GraphDto graph, ConstructionCatalog catalog, PlannerOptions options, List<PlanIssue> issues)
        {
            options = options ?? new PlannerOptions();
            var bounds = GraphDtoValidator.Validate(graph);
            issues.AddRange(bounds);
            if (catalog == null) { issues.Add(new PlanIssue("invalid_graph", null, "catalog missing")); return null; }
            if (bounds.Count != 0) return null;
            var byId = new Dictionary<string, GraphPartDto>(StringComparer.Ordinal);
            foreach (var p in graph.Parts) byId[p.Id] = p;
            int before = issues.Count;
            foreach (var p in graph.Parts)
            {
                var def = catalog.Find(p.Part);
                if (def == null) { issues.Add(new PlanIssue("unknown_part", p.Id)); continue; }
                if (!def.Buildable) issues.Add(new PlanIssue("part_not_buildable", p.Id));
                if (!def.IsVerified) issues.Add(new PlanIssue("part_construction_unverified", p.Id));
            }
            var children = new Dictionary<string, List<GraphPartDto>>(StringComparer.Ordinal);
            foreach (var p in graph.Parts)
            {
                if (p.Id == graph.Root) { if (p.Parent != null) issues.Add(new PlanIssue("root_has_parent", p.Id)); continue; }
                if (p.Parent == null || !byId.ContainsKey(p.Parent)) { issues.Add(new PlanIssue("missing_parent", p.Id)); continue; }
                List<GraphPartDto> list;
                if (!children.TryGetValue(p.Parent, out list)) children[p.Parent] = list = new List<GraphPartDto>();
                list.Add(p);
            }
            // Reachability from the root also rejects cycles.
            var order = new List<GraphPartDto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<GraphPartDto>(); stack.Push(byId[graph.Root]);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!seen.Add(cur.Id)) continue;
                order.Add(cur);
                List<GraphPartDto> kids;
                if (children.TryGetValue(cur.Id, out kids)) for (int i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
            }
            foreach (var p in graph.Parts) if (!seen.Contains(p.Id) && issues.All(i => i.PartId != p.Id || i.Code != "missing_parent")) issues.Add(new PlanIssue("disconnected", p.Id, "unreachable from root or part of a cycle"));
            // Attachment shape, nodes and occupancy.
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in order)
            {
                if (p.Id == graph.Root) continue;
                var parent = byId[p.Parent];
                var pd = catalog.Find(parent.Part); var cd = catalog.Find(p.Part);
                if (pd == null || cd == null) continue;
                if (p.Surface != null)
                {
                    if (!cd.AttachRules.Srf) issues.Add(new PlanIssue("surface_not_supported_by_part", p.Id));
                    if (!pd.AttachRules.AllowSrf) issues.Add(new PlanIssue("surface_attachment_denied", p.Id));
                    if (cd.SurfaceNode == null) issues.Add(new PlanIssue("missing_surface_node", p.Id));
                }
                else
                {
                    if (p.Symmetry.HasValue) issues.Add(new PlanIssue("unsupported_symmetry_attachment", p.Id, "symmetry is supported for surface-attached parts only"));
                    if (p.ParentNode == null || p.Node == null) { issues.Add(new PlanIssue("missing_attach_nodes", p.Id)); continue; }
                    if (!pd.AttachRules.AllowStack || !cd.AttachRules.Stack) issues.Add(new PlanIssue("stack_attachment_denied", p.Id));
                    if (pd.FindNode(p.ParentNode) == null) issues.Add(new PlanIssue("missing_parent_node", p.Id));
                    if (cd.FindNode(p.Node) == null) issues.Add(new PlanIssue("missing_child_node", p.Id));
                    if (!occupied.Add(parent.Id + ":" + p.ParentNode)) issues.Add(new PlanIssue("node_occupied", p.Id));
                    if (!occupied.Add(p.Id + ":" + p.Node)) issues.Add(new PlanIssue("node_occupied", p.Id));
                }
            }
            // A symmetric part under a symmetric ancestor would multiply counts; not supported.
            foreach (var p in order)
            {
                if (!p.Symmetry.HasValue) continue;
                var up = p.Parent == null ? null : byId[p.Parent];
                while (up != null)
                {
                    if (up.Symmetry.HasValue) { issues.Add(new PlanIssue("unsupported_nested_symmetry", p.Id)); break; }
                    up = up.Parent == null ? null : byId[up.Parent];
                }
            }
            if (issues.Count != before) return null;

            var layout = new StructuralLayout();
            var instances = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            uint cid = options.FirstCraftId;
            foreach (var p in order)
            {
                var cd = catalog.Find(p.Part);
                var mine = new List<int>();
                instances[p.Id] = mine;
                if (p.Id == graph.Root)
                {
                    var rp = NewPart(layout, p, cd, ref cid);
                    rp.Kind = AttachKind.Root; rp.Position = new Vector(0, options.RootHeight, 0);
                    rp.AttPos0 = rp.Position; rp.AttRot0 = rp.Rotation;
                    mine.Add(rp.Index); continue;
                }
                var parentInstances = instances[p.Parent];
                int count = p.Symmetry.HasValue ? p.Symmetry.Value : parentInstances.Count;
                for (int k = 0; k < count; k++)
                {
                    var parentPart = layout.Parts[p.Symmetry.HasValue ? parentInstances[0] : parentInstances[k]];
                    var part = NewPart(layout, p, cd, ref cid);
                    part.ParentIndex = parentPart.Index; part.InstanceIndex = k; part.InstanceCount = count;
                    try { Place(part, parentPart, p, k, count, options); }
                    catch (ArgumentException) { issues.Add(new PlanIssue("geometry_out_of_bounds", p.Id)); return null; }
                    mine.Add(part.Index);
                }
                if (count > 1) layout.SymmetryGroups.Add(new List<int>(mine));
            }
            return layout;
        }
        private static LayoutPart NewPart(StructuralLayout layout, GraphPartDto src, ConstructionPart def, ref uint cid)
        {
            var part = new LayoutPart { Source = src, Definition = def, Index = layout.Parts.Count, Cid = cid++ };
            layout.Parts.Add(part);
            return part;
        }
        private static void Place(LayoutPart part, LayoutPart parent, GraphPartDto dto, int k, int count, PlannerOptions options)
        {
            Vector pos; Rotation rot;
            if (dto.Surface != null)
            {
                part.Kind = AttachKind.Surface;
                // Only a part with its own symmetry fans out; a child of a symmetric parent already sits in its parent's rotated frame.
                var alpha = dto.Surface.AngleDegrees + (dto.Symmetry.HasValue ? 360.0 * k / count : 0.0);
                var a = alpha * Math.PI / 180.0;
                var outward = new Vector(Math.Sin(a), 0, Math.Cos(a));
                var local = new Vector(options.SurfaceRadius * outward.X, dto.Surface.HeightOffset, options.SurfaceRadius * outward.Z);
                var worldOutward = RotationMath.Rotate(outward, parent.Rotation);
                var srf = part.Definition.SurfaceNode;
                rot = RotationMath.FromTo(srf.Orientation, RotationMath.Neg(worldOutward), RotationMath.Rotate(new Vector(0, 1, 0), parent.Rotation));
                pos = RotationMath.Sub(RotationMath.Add(parent.Position, RotationMath.Rotate(local, parent.Rotation)), RotationMath.Rotate(srf.Position, rot));
            }
            else
            {
                part.Kind = AttachKind.Stack; part.ParentNodeId = dto.ParentNode; part.NodeId = dto.Node;
                var pn = parent.Definition.FindNode(dto.ParentNode); var cn = part.Definition.FindNode(dto.Node);
                var desired = RotationMath.Neg(RotationMath.Rotate(pn.Orientation, parent.Rotation));
                rot = parent.Rotation;
                var current = RotationMath.Rotate(cn.Orientation, rot);
                var residual = RotationMath.FromTo(current, desired, RotationMath.Rotate(new Vector(0, 1, 0), parent.Rotation));
                if (residual.W < 1 - 1e-12) rot = RotationMath.Multiply(residual, rot);
                pos = AttachmentGeometry.StackPosition(parent.Position, parent.Rotation, pn.Position, rot, cn.Position);
            }
            part.Position = pos; part.Rotation = rot;
            part.AttPos0 = RotationMath.InverseRotate(RotationMath.Sub(pos, parent.Position), parent.Rotation);
            part.AttRot0 = RotationMath.Multiply(RotationMath.Conjugate(parent.Rotation), rot);
        }
        private static StructuralCraft Assemble(GraphDto graph, StructuralLayout layout, List<StagingFields> staging, PlannerOptions options)
        {
            uint counter = 0;
            Func<uint> ids = options.PersistentIdGenerator ?? (() => ++counter);
            var craft = new StructuralCraft { VectorLessAttN = options.VectorLessAttN };
            var h = craft.Header;
            h.Ship = graph.Name; h.Version = options.Version; h.Description = options.Description; h.Type = "VAB";
            h.PersistentId = ids(); h.MissionFlag = options.MissionFlag; h.VesselType = options.VesselType; h.ModVersions = options.ModVersions;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var lp in layout.Parts)
            {
                var sp = new StructuralPart
                {
                    SourceId = lp.Source.Id, Name = lp.Source.Part, Cid = lp.Cid, PersistentId = ids(),
                    Position = lp.Position, Rotation = lp.Rotation, AttPos0 = lp.AttPos0, AttRot0 = lp.AttRot0,
                    Staging = staging[lp.Index]
                };
                craft.Parts.Add(sp);
                minX = Math.Min(minX, lp.Position.X); maxX = Math.Max(maxX, lp.Position.X);
                minY = Math.Min(minY, lp.Position.Y); maxY = Math.Max(maxY, lp.Position.Y);
                minZ = Math.Min(minZ, lp.Position.Z); maxZ = Math.Max(maxZ, lp.Position.Z);
            }
            h.Size = new Vector(maxX - minX, maxY - minY, maxZ - minZ);
            foreach (var lp in layout.Parts)
            {
                if (lp.ParentIndex < 0) continue;
                var child = craft.Parts[lp.Index]; var parent = craft.Parts[lp.ParentIndex];
                parent.Links.Add(child.Ref);
                if (lp.Kind == AttachKind.Surface) child.SurfaceParent = parent.Ref;
                else
                {
                    var pnode = layout.Parts[lp.ParentIndex].Definition.FindNode(lp.ParentNodeId);
                    var cnode = lp.Definition.FindNode(lp.NodeId);
                    child.AttachNodes.Insert(0, new StructuralAttachNode { NodeId = lp.NodeId, Partner = parent.Ref, NodePosition = cnode.Position });
                    parent.AttachNodes.Add(new StructuralAttachNode { NodeId = lp.ParentNodeId, Partner = child.Ref, NodePosition = pnode.Position });
                }
            }
            foreach (var group in layout.SymmetryGroups)
                foreach (var i in group)
                    foreach (var j in group)
                        if (i != j) craft.Parts[i].Sym.Add(craft.Parts[j].Ref);
            return craft;
        }
    }
}
