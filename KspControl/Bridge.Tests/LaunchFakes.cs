using System;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>A scriptable launch port: the facade, the scene, the reservation flag, the balance and the active vessel.</summary>
    internal sealed class FakeLaunchPort : ILaunchPort
    {
        public bool Facade = true;
        public string SceneName = "EDITOR";
        public bool IsConnected = true;
        public LaunchQuote QuoteValue = new LaunchQuote { Success = true, LaunchCost = 1000, ToolingCost = 250, AlreadyTooled = false, Fingerprint = "fp-1" };
        public bool AllowedValue = true;
        public string AllowedReason;
        public bool Pending;
        public double? FundsValue = 50000;
        public string Status;
        public long Serial;
        public double? Charge;
        public bool BeginResult = true;
        public string BeginReason;
        public bool BeginThrows;
        /// <summary>Run OnBegin even when the routine then reports failure (it acted before it failed).</summary>
        public bool ActsBeforeFailing;
        public int BeginCalls, CancelCalls;
        public string BeginSite;
        public bool CancelWorks = true;
        public bool PromptCloses = true;
        public int CloseCalls;
        public Action OnBegin, OnCancel;
        public LaunchVessel Vessel;
        public bool Owned = true;

        public bool FacadeAvailable { get { return Facade; } }
        public string Scene { get { return SceneName; } }
        public bool Connected { get { return IsConnected; } }
        public LaunchQuote Quote() { return QuoteValue; }
        public bool Allowed(out string reason) { reason = AllowedReason; return AllowedValue; }
        public bool LaunchPending { get { return Pending; } }
        public double? Funds { get { return FundsValue; } }
        public string LaunchStatus { get { return Status; } }
        public long ChargeSerial { get { return Serial; } }
        public double? LastCharge { get { return Charge; } }
        public bool BeginLaunch(string site, out string reason)
        {
            BeginCalls++; BeginSite = site; reason = BeginReason;
            if (BeginThrows) throw new InvalidOperationException("launch routine failed");
            if ((BeginResult || ActsBeforeFailing) && OnBegin != null) OnBegin();
            return BeginResult;
        }
        public bool CloseLaunchPrompt() { CloseCalls++; return PromptCloses && !Pending; }
        public bool CancelPendingLaunch() { CancelCalls++; if (!CancelWorks) return false; if (OnCancel != null) OnCancel(); return true; }
        public LaunchVessel ActiveVessel { get { return SceneName == "FLIGHT" ? Vessel : null; } }
        public bool VesselOwned(string vesselId) { return Owned; }
    }

    /// <summary>The operation rig plus the launch layer: port, runner, service. <see cref="Frame"/> mirrors one ControlAddon.Update.</summary>
    internal sealed class LaunchRig
    {
        public readonly OperationRig Rig;
        public readonly FakeLaunchPort Port = new FakeLaunchPort();
        public readonly LaunchJobs Jobs = new LaunchJobs();
        public readonly LaunchRunner Runner;
        public readonly LaunchService Service;
        public readonly LaunchOptions Options = new LaunchOptions();

        public LaunchRig(string[] operations = null)
        {
            Rig = new OperationRig(operations: operations ?? new[] { OperationEffects.Launch, OperationEffects.ReplaceCraft });
            Runner = new LaunchRunner(Port, Rig.Port, Rig.Tracker, Rig.Authority, Rig.Context, () => Rig.Clock.Milliseconds, () => Rig.Context.Epoch, () => Rig.Utc, Options);
            Service = new LaunchService(Port, Rig.Port, Runner, Jobs, Rig.Service, () => Rig.Context.Epoch, () => Rig.Utc);
            Rig.Service.ExtraBusy = () => Runner.Busy;
        }

        public ExecutionAuthority Authority { get { return Rig.Authority; } }
        public string Lease { get { return Rig.Lease; } }

        public void Frame(long milliseconds = 16)
        {
            Rig.Frame(milliseconds);
            Runner.Update();
        }

        public void Run(int frames, long milliseconds = 16) { for (var i = 0; i < frames; i++) Frame(milliseconds); }

        /// <summary>What the game does when the editor launch routine runs: the tooling flow reserves funds and the reservation is pending.</summary>
        public void ReserveOnBegin(double charge = 1000)
        {
            Port.OnBegin = () => { Port.Pending = true; Port.Serial++; Port.Charge = charge; Port.FundsValue = (Port.FundsValue ?? 0) - charge; };
        }

        /// <summary>The scene change a registered launch ends in: the flight scene, a new context, a pad vessel, no pending reservation.</summary>
        public void EnterFlight(string vesselId = "11111111-2222-3333-4444-555555555555", bool prelaunch = true)
        {
            Port.SceneName = "FLIGHT"; Rig.Context.Epoch = "epoch2"; Rig.Context.Entity = "scene:FLIGHT";
            Port.Vessel = new LaunchVessel { Id = vesselId, Name = "Probe One", Prelaunch = prelaunch };
        }

        public BridgeRequest Request(string requestId = "launch-0001", string site = "LaunchPad", long maxSpend = 5000, string token = null, string lease = null)
        {
            return new BridgeRequest
            {
                RequestId = "wire-" + requestId, Operation = EditorOperations.Launch, LeaseId = lease ?? Lease,
                Arguments = new JObject { ["requestId"] = requestId, ["launchSite"] = site, ["maxSpendFunds"] = maxSpend, ["expectedRevision"] = token ?? Rig.Token() }
            };
        }

        public BridgeResponse Launch(string requestId = "launch-0001", long maxSpend = 5000) { return Service.Handle(Request(requestId, maxSpend: maxSpend)); }

        public BridgeRequest StatusRequest(string requestId)
        {
            return new BridgeRequest { RequestId = "wire-status", Operation = EditorOperations.OperationStatus, Arguments = new JObject { ["requestId"] = requestId } };
        }

        /// <summary>Frames until the job is terminal, failing loudly if it never ends.</summary>
        public LaunchJob RunToEnd(long milliseconds = 50, int maxFrames = 8000)
        {
            var job = Runner.Current;
            for (var i = 0; i < maxFrames && !job.Terminal; i++) Frame(milliseconds);
            if (!job.Terminal) throw new InvalidOperationException("the launch never reached a terminal state; phase " + job.Phase);
            return job;
        }
    }
}
