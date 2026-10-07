using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace KspControl.EditorModel
{
    /// <summary>Well-known construction roles used by the topology classifier. Mapping from KSP categories happens bridge-side.</summary>
    public static class PartCategories
    {
        public const string Command = "command";
        public const string Tank = "tank";
        public const string Engine = "engine";
        public const string Decoupler = "decoupler";
        /// <summary>ModuleParachute. Supported only as the single parachute on the command pod's top node (stage group 0).</summary>
        public const string Parachute = "parachute";
        public const string Other = "other";
    }
    public sealed class ConstructionNode
    {
        public string Id { get; set; }
        public Vector Position { get; set; }
        public Vector Orientation { get; set; }
        public int Size { get; set; }
    }
    public sealed class SurfaceNodeDefinition
    {
        public Vector Position { get; set; }
        public Vector Orientation { get; set; }
    }
    /// <summary>Subset of KSP AttachRules: what the part may do and accept.</summary>
    public sealed class AttachRulesDefinition
    {
        public bool Stack { get; set; }
        public bool Srf { get; set; }
        public bool AllowStack { get; set; }
        public bool AllowSrf { get; set; }
        public bool AllowCollision { get; set; }
        public bool AllowDock { get; set; }
    }
    /// <summary>Per-part construction data: effective default-variant nodes, never fabricated by the planner.</summary>
    public sealed class ConstructionPart
    {
        public string Name { get; set; }
        public string Category { get; set; } = PartCategories.Other;
        public bool Buildable { get; set; }
        /// <summary>"verified" or "unverified" (R1-section 0). Anything else is treated as unverified.</summary>
        public string ConstructionSupport { get; set; } = "unverified";
        public List<ConstructionNode> StackNodes { get; set; } = new List<ConstructionNode>();
        public SurfaceNodeDefinition SurfaceNode { get; set; }
        public AttachRulesDefinition AttachRules { get; set; } = new AttachRulesDefinition();
        public ConstructionNode FindNode(string id)
        {
            if (StackNodes == null) return null;
            foreach (var n in StackNodes) if (n != null && string.Equals(n.Id, id, StringComparison.Ordinal)) return n;
            return null;
        }
        public bool IsVerified => string.Equals(ConstructionSupport, "verified", StringComparison.Ordinal);
    }
    public sealed class ConstructionCatalog
    {
        public Dictionary<string, ConstructionPart> Parts { get; set; } = new Dictionary<string, ConstructionPart>(StringComparer.Ordinal);
        public ConstructionPart Find(string name)
        {
            ConstructionPart part;
            return name != null && Parts != null && Parts.TryGetValue(name, out part) ? part : null;
        }
        public ConstructionCatalog Add(ConstructionPart part) { Parts[part.Name] = part; return this; }
        /// <summary>Stable SHA-256 over the canonical catalog content, independent of insertion order.</summary>
        public string Hash()
        {
            var sb = new StringBuilder();
            var names = new List<string>(Parts.Keys); names.Sort(StringComparer.Ordinal);
            foreach (var n in names)
            {
                var p = Parts[n];
                sb.Append(n).Append('|').Append(p.Category).Append('|').Append(p.Buildable).Append('|').Append(p.ConstructionSupport).Append('|');
                var r = p.AttachRules ?? new AttachRulesDefinition();
                sb.Append(r.Stack).Append(r.Srf).Append(r.AllowStack).Append(r.AllowSrf).Append(r.AllowCollision).Append(r.AllowDock).Append('|');
                var nodes = new List<ConstructionNode>(p.StackNodes ?? new List<ConstructionNode>());
                nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                foreach (var node in nodes) sb.Append(node.Id).Append(':').Append(V(node.Position)).Append(':').Append(V(node.Orientation)).Append(':').Append(node.Size.ToString(CultureInfo.InvariantCulture)).Append(';');
                if (p.SurfaceNode != null) sb.Append("srf:").Append(V(p.SurfaceNode.Position)).Append(':').Append(V(p.SurfaceNode.Orientation));
                sb.Append('\n');
            }
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder();
                foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }
        private static string V(Vector v) { return RotationMath.Format(v); }
    }
}
