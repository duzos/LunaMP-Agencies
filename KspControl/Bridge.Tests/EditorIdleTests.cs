using System.Linq;
using KspControl.Bridge;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class EditorIdleTests
    {
        private static readonly string[] None = new string[0];

        [DataTestMethod]
        [DataRow("st_idle")] [DataRow("st_podSelect")] [DataRow("st_offset_select")] [DataRow("st_rotate_select")] [DataRow("st_root_unselected")]
        public void EveryIdleStateIsIdleWithNoPartAndNoLocks(string state)
        {
            var report = EditorIdle.Evaluate(state, false, None);
            Assert.IsTrue(report.Idle); Assert.AreEqual(0, report.Busy.Count); Assert.AreEqual(state, report.FsmState);
        }

        [TestMethod] public void EmptyEditorInPodSelectIsIdle()
        { Assert.IsTrue(EditorIdle.Evaluate("st_podSelect", false, None).Idle); }

        [DataTestMethod]
        [DataRow("st_place")] [DataRow("st_drag")] [DataRow("not_started")] [DataRow("st_something_new")]
        public void OtherFsmStatesAreBusy(string state)
        {
            var report = EditorIdle.Evaluate(state, false, None);
            Assert.IsFalse(report.Idle); CollectionAssert.AreEqual(new[] { "fsm_not_idle" }, report.Busy.ToArray());
        }

        [TestMethod] public void UnreadableFsmWithTheCapabilityPresentIsBusyNotIdle()
        {
            var report = EditorIdle.Evaluate(EditorIdle.FsmUnreadable, false, None);
            Assert.IsFalse(report.Idle); CollectionAssert.AreEqual(new[] { "fsm_unreadable" }, report.Busy.ToArray());
        }

        [TestMethod] public void SelectedPartIsBusyEvenInAnIdleState()
        {
            var report = EditorIdle.Evaluate("st_idle", true, None);
            Assert.IsFalse(report.Idle); CollectionAssert.AreEqual(new[] { "part_held" }, report.Busy.ToArray());
        }

        [DataTestMethod]
        [DataRow("EditorLogic_loadDialog")] [DataRow("LoadConfirmationDialog")] [DataRow("SaveConfirmationDialog")] [DataRow("Saving")]
        [DataRow("EditorLogic_dialog_softLock")] [DataRow("CreatorCraftName")] [DataRow("SaveCraftOverwrite")] [DataRow("NewCraft")] [DataRow("LoadCraft")]
        [DataRow("SaveUpgradeFailDialog")]
        public void EveryDenylistedModalLockIsBusy(string id)
        {
            var report = EditorIdle.Evaluate("st_idle", false, new[] { id });
            Assert.IsFalse(report.Idle); CollectionAssert.AreEqual(new[] { "modal_lock:" + id }, report.Busy.ToArray());
        }

        [TestMethod] public void ForeignHoverAndUnknownLocksAreIgnored()
        {
            var report = EditorIdle.Evaluate("st_idle", false, new[] { "ClickThroughBlocker_Hover_7", "EditorLogic_hover", "_mouseOverGui", "SomeMod_Lock" });
            Assert.IsTrue(report.Idle);
        }

        [TestMethod] public void OperationAndLaunchLocksHaveTheirOwnReasons()
        {
            var report = EditorIdle.Evaluate("st_idle", false, new[] { EditorIdle.OperationLockId, EditorIdle.LaunchLockId });
            CollectionAssert.AreEqual(new[] { "operation_running", "launch_pending" }, report.Busy.ToArray());
        }

        [TestMethod] public void LaunchLockIsLunaMpToolingLaunch()
        { Assert.AreEqual("LMP_ToolingLaunch", EditorIdle.LaunchLockId); Assert.AreEqual("KspControl.Op", EditorIdle.OperationLockId); }

        [TestMethod] public void ReasonsComeInAStableOrderWithModalLocksSorted()
        {
            var report = EditorIdle.Evaluate("st_place", true, new[] { "Saving", EditorIdle.LaunchLockId, "EditorLogic_loadDialog", EditorIdle.OperationLockId });
            CollectionAssert.AreEqual(new[] { "part_held", "fsm_not_idle", "modal_lock:EditorLogic_loadDialog", "modal_lock:Saving", "operation_running", "launch_pending" }, report.Busy.ToArray());
        }

        [TestMethod] public void UnavailableFsmFallsBackToSelectedPartAndLocks()
        {
            var idle = EditorIdle.Evaluate(null, false, None);
            Assert.IsTrue(idle.Idle); Assert.IsNull(idle.FsmState);
            Assert.IsFalse(EditorIdle.Evaluate(null, true, None).Idle);
            Assert.IsFalse(EditorIdle.Evaluate(null, false, new[] { "Saving" }).Idle);
        }

        [TestMethod] public void NullLockCollectionIsTreatedAsEmpty()
        { Assert.IsTrue(EditorIdle.Evaluate("st_idle", false, null).Idle); }

        [DataTestMethod]
        [DataRow(3, 3, 5, false)] [DataRow(4, 3, 5, true)] [DataRow(0, -1, 5, true)] [DataRow(7, 7, 0, false)] [DataRow(9, 1, 0, false)]
        public void UnsavedFollowsKspsOwnTest(int level, int saved, int parts, bool expected)
        { Assert.AreEqual(expected, EditorUnsaved.Evaluate(level, saved, parts)); }

        [TestMethod] public void UnsavedIsUnknownWhenTheCountersCannotBeRead()
        {
            Assert.IsNull(EditorUnsaved.Evaluate(null, null, 3)); Assert.IsNull(EditorUnsaved.Evaluate(4, null, 3)); Assert.IsNull(EditorUnsaved.Evaluate(null, 4, 3));
            Assert.AreEqual(false, EditorUnsaved.Evaluate(null, null, 0), "an empty editor has nothing unsaved");
        }
    }
}
