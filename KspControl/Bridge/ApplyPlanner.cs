using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KspControl.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// Strict graph JSON binder for the bridge (the host binds the same text for craft_plan; a parity test keeps them identical).
    /// Comments, duplicate keys, unknown or wrongly cased members and type coercion are all refused.
    /// </summary>
    internal static class GraphJson
    {
        private static readonly string[] GraphKeys = { "name", "facility", "root", "parts" };
        private static readonly string[] PartKeys = { "id", "part", "parent", "parentNode", "node", "surface", "symmetry", "stage", "configuration" };
        private static readonly string[] SurfaceKeys = { "heightOffset", "angleDegrees" };

        /// <summary>The graph, or null and an invalid_argument detail.</summary>
        public static Pure.GraphDto Parse(string json, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "graph must be a non-empty JSON object"; return null; }
            if (Encoding.UTF8.GetByteCount(json) > ConstructionLimits.MaxGraphBytes) { error = "graph must be at most " + ConstructionLimits.MaxGraphBytes + " bytes of JSON"; return null; }
            JToken root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                    while (reader.Read()) if (reader.TokenType == JsonToken.Comment) { error = "graph is not valid graph JSON: comments are not allowed"; return null; }
                root = JToken.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error, CommentHandling = CommentHandling.Ignore });
            }
            catch (JsonException e) { error = "graph is not valid graph JSON: " + Brief(e.Message); return null; }
            var obj = root as JObject;
            if (obj == null) { error = "graph must be a JSON object"; return null; }
            Pure.GraphDto graph;
            try { graph = Bind(obj); }
            catch (FormatException e) { error = "graph is not valid graph JSON: " + e.Message; return null; }
            if (graph.Parts == null || graph.Parts.Count < 1 || graph.Parts.Count > ConstructionLimits.MaxGraphParts) { error = "graph.parts must contain 1.." + ConstructionLimits.MaxGraphParts + " parts"; return null; }
            if (graph.Parts.Any(p => p == null)) { error = "graph.parts must not contain null entries"; return null; }
            return graph;
        }

        private static void Keys(JObject o, string[] allowed, string where)
        {
            foreach (var p in o.Properties()) if (!allowed.Contains(p.Name, StringComparer.Ordinal)) throw new FormatException("unknown member '" + p.Name + "' in " + where);
        }
        private static string Str(JToken t, string what)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            throw new FormatException(what + " must be a string");
        }
        private static int? Int(JToken t, string what)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer) throw new FormatException(what + " must be an integer");
            var raw = ((JValue)t).Value;
            if (!(raw is long) && !(raw is int)) throw new FormatException(what + " is out of range");
            var v = Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (v < int.MinValue || v > int.MaxValue) throw new FormatException(what + " is out of range");
            return (int)v;
        }
        private static double Dbl(JToken t, string what)
        {
            if (t == null || t.Type == JTokenType.Null) return 0;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) throw new FormatException(what + " must be a number");
            return (double)t;
        }
        private static Pure.GraphDto Bind(JObject o)
        {
            Keys(o, GraphKeys, "graph");
            var graph = new Pure.GraphDto { Name = Str(o["name"], "name"), Facility = Str(o["facility"], "facility"), Root = Str(o["root"], "root") };
            var parts = o["parts"];
            if (parts == null || parts.Type == JTokenType.Null) return graph;
            var array = parts as JArray;
            if (array == null) throw new FormatException("parts must be an array");
            foreach (var item in array)
            {
                if (item.Type == JTokenType.Null) { graph.Parts.Add(null); continue; }
                var po = item as JObject;
                if (po == null) throw new FormatException("each part must be an object");
                Keys(po, PartKeys, "part");
                var part = new Pure.GraphPartDto
                {
                    Id = Str(po["id"], "id"), Part = Str(po["part"], "part"), Parent = Str(po["parent"], "parent"), ParentNode = Str(po["parentNode"], "parentNode"), Node = Str(po["node"], "node"),
                    Symmetry = Int(po["symmetry"], "symmetry"), Stage = Int(po["stage"], "stage")
                };
                var surface = po["surface"];
                if (surface != null && surface.Type != JTokenType.Null)
                {
                    var so = surface as JObject;
                    if (so == null) throw new FormatException("surface must be an object");
                    Keys(so, SurfaceKeys, "surface");
                    part.Surface = new Pure.SurfaceDto { HeightOffset = Dbl(so["heightOffset"], "heightOffset"), AngleDegrees = Dbl(so["angleDegrees"], "angleDegrees") };
                }
                var configuration = po["configuration"];
                if (configuration != null && configuration.Type != JTokenType.Null)
                {
                    var ca = configuration as JArray;
                    if (ca == null) throw new FormatException("configuration must be an array of strings");
                    part.Configuration = ca.Select(c => Str(c, "configuration entry")).ToList();
                }
                graph.Parts.Add(part);
            }
            return graph;
        }

        private static string Brief(string message) { var line = message.Split('\n')[0].Trim(); return line.Length > 200 ? line.Substring(0, 200) : line; }
    }

    internal sealed class ReplanEntry
    {
        public Pure.ConstructionPart Part { get; }
        public bool StockAllowed { get; }
        public string Basis { get; }
        public ReplanEntry(Pure.ConstructionPart part, bool stockAllowed, string basis) { Part = part; StockAllowed = stockAllowed; Basis = basis; }
    }

    internal sealed class ReplanCatalog
    {
        public Dictionary<string, ReplanEntry> Found { get; } = new Dictionary<string, ReplanEntry>(StringComparer.Ordinal);
        public HashSet<string> Missing { get; } = new HashSet<string>(StringComparer.Ordinal);
        public string ResearchAndDevelopment { get; set; }

        /// <summary>The wire-to-model translation of parts.construction_catalog, the same one the host applies for craft_plan.</summary>
        public static bool TryParse(JObject data, IList<string> requested, ReplanCatalog into, out string error)
        {
            error = "";
            var parts = data == null ? null : data["parts"] as JArray;
            if (parts == null) { error = "catalog reply has no parts array"; return false; }
            if (into.ResearchAndDevelopment == null) into.ResearchAndDevelopment = (string)data["researchAndDevelopment"];
            var wanted = new HashSet<string>(requested, StringComparer.Ordinal);
            var replied = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var item in parts)
                {
                    var o = item as JObject;
                    var name = o == null ? null : (string)o["name"];
                    if (name == null || !wanted.Contains(name)) { error = "catalog reply contains an unrequested or malformed part"; return false; }
                    if (!replied.Add(name)) { error = "catalog reply repeats part " + name; return false; }
                    if (!((bool?)o["found"] ?? false)) { into.Missing.Add(name); continue; }
                    var rules = o["attachRules"] as JObject ?? new JObject();
                    var part = new Pure.ConstructionPart
                    {
                        Name = name, Category = Category((string)o["category"]), Buildable = (bool?)o["buildable"] ?? false,
                        ConstructionSupport = (string)o["constructionSupport"] == "verified" ? "verified" : "unverified",
                        AttachRules = new Pure.AttachRulesDefinition
                        {
                            Stack = (bool?)rules["stack"] ?? false, Srf = (bool?)rules["srf"] ?? false, AllowStack = (bool?)rules["allowStack"] ?? false,
                            AllowSrf = (bool?)rules["allowSrf"] ?? false, AllowCollision = (bool?)rules["allowCollision"] ?? false, AllowDock = (bool?)rules["allowDock"] ?? false
                        }
                    };
                    var nodes = o["stackNodes"] as JArray ?? new JArray();
                    foreach (var n in nodes)
                    {
                        var node = n as JObject;
                        var id = node == null ? null : (string)node["id"];
                        if (id == null) { error = "malformed node in " + name; return false; }
                        part.StackNodes.Add(new Pure.ConstructionNode { Id = id, Position = Vec(node["position"]), Orientation = Vec(node["orientation"]), Size = (int?)node["size"] ?? 0 });
                    }
                    var srf = o["surfaceNode"] as JObject;
                    if (srf != null) part.SurfaceNode = new Pure.SurfaceNodeDefinition { Position = Vec(srf["position"]), Orientation = Vec(srf["orientation"]) };
                    into.Found[name] = new ReplanEntry(part, (bool?)o["partsStockAllowed"] ?? false, (string)o["stockAllowedBasis"] ?? "unknown");
                }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidCastException || e is FormatException || e is JsonException)
            { error = "malformed catalog data: " + e.GetType().Name; return false; }
            foreach (var name in requested) if (!replied.Contains(name)) into.Missing.Add(name);
            return true;
        }

        private static string Category(string c)
        {
            switch (c)
            {
                case Pure.PartCategories.Command: case Pure.PartCategories.Tank: case Pure.PartCategories.Engine: case Pure.PartCategories.Decoupler: case Pure.PartCategories.Parachute: case Pure.PartCategories.HeatShield: return c;
                default: return Pure.PartCategories.Other;
            }
        }

        private static Pure.Vector Vec(JToken token)
        {
            var a = token as JArray;
            if (a == null || a.Count != 3) throw new ArgumentException("vector");
            var v = new Pure.Vector((double)a[0], (double)a[1], (double)a[2]);
            if (!v.IsFinite) throw new ArgumentException("vector");
            return v;
        }
    }

    /// <summary>The outcome of re-planning a graph at admission: either a refusal reason with issues, or the rendered structural craft.</summary>
    internal sealed class ApplyPlan
    {
        public Pure.GraphDto Graph { get; set; }
        public Pure.PlanResult Plan { get; set; }
        public Pure.ConstructionCatalog Catalog { get; set; }
        /// <summary>The options the plan was made with; pass 2 of a surface placement re-plans with the same ids and header values.</summary>
        public Pure.PlannerOptions Options { get; set; }
        public string PlanHash { get; set; }
        public string CatalogHash { get; set; }
        public string CraftText { get; set; }
        public int PartCount { get; set; }
        public List<Pure.PlanIssue> Issues { get; } = new List<Pure.PlanIssue>();
        /// <summary>Null when the plan is good; otherwise the reason code to refuse with.</summary>
        public string Reason { get; set; }
        public string Detail { get; set; }
        public bool Ok { get { return Reason == null; } }
    }

    /// <summary>
    /// Re-runs the planner on the bridge against the live catalog (plan R1-section 6.4 step 6) and renders the structural craft with the
    /// live _modVersions. The host plans for craft_plan; the bridge never trusts that: catalogHash and planHash are recomputed here.
    /// </summary>
    internal static class ApplyPlanner
    {
        private static readonly string[] SpecificReasons =
        {
            OperationReasons.PartLocked, OperationReasons.PartNotBuildable, OperationReasons.PartConstructionUnverified,
            OperationReasons.UnsupportedConfiguration, OperationReasons.UnsupportedStagingTopology, ControlReasons.FacilityMismatch
        };

        public static ApplyPlan Prepare(string graphJson, ICatalogPartReader reader, EditorHeader header, EditorUi ui, Func<uint> persistentIds, string facility, ConstructionSupportPolicy policy = null)
        {
            var result = new ApplyPlan();
            string error;
            var graph = GraphJson.Parse(graphJson, out error);
            if (graph == null) { result.Reason = ControlReasons.InvalidArgument; result.Detail = error; return result; }
            result.Graph = graph;
            var shape = Pure.GraphDtoValidator.Validate(graph);
            if (shape.Count != 0) return Refuse(result, shape);
            if (!string.Equals(graph.Facility, facility, StringComparison.Ordinal)) { result.Issues.Add(new Pure.PlanIssue(ControlReasons.FacilityMismatch, null, "graph facility " + graph.Facility + " but the editor is " + facility)); return Refuse(result, new List<Pure.PlanIssue>()); }
            if (header == null || string.IsNullOrEmpty(header.ModVersions)) { result.Reason = OperationReasons.ModVersionsUnavailable; result.Detail = "the live save header carries no _modVersions"; return result; }

            var names = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in graph.Parts) if (seen.Add(p.Part)) names.Add(p.Part);
            var fetch = new ReplanCatalog();
            for (var i = 0; i < names.Count; i += ConstructionLimits.MaxCatalogParts)
            {
                var chunk = names.GetRange(i, Math.Min(ConstructionLimits.MaxCatalogParts, names.Count - i));
                var data = ConstructionCatalogMapper.Build(chunk, reader, policy ?? ConstructionSupportPolicy.Default);
                var previous = fetch.ResearchAndDevelopment;
                if (!ReplanCatalog.TryParse(data, chunk, fetch, out error)) { result.Reason = OperationReasons.InvalidGraph; result.Detail = error; return result; }
                if (previous != null && !string.Equals(previous, (string)data["researchAndDevelopment"], StringComparison.Ordinal)) { result.Reason = OperationReasons.InvalidGraph; result.Detail = "research state changed while reading the catalog"; return result; }
            }
            var catalog = new Pure.ConstructionCatalog();
            foreach (var entry in fetch.Found.Values) catalog.Add(entry.Part);
            result.Catalog = catalog;

            var options = new Pure.PlannerOptions
            {
                ModVersions = header.ModVersions, PersistentIdGenerator = persistentIds,
                Version = string.IsNullOrEmpty(header.Version) ? new Pure.PlannerOptions().Version : header.Version,
                MissionFlag = ui == null || string.IsNullOrEmpty(ui.FlagUrl) ? new Pure.PlannerOptions().MissionFlag : ui.FlagUrl
            };
            result.Options = options;
            var plan = Pure.CraftPlanner.Plan(graph, catalog, options);
            result.Plan = plan;
            var issues = new List<Pure.PlanIssue>(plan.Issues);
            foreach (var p in graph.Parts)
            {
                ReplanEntry entry;
                if (fetch.Found.TryGetValue(p.Part, out entry) && !entry.StockAllowed)
                    issues.Add(new Pure.PlanIssue(OperationReasons.PartLocked, p.Id, "not unlocked or purchased for this save (" + entry.Basis + ")"));
            }
            if (issues.Count == 0 && plan.Ok)
            {
                foreach (var issue in Pure.StructuralCraftValidator.Validate(plan.Craft, catalog)) issues.Add(issue);
            }
            if (issues.Count != 0 || !plan.Ok) return Refuse(result, issues);

            result.CatalogHash = catalog.Hash();
            result.PlanHash = Pure.PlanHasher.Hash(graph, plan, result.CatalogHash);
            result.PartCount = plan.Craft.Parts.Count;
            result.CraftText = plan.Craft.ToText();
            if (result.CraftText.Length > Pure.ConfigText.MaxChars) { result.Reason = OperationReasons.CraftTooLarge; result.Detail = "rendered craft is " + result.CraftText.Length + " characters"; }
            return result;
        }

        private static ApplyPlan Refuse(ApplyPlan result, List<Pure.PlanIssue> issues)
        {
            result.Issues.AddRange(issues);
            var specific = result.Issues.Select(i => i.Code).FirstOrDefault(c => Array.IndexOf(SpecificReasons, c) >= 0);
            result.Reason = specific ?? OperationReasons.InvalidGraph;
            return result;
        }

        public static JArray IssuesJson(IEnumerable<Pure.PlanIssue> issues)
        {
            return new JArray(issues.Take(50).Select(i => (JToken)new JObject { ["code"] = i.Code, ["partId"] = i.PartId, ["reason"] = i.Reason }));
        }
    }
}
