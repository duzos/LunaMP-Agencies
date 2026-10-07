using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace KspControl.EditorModel
{
    /// <summary>
    /// Validates a structure-only craft (as text tree) before it is staged for a load. Pure; no KSP access.
    /// A catalog is optional: without it, node-id and surface-acceptance checks are skipped.
    /// </summary>
    public static class StructuralCraftValidator
    {
        public const int MaxParts = GraphDtoLimits.MaxParts;
        private static readonly string[] VectorKeys = { "pos", "attPos", "attPos0", "mir" };
        private static readonly string[] RotationKeys = { "rot", "attRot", "attRot0" };
        private static readonly string[] IntegerKeys = { "istg", "resPri", "dstg", "sidx", "sqor", "sepI", "attm" };
        private static readonly string[] RequiredKeys = { "pos", "rot", "attPos0", "attRot0" };
        public static List<PlanIssue> Validate(StructuralCraft craft, ConstructionCatalog catalog = null)
        {
            return Validate(craft.ToConfigNode(), catalog);
        }
        public static List<PlanIssue> Validate(ConfigNode root, ConstructionCatalog catalog = null)
        {
            var issues = new List<PlanIssue>();
            Action<string, string, string> add = (code, part, reason) => issues.Add(new PlanIssue(code, part, reason));
            if (root == null) { add("invalid_craft", null, "craft missing"); return issues; }
            if (HasControl(root, 0)) add("control_character", null, "control character in header");
            if (root.First("_modVersions") == null) add("missing_mod_versions", null, "header _modVersions is required (copied from a live SaveShip header)");
            var model = CraftModel.Build(root);
            if (model.Parts.Count == 0) { add("no_parts", null, null); return issues; }
            if (model.Parts.Count > MaxParts) { add("too_many_parts", null, model.Parts.Count + " > " + MaxParts); return issues; }
            foreach (var c in model.DuplicateCids) add("duplicate_cid", null, c);
            foreach (var p in model.Parts) CheckPart(p, root, add);
            // References.
            Action<string, CraftRef, CraftPartInfo> checkRef = (code, r, owner) =>
            {
                if (r == null) return;
                if (!r.CidValid || !model.ByCid.ContainsKey(r.CidText)) { add(code, owner.Ref, r.Name + "_" + r.CidText); return; }
                var target = model.Parts[model.ByCid[r.CidText]];
                if (target.Index == owner.Index) add("self_reference", owner.Ref, code);
                else if (!string.Equals(target.Name, r.Name, StringComparison.Ordinal)) add(code, owner.Ref, "name mismatch for " + r.CidText);
            };
            foreach (var p in model.Parts)
            {
                foreach (var l in p.Links) checkRef("dangling_link", l, p);
                foreach (var s in p.Sym) checkRef("dangling_sym", s, p);
                if (p.HasSrfValue && p.Srf == null) add("invalid_srfn", p.Ref, null);
                checkRef("dangling_srfn", p.Srf, p);
                foreach (var a in p.AttN) checkRef("dangling_attn", a.Partner, p);
            }
            // Tree shape: one parent each, a single root, no cycle, everything reachable.
            var incoming = new Dictionary<int, int>();
            foreach (var p in model.Parts)
                foreach (var l in p.Links)
                {
                    int child;
                    if (l != null && l.CidValid && model.ByCid.TryGetValue(l.CidText, out child) && child != p.Index) { int n; incoming.TryGetValue(child, out n); incoming[child] = n + 1; }
                }
            foreach (var kv in incoming) if (kv.Value > 1) add("multiple_parents", model.Parts[kv.Key].Ref, null);
            var roots = model.Parts.Where(p => !incoming.ContainsKey(p.Index)).ToList();
            if (HasCycle(model)) add("cycle", null, "link graph contains a cycle");
            else
            {
                if (roots.Count != 1) add("no_single_root", null, roots.Count + " roots");
                else
                {
                    var seen = new HashSet<int>(); var stack = new Stack<int>(); stack.Push(roots[0].Index);
                    while (stack.Count > 0)
                    {
                        var i = stack.Pop(); if (!seen.Add(i)) continue;
                        foreach (var l in model.Parts[i].Links) { int c; if (l != null && l.CidValid && model.ByCid.TryGetValue(l.CidText, out c)) stack.Push(c); }
                    }
                    foreach (var p in model.Parts) if (!seen.Contains(p.Index)) add("disconnected", p.Ref, null);
                }
            }
            // Attachment partners must agree with the link tree: srfN names the link parent; attN names the link parent or a link child.
            foreach (var p in model.Parts)
            {
                if (p.Srf != null && p.Srf.CidValid && (p.Parent < 0 || model.Parts[p.Parent].CidText != p.Srf.CidText)) add("attach_partner_not_linked", p.Ref, "srfN");
                foreach (var a in p.AttN)
                {
                    if (a.Partner == null || !a.Partner.CidValid) continue;
                    bool isParent = p.Parent >= 0 && model.Parts[p.Parent].CidText == a.Partner.CidText;
                    bool isChild = p.Children.Any(c => model.Parts[c].CidText == a.Partner.CidText);
                    if (!isParent && !isChild) add("attach_partner_not_linked", p.Ref, "attN " + a.NodeId);
                }
            }
            // Attach nodes.
            foreach (var p in model.Parts)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var a in p.AttN)
                {
                    if (!ids.Add(a.NodeId)) add("node_doubly_occupied", p.Ref, a.NodeId);
                    if (a.Partner != null && a.Partner.CidValid && model.ByCid.ContainsKey(a.Partner.CidText))
                    {
                        var partner = model.Parts[model.ByCid[a.Partner.CidText]];
                        if (!partner.AttN.Any(x => x.Partner != null && x.Partner.CidValid && x.Partner.CidText == p.CidText)) add("attn_not_reciprocal", p.Ref, a.NodeId);
                    }
                }
            }
            // Symmetry must be mutual.
            foreach (var p in model.Parts)
                foreach (var s in p.Sym)
                {
                    int other;
                    if (s != null && s.CidValid && model.ByCid.TryGetValue(s.CidText, out other) && other != p.Index
                        && !model.Parts[other].Sym.Any(x => x != null && x.CidValid && x.CidText == p.CidText)) add("asymmetric_sym", p.Ref, s.CidText);
                }
            // Catalog-dependent checks.
            if (catalog != null)
                foreach (var p in model.Parts)
                {
                    var def = catalog.Find(p.Name);
                    if (def == null) { add("unknown_part", p.Ref, p.Name); continue; }
                    foreach (var a in p.AttN) if (def.FindNode(a.NodeId) == null) add("unknown_attach_node", p.Ref, a.NodeId);
                    if (p.Srf != null && p.Srf.CidValid && model.ByCid.ContainsKey(p.Srf.CidText))
                    {
                        var parentDef = catalog.Find(model.Parts[model.ByCid[p.Srf.CidText]].Name);
                        if (parentDef != null && (parentDef.AttachRules == null || !parentDef.AttachRules.AllowSrf)) add("surface_parent_not_accepting", p.Ref, parentDef.Name);
                        if (!def.AttachRules.Srf) add("surface_not_supported_by_part", p.Ref, def.Name);
                    }
                }
            return issues;
        }
        private static void CheckPart(CraftPartInfo p, ConfigNode root, Action<string, string, string> add)
        {
            if (p.PartValue == null) { add("missing_part_value", p.Ref, null); return; }
            if (!p.CidValid) add("invalid_cid", p.Ref, null);
            if (p.Name.Length == 0) add("invalid_part_name", p.Ref, null);
            else if (p.Name.IndexOf('_') >= 0) add("part_name_underscore", p.Ref, p.Name);
            else if (!GraphDtoValidator.IsPartName(p.Name)) add("invalid_part_name", p.Ref, p.Name);
            if (HasControl(p.Node, 0)) add("control_character", p.Ref, null);
            foreach (var key in RequiredKeys) if (p.Node.First(key) == null) add("missing_key", p.Ref, key);
            foreach (var key in VectorKeys) foreach (var v in p.Node.Values(key)) CheckNumbers(v, 3, key, p, add);
            foreach (var key in RotationKeys) foreach (var v in p.Node.Values(key)) CheckNumbers(v, 4, key, p, add);
            foreach (var key in IntegerKeys)
                foreach (var v in p.Node.Values(key))
                { int n; if (!int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) add("invalid_integer", p.Ref, key); }
            foreach (var child in p.Node.Children())
                add(child.Name == "MODULE" ? "module_in_structural_craft" : "forbidden_node_in_structural_craft", p.Ref, child.Name);
        }
        private static void CheckNumbers(string value, int count, string key, CraftPartInfo p, Action<string, string, string> add)
        {
            var parts = value.Split(',');
            if (parts.Length != count) { add("invalid_number", p.Ref, key); return; }
            foreach (var token in parts)
            {
                var t = token.Trim();
                var lower = t.ToLowerInvariant().TrimStart('+', '-');
                if (lower == "nan" || lower == "infinity" || lower == "inf" || lower == "∞") { add("non_finite_number", p.Ref, key); return; }
                double d;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { add("invalid_number", p.Ref, key); return; }
                if (double.IsNaN(d) || double.IsInfinity(d)) { add("non_finite_number", p.Ref, key); return; }
            }
        }
        private static bool HasControl(ConfigNode node, int depth)
        {
            if (depth > ConfigText.MaxDepth) return true;
            foreach (var e in node.Entries)
            {
                if (e.IsValue) { if (ContainsControl(e.Key) || ContainsControl(e.Value)) return true; }
                else if (ContainsControl(e.Child.Name) || HasControl(e.Child, depth + 1)) return true;
            }
            return false;
        }
        private static bool ContainsControl(string s) { foreach (var c in s) if (char.IsControl(c)) return true; return false; }
        private static bool HasCycle(CraftModel model)
        {
            var color = new int[model.Parts.Count]; // 0 white, 1 grey, 2 black
            for (int s = 0; s < model.Parts.Count; s++)
            {
                if (color[s] != 0) continue;
                var stack = new Stack<KeyValuePair<int, int>>();
                stack.Push(new KeyValuePair<int, int>(s, 0)); color[s] = 1;
                while (stack.Count > 0)
                {
                    var top = stack.Pop();
                    var links = model.Parts[top.Key].Links;
                    if (top.Value >= links.Count) { color[top.Key] = 2; continue; }
                    stack.Push(new KeyValuePair<int, int>(top.Key, top.Value + 1));
                    var l = links[top.Value]; int c;
                    if (l == null || !l.CidValid || !model.ByCid.TryGetValue(l.CidText, out c)) continue;
                    if (color[c] == 1) return true;
                    if (color[c] == 0) { color[c] = 1; stack.Push(new KeyValuePair<int, int>(c, 0)); }
                }
            }
            return false;
        }
    }
}
