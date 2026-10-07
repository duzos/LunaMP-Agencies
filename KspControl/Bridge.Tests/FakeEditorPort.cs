using System;
using System.Collections.Generic;
using System.Text;
using KspControl.Bridge;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>A manual clock shared by the tracker, the fake port and the test.</summary>
    internal sealed class FakeClock
    {
        public long Milliseconds = 1000;
        /// <summary>The cost clock advances only while the fake port "saves", so the measured cost is exactly <see cref="EditorFake.CaptureCost"/>.</summary>
        public double CostMilliseconds;
    }

    internal sealed class FakeSink : ITakeoverSink
    {
        public bool LeaseHeld { get; set; }
        public int Takeovers;
        public void HumanTakeover() { Takeovers++; LeaseHeld = false; }
    }

    /// <summary>
    /// A scriptable editor: a craft text the test edits, a ship identity object, UI fields, locks and FSM state. It records how
    /// often it was saved so tests can assert that no capture happens where none is allowed.
    /// </summary>
    internal sealed class EditorFake : IEditorPort
    {
        private readonly FakeClock clock;
        public EditorFake(FakeClock clock) { this.clock = clock; Ship = new object(); Craft = CraftText(); }

        public bool InEditorValue = true;
        public bool ShipNull;
        public object Ship;
        public string Craft;
        public string Name = "Probe", Description = "", Flag = "Squad/Flags/default";
        public bool Selected;
        public string Fsm = "st_idle";
        public List<string> Locks = new List<string>();
        public string Cheap = "c0";
        public double CaptureCost = 1.0;
        public int Captures;
        public bool CaptureThrows;
        public Action DuringCapture;
        public EditorCapabilities Caps = new EditorCapabilities { Fsm = true, UnsavedMarker = true, SaveOverwriteGuard = true, VesselNameAtLastSave = true, VesselNameAtLastSaveSanitized = true, UndoLevel = true, UndoIndexAtLastSave = true, SetLastSanitizedSaveName = true };
        public bool? UnsavedValue = false;
        public string LastSaved = "Probe";
        public EngineeringData Engineering = new EngineeringData { PartTotal = 3, DeltaVRequested = true };
        public int Parts = 3;

        public bool EditorScene => InEditorValue;
        public bool InEditor => InEditorValue && !ShipNull;
        public object ShipIdentity => InEditor ? Ship : null;
        public string Facility => "VAB";
        public int PartCount => Parts;
        public bool HasSelectedPart => Selected;
        public string FsmState => Fsm;
        public IReadOnlyCollection<string> ActiveLockIds => Locks;
        public EditorUi ReadUi() { return new EditorUi(Name, Description, Flag); }
        public EditorCapabilities Capabilities => Caps;
        public bool? Unsaved => UnsavedValue;
        public string LastSavedName => LastSaved;
        public string CheapHash() { return Cheap; }
        public EngineeringData ReadEngineering(int offset, int limit, bool includeDeltaV) { Engineering.DeltaVRequested = includeDeltaV; return Engineering; }

        public EditorCraft Capture()
        {
            Captures++;
            clock.CostMilliseconds += CaptureCost;
            DuringCapture?.Invoke();
            if (CaptureThrows) throw new InvalidOperationException("save_failed");
            // Like the Unity adapter: an empty editor has nothing to save.
            if (Parts == 0) return new EditorCraft(new Pure.ConfigNode(""), ReadUi());
            return new EditorCraft(Pure.ConfigText.Parse(Craft), ReadUi());
        }

        // ---- operation members: scriptable, with every call recorded ----

        public enum LoadMode { Normal, EmptyAfterLoad, Throws }
        public bool SceneReadyValue = true, Restarting, PartsStarted = true, DeltaVDone = true;
        public EditorHeader Header = new EditorHeader("1.12.5", "mods:probe-1");
        public uint PersistentCounter = 5000;
        public MemoryFiles Files;
        public HashSet<string> KnownParts;
        public Action<EditorEventKind> Raise;
        public LoadMode Mode = LoadMode.Normal;
        public Action<EditorFake, string> OnLoad;
        public int LoadCalls, SetBackupCalls, LockSets, LockClears, UiWrites, GuardWrites;
        public readonly List<string> LoadedPaths = new List<string>();
        public SaveFields SaveState = new SaveFields("Probe", "Probe", 4, 4);
        public bool GuardWritesFail, UnsavedMarkerFails;
        public bool AllPartsFoundResult = true;
        public List<string> CrewList = new List<string> { "100001|0|Jebediah Kerman" };
        public string ReleaseLockOnLoad;

        public bool SceneReady => SceneReadyValue && InEditorValue && !Restarting;
        public bool RestartingEditor => Restarting;
        public bool AllPartsStarted => PartsStarted;
        public bool DeltaVReady => DeltaVDone;
        public bool OperationLockHeld => Locks.Contains(EditorIdle.OperationLockId);
        public void SetOperationLock() { LockSets++; if (!Locks.Contains(EditorIdle.OperationLockId)) Locks.Add(EditorIdle.OperationLockId); }
        public void ClearOperationLock() { LockClears++; Locks.Remove(EditorIdle.OperationLockId); }
        public EditorHeader ReadHeader() { return Header; }
        public uint NextPersistentId() { return ++PersistentCounter; }
        public string SanitizeFileName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? "") sb.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }
        public bool AllPartsFound(string path, out string missing)
        {
            missing = null;
            if (!AllPartsFoundResult) { missing = "forced_missing"; return false; }
            if (KnownParts == null || Files == null) return true;
            foreach (var part in Pure.ConfigText.Parse(Files.Text(path)).Children("PART"))
            {
                var value = part.First("part") ?? "";
                var name = value.Substring(0, Math.Max(0, value.LastIndexOf('_')));
                if (!KnownParts.Contains(name)) { missing = name; return false; }
            }
            return true;
        }
        public void LoadCraftFile(string path)
        {
            LoadCalls++; LoadedPaths.Add(path);
            if (Mode == LoadMode.Throws) throw new InvalidOperationException("load_boom");
            var text = Files.Text(path);
            Raise?.Invoke(EditorEventKind.Restart);
            Ship = new object();
            if (Mode == LoadMode.EmptyAfterLoad) { Craft = ""; Parts = 0; Fsm = "st_podSelect"; Name = "Untitled Space Craft"; Description = ""; UnsavedValue = false; }
            else
            {
                var node = Pure.ConfigText.Parse(text);
                Craft = text; Parts = System.Linq.Enumerable.Count(node.Children("PART")); Fsm = "st_idle";
                Name = node.First("ship") ?? ""; Description = node.First("description") ?? ""; Flag = node.First("missionFlag") ?? Flag;
                UnsavedValue = false; LastSaved = Name; SaveState = new SaveFields(Name, Name, SaveState.UndoLevel + 1, SaveState.UndoLevel + 1);
            }
            Raise?.Invoke(EditorEventKind.SetBackup); Raise?.Invoke(EditorEventKind.ShipModified); Raise?.Invoke(EditorEventKind.Started); Raise?.Invoke(EditorEventKind.Load);
            OnLoad?.Invoke(this, path);
        }
        public void WriteUi(EditorUi ui) { UiWrites++; Name = ui.Name; Description = ui.Description; Flag = ui.FlagUrl; }
        public SaveFields ReadSaveFields() { return Caps.SaveOverwriteGuard && Caps.UnsavedMarker ? SaveState : null; }
        public bool TryWriteSavedName(string name, string sanitized)
        {
            if (!Caps.SaveOverwriteGuard || GuardWritesFail) return false;
            GuardWrites++; LastSaved = name; SaveState = new SaveFields(name, sanitized, SaveState.UndoLevel, SaveState.UndoIndexAtLastSave); return true;
        }
        public bool TryMarkUnsaved()
        {
            if (!Caps.UnsavedMarker || UnsavedMarkerFails) return false;
            SaveState = new SaveFields(SaveState.Name, SaveState.Sanitized, SaveState.UndoLevel, -1); UnsavedValue = Parts > 0; return true;
        }
        public bool TryMarkSaved()
        {
            if (!Caps.UnsavedMarker || UnsavedMarkerFails) return false;
            SaveState = new SaveFields(SaveState.Name, SaveState.Sanitized, SaveState.UndoLevel, SaveState.UndoLevel); UnsavedValue = false; return true;
        }
        public bool TrySetBackup() { SetBackupCalls++; UnsavedValue = Parts > 0; return true; }
        public IReadOnlyList<string> ReadCrew() { return CrewList; }

        // ---- surface placement (P2.8): scriptable live measurements, every call counted ----
        public Func<uint, double, double?> RadiusOf;
        public Func<uint, Pure.SurfaceNodeDefinition> SurfaceNodeOf;
        public int MeasureCalls, NodeReads;
        public double? MeasureSurfaceRadius(uint parentCraftId, double localHeight) { MeasureCalls++; return RadiusOf == null ? (double?)null : RadiusOf(parentCraftId, localHeight); }
        public Pure.SurfaceNodeDefinition ReadSurfaceNode(uint craftId) { NodeReads++; return SurfaceNodeOf == null ? null : SurfaceNodeOf(craftId); }

        /// <summary>A three-part craft. Each argument changes exactly one thing the fingerprint must notice.</summary>
        public static string CraftText(string podStage = "-1", string tankStage = "1", string moduleValue = "10", string cryoTime = "100")
        {
            var sb = new StringBuilder();
            sb.AppendLine("ship = Probe"); sb.AppendLine("version = 1.12.5"); sb.AppendLine("description = "); sb.AppendLine("type = VAB");
            sb.AppendLine("persistentId = 111"); sb.AppendLine("rot = 0,0,0,0"); sb.AppendLine("missionFlag = Squad/Flags/default");
            Part(sb, "mk1pod.v2", 100000, "0,15,0", "attN = bottom,fuelTankSmall_100001_0|-0.405|0", null, "fuelTankSmall_100001", podStage, moduleValue, cryoTime);
            Part(sb, "fuelTankSmall", 100001, "0,14.2,0", "attN = top,mk1pod.v2_100000_0|0.4|0", "mk1pod.v2_100000", "liquidEngine_100002", tankStage, "5", cryoTime);
            Part(sb, "liquidEngine", 100002, "0,13.1,0", "attN = top,fuelTankSmall_100001_0|0.3|0", "fuelTankSmall_100001", null, "0", "5", cryoTime);
            return sb.ToString();
        }

        private static void Part(StringBuilder sb, string name, int cid, string pos, string attN, string parent, string child, string stage, string moduleValue, string cryoTime)
        {
            sb.AppendLine("PART"); sb.AppendLine("{");
            sb.AppendLine("\tpart = " + name + "_" + cid);
            sb.AppendLine("\tpersistentId = " + (cid * 7));
            sb.AppendLine("\tpos = " + pos); sb.AppendLine("\tattPos0 = " + pos); sb.AppendLine("\trot = 0,0,0,1");
            sb.AppendLine("\tistg = " + stage); sb.AppendLine("\tdstg = 0"); sb.AppendLine("\tsidx = -1"); sb.AppendLine("\tsqor = -1"); sb.AppendLine("\tsepI = -1"); sb.AppendLine("\tattm = 0");
            sb.AppendLine("\t" + attN);
            if (child != null) sb.AppendLine("\tlink = " + child);
            sb.AppendLine("\tMODULE"); sb.AppendLine("\t{"); sb.AppendLine("\t\tname = ModuleProbe"); sb.AppendLine("\t\tvalue = " + moduleValue); sb.AppendLine("\t}");
            sb.AppendLine("\tMODULE"); sb.AppendLine("\t{"); sb.AppendLine("\t\tname = ModuleCryoTank"); sb.AppendLine("\t\tLastUpdateTime = " + cryoTime); sb.AppendLine("\t}");
            sb.AppendLine("}");
        }
    }
}
