using System;
using System.Collections.Generic;
using KspControl.Bridge;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>The load members of the fake editor: a scriptable upgrade pipeline and prefab module table.</summary>
    internal sealed partial class EditorFake
    {
        /// <summary>Edits the work node the way an upgrade script would. The reference node is a separate parse and is never passed in.</summary>
        public Action<Pure.ConfigNode> PipelineTransform;
        /// <summary>The pipeline never calls onSuccess: it opens its failure popup and waits for a click.</summary>
        public bool PipelineFails;
        public string PipelineThrows;
        public int PipelineCalls;
        public readonly List<string> PipelinePaths = new List<string>();
        /// <summary>Set after a failing run: the gate the real adapter closes, so a stale click can be simulated.</summary>
        public UpgradeGate<Pure.ConfigNode> LastGate;
        /// <summary>Prefab modules per part; a part not listed gets <see cref="DefaultModules"/> unless <see cref="KnownParts"/> excludes it.</summary>
        public readonly Dictionary<string, string[]> ModulesByPart = new Dictionary<string, string[]>(StringComparer.Ordinal);
        public string[] DefaultModules = { "ModuleProbe", "ModuleCryoTank" };
        public string[] ScriptsApplied;
        /// <summary>Source-file side effects the real pipeline must never have: set by a test to prove they are caught.</summary>
        public Action<string> DuringPipeline;

        public UpgradeOutcome RunUpgradePipeline(string craftPath)
        {
            PipelineCalls++; PipelinePaths.Add(craftPath);
            DuringPipeline?.Invoke(craftPath);
            var text = Files.Text(craftPath);
            var outcome = new UpgradeOutcome { Reference = Pure.ConfigText.Parse(text), ScriptsApplied = ScriptsApplied };
            if (PipelineThrows != null) { outcome.Error = PipelineThrows; return outcome; }
            var work = Pure.ConfigText.Parse(text);
            var gate = new UpgradeGate<Pure.ConfigNode>(); LastGate = gate;
            if (PipelineFails) { gate.OnFail(); gate.Close(); outcome.PopupDismissed = true; outcome.LockRemoved = true; return outcome; }
            PipelineTransform?.Invoke(work);
            gate.OnSuccess(work); gate.Close();
            outcome.Succeeded = gate.Succeeded; outcome.Output = gate.Output;
            return outcome;
        }

        public IReadOnlyCollection<string> PrefabModuleNames(string partName)
        {
            string[] modules;
            if (ModulesByPart.TryGetValue(partName, out modules)) return modules;
            if (KnownParts != null && !KnownParts.Contains(partName)) return null;
            return DefaultModules;
        }
    }
}
