using System;
using System.Collections.Generic;
using System.Linq;
namespace KspControl.EditorModel
{
    public enum AttachmentKind { Stack, Surface }
    public sealed class AttachNodeDefinition
    {
        public string Name { get; set; }
        public AttachmentKind Kind { get; set; }
        public Vector Position { get; set; }
        public Vector Orientation { get; set; }
    }
    public sealed class PartDefinition
    {
        public string Name { get; set; }
        public bool Unlocked { get; set; }
        public bool ConfigurationVerified { get; set; }
        public bool AllowsSurfaceChildren { get; set; }
        public List<AttachNodeDefinition> Nodes { get; set; } = new List<AttachNodeDefinition>();
    }
    public struct Vector
    {
        public double X, Y, Z;
        public Vector(double x,double y,double z) { X=x; Y=y; Z=z; }
        public bool IsFinite => Finite(X) && Finite(Y) && Finite(Z);
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public double LengthSquared => X*X + Y*Y + Z*Z;
    }
    public struct Rotation
    {
        public double X,Y,Z,W;
        public Rotation(double x,double y,double z,double w) { X=x;Y=y;Z=z;W=w; }
        public bool IsUnit => Vector.Finite(X) && Vector.Finite(Y) && Vector.Finite(Z) && Vector.Finite(W) && Math.Abs(X*X+Y*Y+Z*Z+W*W-1)<0.00001;
    }
    public sealed class CraftPart
    {
        public string Id { get; set; }
        public string Definition { get; set; }
        public string ParentId { get; set; }
        public string ParentNode { get; set; }
        public string ChildNode { get; set; }
        public AttachmentKind Attachment { get; set; }
        public Vector Position { get; set; }
        public Rotation Rotation { get; set; } = new Rotation(0,0,0,1);
        public int Stage { get; set; } = -1;
        public string SymmetrySet { get; set; }
    }
    public sealed class CraftGraph
    {
        public string Name { get; set; }
        public string RootId { get; set; }
        public List<CraftPart> Parts { get; set; } = new List<CraftPart>();
    }
    public sealed class GraphIssue
    {
        public string Code { get; }
        public string PartId { get; }
        public GraphIssue(string code,string partId) { Code=code;PartId=partId; }
    }
    /// <summary>Validates a detached proposal, never edits KSP. Geometry collision and flight viability are separate gates.</summary>
    public static class GraphValidator
    {
        public static IReadOnlyList<GraphIssue> Validate(CraftGraph graph,IReadOnlyDictionary<string,PartDefinition> catalog)
        {
            var issues=new List<GraphIssue>();
            Action<string,string> add=(code,id)=>issues.Add(new GraphIssue(code,id));
            if(graph==null || catalog==null) { add("missing_graph_or_catalog",null);return issues; }
            if(graph.Parts==null || graph.Parts.Count==0 || graph.Parts.Count>1000) { add("part_count_out_of_range",null);return issues; }
            if(string.IsNullOrWhiteSpace(graph.Name)||graph.Name.Length>128||graph.Name.Any(char.IsControl)) add("invalid_craft_name",null);
            var parts=new Dictionary<string,CraftPart>(StringComparer.Ordinal);
            foreach(var part in graph.Parts)
            {
                if(part==null || !Handle(part.Id)) { add("invalid_part_id",part?.Id);continue; }
                if(parts.ContainsKey(part.Id)) { add("duplicate_part_id",part.Id);continue; }
                parts.Add(part.Id,part);
                if(!part.Position.IsFinite || part.Position.LengthSquared>1e12 || !part.Rotation.IsUnit) add("invalid_transform",part.Id);
                if(part.Stage < -1 || part.Stage>999) add("invalid_stage",part.Id);
                if(part.Definition==null || !catalog.TryGetValue(part.Definition,out var definition)||definition==null) { add("unknown_part",part.Id);continue; }
                if(!definition.Unlocked) add("part_locked",part.Id);
                if(!definition.ConfigurationVerified) add("configuration_unverified",part.Id);
                if(definition.Nodes==null || definition.Nodes.Any(n=>n==null||!Handle(n.Name)||!n.Position.IsFinite||!n.Orientation.IsFinite||n.Orientation.LengthSquared<1e-12||n.Orientation.LengthSquared>1e12) || definition.Nodes.Where(n=>n!=null).GroupBy(n=>n.Name).Any(g=>g.Count()>1)) add("invalid_catalog_nodes",part.Id);
            }
            if(graph.RootId==null || !parts.TryGetValue(graph.RootId,out var root)) add("missing_root",null);
            else if(root.ParentId!=null) add("root_has_parent",root.Id);
            var occupied=new HashSet<string>(StringComparer.Ordinal);
            foreach(var part in parts.Values)
            {
                if(part.Id==graph.RootId) continue;
                if(part.ParentId==null || !parts.TryGetValue(part.ParentId,out var parent)) { add("missing_parent",part.Id);continue; }
                if(!catalog.TryGetValue(part.Definition??"",out var childDefinition)||!catalog.TryGetValue(parent.Definition??"",out var parentDefinition)||childDefinition==null||parentDefinition==null) continue;
                var childNode=(childDefinition.Nodes??new List<AttachNodeDefinition>()).FirstOrDefault(n=>n!=null&&part.ChildNode!=null&&n.Name==part.ChildNode&&n.Kind==part.Attachment);
                if(childNode==null) add("missing_child_node",part.Id);
                if(part.Attachment==AttachmentKind.Stack)
                {
                    if(!(parentDefinition.Nodes??new List<AttachNodeDefinition>()).Any(n=>n!=null&&part.ParentNode!=null&&n.Name==part.ParentNode&&n.Kind==AttachmentKind.Stack)) add("missing_parent_node",part.Id);
                    if(!occupied.Add(parent.Id+":"+part.ParentNode)) add("node_occupied",part.Id);
                    if(!occupied.Add(part.Id+":"+part.ChildNode)) add("node_occupied",part.Id);
                }
                else if(part.Attachment==AttachmentKind.Surface) { if(!parentDefinition.AllowsSurfaceChildren) add("surface_attachment_denied",part.Id); }
                else add("invalid_attachment_kind",part.Id);
                var visited=new HashSet<string>(StringComparer.Ordinal);var current=part;
                while(current.Id!=graph.RootId)
                {
                    if(!visited.Add(current.Id)) { add("attachment_cycle",part.Id);break; }
                    if(current.ParentId==null||!parts.TryGetValue(current.ParentId,out current)) { add("disconnected",part.Id);break; }
                }
            }
            foreach(var group in parts.Values.Where(p=>p.SymmetrySet!=null).GroupBy(p=>p.SymmetrySet,StringComparer.Ordinal))
            {
                var first=group.First();
                if(!Handle(group.Key)||group.Count()<2||group.Any(p=>p.Definition!=first.Definition||p.ParentId!=first.ParentId||p.Stage!=first.Stage||p.Attachment!=first.Attachment)) add("invalid_symmetry_set",first.Id);
            }
            return issues;
        }
        private static bool Handle(string value) => !string.IsNullOrEmpty(value)&&value.Length<=64&&value.All(c=>char.IsLetterOrDigit(c)||c=='-'||c=='_');
    }
}


