using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    internal sealed class VerifyProblem
    {
        public string Code { get; }
        public string Detail { get; }
        public VerifyProblem(string code, string detail) { Code = code; Detail = detail; }
        public override string ToString() { return Code + ": " + Detail; }
    }

    /// <summary>
    /// The plan-versus-loaded check (plan R1-section 6.4 step 6): the live craft, saved natively and converted to the pure tree, must
    /// match the structural craft that was staged. Structure covers the part set, links, symmetry, attach partners and all staging
    /// integers; geometry covers root-relative positions and rotations and the stack-node gap. Pure.
    /// </summary>
    internal static class PlanVerifier
    {
        /// <summary>Root-relative position tolerance, metres (1 cm).</summary>
        public const double PositionTolerance = 0.01;
        /// <summary>Root-relative rotation tolerance, radians.</summary>
        public const double RotationTolerance = 1e-3;
        public const double NodeGapTolerance = 0.01;
        private const int MaxProblems = 32;
        private static readonly string[] StagingKeys = { "istg", "dstg", "sidx", "sqor", "sepI", "attm" };

        public static List<VerifyProblem> Verify(Pure.StructuralCraft plan, Pure.ConfigNode live, double? maxStackNodeGap, bool? allPartsConnected)
        {
            var problems = new List<VerifyProblem>();
            Action<string, string> add = (code, detail) => { if (problems.Count < MaxProblems) problems.Add(new VerifyProblem(code, detail)); };
            var expected = Pure.CraftModel.Build(plan.ToConfigNode());
            var actual = Pure.CraftModel.Build(live);
            if (expected.Parts.Count != actual.Parts.Count) { add(OperationReasons.StructureMismatchAfterLoad, "part_count " + actual.Parts.Count.ToString(CultureInfo.InvariantCulture) + " expected " + expected.Parts.Count.ToString(CultureInfo.InvariantCulture)); return problems; }
            if (actual.DuplicateCids.Count != 0) { add(OperationReasons.StructureMismatchAfterLoad, "duplicate_craft_ids"); return problems; }

            var map = new Dictionary<int, int>();
            foreach (var part in expected.Parts)
            {
                int index;
                if (!part.CidValid || !actual.ByCid.TryGetValue(part.CidText, out index)) { add(OperationReasons.StructureMismatchAfterLoad, "missing_part " + part.Ref); continue; }
                if (!string.Equals(actual.Parts[index].Name, part.Name, StringComparison.Ordinal)) { add(OperationReasons.StructureMismatchAfterLoad, "part_name " + part.Ref + " loaded as " + actual.Parts[index].Ref); continue; }
                map[part.Index] = index;
            }
            if (problems.Count != 0) return problems;

            foreach (var part in expected.Parts)
            {
                var other = actual.Parts[map[part.Index]];
                CompareSet(part.Links.Select(Key), other.Links.Select(Key), "link", part.Ref, add);
                CompareSet(part.Sym.Select(Key), other.Sym.Select(Key), "sym", part.Ref, add);
                if (Key(part.Srf) != Key(other.Srf)) add(OperationReasons.StructureMismatchAfterLoad, "srfN " + part.Ref);
                var wantNodes = part.AttN.Where(a => a.Partner != null).Select(a => a.NodeId + ">" + Key(a.Partner));
                var haveNodes = other.AttN.Where(a => a.Partner != null).Select(a => a.NodeId + ">" + Key(a.Partner));
                CompareSet(wantNodes, haveNodes, "attN", part.Ref, add);
                foreach (var key in StagingKeys)
                {
                    long want, have;
                    var w = part.Node.First(key); var h = other.Node.First(key);
                    if (!long.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out want) || !long.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out have) || want != have)
                        add(OperationReasons.StructureMismatchAfterLoad, key + " " + part.Ref + " " + (h ?? "absent") + " expected " + (w ?? "absent"));
                }
            }
            if (allPartsConnected == false) add(OperationReasons.StructureMismatchAfterLoad, "parts_not_connected");
            if (problems.Count != 0) return problems;

            // Geometry, in the root part's frame so the absolute spawn height never matters.
            var rootA = expected.Root0(); var rootB = actual.Root0();
            Pure.Vector rootPosA, rootPosB; Pure.Rotation rootRotA, rootRotB;
            if (!Pure.CraftModel.TryVector(expected.Parts[rootA].Node.First("pos"), out rootPosA) || !Pure.CraftModel.TryVector(actual.Parts[rootB].Node.First("pos"), out rootPosB)
                || !Pure.CraftModel.TryRotation(expected.Parts[rootA].Node.First("rot"), out rootRotA) || !Pure.CraftModel.TryRotation(actual.Parts[rootB].Node.First("rot"), out rootRotB))
            { add(OperationReasons.GeometryMismatchAfterLoad, "root_transform_unreadable"); return problems; }
            foreach (var part in expected.Parts)
            {
                var other = actual.Parts[map[part.Index]];
                Pure.Vector posA, posB; Pure.Rotation rotA, rotB;
                if (!Pure.CraftModel.TryVector(part.Node.First("pos"), out posA) || !Pure.CraftModel.TryVector(other.Node.First("pos"), out posB)
                    || !Pure.CraftModel.TryRotation(part.Node.First("rot"), out rotA) || !Pure.CraftModel.TryRotation(other.Node.First("rot"), out rotB))
                { add(OperationReasons.GeometryMismatchAfterLoad, "transform_unreadable " + part.Ref); continue; }
                var relA = Pure.CraftComparator.SafeInverse(Pure.RotationMath.Sub(posA, rootPosA), rootRotA);
                var relB = Pure.CraftComparator.SafeInverse(Pure.RotationMath.Sub(posB, rootPosB), rootRotB);
                var distance = Pure.RotationMath.Length(Pure.RotationMath.Sub(relA, relB));
                if (!(distance <= PositionTolerance)) add(OperationReasons.GeometryMismatchAfterLoad, "position " + part.Ref + " off by " + distance.ToString("0.#####", CultureInfo.InvariantCulture) + " m");
                try
                {
                    var qa = Pure.RotationMath.Multiply(Pure.RotationMath.Conjugate(rootRotA), rotA);
                    var qb = Pure.RotationMath.Multiply(Pure.RotationMath.Conjugate(rootRotB), rotB);
                    var angle = Pure.RotationMath.AngleBetween(qa, qb);
                    if (!(angle <= RotationTolerance)) add(OperationReasons.GeometryMismatchAfterLoad, "rotation " + part.Ref + " off by " + angle.ToString("0.#####", CultureInfo.InvariantCulture) + " rad");
                }
                catch (ArgumentException) { add(OperationReasons.GeometryMismatchAfterLoad, "rotation_unreadable " + part.Ref); }
            }
            if (maxStackNodeGap.HasValue && !(maxStackNodeGap.Value <= NodeGapTolerance))
                add(OperationReasons.GeometryMismatchAfterLoad, "stack_node_gap " + maxStackNodeGap.Value.ToString("0.#####", CultureInfo.InvariantCulture) + " m");
            return problems;
        }

        private static string Key(Pure.CraftRef r) { return r == null ? null : (r.CidValid ? r.Name + "_" + r.CidText : r.Name + "_" + r.CidText + "?"); }

        private static void CompareSet(IEnumerable<string> want, IEnumerable<string> have, string label, string part, Action<string, string> add)
        {
            var a = want.OrderBy(x => x, StringComparer.Ordinal).ToList(); var b = have.OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!a.SequenceEqual(b, StringComparer.Ordinal)) add(OperationReasons.StructureMismatchAfterLoad, label + " " + part);
        }
    }
}
