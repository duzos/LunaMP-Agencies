using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
namespace KspControl.EditorModel
{
    /// <summary>
    /// Canonical projection of a craft and its SHA-256 fingerprint. Applies the volatile-key registry, drops persistentId and
    /// absolute positions (root-relative instead), renumbers part identities canonically and rounds numbers to float precision.
    /// Optional UI fields are part of the hash.
    /// </summary>
    public static class CraftFingerprint
    {
        private static readonly string[] IgnoredHeader = { "version", "persistentId", "steamPublishedFileId", "size" };
        public static string Compute(ConfigNode craft, RoundtripVolatileKeys registry = null, string uiName = null, string uiDescription = null, string flagUrl = null)
        {
            var text = Project(craft, registry ?? RoundtripVolatileKeys.Default(), uiName, uiDescription, flagUrl);
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(Encoding.UTF8.GetBytes(text))) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
        public static string Project(ConfigNode craft, RoundtripVolatileKeys registry, string uiName, string uiDescription, string flagUrl)
        {
            var sb = new StringBuilder();
            if (uiName != null || uiDescription != null || flagUrl != null)
                sb.Append("ui:").Append(uiName).Append('\u0001').Append(uiDescription).Append('\u0001').Append(flagUrl).Append('\n');
            var m = CraftModel.Build(craft);
                        var headerLines = new List<string>();
            foreach (var kv in craft.Values())
            {
                if (IgnoredHeader.Contains(kv.Key)) continue;
                var e = registry.Find("HEADER", kv.Key);
                if (e != null && e.Rule == VolatileRule.Ignore) continue;
                headerLines.Add("H|" + kv.Key + "=" + Canon(kv.Value));
            }
            headerLines.Sort(StringComparer.Ordinal);
            foreach (var l in headerLines) sb.Append(l).Append('\n');
            int root = m.Root0();
            if (root < 0) return sb.ToString();
            // Canonical order: depth-first from the root, children sorted by structural key then geometry then file order.
            var order = new List<int>(); var rank = new Dictionary<int, int>();
            var stack = new Stack<int>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                if (rank.ContainsKey(i)) continue;
                rank[i] = order.Count; order.Add(i);
                var kids = m.Parts[i].Children.OrderBy(c => CraftComparator.ChildKey(m, i, c), StringComparer.Ordinal)
                    .ThenBy(c => Math.Round(CraftComparator.AngleAbout(m, c, root), 3)).ThenBy(c => c).ToList();
                for (int k = kids.Count - 1; k >= 0; k--) stack.Push(kids[k]);
            }
            for (int i = 0; i < m.Parts.Count; i++) if (!rank.ContainsKey(i)) { rank[i] = order.Count; order.Add(i); }
            Vector rp = new Vector(); Rotation rr = RotationMath.Identity;
            CraftModel.TryVector(m.Parts[root].Node.First("pos"), out rp); CraftModel.TryRotation(m.Parts[root].Node.First("rot"), out rr);
            Func<CraftRef, string> id = r => r != null && r.CidValid && m.ByCid.ContainsKey(r.CidText) ? "#" + rank[m.ByCid[r.CidText]] : "?";
            foreach (var idx in order)
            {
                var p = m.Parts[idx];
                sb.Append("P").Append(rank[idx]).Append('|').Append(p.Name).Append('\n');
                var lines = new List<string>();
                foreach (var kv in p.Node.Values())
                {
                    var k = kv.Key; string v = kv.Value;
                    if (k == "part" || k == "persistentId") continue;
                    if (k == "attPos0" && idx == root) continue; // root attPos0 is the absolute position
                    var e = registry.Find("PART", k);
                    if (e != null && e.Rule == VolatileRule.Ignore) continue;
                    if (k == "pos") { Vector pv; if (CraftModel.TryVector(v, out pv)) { var rel = CraftComparator.SafeInverse(RotationMath.Sub(pv, rp), rr); v = Round3(rel.X) + "," + Round3(rel.Y) + "," + Round3(rel.Z); } lines.Add("pos=" + v); continue; }
                    if (k == "rot") { Rotation q; if (CraftModel.TryRotation(v, out q)) { try { v = CanonRotation(RotationMath.Multiply(RotationMath.Conjugate(rr), q)); } catch (ArgumentException) { } } lines.Add("rot=" + v); continue; }
                    if (k == "link" || k == "sym") { lines.Add(k + "=" + id(CraftModel.ParseRef(v))); continue; }
                    if (k == "srfN") { lines.Add("srfN=" + id(p.Srf)); continue; }
                    if (k == "attN")
                    {
                        var a = p.AttN.FirstOrDefault(x => x.Raw == v);
                        if (a == null || a.Partner == null) continue; // open nodes carry no structure
                        lines.Add("attN=" + a.NodeId + ">" + id(a.Partner)); continue;
                    }
                    lines.Add(k + "=" + Canon(v));
                }
                lines.Sort(StringComparer.Ordinal);
                foreach (var l in lines) sb.Append(' ').Append(l).Append('\n');
                foreach (var child in p.Node.Children()) Node(sb, child, child.Name == "MODULE" ? (child.First("name") ?? "") : child.Name, "", 1, registry, craft, rp, rr);
            }
            return sb.ToString();
        }
        private static void Node(StringBuilder sb, ConfigNode n, string module, string path, int depth, RoundtripVolatileKeys registry, ConfigNode header, Vector rootPos, Rotation rootRot)
        {
            var indent = new string(' ', depth * 2);
            sb.Append(indent).Append('[').Append(n.Name).Append(']').Append('\n');
            var lines = new List<string>();
            foreach (var kv in n.Values())
            {
                var e = registry.Find(module, path + kv.Key);
                if (e != null)
                {
                    if (e.Rule == VolatileRule.Ignore) continue;
                    if (e.Rule == VolatileRule.AbsentEqualsDefault && ValueRule.Equal(kv.Value, e.DefaultValue)) continue;
                    if (e.Rule == VolatileRule.HeaderDerived)
                    {
                        var hv = header.First(e.HeaderKey ?? "description") ?? "";
                        lines.Add(kv.Key + "=" + (Norm(kv.Value) == Norm(hv) ? "<header>" : Canon(kv.Value))); continue;
                    }
                    if (e.Rule == VolatileRule.RootRelativeVector)
                    {
                        Vector v; Rotation q;
                        if (CraftModel.TryVector(kv.Value, out v)) { var rel = CraftComparator.SafeInverse(RotationMath.Sub(v, rootPos), rootRot); lines.Add(kv.Key + "=" + Round3(rel.X) + "," + Round3(rel.Y) + "," + Round3(rel.Z)); continue; }
                        if (CraftModel.TryRotation(kv.Value, out q)) { try { lines.Add(kv.Key + "=" + CanonRotation(RotationMath.Multiply(RotationMath.Conjugate(rootRot), q))); continue; } catch (ArgumentException) { } }
                    }
                }
                lines.Add(kv.Key + "=" + Canon(kv.Value));
            }
            lines.Sort(StringComparer.Ordinal);
            foreach (var l in lines) sb.Append(indent).Append(' ').Append(l).Append('\n');
            foreach (var c in n.Children()) Node(sb, c, module, path + c.Name + "/", depth + 1, registry, header, rootPos, rootRot);
        }
        /// <summary>q and -q are the same rotation: flip the whole quaternion so w is positive (first non-zero component when w is zero).</summary>
        private static string CanonRotation(Rotation q)
        {
            double x = q.X, y = q.Y, z = q.Z, w = q.W;
            double lead = Math.Abs(w) > 1e-9 ? w : (Math.Abs(x) > 1e-9 ? x : (Math.Abs(y) > 1e-9 ? y : z));
            if (lead < 0) { x = -x; y = -y; z = -z; w = -w; }
            return Round3(x) + "," + Round3(y) + "," + Round3(z) + "," + Round3(w);
        }
        private static string Norm(string s) { return s.Replace("\r\n", "\n").Replace("\\n", "\n").Replace("\r", "\n").Replace("\u00A8", "\n"); }
        private static string Round3(double v) { var r = Math.Round(v, 3); if (r == 0) r = 0; return r.ToString("0.###", CultureInfo.InvariantCulture); }
        /// <summary>Numeric tokens are normalised to 9 significant digits; other tokens are kept.</summary>
        private static string Canon(string value)
        {
            var sb = new StringBuilder();
            foreach (var t in ValueRule.Tokens(value))
            {
                double d;
                if (ValueRule.TryNumber(t, out d)) sb.Append(d.ToString("G9", CultureInfo.InvariantCulture)); else sb.Append(t);
                sb.Append('\u0002');
            }
            return sb.ToString();
        }
    }
}
