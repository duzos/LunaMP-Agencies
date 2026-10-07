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
        /// <summary>Prefab stack nodes (the base layer). Only stack-type nodes belong here.</summary>
        public IList<CatalogNodeSource> StackNodes { get; set; } = new List<CatalogNodeSource>();
        /// <summary>
        /// Stack nodes the default variant supplies, or null when there is no variant. The variant list is an override: it may be empty
        /// (texture-only variant) or hold only the nodes that move, so it is overlaid on the prefab nodes by id, never used alone.
        /// </summary>
        public IList<CatalogNodeSource> VariantStackNodes { get; set; }
        public string VariantName { get; set; }
        /// <summary>Variant surface node when the variant defines one, else null (the prefab one applies).</summary>
        public CatalogNodeSource VariantSurfaceNode { get; set; }
        /// <summary>Variant attach rules when the variant defines them, else null (the prefab ones apply).</summary>
        public CatalogAttachRulesSource VariantAttachRules { get; set; }
        public CatalogNodeSource SurfaceNode { get; set; }
        public CatalogAttachRulesSource AttachRules { get; set; } = new CatalogAttachRulesSource();
    }

    public interface ICatalogPartReader
    {
        /// <summary>True when ResearchAndDevelopment.Instance exists.</summary>
        bool ResearchAvailable { get; }
        /// <summary>True only when the current game mode is SANDBOX (HighLogic.CurrentGame.Mode). Career/science without R&D is unreadable, not sandbox.</summary>
        bool SandboxMode { get; }
        /// <summary>Null when no loaded part has this name.</summary>
        CatalogPartSource Read(string partName);
    }

    /// <summary>Which stock parts have construction evidence (S0 twin comparison). Everything else is reported "unverified" and the planner refuses it.</summary>
    public sealed class ConstructionSupportPolicy
    {
        /// <summary>
        /// The one data table of parts with live structural-load evidence (plan 37 revision 5; 33-log S0-t1 and S0-t2, sandbox).
        /// Add a row, with its evidence, when a part is verified live. Anything not listed is reported "unverified" and the planner refuses it.
        /// </summary>
        public static readonly KeyValuePair<string, string>[] VerifiedTable =
        {
            new KeyValuePair<string, string>("mk1pod.v2", "S0a twin, structural load"),
            new KeyValuePair<string, string>("fuelTankSmall", "S0a twin, structural load"),
            new KeyValuePair<string, string>("liquidEngine.v2", "S0a twin, structural load"),
            new KeyValuePair<string, string>("probeCoreOcto.v2", "S0-t2 structural load"),
            new KeyValuePair<string, string>("Decoupler.1", "S0-t2 structural load"),
            // TODO(P3 crewed flight): parachuteSingle (Mk16) stays UNVERIFIED until a live structural load of a craft carrying it is recorded.
            // The staging rule is reproduced from stock Orbiter One (EditorModel StagingRules), but a staging oracle is not structural-load evidence.
            // Add the row, with its evidence reference, only after that load has been observed.
        };
        public static string[] DefaultVerified
        {
            get { var names = new List<string>(); foreach (var row in VerifiedTable) names.Add(row.Key); return names.ToArray(); }
        }
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
        // Category vocabulary is the EditorModel one (PartCategories). A nosecone is "other"; a solid booster is an engine; a ModuleParachute part is "parachute";
        // an ablative heat shield (ModuleAblator, with its unstaged ModuleDecouple) is "heatshield".
        public const string Command = "command", Tank = "tank", Engine = "engine", Decoupler = "decoupler", Parachute = "parachute", HeatShield = "heatshield", Other = "other";
        public const int MaxNodes = 32;

        /// <summary>
        /// Role from modules first, then propellant resources, then the KSP editor category. The KSP category fallback matters with fuel-switch mods
        /// (CryoTanks/B9PartSwitch): they remove the RESOURCE nodes from stock tanks, so a prefab FL-T200 carries no LiquidFuel/Oxidizer but is still category FuelTank.
        /// </summary>
        public static string MapCategory(IEnumerable<string> modules, IEnumerable<string> resources, string kspCategory = null)
        {
            var m = new HashSet<string>(modules ?? new string[0], StringComparer.Ordinal);
            var r = new HashSet<string>(resources ?? new string[0], StringComparer.Ordinal);
            if (m.Contains("ModuleCommand")) return Command;
            if (m.Contains("ModuleAblator") && m.Contains("ModuleDecouple")) return HeatShield;
            if (m.Contains("ModuleDecouple") || m.Contains("ModuleAnchoredDecoupler")) return Decoupler;
            if (m.Contains("ModuleEngines") || m.Contains("ModuleEnginesFX")) return Engine;
            if (m.Contains("ModuleParachute")) return Parachute;
            if (!m.Contains("ModuleRCS") && !m.Contains("ModuleRCSFX")
                && (r.Contains("LiquidFuel") || r.Contains("Oxidizer") || r.Contains("MonoPropellant") || r.Contains("XenonGas")
                    || string.Equals(kspCategory, "FuelTank", StringComparison.Ordinal))) return Tank;
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
                parts.Add(source == null ? new JObject { ["name"] = name, ["found"] = false } : Describe(source, reader.ResearchAvailable, reader.SandboxMode, policy));
            }
            return new JObject
            {
                ["requested"] = names.Count,
                ["researchAndDevelopment"] = reader.ResearchAvailable ? "available" : reader.SandboxMode ? "absent_sandbox_allowed" : "unreadable",
                ["nodeFrame"] = "part_local_default_variant",
                ["parts"] = parts
            };
        }

        private static JObject Describe(CatalogPartSource part, bool researchAvailable, bool sandbox, ConstructionSupportPolicy policy)
        {
            var problems = new JArray();
            var merged = Overlay(part.StackNodes, part.VariantStackNodes);
            var nodes = new JArray(); var ids = new HashSet<string>(StringComparer.Ordinal); bool overlaid = false;
            if (merged.Count > MaxNodes) problems.Add("node_count_over_" + MaxNodes);
            foreach (var entry in merged)
            {
                var node = entry.Node;
                if (nodes.Count >= MaxNodes) break;
                if (node == null || string.IsNullOrEmpty(node.Id) || !Finite3(node.Position) || !Finite3(node.Orientation)) { problems.Add("invalid_node"); continue; }
                if (!ids.Add(node.Id)) { problems.Add("duplicate_node_id"); continue; }
                var json = NodeJson(node, true); json["nodeSource"] = entry.FromVariant ? "variant_overlay" : "prefab";
                overlaid |= entry.FromVariant;
                nodes.Add(json);
            }
            JToken surface = JValue.CreateNull();
            var rules = part.VariantAttachRules ?? part.AttachRules ?? new CatalogAttachRulesSource();
            var srfNode = part.VariantSurfaceNode ?? part.SurfaceNode;
            if (srfNode != null)
            {
                if (Finite3(srfNode.Position) && Finite3(srfNode.Orientation)) { var j = NodeJson(srfNode, false); j["nodeSource"] = part.VariantSurfaceNode != null ? "variant_overlay" : "prefab"; surface = j; }
                else problems.Add("invalid_surface_node");
            }
            bool allowed;
            string basis;
            if (researchAvailable && part.TechAvailable.HasValue && part.ModelPurchased.HasValue) { allowed = part.TechAvailable.Value && part.ModelPurchased.Value; basis = "tech_and_model_purchased"; }
            else if (!researchAvailable && sandbox) { allowed = true; basis = "research_absent_sandbox"; }
            else { allowed = false; basis = "research_state_unreadable"; }
            bool verified = policy.IsVerified(part.Name) && problems.Count == 0;
            var result = new JObject
            {
                ["name"] = part.Name,
                ["found"] = true,
                ["category"] = MapCategory(part.ModuleNames, part.ResourceNames, part.KspCategory),
                ["kspCategory"] = part.KspCategory,
                ["buildable"] = part.Buildable,
                ["partsStockAllowed"] = allowed,
                ["stockAllowedBasis"] = basis,
                ["techAvailable"] = part.TechAvailable.HasValue ? (JToken)new JValue(part.TechAvailable.Value) : JValue.CreateNull(),
                ["modelPurchased"] = part.ModelPurchased.HasValue ? (JToken)new JValue(part.ModelPurchased.Value) : JValue.CreateNull(),
                ["constructionSupport"] = verified ? "verified" : "unverified",
                ["nodeSource"] = overlaid ? "variant_overlay" : "prefab",
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

        private struct MergedNode { public CatalogNodeSource Node; public bool FromVariant; }

        /// <summary>Prefab nodes in prefab order, each replaced by the variant node with the same id; variant-only ids follow. A null or empty variant list changes nothing.</summary>
        private static List<MergedNode> Overlay(IList<CatalogNodeSource> prefab, IList<CatalogNodeSource> variant)
        {
            var result = new List<MergedNode>();
            var overrides = new Dictionary<string, CatalogNodeSource>(StringComparer.Ordinal);
            if (variant != null) foreach (var v in variant) if (v != null && v.Id != null && !overrides.ContainsKey(v.Id)) overrides[v.Id] = v;
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in prefab ?? new List<CatalogNodeSource>())
            {
                CatalogNodeSource over;
                if (p != null && p.Id != null && overrides.TryGetValue(p.Id, out over)) { result.Add(new MergedNode { Node = over, FromVariant = true }); used.Add(p.Id); }
                else result.Add(new MergedNode { Node = p, FromVariant = false });
            }
            if (variant != null)
                foreach (var v in variant)
                    if (v == null || v.Id == null) result.Add(new MergedNode { Node = v, FromVariant = true });
                    else if (!used.Contains(v.Id) && overrides[v.Id] == v) { result.Add(new MergedNode { Node = v, FromVariant = true }); used.Add(v.Id); }
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
