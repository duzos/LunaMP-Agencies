using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>What an admitted editor_save_craft job writes: the base file name (".craft" implied), its facility and the replace guard.</summary>
    internal sealed class SaveRequest
    {
        public string FileName { get; set; }
        public string Facility { get; set; }
        public string ReplaceExpectedSha256 { get; set; }
    }

    internal sealed partial class OperationJob
    {
        public SaveRequest Save { get; set; }
        public string SavedPath { get; set; }
        public string SavedSha256 { get; set; }
        public string ReplacedSha256 { get; set; }
        /// <summary>recorded, write_failed or not_recorded.</summary>
        public string LedgerResult { get; set; }
        /// <summary>synced, partial, write_failed or unavailable: how the save-name fields and the unsaved marker were set after the write.</summary>
        public string BookkeepingResult { get; set; }
        public bool? CraftIdentifiersValid { get; set; }
        public Pure.CraftComparison Comparison { get; set; }
        internal Dictionary<string, FileEntry> ShipsBaseline;

        public static string OperationName(OperationKind kind)
        {
            switch (kind)
            {
                case OperationKind.Apply: return "apply_craft";
                case OperationKind.Save: return "save_craft";
                default: return "restore_snapshot";
            }
        }

        private void AddSaveEnvelope(JObject envelope)
        {
            if (Kind != OperationKind.Save) return;
            envelope["savedPath"] = SavedPath == null ? JValue.CreateNull() : (JToken)SavedPath;
            envelope["sha256"] = SavedSha256 == null ? JValue.CreateNull() : (JToken)SavedSha256;
            envelope["replacedSha256"] = ReplacedSha256 == null ? JValue.CreateNull() : (JToken)ReplacedSha256;
            envelope["craftIdentifiersValid"] = CraftIdentifiersValid.HasValue ? (JToken)CraftIdentifiersValid.Value : JValue.CreateNull();
            envelope["ledger"] = LedgerResult == null ? JValue.CreateNull() : (JToken)LedgerResult;
            envelope["saveBookkeeping"] = BookkeepingResult == null ? JValue.CreateNull() : (JToken)BookkeepingResult;
            if (Comparison == null) { envelope["comparison"] = JValue.CreateNull(); return; }
            envelope["comparison"] = new JObject
            {
                ["equal"] = Comparison.Equal, ["mapping"] = Comparison.Mapping, ["totalDifferences"] = Comparison.TotalDifferences,
                ["differences"] = new JArray(Comparison.Differences.Take(Pure.CraftComparator.MaxDifferences).Select(d => (JToken)new JObject
                    { ["partRef"] = d.PartRef, ["path"] = d.Path, ["kind"] = d.Kind, ["a"] = d.A, ["b"] = d.B })),
                ["exclusionsApplied"] = new JArray(Comparison.ExclusionsApplied.Select(x => (JToken)new JObject
                    { ["module"] = x.Module, ["keyPath"] = x.KeyPath, ["rule"] = x.Rule, ["evidenceRef"] = x.EvidenceRef, ["count"] = x.Count }))
            };
        }
    }
}
