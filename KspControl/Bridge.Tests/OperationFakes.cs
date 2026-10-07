using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KspControl.Bridge;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>An in-memory <see cref="IOperationFiles"/> with failure injection and a record of every write, delete and copy.</summary>
    internal sealed class MemoryFiles : IOperationFiles
    {
        public readonly Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, long> Ticks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Reparse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Writes = new List<string>(), Deletes = new List<string>(), Copies = new List<string>();
        public Func<string, bool> FailWrite = p => false, CorruptWrite = p => false, FailDelete = p => false;
        /// <summary>A replace of such a path throws <see cref="FileStateChangedException"/> after leaving the file with its previous bytes, like a ReplaceFileW partial failure.</summary>
        public Func<string, bool> PartialReplace = p => false;
        public readonly List<string> Reads = new List<string>();
        /// <summary>Runs after every successful write, so a test can make a sidecar appear the way another mod would.</summary>
        public Action<string> OnWrite;
        private long now = 100;

        public string Text(string path) { return new UTF8Encoding(false).GetString(Data[path]); }
        public void Put(string path, string text) { Put(path, new UTF8Encoding(false).GetBytes(text)); }
        public void Put(string path, byte[] bytes) { Data[path] = bytes; Ticks[path] = ++now; }
        /// <summary>Rewrites a file with different bytes of the same length, so only the write time distinguishes it.</summary>
        public void Touch(string path) { Ticks[path] = ++now; }

        public bool Exists(string path) { return Data.ContainsKey(path); }
        public byte[] ReadAllBytes(string path)
        {
            byte[] bytes;
            if (!Data.TryGetValue(path, out bytes)) throw new FileNotFoundException(path);
            Reads.Add(path);
            return (byte[])bytes.Clone();
        }
        public void WriteAtomic(string path, byte[] bytes)
        {
            if (FailWrite(path)) throw new IOException("write_failed");
            Writes.Add(path);
            var stored = (byte[])bytes.Clone();
            if (CorruptWrite(path) && stored.Length > 0) stored[stored.Length / 2] ^= 0x55;
            Put(path, stored);
            OnWrite?.Invoke(path);
        }
        public void CreateNew(string path, byte[] bytes)
        {
            if (FailWrite(path)) throw new IOException("write_failed");
            if (Data.ContainsKey(path)) throw new IOException("exists");
            WriteAtomic(path, bytes);
        }
        public void ReplaceExisting(string path, byte[] bytes)
        {
            if (!Data.ContainsKey(path)) throw new FileNotFoundException(path);
            if (PartialReplace(path)) throw new FileStateChangedException("restored_previous", new IOException("1176"));
            WriteAtomic(path, bytes);
        }
        public long FileLength(string path) { byte[] bytes; return Data.TryGetValue(path, out bytes) ? bytes.Length : -1; }
        public void Delete(string path)
        {
            if (FailDelete(path)) throw new IOException("delete_failed");
            Deletes.Add(path); Data.Remove(path); Ticks.Remove(path);
        }
        public void Copy(string from, string to, bool overwrite)
        {
            if (!Data.ContainsKey(from)) throw new FileNotFoundException(from);
            if (!overwrite && Data.ContainsKey(to)) throw new IOException("exists");
            Copies.Add(from + " -> " + to);
            Put(to, (byte[])Data[from].Clone());
        }
        public IReadOnlyList<FileEntry> List(string directory, string suffix)
        {
            var wanted = (directory ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var result = new List<FileEntry>();
            foreach (var kv in Data)
            {
                var dir = (Path.GetDirectoryName(kv.Key) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(kv.Key);
                if (!string.Equals(dir, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (suffix != null && !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new FileEntry(kv.Key, name, kv.Value.Length, Ticks[kv.Key]));
            }
            return result;
        }
        public bool IsReparsePoint(string path) { return Reparse.Contains(path); }
    }

    internal sealed class FakeContextSource : IEditorContextSource
    {
        private readonly Func<EditorRevisionTracker> tracker;
        private readonly Func<IEditorPort> port;
        public string Epoch = "epoch1";
        public bool Throws;
        public FakeContextSource(Func<EditorRevisionTracker> tracker, Func<IEditorPort> port) { this.tracker = tracker; this.port = port; }
        public LeaseContext CurrentContext()
        {
            if (Throws) throw new InvalidOperationException("scene teardown");
            return new LeaseContext(Epoch, "editor:VAB", tracker().EditRevision, port().SceneReady);
        }
        public GrantBinding CurrentBinding() { return new GrantBinding("install", "save", "agency"); }
    }

    /// <summary>The stock part values of the S0 twin, as the live catalog reader would report them.</summary>
    internal sealed class FakeCatalogReader : ICatalogPartReader
    {
        public bool ResearchAvailable { get; set; } = true;
        public bool SandboxMode { get; set; }
        public readonly Dictionary<string, CatalogPartSource> Parts = new Dictionary<string, CatalogPartSource>(StringComparer.Ordinal);
        public int Reads;
        public FakeCatalogReader()
        {
            Parts["mk1pod.v2"] = Part("mk1pod.v2", new[] { "ModuleCommand" }, new[] { "ElectricCharge" }, N("bottom", -0.4050379, -1), N("top", 0.6423756, 1));
            Parts["fuelTankSmall"] = Part("fuelTankSmall", new string[0], new[] { "LiquidFuel", "Oxidizer" }, N("top", 0.55525, 1), N("bottom", -0.55525, -1));
            Parts["liquidEngine.v2"] = Part("liquidEngine.v2", new[] { "ModuleEnginesFX" }, new[] { "LiquidFuel" }, N("top", 0, 1), N("bottom", -1.63, -1));
        }
        public CatalogPartSource Read(string name) { Reads++; CatalogPartSource p; return Parts.TryGetValue(name, out p) ? p : null; }
        private static CatalogNodeSource N(string id, double y, double oy) { return new CatalogNodeSource { Id = id, Position = new[] { 0.0, y, 0.0 }, Orientation = new[] { 0.0, oy, 0.0 }, Size = 1 }; }
        private static CatalogPartSource Part(string name, string[] modules, string[] resources, params CatalogNodeSource[] nodes)
        {
            return new CatalogPartSource
            {
                Name = name, KspCategory = "Propulsion", Buildable = true, TechAvailable = true, ModelPurchased = true, ModuleNames = modules, ResourceNames = resources, StackNodes = nodes,
                AttachRules = new CatalogAttachRulesSource { Stack = true, AllowStack = true, Srf = false, AllowSrf = true }
            };
        }
    }

    /// <summary>
    /// The whole operation layer on fakes: a scriptable editor, memory files, the real execution authority, tracker, pump,
    /// runner and service. <see cref="Frame"/> mirrors one ControlAddon.Update: heartbeat, trust pump, then the runner.
    /// </summary>
    internal sealed class OperationRig
    {
        public const string TwoStageGraph = "{\"name\":\"Probe One\",\"facility\":\"VAB\",\"root\":\"pod\",\"parts\":[{\"id\":\"pod\",\"part\":\"mk1pod.v2\"},"
            + "{\"id\":\"tank\",\"part\":\"fuelTankSmall\",\"parent\":\"pod\",\"parentNode\":\"bottom\",\"node\":\"top\"},"
            + "{\"id\":\"engine\",\"part\":\"liquidEngine.v2\",\"parent\":\"tank\",\"parentNode\":\"bottom\",\"node\":\"top\"}]}";

        public readonly FakeClock Clock = new FakeClock();
        public readonly EditorFake Port;
        public readonly MemoryFiles Files = new MemoryFiles();
        public readonly FakeCatalogReader Reader = new FakeCatalogReader();
        public readonly MemorySuspensionStore Store = new MemorySuspensionStore();
        public readonly ExecutionAuthority Authority;
        public readonly EditorRevisionTracker Tracker;
        public readonly FakeContextSource Context;
        public readonly ControlPump Pump;
        public readonly OperationJobs Jobs = new OperationJobs();
        public readonly EditorOperationRunner Runner;
        public readonly EditorOperationService Service;
        public readonly EditorObservationService Observation;
        public readonly Pure.CraftPaths Paths;
        public readonly RunnerOptions Options = new RunnerOptions();
        public string Lease;
        public DateTime Utc = AuthorityHelpers.Utc0;
        public int SnapshotCounter;
        public int Frames;
        public string KspRoot = Path.Combine(Path.GetTempPath(), "kc-fake-ksp");
        public bool Heartbeats = true;

        public OperationRig(bool lease = true, string policy = "snapshot_then_replace", string[] operations = null, string saveFolder = "TestSave")
        {
            Port = new EditorFake(Clock) { Files = Files };
            Authority = new ExecutionAuthority(() => Clock.Milliseconds, GrantMapping.KnownEffects, 2000, Store, () => Utc);
            Tracker = new EditorRevisionTracker(Port, new AuthorityTakeoverSink(Authority), () => Clock.Milliseconds, () => Clock.CostMilliseconds);
            Port.Raise = Tracker.OnEvent;
            Context = new FakeContextSource(() => Tracker, () => Port);
            Pump = new ControlPump(Authority, null, Context, Tracker);
            Paths = new Pure.CraftPaths(KspRoot, saveFolder, p => Files.IsReparsePoint(p));
            Runner = new EditorOperationRunner(Port, Tracker, Authority, Context, Files, () => Paths, Jobs, () => Clock.Milliseconds, () => "epoch1", () => Utc,
                () => "snap" + (++SnapshotCounter).ToString("D6"), Options);
            Service = new EditorOperationService(Port, Tracker, Authority, Runner, Jobs, () => Reader, () => Paths, Files, () => "epoch1", () => Utc, () => "snap" + (++SnapshotCounter).ToString("D6"));
            Observation = new EditorObservationService(Port, Tracker, () => "epoch1") { Operations = Service };
            // Adopt the baseline, publish the context, then provision a grant and take the lease the way the loopback worker would.
            Frame(0); Frame(300); Frame(300);
            var status = AuthorityHelpers.ValidStatus(); status.UnsavedCraftPolicy = policy;
            Authority.UpdateContext(Context.CurrentContext(), Context.CurrentBinding(), status);
            if (operations == null) Authority.ProvisionGrant(AuthorityHelpers.Grant(1, "grant", 1000));
            else Authority.ProvisionGrant(new TrustedExecutionGrant("grant", 1, AuthorityHelpers.Bind(), AuthorityHelpers.Utc0.AddHours(1000),
                operations.Select(o => new EffectPermission(o, (o.StartsWith("craft.") ? "ships:" : "editor:") + "VAB")), new[] { "editor:VAB" }));
            if (lease) AcquireLease();
        }

        /// <summary>The loopback worker's control.acquire: the tracker sees the lease a frame later and re-baselines silently.</summary>
        public void AcquireLease() { Lease = Authority.AcquireLease(300000, "build probe"); Frame(16); }

        /// <summary>One frame: the lease keeper's heartbeat, the trust pump (tracker, context, tick), then the operation runner.</summary>
        public void Frame(long milliseconds = 16)
        {
            Clock.Milliseconds += milliseconds; Frames++;
            if (Lease != null && Heartbeats) Authority.Heartbeat(Lease);
            Pump.Update();
            Runner.Update();
        }

        public void Run(int frames, long milliseconds = 16) { for (var i = 0; i < frames; i++) Frame(milliseconds); }

        /// <summary>Frames until the current job is terminal. Fails the test run if it never ends.</summary>
        public OperationJob RunToEnd(long milliseconds = 50, int maxFrames = 4000)
        {
            var job = Runner.Current;
            for (var i = 0; i < maxFrames && !job.Terminal; i++) Frame(milliseconds);
            if (!job.Terminal) throw new InvalidOperationException("the job never reached a terminal state; phase " + job.Phase);
            return job;
        }

        public string Token() { var result = Observation.State(); if (result.Reason != null) throw new InvalidOperationException(result.Reason); return (string)result.Data["editorRevision"]; }

        public string PlanHash(string graph)
        {
            var plan = ApplyPlanner.Prepare(graph, Reader, Port.Header, Port.ReadUi(), () => 1, "VAB");
            if (!plan.Ok) throw new InvalidOperationException(plan.Reason + " " + plan.Detail + " " + string.Join(";", plan.Issues.Select(i => i.ToString())));
            return plan.PlanHash;
        }

        public BridgeRequest ApplyRequest(string graph = TwoStageGraph, string requestId = "apply-0001", string token = null, string planHash = null, string lease = null)
        {
            return new BridgeRequest
            {
                RequestId = "wire-" + requestId, Operation = EditorOperations.ApplyCraft, LeaseId = lease ?? Lease,
                Arguments = new JObject { ["requestId"] = requestId, ["graph"] = graph, ["expectedPlanHash"] = planHash ?? PlanHash(graph), ["expectedRevision"] = token ?? Token() }
            };
        }

        public BridgeRequest RestoreRequest(string snapshotId, string requestId = "restore-0001", string token = null, string lease = null)
        {
            return new BridgeRequest
            {
                RequestId = "wire-" + requestId, Operation = EditorOperations.RestoreSnapshot, LeaseId = lease ?? Lease,
                Arguments = new JObject { ["requestId"] = requestId, ["snapshotId"] = snapshotId, ["expectedRevision"] = token ?? Token() }
            };
        }

        public BridgeRequest SaveRequest(string fileName = "Probe Saved", string requestId = "save-00001", string token = null, string replace = null, string lease = null)
        {
            var args = new JObject { ["requestId"] = requestId, ["fileName"] = fileName, ["expectedRevision"] = token ?? Token() };
            if (replace != null) args["replaceExpectedSha256"] = replace;
            return new BridgeRequest { RequestId = "wire-" + requestId, Operation = EditorOperations.SaveCraft, LeaseId = lease ?? Lease, Arguments = args };
        }

        public BridgeResponse Save(string fileName = "Probe Saved", string requestId = "save-00001", string replace = null) { return Service.Handle(SaveRequest(fileName, requestId, null, replace)); }

        /// <summary>The ship file path a save of this base name would write.</summary>
        public string ShipFile(string name) { return Paths.ResolveNewShip("VAB", name).FullPath; }

        public BridgeRequest StatusRequest(string requestId)
        {
            return new BridgeRequest { RequestId = "wire-status", Operation = EditorOperations.OperationStatus, Arguments = new JObject { ["requestId"] = requestId } };
        }

        public BridgeResponse Apply(string graph = TwoStageGraph, string requestId = "apply-0001") { return Service.Handle(ApplyRequest(graph, requestId)); }

        /// <summary>The thumbnail KSP would write for a ship or file stem.</summary>
        public string Thumb(string stem) { return Paths.ThumbnailFile("VAB", stem); }
    }
}
