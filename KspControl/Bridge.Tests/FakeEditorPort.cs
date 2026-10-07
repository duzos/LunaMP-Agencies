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
            return new EditorCraft(Pure.ConfigText.Parse(Craft), ReadUi());
        }

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
