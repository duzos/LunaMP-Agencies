using System;
using System.Collections.Generic;
using System.Linq;

namespace KspControl.Bridge
{
    internal sealed class IdleReport
    {
        public bool Idle { get; }
        /// <summary>Busy reasons: part_held, fsm_not_idle, fsm_unreadable, modal_lock:id, operation_running, launch_pending.</summary>
        public IReadOnlyList<string> Busy { get; }
        /// <summary>The FSM state name, or null when the state machine cannot be read.</summary>
        public string FsmState { get; }
        public IdleReport(bool idle, IReadOnlyList<string> busy, string fsmState) { Idle = idle; Busy = busy; FsmState = fsmState; }
    }

    /// <summary>
    /// The busy/idle decision (plan R1-§6.3, R4-§6.4). Idle needs an FSM state in the idle set, no selected part and no
    /// denylisted lock. Only known modal ids count: other input locks such as hover locks are ignored on purpose.
    /// </summary>
    internal static class EditorIdle
    {
        internal const string OperationLockId = "KspControl.Op";
        internal const string LaunchLockId = "LMP_ToolingLaunch";
        internal const string FsmUnreadable = "unreadable";
        /// <summary>The editor is stable in each of these: an empty editor sits in st_podSelect, and offset/rotate/root-unselected are edit sub-modes with no part in hand.</summary>
        internal static readonly string[] IdleStates = { "st_idle", "st_podSelect", "st_offset_select", "st_rotate_select", "st_root_unselected" };
        /// <summary>
        /// Modal lock ids known from the stock editor's IL, plus SaveUpgradeFailDialog (R4). The list is extended only with live evidence.
        /// </summary>
        internal static readonly string[] ModalLocks =
        {
            "EditorLogic_loadDialog", "LoadConfirmationDialog", "SaveConfirmationDialog", "Saving", "EditorLogic_dialog_softLock",
            "CreatorCraftName", "SaveCraftOverwrite", "NewCraft", "LoadCraft", "SaveUpgradeFailDialog"
        };

        public static IdleReport Evaluate(string fsmState, bool selectedPart, IEnumerable<string> lockIds)
        {
            var busy = new List<string>();
            if (selectedPart) busy.Add("part_held");
            // A null state means the reflection is unavailable: fall back to the selected-part and lock checks only.
            // A transient read failure with the capability present is busy, never idle by fallback.
            if (fsmState == FsmUnreadable) busy.Add("fsm_unreadable");
            else if (fsmState != null && Array.IndexOf(IdleStates, fsmState) < 0) busy.Add("fsm_not_idle");
            var locks = new HashSet<string>(lockIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (var id in ModalLocks.Where(locks.Contains).OrderBy(i => i, StringComparer.Ordinal)) busy.Add("modal_lock:" + id);
            if (locks.Contains(OperationLockId)) busy.Add("operation_running");
            if (locks.Contains(LaunchLockId)) busy.Add("launch_pending");
            return new IdleReport(busy.Count == 0, busy, fsmState);
        }
    }

    internal static class EditorUnsaved
    {
        /// <summary>
        /// KSP's own test (IL of loadShip and NewShip): undoIndexAtLastSave != undoLevel and the craft has parts.
        /// Null when either counter could not be read.
        /// </summary>
        public static bool? Evaluate(int? undoLevel, int? undoIndexAtLastSave, int partCount)
        {
            if (partCount <= 0) return false;
            if (!undoLevel.HasValue || !undoIndexAtLastSave.HasValue) return null;
            return undoLevel.Value != undoIndexAtLastSave.Value;
        }
    }
}
