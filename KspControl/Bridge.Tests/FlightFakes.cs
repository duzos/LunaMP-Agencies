using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>A scriptable vessel behind the three flight ports. Every callback is recorded so tests can prove which ones did NOT run.</summary>
    internal sealed class FakeFlight : IFlightPort, IFlightInputPort, IFlightHumanInput
    {
        public bool InFlightValue = true;
        public FlightSnapshot Snap;
        public readonly Dictionary<string, bool> Groups = new Dictionary<string, bool>(StringComparer.Ordinal);
        public readonly Dictionary<string, List<FlightPartAction>> Bindings = new Dictionary<string, List<FlightPartAction>>(StringComparer.Ordinal);
        public readonly Dictionary<int, List<FlightPartAction>> StageParts = new Dictionary<int, List<FlightPartAction>>();
        public readonly List<string> Calls = new List<string>();
        public bool Locked;
        // Failure injection.
        public bool GroupSetterIgnored, StageIgnored, AbortIgnored, WarpDenied, AttachFails, ThrottleWriteIgnored;
        public Action<int> OnRead;
        public int Reads;
        public bool Attached;
        public bool Pressing;
        private Func<float?> tick;

        public FakeFlight()
        {
            Snap = new FlightSnapshot
            {
                VesselId = "11111111-1111-1111-1111-111111111111", VesselName = "Probe", Owned = true, Controllable = true, Situation = "ORBITING", Body = "Kerbin",
                UniversalTime = 1234.5, Altitude = 100000, VerticalSpeed = 1.5, SurfaceSpeed = 2200, OrbitalSpeed = 2300, CrewCount = 0, PartCount = 12,
                Orbit = new FlightOrbit { ReferenceBody = "Kerbin", ApoapsisAltitude = 101000, PeriapsisAltitude = 99000, InclinationDegrees = 0.1, Eccentricity = 0.001, SemiMajorAxis = 700000, PeriodSeconds = 1800, TimeToApoapsis = 900, TimeToPeriapsis = 1800, PatchEndTransition = "FINAL" },
                Controls = new FlightControlStates { CurrentStage = 2, StageCount = 3 },
                Warp = new FlightWarpInfo { Mode = "rails", CurrentIndex = 0, CurrentRate = 1, Rates = new float[] { 1, 5, 10, 50, 100, 1000, 10000, 100000 }, AltitudeLimitIndex = 7 }
            };
            foreach (var g in FlightLimits.ActionGroups.Concat(new[] { "Abort" })) { Groups[g] = false; Bindings[g] = new List<FlightPartAction>(); }
        }

        public static FlightPartAction Act(string module, string part = "100", string action = "Toggle", bool onStaging = false)
        { return new FlightPartAction { PartId = part, PartName = "p" + part, Module = module, Action = action, ActsOnStaging = onStaging }; }

        public int Count(string prefix) { return Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal)); }

        // ---- IFlightPort ----
        public bool InFlight { get { return InFlightValue; } }
        public FlightSnapshot Read()
        {
            Reads++; if (OnRead != null) OnRead(Reads);
            if (Snap == null) return null;
            Snap.Controls.Sas = Groups["SAS"]; Snap.Controls.Rcs = Groups["RCS"]; Snap.Controls.Gear = Groups["Gear"]; Snap.Controls.Lights = Groups["Light"]; Snap.Controls.Brakes = Groups["Brakes"];
            return Snap;
        }
        public bool GetGroup(string group) { return Groups[group]; }
        public bool SetGroup(string group, bool desired) { Calls.Add("set:" + group + "=" + desired); if (!GroupSetterIgnored) Groups[group] = desired; return Groups[group]; }
        public bool ToggleGroup(string group) { Calls.Add("toggle:" + group); if (!GroupSetterIgnored) Groups[group] = !Groups[group]; return Groups[group]; }
        public bool StagingLocked { get { return Locked; } }
        public IList<FlightPartAction> PartsInStage(int stage) { List<FlightPartAction> list; return StageParts.TryGetValue(stage, out list) ? list : new List<FlightPartAction>(); }
        public IList<FlightPartAction> GroupBindings(string group) { return Bindings[group]; }
        public void ActivateNextStage() { Calls.Add("stage"); if (!StageIgnored) Snap.Controls.CurrentStage--; }
        public void FireAbort() { Calls.Add("abort"); if (!AbortIgnored) Groups["Abort"] = true; }
        public void SetWarpIndex(int index)
        {
            Calls.Add("warp:" + index);
            if (WarpDenied) return;
            Snap.Warp.CurrentIndex = index; Snap.Warp.CurrentRate = Snap.Warp.Rates[index];
        }

        // ---- IFlightInputPort ----
        public bool Attach(Func<float?> t) { Calls.Add("attach"); if (AttachFails) return false; tick = t; Attached = true; return true; }
        public void Detach() { Calls.Add("detach"); tick = null; Attached = false; }
        public void WriteThrottle(float value) { Calls.Add("throttle:" + value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); if (!ThrottleWriteIgnored) Snap.Controls.Throttle = value; }
        public void CancelWarp() { Calls.Add("cancelwarp"); Snap.Warp.CurrentIndex = 0; Snap.Warp.CurrentRate = 1; }

        /// <summary>One vessel tick: what KSP does when it invokes the fly-by-wire callback.</summary>
        public void VesselTick()
        {
            if (tick == null) return;
            var value = tick();
            if (value.HasValue) Snap.Controls.Throttle = value.Value; else Detach();
        }

        // ---- IFlightHumanInput ----
        public bool PlayerIsInputting() { return Pressing; }
    }

    internal sealed class FakeFlightContext : IEditorContextSource
    {
        private readonly FakeFlight flight;
        public string Epoch = "epoch1";
        public bool Ready = true;
        public FakeFlightContext(FakeFlight flight) { this.flight = flight; }
        public LeaseContext CurrentContext() { return new LeaseContext(Epoch, FlightEffects.EntityPrefix + flight.Snap.VesselId, 0, Ready); }
        public GrantBinding CurrentBinding() { return new GrantBinding("install", "save", "agency"); }
    }

    /// <summary>The flight trust stack wired as ControlAddon wires it, on a fake clock.</summary>
    internal sealed class FlightRig
    {
        public readonly FakeFlight Flight = new FakeFlight();
        public readonly FakeFlightContext Context;
        public readonly FakeClock Clock = new FakeClock();
        public readonly MemorySuspensionStore Store = new MemorySuspensionStore();
        public readonly ExecutionAuthority Authority;
        public readonly FlightControlGuard Guard;
        public readonly FlightOperationService Service;
        public readonly FlightStateService State;
        public readonly FlightTakeoverWatcher Takeover;
        public readonly ControlPump Pump;
        public DateTime Utc = AuthorityHelpers.Utc0;
        public string Lease;
        public bool Heartbeats = true;
        public int Counter;

        public FlightRig(bool lease = true, bool grant = true, TrustedExecutionGrant custom = null)
        {
            Context = new FakeFlightContext(Flight);
            Authority = new ExecutionAuthority(() => Clock.Milliseconds, GrantMapping.KnownEffects, 2000, Store, () => Utc);
            Guard = new FlightControlGuard(Flight, () => Authority.LeaseHeld);
            Authority.LeaseEnded += Guard.OnLeaseEnded;
            Service = new FlightOperationService(Flight, Guard, Authority, Context, () => "epoch1", () => Utc);
            State = new FlightStateService(Flight, () => "epoch1");
            Takeover = new FlightTakeoverWatcher(Flight, Authority, 3);
            Pump = new ControlPump(Authority, null, Context, null);
            Frame(0);
            var status = AuthorityHelpers.ValidStatus();
            Authority.UpdateContext(Context.CurrentContext(), Context.CurrentBinding(), status);
            if (custom != null) Authority.ProvisionGrant(custom);
            else if (grant) Authority.ProvisionGrant(FlightGrant());
            if (lease) AcquireLease();
        }

        public static TrustedExecutionGrant FlightGrant(string id = "grant", long generation = 1)
        {
            return new TrustedExecutionGrant(id, generation, AuthorityHelpers.Bind(), AuthorityHelpers.Utc0.AddHours(1000),
                new[] { new EffectPermission(FlightEffects.Family, FlightEffects.EntityWildcard) }, new[] { FlightEffects.EntityWildcard });
        }

        public void AcquireLease() { Lease = Authority.AcquireLease(300000, "flight probe"); Frame(16); }

        /// <summary>One game frame: lease keeper heartbeat, trust pump, guard check, takeover watcher.</summary>
        public void Frame(long milliseconds = 16)
        {
            Clock.Milliseconds += milliseconds;
            if (Lease != null && Heartbeats) Authority.Heartbeat(Lease);
            Pump.Update();
            if (Takeover != null) Takeover.Update();
            Guard.Update();
        }

        public BridgeResponse Send(string operation, JObject args, string lease = null, bool withLease = true)
        {
            var request = new BridgeRequest { RequestId = "wire-" + (++Counter), Operation = operation, Arguments = args, LeaseId = withLease ? (lease ?? Lease) : null };
            return Service.Handle(request);
        }

        public static JObject Args(string requestId, params object[] pairs)
        {
            var o = new JObject { ["requestId"] = requestId };
            for (var i = 0; i < pairs.Length; i += 2) o[(string)pairs[i]] = JToken.FromObject(pairs[i + 1]);
            return o;
        }

        public BridgeResponse SetControls(string id, params object[] pairs) { return Send(FlightOperations.SetControls, Args(id, pairs)); }
        public BridgeResponse Stage(string id, int expected) { return Send(FlightOperations.Stage, Args(id, "expectedStage", expected)); }
        public BridgeResponse Group(string id, string group, bool? state = null)
        { return Send(FlightOperations.ActionGroup, state.HasValue ? Args(id, "group", group, "state", state.Value) : Args(id, "group", group)); }
        public BridgeResponse Abort(string id) { return Send(FlightOperations.Abort, Args(id)); }
        public BridgeResponse Warp(string id, int index) { return Send(FlightOperations.Warp, Args(id, "rateIndex", index)); }
    }
}
