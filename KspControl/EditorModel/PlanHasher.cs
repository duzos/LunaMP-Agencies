using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace KspControl.EditorModel
{
    /// <summary>
    /// Stable identity of a plan. The hash covers what the plan decides (catalog content, topology, every placed instance with its
    /// transforms and staging integers, symmetry groups) and nothing the apply step injects: no persistentIds, no _modVersions,
    /// no description or mission flag. Numbers use the same float-rounded invariant text as the craft writer, so the value is
    /// identical on .NET Framework, Mono and modern .NET (placement values are first snapped to 1e-5).
    /// </summary>
    public static class PlanHasher
    {
        public const string Version = "plan1";

        // Values are snapped to a 1e-5 grid before formatting: trig and normalisation noise (about 1e-16, which differs between
        // .NET Framework and modern .NET) must not reach the hash, and 10 micrometres is far below any meaningful placement change.
        private static string N(double v)
        {
            var r = Math.Round(v, 5, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return RotationMath.Number(r);
        }
        private static string V(Vector v) { return N(v.X) + "," + N(v.Y) + "," + N(v.Z); }
        private static string Q(Rotation q) { return N(q.X) + "," + N(q.Y) + "," + N(q.Z) + "," + N(q.W); }

        /// <summary>Canonical text that is hashed; exposed for diagnostics and tests. Null when the plan has issues or no craft.</summary>
        public static string Canonical(GraphDto graph, PlanResult plan, string catalogHash)
        {
            if (graph == null || plan == null || catalogHash == null || !plan.Ok || plan.Layout == null) return null;
            var sb = new StringBuilder();
            sb.Append(Version).Append('\n');
            sb.Append("catalog:").Append(catalogHash).Append('\n');
            sb.Append("name:").Append(graph.Name).Append('\n');
            sb.Append("topology:").Append(plan.Topology).Append('\n');
            foreach (var p in plan.Layout.Parts)
            {
                var s = plan.Craft.Parts[p.Index].Staging;
                sb.Append(p.Index.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(p.Source.Id).Append('|').Append(p.Source.Part).Append('|').Append(p.Kind).Append('|')
                  .Append(p.ParentIndex.ToString(CultureInfo.InvariantCulture)).Append('|').Append(p.ParentNodeId ?? "").Append('|').Append(p.NodeId ?? "").Append('|')
                  .Append(p.InstanceIndex.ToString(CultureInfo.InvariantCulture)).Append('/').Append(p.InstanceCount.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(V(p.Position)).Append('|').Append(Q(p.Rotation)).Append('|')
                  .Append(V(p.AttPos0)).Append('|').Append(Q(p.AttRot0)).Append('|')
                  .Append(p.Cid.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(s.Istg.ToString(CultureInfo.InvariantCulture)).Append(',').Append(s.Dstg.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.Sidx.ToString(CultureInfo.InvariantCulture)).Append(',').Append(s.Sqor.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.SepI.ToString(CultureInfo.InvariantCulture)).Append(',').Append(s.Attm.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            foreach (var group in plan.Layout.SymmetryGroups)
                sb.Append("sym:").Append(string.Join(",", group.ConvertAll(i => i.ToString(CultureInfo.InvariantCulture)).ToArray())).Append('\n');
            return sb.ToString();
        }

        /// <summary>Lowercase hex SHA-256 of <see cref="Canonical"/>, or null when there is nothing valid to hash.</summary>
        public static string Hash(GraphDto graph, PlanResult plan, string catalogHash)
        {
            var text = Canonical(graph, plan, catalogHash);
            if (text == null) return null;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var hex = new StringBuilder();
                foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }
    }
}
