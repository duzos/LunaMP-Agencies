using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>A failed pre-load check: the reason code and the first problems found.</summary>
    internal sealed class LoadCheck
    {
        public string Reason { get; }
        public string Detail { get; }
        public LoadCheck(string reason, string detail) { Reason = reason; Detail = detail; }
    }

    /// <summary>
    /// The checks an existing craft must pass before it is loaded (plan R1-section 10, R3-section 4): link integrity and the MODULE
    /// names the part prefabs do not carry. Both run on the craft text alone, so a broken file is refused before KSP sees it. Pure.
    /// </summary>
    internal static class LoadValidator
    {
        private const int MaxReported = 8;

        /// <summary>
        /// Every link, symmetry, stack-partner and surface-partner reference names a part in the file; every craft id is valid and
        /// unique; no part has two parents or is its own parent; and exactly one root reaches every part. Null when the structure is sound.
        /// </summary>
        public static LoadCheck Links(Pure.ConfigNode craft)
        {
            var problems = new List<string>();
            Action<string> add = text => { if (problems.Count < MaxReported) problems.Add(text); };
            var model = Pure.CraftModel.Build(craft);
            if (model.Parts.Count == 0) return new LoadCheck(LoadReasons.CraftInvalidLinks, "no_parts");
            foreach (var part in model.Parts) if (!part.CidValid) add("invalid_part_id " + part.Ref);
            foreach (var cid in model.DuplicateCids) add("duplicate_craft_id " + cid);
            if (problems.Count != 0) return Fail(problems);

            var parents = new int[model.Parts.Count];
            foreach (var part in model.Parts)
            {
                foreach (var link in part.Links)
                {
                    int target;
                    if (!Resolve(model, link, out target)) { add("dangling_link " + part.Ref + " -> " + Text(link)); continue; }
                    if (target == part.Index) { add("self_link " + part.Ref); continue; }
                    parents[target]++;
                }
                foreach (var sym in part.Sym) { int ignored; if (!Resolve(model, sym, out ignored)) add("dangling_sym " + part.Ref + " -> " + Text(sym)); }
                foreach (var node in part.AttN) { int ignored; if (node.Partner != null && !Resolve(model, node.Partner, out ignored)) add("dangling_attN " + part.Ref + " " + node.NodeId + " -> " + Text(node.Partner)); }
                { int ignored; if (part.Srf != null && !Resolve(model, part.Srf, out ignored)) add("dangling_srfN " + part.Ref + " -> " + Text(part.Srf)); }
            }
            for (var i = 0; i < parents.Length; i++) if (parents[i] > 1) add("multiple_parents " + model.Parts[i].Ref);
            if (problems.Count != 0) return Fail(problems);

            var roots = model.Parts.Where(p => p.Parent < 0).ToList();
            if (roots.Count != 1) return Fail(new List<string> { "roots=" + roots.Count.ToString(CultureInfo.InvariantCulture) });
            var seen = new HashSet<int>(); var queue = new Queue<int>(); queue.Enqueue(roots[0].Index); seen.Add(roots[0].Index);
            while (queue.Count > 0)
                foreach (var child in model.Parts[queue.Dequeue()].Children) if (seen.Add(child)) queue.Enqueue(child);
            if (seen.Count != model.Parts.Count)
                return Fail(new List<string> { "unreachable_parts=" + (model.Parts.Count - seen.Count).ToString(CultureInfo.InvariantCulture) });
            return null;
        }

        /// <summary>
        /// Every MODULE the file lists for a part is a module of that part's prefab. <paramref name="prefabModules"/> answers the module names
        /// of a part, or null when the part is not installed (craft_parts_missing). Null when everything is present.
        /// </summary>
        public static LoadCheck Modules(Pure.ConfigNode craft, Func<string, IReadOnlyCollection<string>> prefabModules)
        {
            var missingParts = new List<string>(); var missingModules = new List<string>();
            var seenParts = new HashSet<string>(StringComparer.Ordinal);
            var cache = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
            foreach (var part in Pure.CraftModel.Build(craft).Parts)
            {
                IReadOnlyCollection<string> known;
                if (!cache.TryGetValue(part.Name, out known)) { known = prefabModules(part.Name); cache[part.Name] = known; }
                if (known == null) { if (seenParts.Add(part.Name) && missingParts.Count < MaxReported) missingParts.Add(part.Name); continue; }
                foreach (var module in part.Node.Children("MODULE"))
                {
                    var name = module.First("name");
                    if (name == null || known.Contains(name)) continue;
                    var entry = part.Name + ":" + name;
                    if (!missingModules.Contains(entry) && missingModules.Count < MaxReported) missingModules.Add(entry);
                }
            }
            if (missingParts.Count != 0) return new LoadCheck(OperationReasons.CraftPartsMissing, string.Join(",", missingParts));
            if (missingModules.Count != 0) return new LoadCheck(LoadReasons.ModuleNotInstalled, string.Join(",", missingModules));
            return null;
        }

        private static bool Resolve(Pure.CraftModel model, Pure.CraftRef reference, out int index)
        {
            index = -1;
            if (reference == null || !reference.CidValid || !model.ByCid.TryGetValue(reference.CidText, out index)) return false;
            return string.Equals(model.Parts[index].Name, reference.Name, StringComparison.Ordinal);
        }

        private static string Text(Pure.CraftRef reference) { return reference == null ? "(null)" : reference.Name + "_" + reference.CidText; }

        private static LoadCheck Fail(List<string> problems) { return new LoadCheck(LoadReasons.CraftInvalidLinks, string.Join("; ", problems)); }
    }

    /// <summary>
    /// The two callbacks handed to the upgrade pipeline (plan R4-section 6.4 step 4). The pipeline is synchronous, so success arrives before
    /// Process returns; the failure callback fires only on a human click on the pipeline's own popup, which may be long after. Closing the
    /// gate when the run ends turns both into no-ops, so a stale click can never touch a later job.
    /// </summary>
    internal sealed class UpgradeGate<T> where T : class
    {
        private bool closed;
        public bool Succeeded { get; private set; }
        public T Output { get; private set; }
        public int FailCalls { get; private set; }
        /// <summary>Callbacks that arrived after <see cref="Close"/>, and were ignored.</summary>
        public int LateCalls { get; private set; }

        public void OnSuccess(T output) { if (closed) { LateCalls++; return; } Succeeded = true; Output = output; }
        public void OnFail() { if (closed) { LateCalls++; return; } FailCalls++; }
        public void Close() { closed = true; }
    }
}
