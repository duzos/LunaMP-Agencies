using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace KspControl.EditorModel
{
    public sealed class ComparatorOptions
    {
        /// <summary>Twin mode: always tree mapping; the header ship name is ignored (twins are named differently); surface-attached parts compare transforms at 5 cm / 2 degrees. rot and description are compared like any other header key.</summary>
        public bool TwinMode { get; set; }
        /// <summary>Test-only: compare only the structure fields a planner emits (no MODULE/RESOURCE/etc., no extra part keys, short attN vectors allowed). Not reachable from production code.</summary>
        internal bool StructureOnly { get; set; }
        public RoundtripVolatileKeys Registry { get; set; } = RoundtripVolatileKeys.Default();
    }
    public sealed class CraftDifference
    {
        public string PartRef { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; }
        public string A { get; set; }
        public string B { get; set; }
        public override string ToString() { return Kind + " " + PartRef + " " + Path + ": " + A + " | " + B; }
    }
    public sealed class ExclusionApplied
    {
        public string Module { get; set; }
        public string KeyPath { get; set; }
        public string Rule { get; set; }
        public string EvidenceRef { get; set; }
        public int Count { get; set; }
    }
    public sealed class CraftComparison
    {
        public bool Equal { get; set; }
        public List<CraftDifference> Differences { get; } = new List<CraftDifference>();
        public int TotalDifferences { get; set; }
        public List<ExclusionApplied> ExclusionsApplied { get; } = new List<ExclusionApplied>();
        /// <summary>"craftId" or "tree".</summary>
        public string Mapping { get; set; }
    }
    /// <summary>Compares a reference craft A against a candidate B (R1-section 10, R3-section 10, R2 twin mode). Pure.</summary>
    public static class CraftComparator
    {
        public const int MaxDifferences = 100;
        public static CraftComparison Compare(ConfigNode a, ConfigNode b, ComparatorOptions options = null)
        {
            return new Run(a, b, options ?? new ComparatorOptions()).Execute();
        }
        internal static string ChildKey(CraftModel m, int parent, int child)
        {
            var c = m.Parts[child];
            if (c.Srf != null) return "srf||" + c.Name;
            string node = "";
            foreach (var an in m.Parts[parent].AttN)
                if (an.Partner != null && an.Partner.CidText == c.CidText) { node = an.NodeId; break; }
            return "stk|" + node + "|" + c.Name;
        }
        /// <summary>Angle in [0, 2pi) of a part about its parent's vertical axis, root-relative. Values within 1e-3 rad of 2pi wrap to 0 so counterparts at 0 stay together.</summary>
        internal static double AngleAbout(CraftModel m, int part, int root)
        {
            var info = m.Parts[part];
            Vector p, pp, rp = new Vector(); Rotation rr = RotationMath.Identity;
            if (root >= 0) { CraftModel.TryVector(m.Parts[root].Node.First("pos"), out rp); CraftModel.TryRotation(m.Parts[root].Node.First("rot"), out rr); }
            if (!CraftModel.TryVector(info.Node.First("pos"), out p)) return 0;
            var rel = SafeInverse(RotationMath.Sub(p, rp), rr);
            var origin = new Vector(0, 0, 0);
            if (info.Parent >= 0 && CraftModel.TryVector(m.Parts[info.Parent].Node.First("pos"), out pp)) origin = SafeInverse(RotationMath.Sub(pp, rp), rr);
            var a = Math.Atan2(rel.X - origin.X, rel.Z - origin.Z);
            if (a < 0) a += 2 * Math.PI;
            if (a > 2 * Math.PI - 1e-3) a = 0;
            return a;
        }
        internal static Vector SafeInverse(Vector v, Rotation q)
        {
            try { return RotationMath.InverseRotate(v, q); } catch (ArgumentException) { return v; }
        }
        private sealed class Run
        {
            private readonly ConfigNode ra, rb;
            private readonly CraftModel ma, mb;
            private readonly ComparatorOptions o;
            private readonly CraftComparison result = new CraftComparison();
            private int[] map;
            private int rootA, rootB;
            private Vector posA, posB; private Rotation rotA = RotationMath.Identity, rotB = RotationMath.Identity;
            private static readonly string[] SpecialKeys = { "part", "persistentId", "pos", "rot", "link", "attN", "srfN", "sym" };
            private static readonly string[] StructureKeys = { "attPos", "attPos0", "attRot", "attRot0", "mir", "symMethod", "autostrutMode", "rigidAttachment", "istg", "dstg", "sidx", "sqor", "sepI", "attm" };
            private static readonly string[] IgnoredHeader = { "version", "persistentId", "steamPublishedFileId", "size" };
            private static readonly string[] TwinIgnoredHeader = { "ship" };
            private static readonly string[] StructureHeader = { "type", "missionFlag", "vesselType", "_modVersions" };
            public Run(ConfigNode a, ConfigNode b, ComparatorOptions options)
            { ra = a; rb = b; o = options; ma = CraftModel.Build(a); mb = CraftModel.Build(b); }
            private void Diff(string part, string path, string kind, string a, string b)
            {
                result.TotalDifferences++;
                if (result.Differences.Count < MaxDifferences) result.Differences.Add(new CraftDifference { PartRef = part, Path = path, Kind = kind, A = a, B = b });
            }
            private void Excluded(VolatileKeyEntry e)
            {
                var hit = result.ExclusionsApplied.FirstOrDefault(x => x.Module == e.Module && x.KeyPath == e.KeyPath);
                if (hit == null) result.ExclusionsApplied.Add(new ExclusionApplied { Module = e.Module, KeyPath = e.KeyPath, Rule = e.Rule.ToString(), EvidenceRef = e.EvidenceRef, Count = 1 });
                else hit.Count++;
            }
            public CraftComparison Execute()
            {
                rootA = ma.Root0(); rootB = mb.Root0();
                if (rootA >= 0) { CraftModel.TryVector(ma.Parts[rootA].Node.First("pos"), out posA); CraftModel.TryRotation(ma.Parts[rootA].Node.First("rot"), out rotA); }
                if (rootB >= 0) { CraftModel.TryVector(mb.Parts[rootB].Node.First("pos"), out posB); CraftModel.TryRotation(mb.Parts[rootB].Node.First("rot"), out rotB); }
                CompareHeader();
                if (!BuildMapping()) { result.Equal = false; return result; }
                for (int i = 0; i < ma.Parts.Count; i++) if (map[i] >= 0) ComparePart(ma.Parts[i], mb.Parts[map[i]]);
                result.Equal = result.TotalDifferences == 0;
                return result;
            }
            // ---------- header ----------
            private void CompareHeader()
            {
                var keys = new List<string>();
                foreach (var kv in ra.Values()) if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
                foreach (var kv in rb.Values()) if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
                foreach (var key in keys)
                {
                    if (IgnoredHeader.Contains(key)) continue;
                    if (o.TwinMode && TwinIgnoredHeader.Contains(key)) continue;
                    if (o.StructureOnly && !StructureHeader.Contains(key)) continue;
                    CompareKey("HEADER", "", "HEADER", key, ra.Values(key).ToList(), rb.Values(key).ToList(), HeaderValue(ra, "description"), HeaderValue(rb, "description"), true);
                }
            }
            private static string HeaderValue(ConfigNode root, string key) { return root.First(key) ?? ""; }
            // ---------- mapping ----------
            private bool BuildMapping()
            {
                map = Enumerable.Repeat(-1, ma.Parts.Count).ToArray();
                if (!o.TwinMode && CidMappingPossible())
                {
                    result.Mapping = "craftId";
                    for (int i = 0; i < ma.Parts.Count; i++) map[i] = mb.ByCid[ma.Parts[i].CidText];
                    return true;
                }
                result.Mapping = "tree";
                if (ma.Parts.Count == 0 && mb.Parts.Count == 0) return true;
                if (rootA < 0 || rootB < 0) { Diff(null, "PART", "part_count", ma.Parts.Count.ToString(CultureInfo.InvariantCulture), mb.Parts.Count.ToString(CultureInfo.InvariantCulture)); return false; }
                bool ok = MapTree(rootA, rootB);
                if (!ok) { Diff(null, "PART", "mapping_ambiguous", null, null); return false; }
                // Parts the walk never reached (unmatched children, orphans, other roots) are reported, not silently dropped.
                var mapped = new HashSet<int>(map.Where(x => x >= 0));
                for (int i = 0; i < ma.Parts.Count; i++) if (map[i] < 0) Diff(ma.Parts[i].Ref, "PART", "missing_part", ma.Parts[i].Ref, null);
                for (int i = 0; i < mb.Parts.Count; i++) if (!mapped.Contains(i)) Diff(mb.Parts[i].Ref, "PART", "extra_part", null, mb.Parts[i].Ref);
                return true;
            }
            private bool CidMappingPossible()
            {
                if (ma.Parts.Count != mb.Parts.Count || ma.DuplicateCids.Count != 0 || mb.DuplicateCids.Count != 0) return false;
                if (ma.Parts.Any(p => !p.CidValid) || mb.Parts.Any(p => !p.CidValid)) return false;
                return ma.Parts.All(p => mb.ByCid.ContainsKey(p.CidText));
            }
            private bool MapTree(int ia, int ib)
            {
                map[ia] = ib;
                var ga = Group(ma, ia); var gb = Group(mb, ib);
                var keys = ga.Keys.Concat(gb.Keys.Where(k => !ga.ContainsKey(k))).ToList();
                foreach (var key in keys)
                {
                    List<int> la, lb;
                    if (!ga.TryGetValue(key, out la)) la = new List<int>();
                    if (!gb.TryGetValue(key, out lb)) lb = new List<int>();
                    if (la.Count > 1 || lb.Count > 1)
                    {
                        if (!IsSymGroup(ma, la) || !IsSymGroup(mb, lb)) return false;
                        la = la.OrderBy(i => CraftComparator.AngleAbout(ma, i, rootA)).ThenBy(i => i).ToList();
                        lb = lb.OrderBy(i => CraftComparator.AngleAbout(mb, i, rootB)).ThenBy(i => i).ToList();
                    }
                    int n = Math.Min(la.Count, lb.Count);
                    for (int i = 0; i < n; i++) if (!MapTree(la[i], lb[i])) return false;
                }
                return true;
            }
            private static bool IsSymGroup(CraftModel m, List<int> group)
            {
                if (group.Count <= 1) return true;
                return group.All(i => m.Parts[i].Sym.Count >= group.Count - 1);
            }
            private static Dictionary<string, List<int>> Group(CraftModel m, int parent)
            {
                var d = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (var c in m.Parts[parent].Children)
                {
                    var key = CraftComparator.ChildKey(m, parent, c);
                    List<int> l;
                    if (!d.TryGetValue(key, out l)) d[key] = l = new List<int>();
                    l.Add(c);
                }
                return d;
            }
            // ---------- parts ----------
            private string Translate(CraftRef r)
            {
                if (r == null) return null;
                int ia;
                if (r.CidValid && ma.ByCid.TryGetValue(r.CidText, out ia) && map[ia] >= 0) return mb.Parts[map[ia]].CidText;
                return "?" + r.CidText;
            }
            private void ComparePart(CraftPartInfo a, CraftPartInfo b)
            {
                var pr = a.Ref;
                if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) Diff(pr, "part", "part_name", a.Name, b.Name);
                // Transforms, root-relative.
                bool surface = a.Srf != null || b.Srf != null;
                double posTol = o.TwinMode && surface ? 0.05 : 0.001;
                double rotTol = o.TwinMode && surface ? 2 * Math.PI / 180 : 1e-3;
                CompareTransform(pr, "pos", a.Node.First("pos"), b.Node.First("pos"), posTol);
                CompareRotation(pr, "rot", a.Node.First("rot"), b.Node.First("rot"), rotTol);
                // Links by mapped identity.
                var la = new HashSet<string>(a.Links.Where(x => x != null).Select(Translate));
                var lb = new HashSet<string>(b.Links.Where(x => x != null).Select(x => x.CidText));
                if (!la.SetEquals(lb)) Diff(pr, "link", "link", string.Join(",", a.Links.Where(x => x != null).Select(x => x.Name + "_" + x.CidText)), string.Join(",", b.Links.Where(x => x != null).Select(x => x.Name + "_" + x.CidText)));
                var sa = new HashSet<string>(a.Sym.Where(x => x != null).Select(Translate));
                var sb = new HashSet<string>(b.Sym.Where(x => x != null).Select(x => x.CidText));
                if (!sa.SetEquals(sb)) Diff(pr, "sym", "sym", string.Join(",", sa.OrderBy(x => x)), string.Join(",", sb.OrderBy(x => x)));
                var srfA = Translate(a.Srf); var srfB = b.Srf == null ? null : b.Srf.CidText;
                if (srfA != srfB) Diff(pr, "srfN", "srfN", srfA, srfB);
                CompareAttN(pr, a, b);
                // Remaining keys by default rule / registry.
                var keys = new List<string>();
                foreach (var kv in a.Node.Values()) if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
                foreach (var kv in b.Node.Values()) if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
                foreach (var key in keys)
                {
                    if (SpecialKeys.Contains(key)) continue;
                    if (o.StructureOnly && !StructureKeys.Contains(key)) continue;
                    var va = a.Node.Values(key).ToList(); var vb = b.Node.Values(key).ToList();
                    if (o.TwinMode && surface && (key == "attPos0" || key == "attRot0") && va.Count == 1 && vb.Count == 1)
                    {
                        if (key == "attPos0") CompareLoose(pr, key, va[0], vb[0], posTol, false);
                        else CompareLoose(pr, key, va[0], vb[0], rotTol, true);
                        continue;
                    }
                    CompareKey("PART", "", pr, key, va, vb, HeaderValue(ra, "description"), HeaderValue(rb, "description"), false);
                }
                if (o.StructureOnly) return;
                CompareChildren(a.Node, b.Node, pr, "");
            }
            private void CompareLoose(string pr, string key, string a, string b, double tol, bool rotation)
            {
                if (rotation)
                {
                    Rotation qa, qb;
                    if (!CraftModel.TryRotation(a, out qa) || !CraftModel.TryRotation(b, out qb) || RotationMath.AngleBetween(qa, qb) > tol) Diff(pr, key, "value", a, b);
                }
                else
                {
                    Vector va, vb;
                    if (!CraftModel.TryVector(a, out va) || !CraftModel.TryVector(b, out vb) || RotationMath.Length(RotationMath.Sub(va, vb)) > tol) Diff(pr, key, "value", a, b);
                }
            }
            private void CompareTransform(string pr, string key, string a, string b, double tol)
            {
                if (a == null || b == null) { if (a != b) Diff(pr, key, "missing_key", a, b); return; }
                Vector va, vb;
                if (!CraftModel.TryVector(a, out va) || !CraftModel.TryVector(b, out vb)) { if (!ValueRule.Equal(a, b)) Diff(pr, key, "value", a, b); return; }
                var d = RotationMath.Sub(CraftComparator.SafeInverse(RotationMath.Sub(va, posA), rotA), CraftComparator.SafeInverse(RotationMath.Sub(vb, posB), rotB));
                if (RotationMath.Length(d) > tol) Diff(pr, key, "value", a, b);
            }
            private void CompareRotation(string pr, string key, string a, string b, double tol)
            {
                if (a == null || b == null) { if (a != b) Diff(pr, key, "missing_key", a, b); return; }
                Rotation qa, qb;
                if (!CraftModel.TryRotation(a, out qa) || !CraftModel.TryRotation(b, out qb)) { if (!ValueRule.Equal(a, b)) Diff(pr, key, "value", a, b); return; }
                try
                {
                    var ra2 = RotationMath.Multiply(RotationMath.Conjugate(rotA), qa); var rb2 = RotationMath.Multiply(RotationMath.Conjugate(rotB), qb);
                    if (RotationMath.AngleBetween(ra2, rb2) > tol) Diff(pr, key, "value", a, b);
                }
                catch (ArgumentException) { if (!ValueRule.Equal(a, b)) Diff(pr, key, "value", a, b); }
            }
            private void CompareAttN(string pr, CraftPartInfo a, CraftPartInfo b)
            {
                var da = a.AttN.Where(x => x.Partner != null).GroupBy(x => x.NodeId).ToDictionary(g => g.Key, g => g.ToList());
                var db = b.AttN.Where(x => x.Partner != null).GroupBy(x => x.NodeId).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var id in da.Keys.Concat(db.Keys.Where(k => !da.ContainsKey(k))))
                {
                    List<CraftAttN> la, lb; if (!da.TryGetValue(id, out la)) la = new List<CraftAttN>(); if (!db.TryGetValue(id, out lb)) lb = new List<CraftAttN>();
                    if (la.Count > 1 || lb.Count > 1) { Diff(pr, "attN/" + id, "attN_duplicate", la.Count.ToString(CultureInfo.InvariantCulture), lb.Count.ToString(CultureInfo.InvariantCulture)); continue; }
                    CraftAttN x = la.Count == 1 ? la[0] : null, y = lb.Count == 1 ? lb[0] : null;
                    var tx = x == null ? null : Translate(x.Partner); var ty = y == null ? null : y.Partner.CidText;
                    if (tx != ty) { Diff(pr, "attN/" + id, "attN", x == null ? null : x.Raw, y == null ? null : y.Raw); continue; }
                    if (x != null && y != null && x.Partner.VectorText != null && y.Partner.VectorText != null && !VectorsEqual(x.Partner.VectorText, y.Partner.VectorText))
                        Diff(pr, "attN/" + id, "attN_vector", x.Raw, y.Raw);
                }
            }
            /// <summary>Vector segments must agree in count; only the internal structure-only mode accepts a short form against a full one.</summary>
            private bool VectorsEqual(string a, string b)
            {
                var sa = a.Split('_'); var sb = b.Split('_');
                if (sa.Length != sb.Length && !o.StructureOnly) return false;
                int n = Math.Min(sa.Length, sb.Length);
                for (int i = 0; i < n; i++) if (!ValueRule.EqualAbsolute(sa[i], sb[i], 1e-4)) return false;
                return true;
            }
            // ---------- child nodes (modules, resources, events, ...) ----------
            private void CompareChildren(ConfigNode a, ConfigNode b, string pr, string prefix)
            {
                var names = new List<string>();
                foreach (var c in a.Children()) if (!names.Contains(c.Name)) names.Add(c.Name);
                foreach (var c in b.Children()) if (!names.Contains(c.Name)) names.Add(c.Name);
                foreach (var name in names)
                {
                    var ca = a.Children(name).ToList(); var cb = b.Children(name).ToList();
                    if (prefix == "" && name == "MODULE") { CompareModules(ca, cb, pr); continue; }
                    if (prefix == "" && name == "RESOURCE") { CompareResources(ca, cb, pr); continue; }
                    if (ca.Count != cb.Count) { Diff(pr, prefix + name, "node_count", ca.Count.ToString(CultureInfo.InvariantCulture), cb.Count.ToString(CultureInfo.InvariantCulture)); }
                    int n = Math.Min(ca.Count, cb.Count);
                    for (int i = 0; i < n; i++) CompareNode(ca[i], cb[i], pr, prefix == "" ? name : "", prefix + name + (ca.Count > 1 ? "[" + i + "]" : "") + "/", prefix == "" ? name : null);
                }
            }
            private void CompareModules(List<ConfigNode> ca, List<ConfigNode> cb, string pr)
            {
                var na = ca.Select(x => x.First("name") ?? "").ToList(); var nb = cb.Select(x => x.First("name") ?? "").ToList();
                if (!na.SequenceEqual(nb)) { Diff(pr, "MODULE", "module_sequence", string.Join(",", na), string.Join(",", nb)); return; }
                for (int i = 0; i < ca.Count; i++) CompareNode(ca[i], cb[i], pr, na[i], "MODULE[" + na[i] + "]/", null);
            }
            private void CompareResources(List<ConfigNode> ca, List<ConfigNode> cb, string pr)
            {
                var ga = ca.GroupBy(x => x.First("name") ?? "").ToDictionary(g => g.Key, g => g.ToList());
                var gb = cb.GroupBy(x => x.First("name") ?? "").ToDictionary(g => g.Key, g => g.ToList());
                foreach (var name in ga.Keys.Concat(gb.Keys.Where(k => !ga.ContainsKey(k))))
                {
                    List<ConfigNode> la, lb;
                    if (!ga.TryGetValue(name, out la)) la = new List<ConfigNode>();
                    if (!gb.TryGetValue(name, out lb)) lb = new List<ConfigNode>();
                    if (la.Count != lb.Count) { Diff(pr, "RESOURCE[" + name + "]", "resource_count", la.Count.ToString(CultureInfo.InvariantCulture), lb.Count.ToString(CultureInfo.InvariantCulture)); }
                    for (int i = 0; i < Math.Min(la.Count, lb.Count); i++) CompareNode(la[i], lb[i], pr, "RESOURCE", "RESOURCE[" + name + "]/", null);
                }
            }
            /// <summary>Recursive node compare. module is the registry scope ("" inherits the caller scope via fixedModule).</summary>
            private void CompareNode(ConfigNode a, ConfigNode b, string pr, string module, string display, string containerScope)
            {
                // For ACTIONS/EVENTS/PARTDATA/VESSELNAMING the registry module is the container name and keyPath starts at its children.
                string scope = module; string path = "";
                if (containerScope != null) { scope = containerScope; }
                CompareNodeInner(a, b, pr, scope, path, display);
            }
            private void CompareNodeInner(ConfigNode a, ConfigNode b, string pr, string module, string path, string display)
            {
                var keys = new List<string>();
                foreach (var kv in a.Values()) if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
                foreach (var kv in b.Values()) if (!keys.Contains(kv.Key)) keys.Add(kv.Key);
                // Registry entries whose key is absent from both sides but defaulted still need no action.
                foreach (var key in keys)
                    CompareKey(module, path, pr, key, a.Values(key).ToList(), b.Values(key).ToList(), HeaderValue(ra, "description"), HeaderValue(rb, "description"), false, display);
                // Keys that only the registry knows (absent-equals-default) are covered above only when present on one side.
                var names = new List<string>();
                foreach (var c in a.Children()) if (!names.Contains(c.Name)) names.Add(c.Name);
                foreach (var c in b.Children()) if (!names.Contains(c.Name)) names.Add(c.Name);
                foreach (var name in names)
                {
                    var ca = a.Children(name).ToList(); var cb = b.Children(name).ToList();
                    if (ca.Count != cb.Count) Diff(pr, display + name, "node_count", ca.Count.ToString(CultureInfo.InvariantCulture), cb.Count.ToString(CultureInfo.InvariantCulture));
                    for (int i = 0; i < Math.Min(ca.Count, cb.Count); i++)
                        CompareNodeInner(ca[i], cb[i], pr, module, path + name + (ca.Count > 1 ? "[" + i + "]" : "") + "/", display + name + (ca.Count > 1 ? "[" + i + "]" : "") + "/");
                }
            }
            // ---------- key comparison with registry ----------
            private void CompareKey(string module, string path, string pr, string key, List<string> va, List<string> vb, string descA, string descB, bool header, string display = null)
            {
                var entry = o.Registry == null ? null : o.Registry.Find(module, path + key);
                string shown = (display ?? "") + key;
                int n = Math.Max(va.Count, vb.Count);
                for (int i = 0; i < n; i++)
                {
                    string x = i < va.Count ? va[i] : null, y = i < vb.Count ? vb[i] : null;
                    string ordinalPath = shown + (n > 1 ? "[" + i + "]" : "");
                    if (entry != null)
                    {
                        switch (entry.Rule)
                        {
                            case VolatileRule.Ignore:
                                if (!Same(x, y)) Excluded(entry);
                                continue;
                            case VolatileRule.AbsentEqualsDefault:
                                {
                                    var ex = x ?? entry.DefaultValue; var ey = y ?? entry.DefaultValue;
                                    if (!ValueRule.Equal(ex, ey)) Diff(pr, ordinalPath, "value", x, y);
                                    else if (x == null || y == null) Excluded(entry);
                                    continue;
                                }
                            case VolatileRule.HeaderDerived:
                                {
                                    var hk = entry.HeaderKey ?? "description";
                                    var hx = Norm(x) == Norm(HeaderValue(ra, hk)); var hy = Norm(y) == Norm(HeaderValue(rb, hk));
                                    if (x == null || y == null) { Diff(pr, ordinalPath, "missing_key", x, y); continue; }
                                    if (!hx || !hy) Diff(pr, ordinalPath, "header_derived_mismatch", x, y);
                                    else if (!Same(x, y)) Excluded(entry);
                                    continue;
                                }
                            case VolatileRule.RootRelativeVector:
                                if (x == null || y == null) { Diff(pr, ordinalPath, "missing_key", x, y); continue; }
                                if (!RootRelativeEqual(x, y)) Diff(pr, ordinalPath, "value", x, y);
                                else if (!Same(x, y)) Excluded(entry);
                                continue;
                        }
                    }
                    if (x == null) Diff(pr, ordinalPath, "missing_in_a", null, y);
                    else if (y == null) Diff(pr, ordinalPath, "missing_in_b", x, null);
                    else if (!ValueRule.Equal(x, y)) Diff(pr, ordinalPath, "value", x, y);
                }
            }
            private static bool Same(string x, string y) { return x != null && y != null && ValueRule.Equal(x, y); }
            /// <summary>KSP stores newlines in header descriptions as U+00A8; module copies may hold real newlines or a literal backslash-n.</summary>
            private static string Norm(string s) { return s == null ? null : s.Replace("\r\n", "\n").Replace("\\n", "\n").Replace("\r", "\n").Replace("\u00A8", "\n"); }
            private bool RootRelativeEqual(string x, string y)
            {
                Vector vx, vy; Rotation qx, qy;
                if (CraftModel.TryVector(x, out vx) && CraftModel.TryVector(y, out vy))
                    return RotationMath.Length(RotationMath.Sub(CraftComparator.SafeInverse(RotationMath.Sub(vx, posA), rotA), CraftComparator.SafeInverse(RotationMath.Sub(vy, posB), rotB))) <= 1e-3;
                if (CraftModel.TryRotation(x, out qx) && CraftModel.TryRotation(y, out qy))
                {
                    try { return RotationMath.AngleBetween(RotationMath.Multiply(RotationMath.Conjugate(rotA), qx), RotationMath.Multiply(RotationMath.Conjugate(rotB), qy)) <= 1e-3; }
                    catch (ArgumentException) { return false; }
                }
                return ValueRule.Equal(x, y);
            }
        }
    }
}
