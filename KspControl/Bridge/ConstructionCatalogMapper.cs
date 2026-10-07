using System;
using System.Collections.Generic;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>One attach node as read from a part. Positions and orientations are part-local, in the units KSP reports.</summary>
    public sealed class CatalogNodeSource
    {
        public string Id { get; set; }
        public double[] Position { get; set; }
        public double[] Orientation { get; set; }
        public int Size { get; set; }
    }

    public sealed class CatalogAttachRulesSource
    {
        public bool Stack { get; set; }
        public bool Srf { get; set; }
        public bool AllowStack { get; set; }
        public bool AllowSrf { get; set; }
        public bool AllowCollision { get; set; }
        public bool AllowDock { get; set; }
    }

    /// <summary>Everything the mapper needs from one loaded part, as primitives. The KSP-facing reader fills this on the main thread.</summary>
    public sealed class CatalogPartSource
    {
        public string Name { get; set; }
        /// <summary>KSP PartCategories name (Pods, Propulsion, Coupling, ...). Informational.</summary>
        public string KspCategory { get; set; }
        public IList<string> ModuleNames { get; set; } = new List<string>();
        public IList<string> ResourceNames { get; set; } = new List<string>();
        public bool Buildable { get; set; }
        /// <summary>Null when ResearchAndDevelopment is absent (sandbox).</summary>
        public bool? TechAvailable { get; set; }
        /// <summary>Null when ResearchAndDevelopment is absent (sandbox).</summary>
        public bool? ModelPurchased { get; set; }
        /// <summary>Effective default-variant stack nodes: AvailablePart.variant.AttachNodes when present, else the prefab attach nodes.</summary>
        public IList<CatalogNodeSource> StackNodes { get; set; } = new List<CatalogNodeSource>();
        public bool StackNodesFromVariant { get; set; }
        public string VariantName { get; set; }
        /// <summary>Total attach nodes before the bridge's own cap, so truncation can be detected.</summary>
        public int RawStackNodeCount { get; set; }
        public CatalogNodeSource SurfaceNode { get; set; }
        public CatalogAttachRulesSource AttachRules { get; set; } = new CatalogAttachRulesSource();
    }

    public interface ICatalogPartReader
    {
        /// <summary>True when ResearchAndDevelopment is live; false means sandbox (every part is treated as allowed and the response says so).</summary>
        bool ResearchAvailable { get; }
        /// <summary>Null when no loaded part has this name.</summary>
        CatalogPartSource Read(string partName);
    }

    /// <summary>Which stock parts have construction evidence (S0 twin comparison). Everything else is reported "unverified" and the planner refuses it.</summary>
    public sealed class ConstructionSupportPolicy
    {
        /// <summary>Parts compared against a KSP-built twin in S0 (plan 37, 33-log): the T1 twin set. Extended only with live evidence.</summary>
        public static readonly string[] DefaultVerified = { "mk1pod.v2", "fuelTankSmall", "liquidEngine.v2" };
        public static readonly ConstructionSupportPolicy Default = new ConstructionSupportPolicy(DefaultVerified);
        private readonly HashSet<string> verified;
        public ConstructionSupportPolicy(IEnumerable<string> verifiedParts)
        {
            verified = new HashSet<string>(verifiedParts ?? new string[0], StringComparer.Ordinal);
        }
        public bool IsVerified(string partName) { return partName != null && verified.Contains(partName); }
    }

    /// <summary>Pure mapping from loaded-part primitives to the construction catalog wire shape. No Unity or KSP types.</summary>
    public static class ConstructionCatalogMapper
    {
        // Category vocabulary is the EditorModel one (PartCategories). booster, nosecone and parachute are not supported there, so they map to "other".
        public const string Command = "command", Tank = "tank", Engine = "engine", Decoupler = "decoupler", Other = "other";
        public const int MaxNodes = 32;

        public static string MapCategory(IEnumerable<string> modules, IEnumerable<string> resources)
        {
            var m = new HashSet<string>(modules ?? new string[0], StringComparer.Ordinal);
            var r = new HashSet<string>(resources ?? new string[0], StringComparer.Ordinal);
            if (m.Contains("ModuleCommand")) return Command;
            if (m.Contains("ModuleDecouple") || m.Contains("ModuleAnchoredDecoupler")) return Decoupler;
            if (m.Contains("ModuleEngines") || m.Contains("ModuleEnginesFX")) return Engine;
            if (!m.Contains("ModuleRCS") && !m.Contains("ModuleRCSFX")
                && (r.Contains("LiquidFuel") || r.Contains("Oxidizer") || r.Contains("MonoPropellant") || r.Contains("XenonGas"))) return Tank;
            return Other;
        }

        /// <summary>Parses and bounds the part name list. Throws ArgumentException (mapped to invalid_argument by the caller).</summary>
        public static List<string> ParseNames(JToken token)
        {
            var array = token as JArray;
            if (array == null || array.Count == 0 || array.Count > ConstructionLimits.MaxCatalogParts) throw new ArgumentException("partNames");
            var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array)
            {
                if (item == null || item.Type != JTokenType.String) throw new ArgumentException("partNames");
                var name = (string)item;
                if (!IsPartName(name)) throw new ArgumentException("partNames");
                if (seen.Add(name)) result.Add(name);
            }
            return result;
        }

        public static bool IsPartName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > ConstructionLimits.MaxPartNameLength) return false;
            foreach (var c in name)
                if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '-'))) return false;
            return true;
        }

        public static JObject Build(IList<string> names, ICatalogPartReader reader, ConstructionSupportPolicy policy)
        {
            policy = policy ?? ConstructionSupportPolicy.Default;
            var parts = new JArray();
            foreach (var name in names)
            {
                var source = reader.Read(name);
                parts.Add(source == null ? new JObject { ["name"] = name, ["found"] = false } : Describe(source, reader.ResearchAvailable, policy));
            }
            return new JObject
            {
                ["requested"] = names.Count,
                ["researchAndDevelopment"] = reader.ResearchAvailable ? "available" : "absent_sandbox_allowed",
                ["nodeFrame"] = "part_local_default_variant",
                ["parts"] = parts
            };
        }

        private static JObject Describe(CatalogPartSource part, bool researchAvailable, ConstructionSupportPolicy policy)
        {
            var problems = new JArray();
            var nodes = new JArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
            if (part.RawStackNodeCount > MaxNodes) problems.Add("node_count_over_" + MaxNodes);
            foreach (var node in part.StackNodes ?? new List<CatalogNodeSource>())
            {
                if (nodes.Count >= MaxNodes) break;
                if (node == null || string.IsNullOrEmpty(node.Id) || !Finite3(node.Position) || !Finite3(node.Orientation)) { problems.Add("invalid_node"); continue; }
                if (!ids.Add(node.Id)) { problems.Add("duplicate_node_id"); continue; }
                nodes.Add(NodeJson(node, true));
            }
            JToken surface = JValue.CreateNull();
            var rules = part.AttachRules ?? new CatalogAttachRulesSource();
            if (part.SurfaceNode != null)
            {
                if (Finite3(part.SurfaceNode.Position) && Finite3(part.SurfaceNode.Orientation)) surface = NodeJson(part.SurfaceNode, false);
                else problems.Add("invalid_surface_node");
            }
            bool allowed;
            string basis;
            if (researchAvailable && part.TechAvailable.HasValue && part.ModelPurchased.HasValue) { allowed = part.TechAvailable.Value && part.ModelPurchased.Value; basis = "tech_and_model_purchased"; }
            else if (!researchAvailable) { allowed = true; basis = "research_absent_sandbox"; }
            else { allowed = false; basis = "research_state_unreadable"; }
            bool verified = policy.IsVerified(part.Name) && problems.Count == 0;
            var result = new JObject
            {
                ["name"] = part.Name,
                ["found"] = true,
                ["category"] = MapCategory(part.ModuleNames, part.ResourceNames),
                ["kspCategory"] = part.KspCategory,
                ["buildable"] = part.Buildable,
                ["partsStockAllowed"] = allowed,
                ["stockAllowedBasis"] = basis,
                ["techAvailable"] = part.TechAvailable.HasValue ? (JToken)new JValue(part.TechAvailable.Value) : JValue.CreateNull(),
                ["modelPurchased"] = part.ModelPurchased.HasValue ? (JToken)new JValue(part.ModelPurchased.Value) : JValue.CreateNull(),
                ["constructionSupport"] = verified ? "verified" : "unverified",
                ["nodeSource"] = part.StackNodesFromVariant ? "variant" : "prefab",
                ["variant"] = part.VariantName,
                ["stackNodes"] = nodes,
                ["surfaceNode"] = surface,
                ["attachRules"] = new JObject
                {
                    ["stack"] = rules.Stack, ["srf"] = rules.Srf, ["allowStack"] = rules.AllowStack,
                    ["allowSrf"] = rules.AllowSrf, ["allowCollision"] = rules.AllowCollision, ["allowDock"] = rules.AllowDock
                }
            };
            if (problems.Count != 0) result["problems"] = problems;
            return result;
        }

        private static JObject NodeJson(CatalogNodeSource node, bool withIdAndSize)
        {
            var json = new JObject();
            if (withIdAndSize) json["id"] = node.Id;
            json["position"] = new JArray(node.Position[0], node.Position[1], node.Position[2]);
            json["orientation"] = new JArray(node.Orientation[0], node.Orientation[1], node.Orientation[2]);
            if (withIdAndSize) json["size"] = node.Size;
            return json;
        }

        private static bool Finite3(double[] v)
        {
            if (v == null || v.Length != 3) return false;
            foreach (var d in v) if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            return true;
        }
    }
}
