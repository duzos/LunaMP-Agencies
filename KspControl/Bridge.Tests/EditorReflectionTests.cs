using System;
using KspControl.Bridge;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#pragma warning disable CS0414, CS0169, CS0649
namespace KspControl.BridgeTests
{
    // Fakes shaped like the stock editor classes: private members on the base and derived types, the state machine behind a private field.
    public sealed class FakeFsm
    {
        public string currentStateName = "st_idle";
        public bool Started { get; set; } = true;
    }

    public class FakeEditorBase
    {
        private FakeFsm fsm = new FakeFsm();
        public void SetState(string name, bool started = true) { fsm.currentStateName = name; fsm.Started = started; }
        public void NullMachine() { fsm = null; }
    }

    public class FakeEditor : FakeEditorBase
    {
        private string vesselNameAtLastSave = "Probe";
        private string vesselNameAtLastSave_Sanitized = "Probe";
        private int undoLevel = 4;
        private int undoIndexAtLastSave = 4;
        public int SetterCalls;
        private void SetLastSanitizedSaveName() { SetterCalls++; }
        public void Edit() { undoLevel++; }
    }

    public class BareEditor { }

    public class WrongTypesEditor
    {
        private float undoLevel;
        private int undoIndexAtLastSave;
        private int vesselNameAtLastSave;
        private string vesselNameAtLastSave_Sanitized = "";
        private object fsm = new object();
    }

    public class NoSetterReadonlySanitized
    {
        private string vesselNameAtLastSave = "";
        private readonly string vesselNameAtLastSave_Sanitized = "";
        private int undoLevel, undoIndexAtLastSave;
    }

    public class FieldSetterOnly
    {
        private string vesselNameAtLastSave = "";
        private string vesselNameAtLastSave_Sanitized = "";
        private int undoLevel, undoIndexAtLastSave;
    }

    [TestClass]
    public class EditorReflectionTests
    {
        [TestMethod] public void ResolvesPrivateMembersOnTheTypeAndItsBaseAndSetsEveryCapability()
        {
            var reflection = new EditorReflection(typeof(FakeEditor)); var caps = reflection.Capabilities;
            Assert.IsTrue(caps.Fsm); Assert.IsTrue(caps.UnsavedMarker); Assert.IsTrue(caps.SaveOverwriteGuard);
            Assert.IsTrue(caps.VesselNameAtLastSave); Assert.IsTrue(caps.VesselNameAtLastSaveSanitized);
            Assert.IsTrue(caps.UndoLevel); Assert.IsTrue(caps.UndoIndexAtLastSave); Assert.IsTrue(caps.SetLastSanitizedSaveName);
        }

        [TestMethod] public void ReadsFsmStateUndoCountersAndLastSavedName()
        {
            var editor = new FakeEditor(); var reflection = new EditorReflection(typeof(FakeEditor));
            string state, name; int level, saved;
            Assert.IsTrue(reflection.TryReadFsmState(editor, out state)); Assert.AreEqual("st_idle", state);
            Assert.IsTrue(reflection.TryReadUndo(editor, out level, out saved)); Assert.AreEqual(4, level); Assert.AreEqual(4, saved);
            editor.Edit(); Assert.IsTrue(reflection.TryReadUndo(editor, out level, out saved)); Assert.AreEqual(5, level);
            Assert.IsTrue(reflection.TryReadLastSavedName(editor, out name)); Assert.AreEqual("Probe", name);
            editor.SetState("st_place"); Assert.IsTrue(reflection.TryReadFsmState(editor, out state)); Assert.AreEqual("st_place", state);
        }

        [TestMethod] public void MachineThatHasNotStartedReportsNotStarted()
        {
            var editor = new FakeEditor(); editor.SetState("st_idle", false);
            string state; Assert.IsTrue(new EditorReflection(typeof(FakeEditor)).TryReadFsmState(editor, out state)); Assert.AreEqual("not_started", state);
        }

        [TestMethod] public void MissingMembersMakeEveryCapabilityUnavailableAndReadsFail()
        {
            var reflection = new EditorReflection(typeof(BareEditor)); var caps = reflection.Capabilities;
            Assert.IsFalse(caps.Fsm); Assert.IsFalse(caps.UnsavedMarker); Assert.IsFalse(caps.SaveOverwriteGuard);
            Assert.IsFalse(caps.VesselNameAtLastSave); Assert.IsFalse(caps.VesselNameAtLastSaveSanitized); Assert.IsFalse(caps.UndoLevel); Assert.IsFalse(caps.UndoIndexAtLastSave); Assert.IsFalse(caps.SetLastSanitizedSaveName);
            string state, name; int level, saved; var editor = new BareEditor();
            Assert.IsFalse(reflection.TryReadFsmState(editor, out state)); Assert.IsNull(state);
            Assert.IsFalse(reflection.TryReadUndo(editor, out level, out saved));
            Assert.IsFalse(reflection.TryReadLastSavedName(editor, out name)); Assert.IsNull(name);
        }

        [TestMethod] public void NullTypeAndNullInstanceNeverThrow()
        {
            var none = new EditorReflection(null); string state; int a, b;
            Assert.IsFalse(none.Capabilities.Fsm); Assert.IsFalse(none.TryReadFsmState(null, out state)); Assert.IsFalse(none.TryReadUndo(null, out a, out b));
            var real = new EditorReflection(typeof(FakeEditor));
            Assert.IsFalse(real.TryReadFsmState(null, out state)); Assert.IsFalse(real.TryReadUndo(null, out a, out b));
        }

        [TestMethod] public void MembersOfTheWrongTypeAreRejectedRatherThanGuessed()
        {
            var caps = new EditorReflection(typeof(WrongTypesEditor)).Capabilities;
            Assert.IsFalse(caps.UndoLevel, "undoLevel is a float here");
            Assert.IsFalse(caps.VesselNameAtLastSave, "vesselNameAtLastSave is an int here");
            Assert.IsFalse(caps.UnsavedMarker); Assert.IsFalse(caps.SaveOverwriteGuard); Assert.IsFalse(caps.Fsm, "the state machine type has no currentStateName");
        }

        [TestMethod] public void FsmMachineThatIsNullAtRuntimeIsUnreadableNotAnError()
        {
            var editor = new FakeEditor(); editor.NullMachine(); string state;
            Assert.IsFalse(new EditorReflection(typeof(FakeEditor)).TryReadFsmState(editor, out state));
        }

        [TestMethod] public void GuardNeedsAWayToSetTheSanitisedName()
        {
            Assert.IsFalse(new EditorReflection(typeof(NoSetterReadonlySanitized)).Capabilities.SaveOverwriteGuard, "readonly field and no setter method");
            var direct = new EditorReflection(typeof(FieldSetterOnly)).Capabilities;
            Assert.IsTrue(direct.SaveOverwriteGuard, "a writable field is an acceptable way to set it");
            Assert.IsFalse(direct.SetLastSanitizedSaveName);
        }

        [TestMethod] public void ProbeNeverInvokesTheSetterMethod()
        {
            var editor = new FakeEditor(); new EditorReflection(typeof(FakeEditor)); Assert.AreEqual(0, editor.SetterCalls);
        }
    }
}
