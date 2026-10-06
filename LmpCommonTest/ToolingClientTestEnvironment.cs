using System;
using System.Collections.Generic;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Interface;

// Test-only environment for the linked, unmodified production ToolingClient.
// Gameplay entry points deliberately throw; these tests exercise synchronization only.
public class TestUnityObject { public static implicit operator bool(TestUnityObject value) => value != null; }
public enum EditorFacility { VAB }
public enum GameScenes { SPACECENTER }
public enum ControlTypes { EDITOR_LAUNCH }
public class Funding : TestUnityObject { public static Funding Instance; }
public class ResearchAndDevelopment : TestUnityObject { public static ResearchAndDevelopment Instance; }
public class CrewMember { public string name; }
public class VesselCrewManifest { public IEnumerable<CrewMember> GetAllCrew(bool value) => throw new NotSupportedException(); }
public class Vessel : TestUnityObject { public Guid id; public bool loaded, packed; public CelestialBody mainBody; public Part rootPart; public Vector3d CoMD, velocityD; }
public class Part : TestUnityObject { public uint flightID, craftID; public TestUnityObject rb; public Vessel vessel; public List<CrewMember> protoModuleCrew = new List<CrewMember>(); }
public class ShipConstruct { public List<Part> parts = new List<Part>(); }
public class ProtoPartSnapshot { public uint flightID; }
public class ProtoVessel { public Guid vesselID; public bool landed, splashed; public OrbitSnapshot orbitSnapShot; public List<ProtoPartSnapshot> protoPartSnapshots = new List<ProtoPartSnapshot>(); }
public class ShipTemplate { public ConfigNode config; }
public class ConfigNode
{
    public string name;
    public class Value { public string name, value; }
    public List<ConfigNode> nodes = new List<ConfigNode>();
    public List<Value> values = new List<Value>();
    public ConfigNode(string name = "") { this.name = name; }
    public void AddValue(string key, string value) => values.Add(new Value { name = key, value = value });
    public void AddNode(ConfigNode node) => nodes.Add(node);
    public void RemoveNode(ConfigNode node) => nodes.Remove(node);
    public void ClearNodes() => nodes.Clear();
    public static ConfigNode Load(string path) => throw new NotSupportedException();
    public ConfigNode[] GetNodes(string name) => nodes.FindAll(n => n.name == name).ToArray();
    public string GetValue(string name) => values.Find(v => v.name == name)?.value;
}
public class EditorLogic { public static EditorLogic fetch; public ShipConstruct ship; }
public static class ShipConstruction { public static VesselCrewManifest ShipManifest; }
public static class HighLogic { public static GameScenes LoadedScene; public static bool LoadedSceneIsEditor; }
public static class FlightGlobals { public static Vessel ActiveVessel; }
public static class FlightDriver { public static void StartWithNewLaunch(string path, string flag, string site, VesselCrewManifest crew) => throw new NotSupportedException(); }
public static class InputLockManager { public static void SetControlLock(ControlTypes type, string name) { } public static void RemoveControlLock(string name) { } }
public static class GameEvents { public class FromToAction<TFrom,TTo> { public TFrom from; public TTo to; } }
namespace LmpClient
{
    public class MainSystem { public static ClientState NetworkState; public static MainSystem Singleton = new MainSystem(); public bool ForceQuit; }
}
namespace LmpClient.Diagnostics
{
    internal static class PlaytestDiagnostics { internal static void Write(string name, Func<string> details) { } }
}
namespace LmpClient.Network
{
    internal static class NetworkMain { internal static readonly ClientMessageFactory CliMsgFactory = new ClientMessageFactory(); }
    internal static class NetworkSender
    {
        internal static readonly List<IMessageBase> Sent = new List<IMessageBase>();
        internal static void QueueOutgoingMessage(IMessageBase message) => Sent.Add(message);
    }
    internal static class NetworkConnection { internal static void Disconnect(string reason) => throw new InvalidOperationException(reason); }
}
namespace LmpClient.Systems.SettingsSys
{
    internal sealed class TestSettings
    {
        internal bool AgencyTooling, AgencyTrade;
        internal bool CanRevert = true;
        internal double ToolingCostMultiplier = ToolingDefaults.ToolingCost, TooledLaunchMultiplier = ToolingDefaults.TooledLaunch, UntooledLaunchMultiplier = ToolingDefaults.UntooledLaunch, ToolingCombineMultiplier = ToolingDefaults.Combine;
    }
    internal static class SettingsSystem { internal static TestSettings ServerSettings = new TestSettings(); }
}
namespace LmpClient.Systems.ShareFunds
{
    internal sealed class ShareFundsSystem
    {
        internal static readonly ShareFundsSystem Singleton = new ShareFundsSystem();
        internal double? Applied; internal Action OnApply;
        internal void SetFundsWithoutTriggeringEvent(double value) { Applied = value; OnApply?.Invoke(); }
    }
}
namespace LmpClient.Systems.ShareScience
{
    internal sealed class ShareScienceSystem
    {
        internal static readonly ShareScienceSystem Singleton = new ShareScienceSystem();
        internal float? Applied; internal Action OnApply;
        internal void SetScienceWithoutTriggeringEvent(float value) { Applied = value; OnApply?.Invoke(); }
    }
}
namespace LmpClient.Systems.Agency
{
    internal sealed class AgencySystem { internal static readonly AgencySystem Singleton = new AgencySystem(); internal Guid MyAgencyId; }
    internal static class TradeClient
    {
        internal static void Receive(EconomySnapshot state) { }
        internal static void HandleResult(EconomyResult result) { }
        internal static void Tick() { }
    }
    internal static class ToolingManifestBuilder
    {
        internal static ToolingManifest Build(ShipConstruct ship, VesselCrewManifest crew) => throw new NotSupportedException();
        internal static ToolingManifest FromFile(string path, VesselCrewManifest crew) => throw new NotSupportedException();
        internal static ToolingManifest FromConfig(ConfigNode node, VesselCrewManifest crew) => throw new NotSupportedException();
    }
    // Only unlinked boarding/splitting partial-file dependencies are supplied here.
    public static partial class ToolingClient
    {
        private static object boarding;
        private sealed class TestSplit { internal DateTime Deadline; }
        private static TestSplit splitting;
        private static readonly Queue<object> splitQueue = new Queue<object>();
        private static long splitBytes;
        private const string BoardingLock = "test-boarding", SplitLock = "test-split";
        private static void TickBoarding() { }
        private static bool HandleBoarding(EconomyResult result) => false;
        private static bool HandleSplit(EconomyResult result) => false;
    }
}

namespace LmpClient.Systems.VesselRemoveSys { public static class VesselRemoveMessageSender { public static void FlushRetainedRemovals() { } } }
namespace LmpClient.Harmony { internal static class AgencyCostDisplay { internal static void Refresh() { } internal static void Clear() { } } }
