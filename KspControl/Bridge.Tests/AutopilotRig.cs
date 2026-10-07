using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>A scriptable MechJeb port that records every call. The reflection adapter is tested separately against look-alike classes.</summary>
    internal sealed class FakeMechJebPort : IMechJebPort
    {
        public MechJebCapabilities Caps { get; } = new MechJebCapabilities { Installed = true, Version = "2.15.2.0", VersionSupported = true };
        public MechJebCapabilities Capabilities { get { return Caps; } }
        public bool Core = true;
        public List<string> Competitors = new List<string>();
        public List<string> Calls = new List<string>();
        public Func<MechJebException> ConfigureFails, EngageFails, ReadFails;
        public bool ReleaseThrows;
        // ascent module
        public bool AscentEnabled, AscentOwn; public int AscentOthers; public string AscentStatusText = "Pre-launch";
        // node executor
        public bool NodeEnabled, NodeOwn; public int NodeOthers; public string NodeState = "IDLE";
        public bool NodeAll;
        public AscentSettingsView Configured;
        /// <summary>The vessel id every call was made for: a job must only ever name its own vessel.</summary>
        public List<string> Vessels = new List<string>();
        public bool ViaWindow;
        public List<bool> WindowFlags = new List<bool>();
        public bool SavedAutowarp = true; public bool? RestoredAutowarp; public bool RestoreSeen;
        public List<string> CompetitorQueries = new List<string>();

        public FakeMechJebPort()
        {
            foreach (var module in new[] { "ascent", "ascentSettings", "node", "landing", "thrust", "attitude" }) Caps.Modules[module] = true;
        }

        public bool HasCore(string vesselId) { Vessels.Add(vesselId); return Core; }
        public JObject ReadStatus() { return new JObject { ["mechjeb"] = Caps.State, ["installed"] = Caps.Installed, ["version"] = Caps.Version, ["vesselCore"] = Core }; }

        public List<string> FindCompetitors(string vesselId, object ownUser, bool includeWindow)
        {
            Vessels.Add(vesselId); CompetitorQueries.Add(vesselId); WindowFlags.Add(includeWindow);
            return Competitors.ToList();
        }

        public AscentSettingsView ConfigureAscent(string vesselId, double altitudeMeters, double inclinationDegrees, bool autostage)
        {
            Vessels.Add(vesselId);
            if (ConfigureFails != null) throw ConfigureFails();
            Calls.Add("configure:" + altitudeMeters + ":" + inclinationDegrees + ":" + autostage);
            return Configured = new AscentSettingsView { AscentType = "CLASSIC", TargetAltitudeMeters = altitudeMeters, InclinationDegrees = inclinationDegrees, Autostage = autostage, SkipCircularization = false };
        }

        public void EngageAscent(string vesselId, object user)
        {
            Vessels.Add(vesselId);
            if (EngageFails != null) throw EngageFails();
            Calls.Add("engage_ascent"); AscentEnabled = true; AscentOwn = true;
        }

        public void DisengageAscent(string vesselId, object user)
        {
            Vessels.Add(vesselId);
            Calls.Add("disengage_ascent");
            if (ReleaseThrows) throw new MechJebException("engage_failed");
            AscentEnabled = false; AscentOwn = false;
        }

        public AscentReading ReadAscent(string vesselId, object user)
        {
            Vessels.Add(vesselId);
            if (ReadFails != null) throw ReadFails();
            return new AscentReading { Enabled = AscentEnabled, Status = AscentStatusText, OwnUserPresent = AscentOwn, OtherUsers = AscentOthers, ViaWindow = ViaWindow };
        }

        public bool EngageNode(string vesselId, object user, bool all)
        {
            Vessels.Add(vesselId);
            if (EngageFails != null) throw EngageFails();
            Calls.Add(all ? "engage_node_all" : "engage_node_one"); NodeEnabled = true; NodeOwn = true; NodeAll = all; NodeState = "WARPALIGN";
            return SavedAutowarp;
        }

        public void DisengageNode(string vesselId, object user, bool? restoreAutowarp)
        {
            Vessels.Add(vesselId); RestoreSeen = true; RestoredAutowarp = restoreAutowarp;
            Calls.Add("disengage_node");
            if (ReleaseThrows) throw new MechJebException("engage_failed");
            NodeEnabled = false; NodeOwn = false; NodeState = "IDLE";
        }

        public NodeReading ReadNode(string vesselId, object user)
        {
            Vessels.Add(vesselId);
            if (ReadFails != null) throw ReadFails();
            return new NodeReading { Enabled = NodeEnabled, State = NodeState, OwnUserPresent = NodeOwn, OtherUsers = NodeOthers };
        }

        public void ThrustOff(string vesselId) { Vessels.Add(vesselId); Calls.Add("thrust_off"); }
    }

    internal sealed class FakeFlightPort : IAutopilotFlightPort
    {
        public bool InFlightValue = true, Human;
        public FlightTelemetry Telemetry = new FlightTelemetry
        {
            VesselId = "vessel-1", BodyName = "Kerbin", AltitudeMeters = 0, ApoapsisMeters = 0, PeriapsisMeters = -600000, AtmosphereTopMeters = 70000, Situation = "PRELAUNCH", Orbiting = false, ManeuverNodes = 0
        };
        public int Throttle100 = 100;
        public int CutCalls; public List<string> CutVessels = new List<string>();
        public PlanOutcome CircularizeResult = new PlanOutcome { Ok = true, Data = new JObject { ["source"] = "stock_math", ["kind"] = "circularize_at_apoapsis", ["deltaVMetersPerSecond"] = 88.0 } };
        public PlanOutcome HohmannResult = new PlanOutcome { Ok = true, Data = new JObject { ["source"] = "stock_math", ["kind"] = "hohmann_phase_wait_estimate", ["deltaVMetersPerSecond"] = 842.0 } };
        public string HohmannTarget; public int PlanCalls;
        public bool InFlight { get { return InFlightValue; } }
        public FlightTelemetry Read() { return InFlightValue ? Telemetry : null; }
        public bool HumanInputDetected() { return Human; }
        public void CutThrottle(string vesselId) { CutCalls++; CutVessels.Add(vesselId); Throttle100 = 0; }
        public PlanOutcome PlanCircularize() { PlanCalls++; if (CircularizeResult.Ok) Telemetry.ManeuverNodes++; return CircularizeResult; }
        public PlanOutcome PlanHohmann(string targetBodyName) { PlanCalls++; HohmannTarget = targetBodyName; if (HohmannResult.Ok) Telemetry.ManeuverNodes++; return HohmannResult; }
    }

    internal sealed class FlightContextSource : IEditorContextSource
    {
        public string Epoch = "epoch1", Entity = AutopilotOperations.EntityPrefix + "vessel-1";
        public bool Ready = true;
        public LeaseContext CurrentContext() { return new LeaseContext(Epoch, Entity, 0, Ready); }
        public GrantBinding CurrentBinding() { return AuthorityHelpers.Bind(); }
    }

    /// <summary>
    /// The autopilot layer on fakes around the real execution authority: a flight scene context, a provisioned grant with the flight family,
    /// a lease, the runner and the service. <see cref="Frame"/> mirrors one ControlAddon.Update: heartbeat, trust pump, then the runner.
    /// </summary>
    internal sealed class AutopilotRig
    {
        public readonly FakeClock Clock = new FakeClock();
        public readonly MemorySuspensionStore Store = new MemorySuspensionStore();
        public readonly ExecutionAuthority Authority;
        public readonly FlightContextSource Context = new FlightContextSource();
        public readonly ControlPump Pump;
        public readonly FakeMechJebPort MechJeb = new FakeMechJebPort();
        public readonly FakeFlightPort Flight = new FakeFlightPort();
        public readonly AutopilotJobs Jobs = new AutopilotJobs();
        public readonly AutopilotOptions Options = new AutopilotOptions();
        public readonly AutopilotRunner Runner;
        public readonly MechJebService Service;
        public DateTime Utc = AuthorityHelpers.Utc0;
        public string Lease;
        public bool Heartbeats = true;
        private int counter;

        public AutopilotRig(bool lease = true, string[] operations = null, string[] facilities = null)
        {
            Authority = new ExecutionAuthority(() => Clock.Milliseconds, GrantMapping.KnownEffects, 2000, Store, () => Utc);
            Pump = new ControlPump(Authority, null, Context);
            Runner = new AutopilotRunner(Authority, Context, MechJeb, Flight, () => Clock.Milliseconds, () => Utc, Options);
            Service = new MechJebService(Authority, Runner, Jobs, MechJeb, Flight, () => Context.Epoch, () => Utc);
            Authority.UpdateContext(Context.CurrentContext(), Context.CurrentBinding(), AuthorityHelpers.ValidStatus());
            Authority.ProvisionGrant(GrantMapping.ToGrant(Payload(operations ?? new[] { AutopilotOperations.Effect }, facilities ?? new[] { AutopilotOperations.Facility })));
            if (lease) AcquireLease();
        }

        private static GrantPayload Payload(string[] operations, string[] facilities)
        {
            return new GrantPayload
            {
                GrantId = "grant", Generation = 1, IssuedUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0), ExpiresUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0.AddHours(1000)),
                Binding = new GrantBindingInfo { InstallId = "install", SaveFolder = "save", Agency = "agency" }, Operations = operations, Facilities = facilities,
                UnsavedCraftPolicy = "refuse", MaxParts = 250, SpendLimitFunds = 0, Revoked = false
            };
        }

        public void AcquireLease() { Lease = Authority.AcquireLease(300000, "fly to orbit"); Frame(16); }

        public void Frame(long milliseconds = 16)
        {
            Clock.Milliseconds += milliseconds;
            if (Lease != null && Heartbeats) Authority.Heartbeat(Lease);
            Pump.Update();
            Runner.Update();
        }

        public void Run(int frames, long milliseconds = 16) { for (var i = 0; i < frames; i++) Frame(milliseconds); }

        public AutopilotJob RunToEnd(long milliseconds = 100, int maxFrames = 5000)
        {
            var job = Runner.Current;
            for (var i = 0; i < maxFrames && !job.Terminal; i++) Frame(milliseconds);
            if (!job.Terminal) throw new InvalidOperationException("the job never ended; phase " + job.Phase);
            return job;
        }

        public string NextId() { return "req-" + (++counter).ToString("D6"); }

        public BridgeRequest Ascent(string requestId = null, int altitude = 100000, double inclination = 0, bool autostage = true, string lease = "default", JObject extra = null)
        {
            var args = new JObject { ["requestId"] = requestId ?? "ascent-0001", ["targetAltitudeMeters"] = altitude, ["inclinationDegrees"] = inclination, ["autostage"] = autostage };
            if (extra != null) args.Merge(extra);
            return Request(AutopilotOperations.Ascent, args, lease);
        }

        public BridgeRequest ExecuteNode(string requestId = "node-0001", bool all = false, string lease = "default")
        { return Request(AutopilotOperations.ExecuteNode, new JObject { ["requestId"] = requestId, ["all"] = all }, lease); }

        public BridgeRequest PlanCircularize(string requestId = "circ-0001", string lease = "default")
        { return Request(AutopilotOperations.PlanCircularize, new JObject { ["requestId"] = requestId }, lease); }

        public BridgeRequest PlanHohmann(string requestId = "hohm-0001", string body = "Mun", string lease = "default")
        { return Request(AutopilotOperations.PlanHohmann, new JObject { ["requestId"] = requestId, ["targetBodyName"] = body }, lease); }

        public BridgeRequest Request(string operation, JObject args, string lease = "default")
        {
            return new BridgeRequest { RequestId = "wire-" + (args["requestId"] ?? "x"), Operation = operation, LeaseId = lease == "default" ? Lease : lease, Arguments = args };
        }

        /// <summary>Puts the vessel in the described state.</summary>
        public void Telemetry(double altitude, double apoapsis, double periapsis, bool orbiting)
        {
            Flight.Telemetry.AltitudeMeters = altitude; Flight.Telemetry.ApoapsisMeters = apoapsis; Flight.Telemetry.PeriapsisMeters = periapsis; Flight.Telemetry.Orbiting = orbiting;
            Flight.Telemetry.Situation = orbiting ? "ORBITING" : altitude > 100 ? "SUB_ORBITAL" : "PRELAUNCH";
        }
    }
}
