using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.ShareFunds;
using LmpClient.Systems.ShareScience;

namespace LmpClient.Systems.Agency
{
    public static partial class ToolingClient
    {
        private static readonly ConcurrentQueue<EconomySnapshot> snapshots = new ConcurrentQueue<EconomySnapshot>();
        private static readonly ConcurrentQueue<EconomyResult> results = new ConcurrentQueue<EconomyResult>();
        private static readonly object stateLock = new object();
        private static EconomySnapshot snapshot;
        private static long revision = -1, nextSequence;
        private static Guid economySession;
        private static readonly Dictionary<uint, Tuple<Guid,string>> evaParents = new Dictionary<uint, Tuple<Guid,string>>();
        private static DateTime nextQuote;
        private static string editorQuoteHash;
        private static PendingLaunch pending;
        private static bool resumeLaunch, resumeRevert;
        private static Guid pendingRevert;
        private static DateTime settlementDeadline;
        private static EditorFacility revertFacility;
        private static bool revertToLaunch;
        private static readonly HashSet<Guid> settling = new HashSet<Guid>();
        internal static void MarkRecovering(Guid id) { lock (stateLock) { settling.Add(id); if (settlementDeadline == default(DateTime)) settlementDeadline = DateTime.UtcNow.AddSeconds(45); } }
        internal static bool IsRecovering(Guid id) { lock (stateLock) return Enabled && settling.Contains(id); }
        private static readonly Dictionary<Guid, LaunchBinding> bindings = new Dictionary<Guid, LaunchBinding>();
        private const string LaunchLock = "LMP_ToolingLaunch";
        internal static bool ApplyingBalance;
        public static bool Enabled => MainSystem.NetworkState >= ClientState.Handshaking && SettingsSystem.ServerSettings.AgencyTooling;
        public static bool BalanceAuthorityEnabled => MainSystem.NetworkState >= ClientState.Handshaking && (SettingsSystem.ServerSettings.AgencyTooling || SettingsSystem.ServerSettings.AgencyTrade);
        public static bool BalanceReady { get { lock (stateLock) return BalanceAuthorityEnabled && snapshot != null && snapshot.Ready && snapshot.AgencyId == AgencySystem.Singleton.MyAgencyId; } }
        public static bool Ready { get { lock (stateLock) return Enabled && snapshot != null && snapshot.Ready && snapshot.AgencyId == AgencySystem.Singleton.MyAgencyId; } }
        public static string LatestStatus { get; private set; }
        public static ToolingQuote EditorQuote { get; private set; }
        private sealed class PendingLaunch
        {
            internal Guid Request, Launch, Token;
            internal string Path, FileHash, Flag, Site, ManifestHash, Crew;
            internal ToolingManifest Manifest;
            internal VesselCrewManifest CrewManifest;
            internal bool Started;
            internal GameScenes Scene;
            internal Dictionary<uint,int> CraftIndices;
            internal DateTime Deadline;
        }
        private sealed class LaunchBinding { internal Guid Launch, Token; internal Dictionary<uint,int> Indices; }
        internal static void Receive(EconomySnapshot value) { if (value != null) snapshots.Enqueue(value); }
        internal static void Receive(EconomyResult value) { if (value != null) results.Enqueue(value); }
        public static void RequestQuoteRefresh() { nextQuote = DateTime.MinValue; }
        internal static Guid Send(EconomyCommand command)
        {
            if (command.RequestId == Guid.Empty) command.RequestId = Guid.NewGuid();
            lock (stateLock)
            {
                if (economySession == Guid.Empty)
                {
                    LatestStatus = "Waiting for economy session.";
                    Diagnostics.PlaytestDiagnostics.Write("client.economy.command-refused", () => $"operation={command.Operation} reason=no-session");
                    return Guid.Empty;
                }
                command.SessionId = economySession; command.Sequence = ++nextSequence;
                var data = global::LmpClient.Network.NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyEconomyCommandMsgData>();
                data.Command = command;
                // Queue under the sequence lock; the general sender schedules tasks that could reorder commands.
                global::LmpClient.Network.NetworkSender.QueueOutgoingMessage(global::LmpClient.Network.NetworkMain.CliMsgFactory.CreateNew<LmpCommon.Message.Client.AgencyCliMsg>(data));
                Diagnostics.PlaytestDiagnostics.Write("client.economy.command", () => $"operation={command.Operation} request={command.RequestId} session={command.SessionId} sequence={command.Sequence} fundsDelta={command.FundsDelta} scienceDelta={command.ScienceDelta}");
                return command.RequestId;
            }
        }
        public static Guid PurchaseTooling()
        {
            try
            {
                var manifest = CurrentManifest();
                if (!Ready || EditorQuote == null || !EditorQuote.Success || editorQuoteHash != ToolingPolicy.ManifestHash(manifest))
                    throw new InvalidOperationException("Craft changed or tooling is still syncing. Review the updated quote.");
                LatestStatus = "Purchasing tooling...";
                return Send(new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
            }
            catch (Exception e) { LatestStatus = e.Message; return Guid.Empty; }
        }
        private static ToolingManifest CurrentManifest() => ToolingManifestBuilder.Build(EditorLogic.fetch?.ship, ShipConstruction.ShipManifest);
        private static ToolingQuote Quote(ToolingManifest manifest)
        {
            lock (stateLock)
                return ToolingPolicy.Quote(manifest, snapshot?.Designs ?? Array.Empty<ToolingDesign>(), SettingsSystem.ServerSettings.ToolingCostMultiplier,
                    SettingsSystem.ServerSettings.TooledLaunchMultiplier, SettingsSystem.ServerSettings.ToolingCombineMultiplier);
        }
        internal static ToolingQuote DisplayQuote(ShipConstruct ship, ShipTemplate template, VesselCrewManifest crew)
        {
            if (!Ready) return null;
            if (template != null) return Quote(ToolingManifestBuilder.FromConfig(template.config, crew));
            return ship == null ? null : Quote(ToolingManifestBuilder.Build(ship, crew));
        }
        internal static bool BeginLaunch(string path, string flag, string site, VesselCrewManifest crew)
        {
            if (!Enabled) return true;
            if (resumeLaunch) { resumeLaunch = false; return true; }
            if (pending != null) return false;
            try
            {
                if (!Ready) throw new InvalidOperationException("Waiting for agency economy.");
                var manifest = ToolingManifestBuilder.FromFile(path, crew);
                pending = new PendingLaunch { Request = Guid.NewGuid(), Launch = Guid.NewGuid(), Path = path, Flag = flag, Site = site,
                    FileHash = HashFile(path), Scene = HighLogic.LoadedScene, CraftIndices = CraftIndices(path), Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest), CrewManifest = crew,
                    Crew = CrewKey(crew), Deadline = DateTime.UtcNow.AddSeconds(45) };
                Send(new EconomyCommand { RequestId = pending.Request, Operation = EconomyOperation.PrepareLaunch, LaunchId = pending.Launch,
                    Manifest = manifest, ManifestHash = pending.ManifestHash });
                InputLockManager.SetControlLock(ControlTypes.EDITOR_LAUNCH, LaunchLock);
                LatestStatus = "Reserving launch funds...";
            }
            catch (Exception e) { LatestStatus = e.Message; pending = null; }
            return false;
        }
        private static Dictionary<uint,int> CraftIndices(string path)
        {
            var result = new Dictionary<uint,int>();
            var nodes = ConfigNode.Load(path).GetNodes("PART");
            for (var i = 0; i < nodes.Length; i++)
            {
                var key = nodes[i].GetValue("part");
                if (string.IsNullOrEmpty(key) || !uint.TryParse(key.Substring(key.LastIndexOf('_') + 1), out var id) || result.ContainsKey(id))
                    throw new InvalidOperationException("Craft part identifiers are invalid.");
                result.Add(id, i);
            }
            return result;
        }
        internal static void SceneChanged(GameScenes scene)
        {
            if (pending != null && !pending.Started) CancelLaunch();
            ApplyCachedBalance();
            RequestQuoteRefresh();
        }
        internal static void ApplyCachedBalance()
        {
            EconomySnapshot state; lock (stateLock) state = snapshot;
            if (!BalanceAuthorityEnabled || state == null || !state.Ready) return;
            ApplyingBalance = true;
            try
            {
                if (Funding.Instance) ShareFundsSystem.Singleton.SetFundsWithoutTriggeringEvent(state.Funds);
                if (ResearchAndDevelopment.Instance) ShareScienceSystem.Singleton.SetScienceWithoutTriggeringEvent((float)state.Science);
            }
            finally { ApplyingBalance = false; }
        }
        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }
        private static string CrewKey(VesselCrewManifest crew) => crew == null ? string.Empty : string.Join("|", crew.GetAllCrew(false).Select(c => c.name).OrderBy(n => n, StringComparer.Ordinal));
        internal static void BindLaunch(Vessel vessel, ShipConstruct ship)
        {
            if (!Enabled || pending == null || !pending.Started || !vessel) return;
            var indices = new Dictionary<uint,int>();
            foreach (var part in ship.parts) indices[part.flightID] = pending.CraftIndices.TryGetValue(part.craftID, out var index) ? index : -1;
            lock (stateLock) bindings[vessel.id] = new LaunchBinding { Launch = pending.Launch, Token = pending.Token, Indices = indices };
        }
        internal static void FillLaunchTail(ProtoVessel vessel, LmpCommon.Message.Data.Vessel.VesselProtoMsgData data)
        {
            data.EconomySplitOperationId = Guid.Empty; data.EconomySplitParentData = Array.Empty<byte>();
            data.EconomyParentVesselId = Guid.Empty; data.EconomyEvaCrew = null;
            data.EconomyLaunchId = Guid.Empty; data.EconomyLaunchToken = Guid.Empty; data.EconomyManifestIndices = Array.Empty<int>();
            if (!Enabled) return;
            lock (stateLock)
            {
                if (vessel.protoPartSnapshots.Count > 0 && evaParents.TryGetValue(vessel.protoPartSnapshots[0].flightID, out var eva))
                { data.EconomyParentVesselId = eva.Item1; data.EconomyEvaCrew = eva.Item2; }
                if (bindings.TryGetValue(vessel.vesselID, out var binding))
                {
                    data.EconomyLaunchId = binding.Launch; data.EconomyLaunchToken = binding.Token;
                    data.EconomyManifestIndices = vessel.protoPartSnapshots.Select(p => binding.Indices.TryGetValue(p.flightID, out var index) ? index : -1).ToArray();
                }
            }
        }
        internal static void BindEva(GameEvents.FromToAction<Part, Part> data)
        {
            if (!Enabled || !data.from || !data.to) return;
            var crew = data.to.protoModuleCrew.FirstOrDefault()?.name;
            if (string.IsNullOrEmpty(crew)) return;
            lock (stateLock) evaParents[data.to.flightID] = Tuple.Create(data.from.vessel.id, crew);
        }
        internal static bool BeforeRollout() => !Enabled;
        internal static float AffordableCost(ShipConstruct ship)
        {
            if (!Ready) return float.MaxValue;
            var quote = Quote(ToolingManifestBuilder.Build(ship, ShipConstruction.ShipManifest));
            return quote.Success && quote.LaunchCost <= float.MaxValue ? (float)quote.LaunchCost : float.MaxValue;
        }
        internal static float AffordableTemplateCost(ShipTemplate ship)
        {
            if (!Ready) return float.MaxValue;
            var quote = Quote(ToolingManifestBuilder.FromConfig(ship.config, ShipConstruction.ShipManifest));
            return quote.Success && quote.LaunchCost <= float.MaxValue ? (float)quote.LaunchCost : float.MaxValue;
        }
        internal static void SendDelta(double funds, double science)
        {
            if (!BalanceAuthorityEnabled || ApplyingBalance || funds == 0 && science == 0) return;
            Send(new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = funds, ScienceDelta = science });
        }
        internal static PaidVesselRecord PaidVessel(Guid id)
        {
            lock (stateLock) return snapshot?.Vessels?.FirstOrDefault(v => v.VesselId == id);
        }
        internal static bool BeginRevert(EditorFacility facility, bool toLaunch)
        {
            if (MainSystem.NetworkState >= ClientState.Connected && !SettingsSystem.ServerSettings.CanRevert)
            {
                resumeRevert = false;
                LatestStatus = "Reverting is disabled by the server.";
                Diagnostics.PlaytestDiagnostics.Write("client.revert.denied", () => $"toLaunch={toLaunch} reason=server-policy");
                return false;
            }
            if (!Enabled || resumeRevert) { resumeRevert = false; return true; }
            if (pendingRevert != Guid.Empty || !Ready || !FlightGlobals.ActiveVessel) return false;
            var paid = PaidVessel(FlightGlobals.ActiveVessel.id);
            var launches = paid?.Parts?.Select(p => p.LaunchId).Where(id => id != Guid.Empty).Distinct().ToArray();
            if (launches == null || launches.Length != 1) { LatestStatus = "This craft has no single eligible launch to revert."; return false; }
            revertFacility = facility; revertToLaunch = toLaunch;
            pendingRevert = Send(new EconomyCommand { Operation = toLaunch ? EconomyOperation.RevertLaunch : EconomyOperation.Revert,
                LaunchId = launches[0], VesselId = FlightGlobals.ActiveVessel.id });
            settlementDeadline = DateTime.UtcNow.AddSeconds(45);
            LatestStatus = "Checking revert eligibility..."; return false;
        }
        internal static Guid Recover(EconomyCommand command) => Send(command);
        internal static void Tick()
        {
            // The server sends the initial economy snapshot during handshake, before feature
            // settings arrive. Keep it queued until those flags can be evaluated reliably.
            if (MainSystem.NetworkState < ClientState.SettingsSynced) return;
            TickBoarding();
            if (splitting != null && DateTime.UtcNow > splitting.Deadline) { RecoveryDisconnect("Split confirmation timed out."); return; }
            while (snapshots.TryDequeue(out var state))
            {
                if (!BalanceAuthorityEnabled || state.AgencyId != AgencySystem.Singleton.MyAgencyId || state.Revision < revision) continue;
                lock (stateLock)
                {
                    if (economySession != state.SessionId) { economySession = state.SessionId; nextSequence = state.LastSequence; }
                    else nextSequence = Math.Max(nextSequence, state.LastSequence);
                    snapshot = state; revision = state.Revision;
                    TradeClient.Receive(state);
                }
                Diagnostics.PlaytestDiagnostics.Write("client.economy.snapshot", () => $"agency={state.AgencyId} ready={state.Ready} revision={state.Revision} session={state.SessionId} funds={state.Funds} science={state.Science}");
                ApplyingBalance = true;
                try
                {
                    if (Funding.Instance) ShareFundsSystem.Singleton.SetFundsWithoutTriggeringEvent(state.Funds);
                    if (ResearchAndDevelopment.Instance) ShareScienceSystem.Singleton.SetScienceWithoutTriggeringEvent((float)state.Science);
                }
                finally { ApplyingBalance = false; }
                RequestQuoteRefresh();
            }
            while (results.TryDequeue(out var result)) { TradeClient.HandleResult(result); Handle(result); }
            global::LmpClient.Systems.VesselRemoveSys.VesselRemoveMessageSender.FlushRetainedRemovals();
            TradeClient.Tick();
            if (settlementDeadline != default(DateTime) && DateTime.UtcNow > settlementDeadline)
            {
                settlementDeadline = default(DateTime);
                RecoveryDisconnect("Economy settlement timed out. Reconnect to reload authoritative state.");
                return;
            }
            if (pending != null && (!Enabled || DateTime.UtcNow > pending.Deadline || !pending.Started && HighLogic.LoadedScene != pending.Scene)) CancelLaunch();
            if (!Enabled || !HighLogic.LoadedSceneIsEditor || DateTime.UtcNow < nextQuote) return;
            nextQuote = DateTime.UtcNow.AddMilliseconds(500);
            try
            {
                var manifest = Ready && EditorLogic.fetch?.ship != null ? CurrentManifest() : null;
                EditorQuote = manifest == null ? null : Quote(manifest);
                editorQuoteHash = manifest == null ? null : ToolingPolicy.ManifestHash(manifest);
            }
            catch (Exception e) { EditorQuote = null; LatestStatus = e.Message; }
            LmpClient.Harmony.AgencyCostDisplay.Refresh();
        }
        private static void Handle(EconomyResult result)
        {
            if (BalanceAuthorityEnabled && result.RecoveryRequired) { RecoveryDisconnect(result.Reason); return; }
            if (!Enabled) return;
            LatestStatus = result.Reason;
            if (HandleBoarding(result) || HandleSplit(result)) return;
            if (result.Operation == EconomyOperation.Recover)
            {
                if (!result.Success) { RecoveryDisconnect(result.Reason); return; }
                lock (stateLock) { settling.Remove(result.VesselId); if (settling.Count == 0 && pendingRevert == Guid.Empty) settlementDeadline = default(DateTime); }
            }
            if (result.RecoveryRequired) { RecoveryDisconnect(result.Reason); return; }
            if (pending != null && result.Operation == EconomyOperation.RegisterLaunch && result.LaunchId == pending.Launch)
            {
                if (!result.Success) { RecoveryDisconnect(result.Reason); return; }
                lock (stateLock) bindings.Remove(result.VesselId);
                pending = null; InputLockManager.RemoveControlLock(LaunchLock); return;
            }
            if (result.RequestId == pendingRevert)
            {
                pendingRevert = Guid.Empty;
                lock (stateLock) { if (settling.Count == 0) settlementDeadline = default(DateTime); }
                if (result.Success)
                {
                    RecoveryDisconnect("Revert completed. Reconnect to load the authoritative saved state.");
                }
                return;
            }
            if (pending == null || result.RequestId != pending.Request || result.Operation != EconomyOperation.PrepareLaunch) return;
            if (!result.Success) { pending = null; InputLockManager.RemoveControlLock(LaunchLock); return; }
            pending.Token = result.LaunchToken;
            try
            {
                if (HighLogic.LoadedScene != pending.Scene || HashFile(pending.Path) != pending.FileHash || CrewKey(pending.CrewManifest) != pending.Crew ||
                    ToolingPolicy.ManifestHash(ToolingManifestBuilder.FromFile(pending.Path, pending.CrewManifest)) != pending.ManifestHash || result.ExpiresUtcTicks <= DateTime.UtcNow.Ticks)
                    throw new InvalidOperationException("Launch changed while awaiting confirmation.");
                pending.Started = true; pending.Deadline = new DateTime(result.ExpiresUtcTicks, DateTimeKind.Utc);
                resumeLaunch = true;
                FlightDriver.StartWithNewLaunch(pending.Path, pending.Flag, pending.Site, pending.CrewManifest);
                resumeLaunch = false;
            }
            catch (Exception e) { LatestStatus = e.Message; CancelLaunch(); }
        }
        private static void CancelLaunch()
        {
            var cancelled = pending; pending = null; resumeLaunch = false; InputLockManager.RemoveControlLock(LaunchLock);
            if (cancelled != null && Enabled) Send(new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = cancelled.Launch, LaunchToken = cancelled.Token });
            if (cancelled?.Started == true) RecoveryDisconnect("Launch registration failed. Reconnect to reload authoritative state.");
        }
        internal static void RecoveryDisconnect(string reason)
        {
            global::LmpClient.Network.NetworkConnection.Disconnect(reason ?? "Economy recovery required.");
            MainSystem.Singleton.ForceQuit = true;
        }
        internal static void Clear()
        {
            LmpClient.Harmony.AgencyCostDisplay.Clear();
            lock (stateLock) { snapshot = null; revision = -1; nextSequence = 0; economySession = Guid.Empty; bindings.Clear(); settling.Clear(); evaParents.Clear(); }
            while (snapshots.TryDequeue(out _)) { } while (results.TryDequeue(out _)) { }
            settlementDeadline = default(DateTime);
            boarding = null; InputLockManager.RemoveControlLock(BoardingLock);
            splitting = null; splitQueue.Clear(); splitBytes = 0; InputLockManager.RemoveControlLock(SplitLock);
            pending = null; pendingRevert = Guid.Empty; resumeLaunch = resumeRevert = false; EditorQuote = null; LatestStatus = null;
            InputLockManager.RemoveControlLock(LaunchLock);
        }
    }
}
