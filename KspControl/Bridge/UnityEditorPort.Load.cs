using System;
using System.Collections.Generic;
using System.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// The load members of the Unity editor port (plan R4-section 6.4): one KSP call each, no decisions. Main thread only. Not linked into the
    /// unit-test assembly; the runner is tested against a fake of the same interface.
    /// </summary>
    internal sealed partial class UnityEditorPort
    {
        private const string UpgradeFailPopup = "SaveUpgradeFail";
        private const string UpgradeFailLock = "SaveUpgradeFailDialog";
        private bool upgradeRunning;

        public UpgradeOutcome RunUpgradePipeline(string craftPath)
        {
            var outcome = new UpgradeOutcome();
            if (upgradeRunning) { outcome.Error = "reentrant_call"; return outcome; }
            upgradeRunning = true;
            var gate = new UpgradeGate<ConfigNode>();
            try
            {
                // Two independent loads of the same file: the reference is never handed to the pipeline.
                var reference = ConfigNode.Load(craftPath);
                var work = ConfigNode.Load(craftPath);
                if (reference == null || work == null) { outcome.Error = "craft_unreadable"; return outcome; }
                var budget = new int[] { MaxConfigEntries };
                outcome.Reference = Convert(reference, "", 0, budget);
                // Process, not SaveUpgradePipeline.Run: KSPCF's prefix on Process loads _modVersions into its per-call state, and skipping it would
                // diverge from a real load. The pipeline is synchronous, so onSuccess has fired (or not) before Process returns.
                KSPUpgradePipeline.Process(work, craftPath, SaveUpgradePipeline.LoadContext.Craft,
                    node => gate.OnSuccess(node),
                    (option, node) => gate.OnFail());
                if (gate.Succeeded && gate.Output != null)
                {
                    outcome.Succeeded = true;
                    outcome.Output = Convert(gate.Output, "", 0, new int[] { MaxConfigEntries });
                }
            }
            catch (Exception error) { outcome.Succeeded = false; outcome.Error = error.GetType().Name; }
            finally
            {
                // No success means the pipeline opened its modal failure popup and set its input lock, and onFail waits for a human click.
                // Close the gate first so a later click runs nothing, then clear the popup and the lock.
                gate.Close();
                upgradeRunning = false;
                if (!outcome.Succeeded) ClearUpgradeFailure(outcome);
            }
            return outcome;
        }

        private static void ClearUpgradeFailure(UpgradeOutcome outcome)
        {
            try { PopupDialog.DismissPopup(UpgradeFailPopup); outcome.PopupDismissed = true; }
            catch (Exception) { outcome.PopupDismissed = false; }
            try
            {
                var stack = InputLockManager.lockStack;
                if (stack != null && stack.ContainsKey(UpgradeFailLock)) { InputLockManager.RemoveControlLock(UpgradeFailLock); outcome.LockRemoved = true; }
            }
            catch (Exception) { outcome.LockRemoved = false; }
        }

        public IReadOnlyCollection<string> PrefabModuleNames(string partName)
        {
            try
            {
                var info = PartLoader.getPartInfoByName(partName);
                if (info == null || info.partPrefab == null) return null;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (PartModule module in info.partPrefab.Modules) if (module != null) names.Add(module.moduleName);
                return names;
            }
            catch (Exception) { return null; }
        }
    }
}
