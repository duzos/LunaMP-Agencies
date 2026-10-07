using System;
using System.Collections.Generic;
using System.Linq;
namespace KspControl.EditorModel
{
    public enum TopologyKind { Unsupported, T1, T2, T3 }
    /// <summary>
    /// Classifies the placed layout into the topologies the staging rules know about.
    /// T1: command, tank(s), engine in a single stack chain. T2: a stack chain with exactly one decoupler.
    /// T3: a T1 core with a radially symmetric pair surface-attached to a core tank.
    /// </summary>
    public static class TopologyClassifier
    {
        public static TopologyKind Classify(StructuralLayout layout, out string reason)
        {
            reason = null;
            if (layout == null || layout.Parts.Count == 0) { reason = "empty layout"; return TopologyKind.Unsupported; }
            var root = layout.Parts[0];
            if (root.Definition.Category != PartCategories.Command) { reason = "root is not a command part"; return TopologyKind.Unsupported; }
            var surface = layout.Parts.Where(p => p.Kind == AttachKind.Surface).ToList();
            var core = layout.Parts.Where(p => !IsInSurfaceSubtree(layout, p)).ToList();
            if (!IsChain(layout, core)) { reason = "core is not a single stack chain"; return TopologyKind.Unsupported; }
            var cats = core.Select(p => p.Definition.Category).ToList();
            if (surface.Count == 0)
            {
                int decouplers = cats.Count(c => c == PartCategories.Decoupler);
                if (decouplers == 0 && IsT1Sequence(cats)) return TopologyKind.T1;
                if (decouplers == 1 && cats[cats.Count - 1] == PartCategories.Engine) return TopologyKind.T2;
                reason = "stack chain is neither T1 nor T2"; return TopologyKind.Unsupported;
            }
            if (!IsT1Sequence(cats)) { reason = "radial parts need a T1 core"; return TopologyKind.Unsupported; }
            var roots = surface.Where(p => layout.Parts[p.ParentIndex].Kind != AttachKind.Surface && !IsInSurfaceSubtree(layout, layout.Parts[p.ParentIndex])).ToList();
            if (roots.Count == 2 && roots[0].Source == roots[1].Source && roots[0].InstanceCount == 2
                && roots.All(r => layout.Parts[r.ParentIndex].Definition.Category == PartCategories.Tank)) return TopologyKind.T3;
            reason = "radial parts are not a single symmetric pair on a core tank"; return TopologyKind.Unsupported;
        }
        private static bool IsT1Sequence(List<string> cats)
        {
            if (cats.Count < 3 || cats[0] != PartCategories.Command || cats[cats.Count - 1] != PartCategories.Engine) return false;
            for (int i = 1; i < cats.Count - 1; i++) if (cats[i] != PartCategories.Tank) return false;
            return true;
        }
        private static bool IsInSurfaceSubtree(StructuralLayout layout, LayoutPart p)
        {
            while (p != null)
            {
                if (p.Kind == AttachKind.Surface) return true;
                p = p.ParentIndex < 0 ? null : layout.Parts[p.ParentIndex];
            }
            return false;
        }
        private static bool IsChain(StructuralLayout layout, List<LayoutPart> core)
        {
            // Every core part has at most one core child, and the core is connected (parents precede children).
            var childCount = new Dictionary<int, int>();
            foreach (var p in core)
            {
                if (p.ParentIndex < 0) continue;
                int n; childCount.TryGetValue(p.ParentIndex, out n); childCount[p.ParentIndex] = n + 1;
            }
            return childCount.Values.All(c => c <= 1);
        }
    }
    /// <summary>Staging rule for one topology. Rules exist only where a hand-built twin evidences them.</summary>
    public interface IStagingRule
    {
        TopologyKind Kind { get; }
        bool TryApply(StructuralLayout layout, out List<StagingFields> fields, out string reason);
    }
    public static class StagingRules
    {
        private static readonly Dictionary<TopologyKind, IStagingRule> Rules = new Dictionary<TopologyKind, IStagingRule>
        {
            { TopologyKind.T1, new T1StagingRule() }
            // TODO(T2/T3): the coordinator supplied observed integers for stock Orbiter One and GDLV3
            // (istg/dstg/sidx/sqor/sepI per part). They fit "non-stageables inherit the decoupler group's
            // istg/sepI" qualitatively, but sidx/sqor (global ordering across stages and symmetry groups) and
            // dstg for non-decoupler parts cannot be derived unambiguously from them without a twin built from
            // the same parts. Rules are deliberately NOT guessed; add T2/T3 here once S0f-manual/S0b-manual exist.
        };
        public static IStagingRule For(TopologyKind kind)
        {
            IStagingRule rule;
            return Rules.TryGetValue(kind, out rule) ? rule : null;
        }
    }
    /// <summary>
    /// T1 twin evidence (S0a-manual): command pod and tank have istg=-1 dstg=0 sidx=-1 sqor=-1 sepI=-1 attm=0;
    /// the engine has istg=0 dstg=0 sidx=0 sqor=0 sepI=-1 attm=0.
    /// </summary>
    public sealed class T1StagingRule : IStagingRule
    {
        public TopologyKind Kind { get { return TopologyKind.T1; } }
        public bool TryApply(StructuralLayout layout, out List<StagingFields> fields, out string reason)
        {
            fields = new List<StagingFields>(); reason = null;
            foreach (var p in layout.Parts)
            {
                bool engine = p.Definition.Category == PartCategories.Engine;
                var f = engine ? new StagingFields(0, 0, 0, 0, -1, 0) : new StagingFields(-1, 0, -1, -1, -1, 0);
                if (p.Source.Stage.HasValue && p.Source.Stage.Value != f.Istg)
                { reason = "T1: stage override " + p.Source.Stage.Value + " on '" + p.Source.Id + "' differs from the twin-verified rule (" + f.Istg + ")"; fields = null; return false; }
                fields.Add(f);
            }
            return true;
        }
    }
}
