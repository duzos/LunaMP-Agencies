using System;
using System.Collections.Generic;
using System.Linq;
namespace KspControl.EditorModel
{
    public enum TopologyKind { Unsupported, T1, T2, T3 }
    /// <summary>
    /// Classifies the placed layout into the topologies the staging rules cover.
    /// T1: command, tank(s), engine in one stack chain. T2: a stack chain whose sections are separated by stack decouplers.
    /// T3: a T1 core with a radially symmetric group (pair, quad, ...) of radial decoupler -> booster (-> optional nose cone) on a core tank.
    /// Each shape may also carry one parachute (Mk16 parachuteSingle) on the command pod's top node; it is the last stage group (number 0).
    /// Everything else (other parachute placements, fins, fairings, struts, other staged parts, radial parts on a T2 core) is Unsupported.
    /// </summary>
    public static class TopologyClassifier
    {
        public static TopologyKind Classify(StructuralLayout layout, out string reason)
        {
            reason = null;
            if (layout == null || layout.Parts.Count == 0) { reason = "empty layout"; return TopologyKind.Unsupported; }
            if (layout.Parts[0].Definition.Category != PartCategories.Command) { reason = "root is not a command part"; return TopologyKind.Unsupported; }
            var chute = StagingMath.Chute(layout);
            if (layout.Parts.Count(p => p.Definition.Category == PartCategories.Parachute) > 1) { reason = "more than one parachute"; return TopologyKind.Unsupported; }
            if (chute != null && !ChuteOnTopNode(layout, chute)) { reason = "a parachute is supported only on the command pod's top node"; return TopologyKind.Unsupported; }
            var core = layout.Parts.Where(p => p != chute && !InSurfaceSubtree(layout, p)).ToList();
            var surface = layout.Parts.Where(p => InSurfaceSubtree(layout, p)).ToList();
            if (!IsChain(core)) { reason = "core is not a single stack chain"; return TopologyKind.Unsupported; }
            var cats = core.Select(p => p.Definition.Category).ToList();
            if (cats.Any(c => c != PartCategories.Command && c != PartCategories.Tank && c != PartCategories.Engine && c != PartCategories.Decoupler))
            { reason = "core has parts outside command/tank/engine/decoupler"; return TopologyKind.Unsupported; }
            int sections;
            if (!ParseSections(cats, out sections)) { reason = "core stack chain does not match the command/tank/engine/decoupler section pattern"; return TopologyKind.Unsupported; }
            if (surface.Count == 0) return sections == 0 ? TopologyKind.T1 : TopologyKind.T2;
            if (sections != 0) { reason = "radial parts are supported on a T1 core only"; return TopologyKind.Unsupported; }
            if (!RadialGroup(layout, surface, out reason)) return TopologyKind.Unsupported;
            return TopologyKind.T3;
        }
        /// <summary>The parachute is a stack child of the root command part, on a node that points up, and has nothing attached.</summary>
        private static bool ChuteOnTopNode(StructuralLayout layout, LayoutPart chute)
        {
            if (chute.Kind != AttachKind.Stack || chute.ParentIndex != 0 || Children(layout, chute.Index).Count != 0) return false;
            var node = layout.Parts[0].Definition.FindNode(chute.ParentNodeId);
            return node != null && node.Orientation.Y > 0.5;
        }

        /// <summary>
        /// Parses the core category sequence. Returns the number of decouplers (0 for a T1 chain: command, tank+, engine).
        /// With decouplers: the command section is tank* then an optional engine, and every later section is tank* then exactly one engine.
        /// </summary>
        public static bool ParseSections(IList<string> cats, out int decouplers)
        {
            decouplers = 0;
            if (cats.Count == 0 || cats[0] != PartCategories.Command) return false;
            var chunks = new List<List<string>> { new List<string>() };
            for (int i = 1; i < cats.Count; i++)
            {
                if (cats[i] == PartCategories.Decoupler) { decouplers++; chunks.Add(new List<string>()); }
                else if (cats[i] == PartCategories.Command) return false;
                else chunks[chunks.Count - 1].Add(cats[i]);
            }
            for (int c = 0; c < chunks.Count; c++)
            {
                var ch = chunks[c];
                int engines = ch.Count(x => x == PartCategories.Engine);
                if (engines > 1 || (engines == 1 && ch[ch.Count - 1] != PartCategories.Engine)) return false;
                if (c > 0 && engines != 1) return false;
            }
            if (decouplers == 0) { var ch = chunks[0]; return ch.Count >= 2 && ch[ch.Count - 1] == PartCategories.Engine; }
            return true;
        }
        private static bool RadialGroup(StructuralLayout layout, List<LayoutPart> surface, out string reason)
        {
            reason = "radial parts are not a symmetric radial decoupler -> booster group on a core tank";
            var roots = surface.Where(p => !InSurfaceSubtree(layout, layout.Parts[p.ParentIndex])).ToList();
            int n = roots.Count;
            if (n < 2 || n > GraphDtoLimits.MaxSymmetry || roots.Any(r => r.Source != roots[0].Source || r.InstanceCount != n)) return false;
            int expected = 0;
            foreach (var r in roots)
            {
                if (r.Kind != AttachKind.Surface || r.Definition.Category != PartCategories.Decoupler) return false;
                if (layout.Parts[r.ParentIndex].Definition.Category != PartCategories.Tank) return false;
                var kids = Children(layout, r.Index);
                if (kids.Count != 1 || kids[0].Kind != AttachKind.Surface || kids[0].Definition.Category != PartCategories.Engine) return false;
                expected += 2;
                // Optional single non-stageable cap (nose cone) stack-attached to the booster.
                var tail = Children(layout, kids[0].Index);
                if (tail.Count > 1) return false;
                if (tail.Count == 1)
                {
                    if (tail[0].Kind != AttachKind.Stack || tail[0].Definition.Category != PartCategories.Other || Children(layout, tail[0].Index).Count != 0) return false;
                    expected++;
                }
            }
            if (expected != surface.Count) return false;
            reason = null; return true;
        }
        internal static List<LayoutPart> Children(StructuralLayout layout, int index)
        {
            return layout.Parts.Where(p => p.ParentIndex == index).ToList();
        }
        private static bool InSurfaceSubtree(StructuralLayout layout, LayoutPart p)
        {
            while (p != null)
            {
                if (p.Kind == AttachKind.Surface) return true;
                p = p.ParentIndex < 0 ? null : layout.Parts[p.ParentIndex];
            }
            return false;
        }
        private static bool IsChain(List<LayoutPart> core)
        {
            var childCount = new Dictionary<int, int>();
            foreach (var p in core)
            {
                if (p.ParentIndex < 0) continue;
                int n; childCount.TryGetValue(p.ParentIndex, out n); childCount[p.ParentIndex] = n + 1;
            }
            return childCount.Values.All(c => c <= 1);
        }
    }
    /// <summary>Staging rule for one topology. Rules exist only where stock KSP-editor-staged craft evidence them.</summary>
    public interface IStagingRule
    {
        TopologyKind Kind { get; }
        bool TryApply(StructuralLayout layout, out List<StagingFields> fields, out string reason);
    }
    public static class StagingRules
    {
        private static readonly Dictionary<TopologyKind, IStagingRule> Rules = new Dictionary<TopologyKind, IStagingRule>
        {
            { TopologyKind.T1, new StackStagingRule(TopologyKind.T1) },
            { TopologyKind.T2, new StackStagingRule(TopologyKind.T2) },
            { TopologyKind.T3, new RadialStagingRule() },
        };
        public static IStagingRule For(TopologyKind kind)
        {
            IStagingRule rule;
            return Rules.TryGetValue(kind, out rule) ? rule : null;
        }
    }
    /// <summary>
    /// Shared field derivation. Evidence: T1 hand twin S0a-manual, stock Orbiter One (stack decouplers) and GDLV3 (4 radial decoupler+booster),
    /// both staged in the KSP editor. Rules:
    ///  * Stage groups are numbered so the last group to fire is 0 and the first is the highest. A group's number is its istg.
    ///  * istg: a stageable part (engine, decoupler, parachute) takes its group number; any other part takes its parent's istg (the root is -1).
    ///  * dstg = parent.dstg + (child is decoupler ? 1 : 0) + (parent is decoupler ? 1 : 0), root 0.
    ///  * sepI: a decoupler's sepI is its own istg; every other part takes its parent's sepI (-1 above any decoupler).
    ///  * sidx: index of a stageable part inside its group (counterparts of a symmetric part share one index); -1 otherwise.
    ///  * sqor: the group's slot number. Each supported group occupies one slot, so sqor = istg (GDLV3 shows an extra slot only
    ///    for the fairing, which is out of scope). Non-stageable parts have -1.
    ///  * attm: 1 for surface-attached parts, else 0.
    /// </summary>
    internal static class StagingMath
    {
        public sealed class Group { public List<List<LayoutPart>> Slots = new List<List<LayoutPart>>(); }
        public static bool IsStageable(LayoutPart p) { var c = p.Definition.Category; return c == PartCategories.Engine || c == PartCategories.Decoupler || c == PartCategories.Parachute; }
        /// <summary>The parachute part, or null. The classifier guarantees at most one, on the command pod's top node.</summary>
        public static LayoutPart Chute(StructuralLayout layout) { return layout.Parts.FirstOrDefault(p => p.Definition.Category == PartCategories.Parachute); }
        /// <summary>
        /// Adds the parachute as the last group to fire. Evidence: stock Orbiter One, staged in the KSP editor: parachuteSingle istg=0 dstg=0 sidx=0
        /// sqor=0 sepI=-1 attm=0, every other group numbered one higher than it would be without the parachute.
        /// </summary>
        public static void AppendChute(StructuralLayout layout, List<List<List<LayoutPart>>> groups)
        {
            var chute = Chute(layout);
            if (chute != null) groups.Add(new List<List<LayoutPart>> { new List<LayoutPart> { chute } });
        }
        public static bool IsDecoupler(LayoutPart p) { return p.Definition.Category == PartCategories.Decoupler; }
        /// <param name="groups">Firing order, first to fire first; within a group, parts in sidx order (a list per sidx).</param>
        public static bool Build(StructuralLayout layout, List<List<List<LayoutPart>>> groups, out List<StagingFields> fields, out string reason)
        {
            fields = null; reason = null;
            var istg = new int[layout.Parts.Count]; var sidx = new int[layout.Parts.Count]; var sqor = new int[layout.Parts.Count];
            for (int i = 0; i < istg.Length; i++) { istg[i] = int.MinValue; sidx[i] = -1; sqor[i] = -1; }
            for (int g = 0; g < groups.Count; g++)
            {
                int number = groups.Count - 1 - g;
                for (int s = 0; s < groups[g].Count; s++)
                    foreach (var p in groups[g][s]) { istg[p.Index] = number; sidx[p.Index] = s; sqor[p.Index] = number; }
            }
            var result = new StagingFields[layout.Parts.Count]; var dstg = new int[layout.Parts.Count]; var sepI = new int[layout.Parts.Count];
            foreach (var p in layout.Parts)
            {
                bool root = p.ParentIndex < 0;
                var parent = root ? null : layout.Parts[p.ParentIndex];
                if (!IsStageable(p)) istg[p.Index] = root ? -1 : istg[parent.Index];
                else if (istg[p.Index] == int.MinValue) { reason = "stageable part '" + p.Source.Id + "' is not in any stage group"; return false; }
                dstg[p.Index] = root ? 0 : dstg[parent.Index] + (IsDecoupler(p) ? 1 : 0) + (IsDecoupler(parent) ? 1 : 0);
                sepI[p.Index] = IsDecoupler(p) ? istg[p.Index] : (root ? -1 : sepI[parent.Index]);
                if (p.Source.Stage.HasValue && p.Source.Stage.Value != istg[p.Index])
                { reason = "stage override " + p.Source.Stage.Value + " on '" + p.Source.Id + "' differs from the derived stage " + istg[p.Index]; return false; }
                result[p.Index] = new StagingFields(istg[p.Index], dstg[p.Index], sidx[p.Index], sqor[p.Index], sepI[p.Index], p.Kind == AttachKind.Surface ? 1 : 0);
            }
            fields = result.ToList();
            return true;
        }
    }
    /// <summary>T1 and T2: stack chain. Firing order is, bottom section first: its engine, then the decoupler above it; the command section's engine (if any) last.</summary>
    public sealed class StackStagingRule : IStagingRule
    {
        private readonly TopologyKind kind;
        public StackStagingRule(TopologyKind kind) { this.kind = kind; }
        public TopologyKind Kind { get { return kind; } }
        public bool TryApply(StructuralLayout layout, out List<StagingFields> fields, out string reason)
        {
            fields = null; reason = null;
            var chain = layout.Parts;
            // Sections: index 0 is the command section; each decoupler starts the next one.
            var engines = new List<LayoutPart>(); var decouplers = new List<LayoutPart>(); var sectionEngine = new List<LayoutPart> { null };
            foreach (var p in chain)
            {
                if (StagingMath.IsDecoupler(p)) { decouplers.Add(p); sectionEngine.Add(null); }
                else if (p.Definition.Category == PartCategories.Engine) sectionEngine[sectionEngine.Count - 1] = p;
            }
            var groups = new List<List<List<LayoutPart>>>();
            for (int s = sectionEngine.Count - 1; s >= 0; s--)
            {
                if (sectionEngine[s] != null) groups.Add(new List<List<LayoutPart>> { new List<LayoutPart> { sectionEngine[s] } });
                if (s >= 1) groups.Add(new List<List<LayoutPart>> { new List<LayoutPart> { decouplers[s - 1] } });
            }
            StagingMath.AppendChute(layout, groups);
            return StagingMath.Build(layout, groups, out fields, out reason);
        }
    }
    /// <summary>T3: T1 core plus a symmetric group of radial decoupler -> booster. Ignition group (boosters sidx 0, core engine sidx 1) fires first, then the radial decouplers.</summary>
    public sealed class RadialStagingRule : IStagingRule
    {
        public TopologyKind Kind { get { return TopologyKind.T3; } }
        public bool TryApply(StructuralLayout layout, out List<StagingFields> fields, out string reason)
        {
            fields = null; reason = null;
            var engine = layout.Parts.First(p => p.Kind != AttachKind.Surface && p.Definition.Category == PartCategories.Engine && p.ParentIndex >= 0 && !HasSurfaceAncestor(layout, p));
            var radials = layout.Parts.Where(p => p.Kind == AttachKind.Surface && StagingMath.IsDecoupler(p)).ToList();
            var boosters = layout.Parts.Where(p => p.Kind == AttachKind.Surface && p.Definition.Category == PartCategories.Engine).ToList();
            var groups = new List<List<List<LayoutPart>>>
            {
                new List<List<LayoutPart>> { boosters, new List<LayoutPart> { engine } },
                new List<List<LayoutPart>> { radials },
            };
            StagingMath.AppendChute(layout, groups);
            return StagingMath.Build(layout, groups, out fields, out reason);
        }
        private static bool HasSurfaceAncestor(StructuralLayout layout, LayoutPart p)
        {
            while (p != null) { if (p.Kind == AttachKind.Surface) return true; p = p.ParentIndex < 0 ? null : layout.Parts[p.ParentIndex]; }
            return false;
        }
    }
}
