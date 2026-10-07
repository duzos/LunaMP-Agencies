using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{

    /// <summary>Everything editor_load_craft adds to a job: the request, the pipeline result and the comparison. Reported in the envelope.</summary>
    internal sealed class LoadState
    {
        public string Facility, FileName, SourcePath, ExpectedSha256;
        public bool AllowUpgrade;
        public string SourceSha256;
        /// <summary>The staged copy of the source, <c>staging/kc-&lt;requestId&gt;.craft</c>.</summary>
        public string CopyPath;
        /// <summary>The staged pipeline output, <c>staging/kc-&lt;requestId&gt;.upgraded.craft</c>, when the pipeline changed the craft and the caller allowed it.</summary>
        public string UpgradedPath;
        public bool PipelineRan, UpgradedOnLoad, PopupDismissed, LockRemoved;
        public string PipelineError;
        public IReadOnlyList<string> ScriptsApplied;
        public Pure.CraftComparison PipelineComparison;
        public string HeaderVersionReference, HeaderVersionOutput;
        /// <summary>What the editor was asked to hold: the pipeline output (equal to the source when nothing changed).</summary>
        public Pure.ConfigNode Output;
        public string ShipName;
        public Pure.CraftComparison Comparison;
        /// <summary>The source and its sidecars as hashed (or "absent") when staging began, keyed by path.</summary>
        public readonly Dictionary<string, string> SidecarBaseline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>true, false, or null before the check ran.</summary>
        public bool? SourceUnchanged;
        public readonly List<string> SidecarChanges = new List<string>();

        public JObject ToJson()
        {
            return new JObject
            {
                ["facility"] = Facility, ["fileName"] = FileName, ["sourceSha256"] = SourceSha256 == null ? JValue.CreateNull() : (JToken)SourceSha256,
                ["allowUpgrade"] = AllowUpgrade, ["pipelineRan"] = PipelineRan,
                ["pipelineError"] = PipelineError == null ? JValue.CreateNull() : (JToken)PipelineError,
                ["popupDismissed"] = PopupDismissed, ["upgradeLockRemoved"] = LockRemoved,
                ["sourceUnchanged"] = SourceUnchanged.HasValue ? (JToken)SourceUnchanged.Value : JValue.CreateNull(),
                ["sidecarChanges"] = new JArray(SidecarChanges),
                ["loadedNode"] = UpgradedOnLoad ? "pipeline_output" : "source",
                ["humanFileModified"] = SourceUnchanged.HasValue ? (JToken)(!SourceUnchanged.Value) : JValue.CreateNull()
            };
        }

        public static JObject ComparisonJson(Pure.CraftComparison comparison)
        {
            if (comparison == null) return null;
            return new JObject
            {
                ["equal"] = comparison.Equal, ["totalDifferences"] = comparison.TotalDifferences, ["mapping"] = comparison.Mapping,
                ["differences"] = new JArray(comparison.Differences.Select(d => (JToken)new JObject { ["partRef"] = d.PartRef, ["path"] = d.Path, ["kind"] = d.Kind, ["a"] = d.A, ["b"] = d.B })),
                ["exclusionsApplied"] = new JArray(comparison.ExclusionsApplied.Select(e => (JToken)new JObject { ["module"] = e.Module, ["keyPath"] = e.KeyPath, ["rule"] = e.Rule, ["evidenceRef"] = e.EvidenceRef, ["count"] = e.Count }))
            };
        }
    }

    internal sealed partial class OperationJob
    {
        /// <summary>Set for editor_load_craft jobs only.</summary>
        public LoadState Load { get; set; }

        /// <summary>The load-only fields of the envelope (plan R3-section 4): upgradedOnLoad, pipelineDifferences, scriptsApplied, comparison.</summary>
        private void AppendLoad(JObject envelope)
        {
            var load = Load;
            envelope["load"] = load.ToJson();
            envelope["upgradedOnLoad"] = load.PipelineRan ? (JToken)load.UpgradedOnLoad : JValue.CreateNull();
            var differences = new JArray();
            var total = 0;
            if (load.PipelineComparison != null)
            {
                foreach (var d in load.PipelineComparison.Differences) differences.Add(new JObject { ["partRef"] = d.PartRef, ["path"] = d.Path, ["kind"] = d.Kind, ["a"] = d.A, ["b"] = d.B });
                total = load.PipelineComparison.TotalDifferences;
            }
            if (load.HeaderVersionReference != load.HeaderVersionOutput) { differences.Add(new JObject { ["partRef"] = "HEADER", ["path"] = "version", ["kind"] = "changed", ["a"] = load.HeaderVersionReference, ["b"] = load.HeaderVersionOutput }); total++; }
            envelope["pipelineDifferences"] = differences;
            envelope["pipelineDifferencesTotal"] = total;
            envelope["scriptsApplied"] = load.ScriptsApplied == null ? (JToken)"unavailable" : new JArray(load.ScriptsApplied);
            var comparison = LoadState.ComparisonJson(load.Comparison);
            envelope["comparison"] = comparison == null ? JValue.CreateNull() : (JToken)comparison;
        }
    }
}
