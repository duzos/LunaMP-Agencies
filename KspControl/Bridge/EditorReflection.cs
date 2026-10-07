using System;
using System.Reflection;

namespace KspControl.Bridge
{
    /// <summary>
    /// Guarded access to private editor members (plan R1-§6.3, R2-§6.3). Every member is resolved once by name against the
    /// supplied type, never assumed: a missing or differently typed member leaves the matching capability off and every
    /// read returns false. Contains no Unity types, so it is tested against fake editor classes on both runtimes.
    /// </summary>
    internal sealed class EditorReflection
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly FieldInfo fsm, vesselName, vesselNameSanitized, undoLevel, undoIndex;
        private readonly MethodInfo setSanitized;
        private readonly FieldInfo fsmStateName;
        private readonly MemberInfo fsmStarted;
        public EditorCapabilities Capabilities { get; }

        public EditorReflection(Type editorType)
        {
            var caps = new EditorCapabilities();
            try
            {
                if (editorType != null)
                {
                    fsm = Find(editorType, "fsm", null);
                    vesselName = Find(editorType, "vesselNameAtLastSave", typeof(string));
                    vesselNameSanitized = Find(editorType, "vesselNameAtLastSave_Sanitized", typeof(string));
                    undoLevel = Find(editorType, "undoLevel", typeof(int));
                    undoIndex = Find(editorType, "undoIndexAtLastSave", typeof(int));
                    setSanitized = FindMethod(editorType, "SetLastSanitizedSaveName");
                    if (fsm != null)
                    {
                        // The state machine member names are resolved on the declared field type, so absence is known at startup.
                        fsmStateName = fsm.FieldType.GetField("currentStateName", Instance);
                        if (fsmStateName != null && fsmStateName.FieldType != typeof(string)) fsmStateName = null;
                        fsmStarted = (MemberInfo)fsm.FieldType.GetProperty("Started", Instance) ?? fsm.FieldType.GetField("fsmStarted", Instance);
                    }
                }
            }
            catch (Exception) { /* reflection must never break the bridge: whatever resolved stays, the rest stays off */ }
            caps.Fsm = fsm != null && fsmStateName != null;
            caps.VesselNameAtLastSave = vesselName != null;
            caps.VesselNameAtLastSaveSanitized = vesselNameSanitized != null;
            caps.UndoLevel = undoLevel != null;
            caps.UndoIndexAtLastSave = undoIndex != null;
            caps.SetLastSanitizedSaveName = setSanitized != null;
            caps.UnsavedMarker = undoLevel != null && undoIndex != null;
            // The guard needs both save-name fields, both undo counters, and a way to set the sanitised name.
            caps.SaveOverwriteGuard = caps.VesselNameAtLastSave && caps.VesselNameAtLastSaveSanitized && caps.UnsavedMarker
                && (caps.SetLastSanitizedSaveName || !vesselNameSanitized.IsInitOnly);
            Capabilities = caps;
        }

        /// <summary>Walks the type and its bases: private fields are only visible on their declaring type.</summary>
        private static FieldInfo Find(Type type, string name, Type expected)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Instance | BindingFlags.DeclaredOnly);
                if (field != null) return expected == null || field.FieldType == expected ? field : null;
            }
            return null;
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var method = t.GetMethod(name, Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (method != null) return method;
            }
            return null;
        }

        /// <summary>The FSM state name, "not_started" while the machine has not started, false when unreadable.</summary>
        public bool TryReadFsmState(object editor, out string state)
        {
            state = null;
            if (!Capabilities.Fsm || editor == null) return false;
            try
            {
                var machine = fsm.GetValue(editor);
                if (machine == null) return false;
                var started = true;
                var property = fsmStarted as PropertyInfo;
                var field = fsmStarted as FieldInfo;
                if (property != null && property.PropertyType == typeof(bool)) started = (bool)property.GetValue(machine, null);
                else if (field != null && field.FieldType == typeof(bool)) started = (bool)field.GetValue(machine);
                if (!started) { state = "not_started"; return true; }
                var name = fsmStateName.GetValue(machine) as string;
                if (string.IsNullOrEmpty(name)) return false;
                state = name; return true;
            }
            catch (Exception) { return false; }
        }

        public bool TryReadUndo(object editor, out int level, out int savedIndex)
        {
            level = 0; savedIndex = 0;
            if (!Capabilities.UnsavedMarker || editor == null) return false;
            try { level = (int)undoLevel.GetValue(editor); savedIndex = (int)undoIndex.GetValue(editor); return true; }
            catch (Exception) { return false; }
        }

        public bool TryReadLastSavedName(object editor, out string name)
        {
            name = null;
            if (!Capabilities.VesselNameAtLastSave || editor == null) return false;
            try { name = (string)vesselName.GetValue(editor); return true; }
            catch (Exception) { return false; }
        }
    }
}
