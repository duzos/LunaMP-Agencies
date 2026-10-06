using Lidgren.Network;
using LmpCommon;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Base;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Kerbal;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Data.PlayerConnection;
using LmpCommon.Locks;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

namespace HeadlessTest.Infrastructure;

internal abstract record BotSnapshot(int Generation);
internal sealed record StatusSnapshot(int Generation, NetConnectionStatus Status, string Reason) : BotSnapshot(Generation);
internal sealed record HandshakeSnapshot(int Generation, HandshakeReply Response, string Reason) : BotSnapshot(Generation);
internal sealed record AgencySnapshot(Guid Id, string Name, bool IsSolo, IReadOnlyList<string> Members);
internal sealed record SiteAssignmentSnapshot(string SiteId, Guid AgencyId);
internal sealed record AgencySyncSnapshot(int Generation, Guid MyAgencyId, IReadOnlyList<AgencySnapshot> Agencies,
    bool LaunchSitesReady, long LaunchSitesRevision, IReadOnlyList<SiteAssignmentSnapshot> LaunchSites) : BotSnapshot(Generation);
internal sealed record AgencyUpsertSnapshot(int Generation, AgencySnapshot Agency) : BotSnapshot(Generation);
internal sealed record AgencyReplySnapshot(int Generation, bool Success, string Message) : BotSnapshot(Generation);
internal sealed record JoinRequestSnapshot(int Generation, Guid AgencyId, string PlayerIdentity) : BotSnapshot(Generation);
internal sealed record ChatSnapshot(int Generation, string From, string Text, ChatChannel Channel, Guid AgencyId) : BotSnapshot(Generation);

internal sealed record KerbalSnapshot(string Name, string ConfigNodeText);
internal sealed record KerbalRosterSnapshot(int Generation, IReadOnlyList<KerbalSnapshot> Kerbals) : BotSnapshot(Generation);
internal sealed record KerbalProtoSnapshot(int Generation, KerbalSnapshot Kerbal) : BotSnapshot(Generation);
internal sealed record ControlSnapshot(int Generation, Guid VesselId, string Player, bool Granted, string Reason) : BotSnapshot(Generation);
internal sealed record OwnershipResultSnapshot(int Generation, Guid RequestId, Guid VesselId, bool Success, string Reason) : BotSnapshot(Generation);
internal sealed record DockStatusSnapshot(int Generation, Guid RequestId, Guid SourceVesselId, Guid TargetVesselId,
    Guid RequesterAgencyId, DockConsentStatus Status, long ExpiresUtcTicks, string Reason, Guid OperationId, Guid GrantId) : BotSnapshot(Generation);
internal sealed record VesselProtoSnapshot(int Generation, Guid VesselId, string Text) : BotSnapshot(Generation);
internal sealed record VesselOwnerSnapshot(Guid VesselId, Guid OwnerAgencyId, IReadOnlyList<Guid> CoOwners, VesselDockingPolicy Policy);
internal sealed record OwnershipMapSnapshot(int Generation, bool Ready, long Revision, IReadOnlyList<VesselOwnerSnapshot> Records) : BotSnapshot(Generation);
internal sealed record PlayerLeftSnapshot(int Generation, string Player) : BotSnapshot(Generation);

/// <summary>Real protocol client. All retained state is copied before pooled messages are recycled.</summary>
internal sealed class BotClient : IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private readonly object _gate = new();
    private readonly object _factoryGate = new();
    private readonly ClientMessageFactory _outgoing = new();
    private readonly ServerMessageFactory _incoming = new();
    private readonly List<BotSnapshot> _pending = new();
    private readonly List<ChatSnapshot> _chatHistory = new();
    private readonly List<KerbalProtoSnapshot> _kerbalHistory = new();
    private readonly Queue<string> _transcript = new();
    private TaskCompletionSource<bool> _changed = NewSignal();
    private Exception _fault;
    private NetClient _peer;
    private CancellationTokenSource _pumpCancellation;
    private Task _pump;
    private Func<string, bool> _expectedDisconnectReason;
    private int _generation;

    public string Name { get; }
    public string Identity { get; }
    public int Generation => _generation;
    public string Transcript { get { lock (_gate) return string.Join(Environment.NewLine, _transcript); } }
    public IReadOnlyList<ChatSnapshot> ChatSnapshots { get { lock (_gate) { ThrowIfFaulted(); return _chatHistory.ToArray(); } } }
    public IReadOnlyList<KerbalProtoSnapshot> KerbalProtos { get { lock (_gate) { ThrowIfFaulted(); return _kerbalHistory.ToArray(); } } }
    public BotClient(string name, string identity) { Name = name; Identity = identity; }
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<AgencySyncSnapshot> ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        await StopPeerAsync();
        lock (_gate)
        {
            _generation++;
            _fault = null;
            _expectedDisconnectReason = null;
            // Prior generations remain in the transcript/history but cannot satisfy a new wait.
            _pending.Clear();
        }
        var generation = _generation;
        var configuration = new NetPeerConfiguration("LMP")
        {
            LocalAddress = IPAddress.Loopback,
            AutoFlushSendQueue = true,
            ConnectionTimeout = 15,
            PingInterval = 1
        };
        var peer = _peer = new NetClient(configuration);
        _pumpCancellation = new CancellationTokenSource();
        peer.Start();
        _pump = Task.Run(() => ReceiveAsync(peer, generation, _pumpCancellation.Token));
        var hail = peer.CreateMessage();
        hail.Write(string.Empty);
        peer.Connect(new IPEndPoint(IPAddress.Loopback, port), hail);
        await WaitForAsync<StatusSnapshot>(s => s.Status == NetConnectionStatus.Connected, cancellationToken);
        Send<HandshakeCliMsg, HandshakeRequestMsgData>(d =>
        {
            d.PlayerName = Name;
            d.UniqueIdentifier = Identity;
            d.KspVersion = "1.12.5";
        });
        var handshake = await WaitForAsync<HandshakeSnapshot>(_ => true, cancellationToken);
        if (handshake.Response != HandshakeReply.HandshookSuccessfully)
            throw new InvalidOperationException($"{Name} handshake rejected: {handshake.Response}: {handshake.Reason}");
        var sync = await WaitForAsync<AgencySyncSnapshot>(_ => true, cancellationToken);
        if (sync.MyAgencyId == Guid.Empty) throw new InvalidOperationException($"{Name} received empty agency membership");
        return sync;
    }

    public void ArmExpectedDisconnect(Func<string, bool> reasonMatches)
    {
        ArgumentNullException.ThrowIfNull(reasonMatches);
        lock (_gate) { ThrowIfFaulted(); _expectedDisconnectReason = reasonMatches; }
    }

    public Task<StatusSnapshot> WaitForDisconnectAsync(CancellationToken cancellationToken = default)
        => WaitForAsync<StatusSnapshot>(s => s.Status == NetConnectionStatus.Disconnected, cancellationToken);

    public void RequestKerbals() => Send<KerbalCliMsg, KerbalsRequestMsgData>(_ => { });
    public void RequestVessels() => Send<VesselCliMsg, VesselSyncMsgData>(d => { d.VesselIds = Array.Empty<Guid>(); d.VesselsCount = 0; });
    public void AcquireControl(Guid vessel, bool force = false) => Send<LockCliMsg, LockAcquireMsgData>(d =>
    {
        d.Lock = new LockDefinition(LockType.Control, Name, vessel);
        d.Force = force;
    });
    public void UploadVessel(Guid vessel, string text) => Send<VesselCliMsg, VesselProtoMsgData>(d =>
    {
        d.VesselId = vessel;
        d.Data = Encoding.UTF8.GetBytes(text);
        d.NumBytes = d.Data.Length;
        d.ForceReload = false;
        d.Reason = "Headless ownership fixture";
    });
    public Guid OwnershipCommand(VesselOwnershipOperation operation, Guid vessel, Guid target = default, VesselDockingPolicy policy = VesselDockingPolicy.Nobody)
    {
        var request = Guid.NewGuid();
        Send<AgencyCliMsg, AgencyVesselOwnershipCommandMsgData>(d =>
        {
            d.RequestId = request; d.VesselId = vessel; d.TargetAgencyId = target; d.Operation = operation; d.DockingPolicy = policy;
        });
        return request;
    }
    public Guid RequestDock(Guid source, Guid target)
    {
        var request = Guid.NewGuid();
        Send<AgencyCliMsg, AgencyDockRequestMsgData>(d => { d.RequestId = request; d.SourceVesselId = source; d.TargetVesselId = target; });
        return request;
    }
    public void AnswerDock(Guid request, bool accept) => Send<AgencyCliMsg, AgencyDockResponseMsgData>(d => { d.RequestId = request; d.Accept = accept; });
    public void CompleteDock(Guid operation, Guid grant, Guid dominant, uint dominantPart, Guid weak, uint weakPart, string merged)
        => Send<VesselCliMsg, VesselCoupleMsgData>(d =>
        {
            d.OperationId = operation; d.GrantId = grant; d.VesselId = dominant; d.PartFlightId = dominantPart;
            d.CoupledVesselId = weak; d.CoupledPartFlightId = weakPart; d.Trigger = (int)CoupleTrigger.DockingNode;
            d.SubspaceId = 0; d.MergedVesselData = Encoding.UTF8.GetBytes(merged);
        });
    public void SendAdmin(string password, AgencyAdminOp operation, Guid agency, string argument)
        => Send<AgencyCliMsg, AgencyAdminOpMsgData>(data =>
        {
            data.AdminPassword = password;
            data.Op = operation;
            data.TargetAgencyId = agency;
            data.StringArg = argument;
            data.NumericArg = 0;
        });
    public void SendKerbal(string name, string configNodeText) => Send<KerbalCliMsg, KerbalProtoMsgData>(d =>
    {
        var bytes = Encoding.UTF8.GetBytes(configNodeText);
        d.Kerbal = new KerbalInfo { KerbalName = name, KerbalData = bytes, NumBytes = bytes.Length };
    });

    public async Task DisconnectForReconnectAsync(CancellationToken cancellationToken = default)
    {
        const string reason = "Headless reconnect";
        ArmExpectedDisconnect(received => received == reason);
        _peer.Disconnect(reason);
        await WaitForDisconnectAsync(cancellationToken);
    }

    public void CreateAgency(string name) => Send<AgencyCliMsg, AgencyCreateMsgData>(d => d.Name = name);
    public void RequestJoin(Guid agency) => Send<AgencyCliMsg, AgencyJoinRequestMsgData>(d => d.AgencyId = agency);
    public void ApproveJoin(Guid agency, string playerIdentity) => Send<AgencyCliMsg, AgencyApproveJoinMsgData>(d =>
    {
        d.AgencyId = agency;
        d.PlayerUniqueId = playerIdentity;
    });
    public void SendChat(string text, ChatChannel channel, Guid agency) => Send<ChatCliMsg, ChatMsgData>(d =>
    {
        d.From = Name;
        d.Text = text;
        d.Relay = true;
        d.Channel = channel;
        d.AgencyId = agency;
    });

    private void Send<TMessage, TData>(Action<TData> fill)
        where TMessage : class, IMessageBase where TData : class, IMessageData
    {
        lock (_gate) ThrowIfFaulted();
        lock (_factoryGate)
        {
            var message = _outgoing.CreateNew<TMessage, TData>();
            try
            {
                fill((TData)message.Data);
                ((MessageData)message.Data).MajorVersion = LmpVersioning.MajorVersion;
                ((MessageData)message.Data).MinorVersion = LmpVersioning.MinorVersion;
                ((MessageData)message.Data).BuildVersion = LmpVersioning.BuildVersion;
                message.Data.ReceiveTime = 0;
                message.Data.SentTime = DateTime.UtcNow.Ticks;
                var packet = _peer.CreateMessage(message.GetMessageSize());
                message.Serialize(packet);
                var result = _peer.SendMessage(packet, message.NetDeliveryMethod, message.Channel);
                Record($"send generation={_generation} type={typeof(TData).Name} channel={message.Channel} result={result}");
                if (result == NetSendResult.FailedNotConnected || result == NetSendResult.Dropped)
                    throw new InvalidOperationException($"{Name} send failed: {result}");
            }
            finally { message.Recycle(); }
        }
    }

    public async Task<T> WaitForAsync<T>(Func<T, bool> predicate, CancellationToken cancellationToken = default) where T : BotSnapshot
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        var generation = _generation;
        try
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    ThrowIfFaulted();
                    var index = _pending.FindIndex(item => item.Generation == generation && item is T typed && predicate(typed));
                    if (index >= 0)
                    {
                        var found = (T)_pending[index];
                        _pending.RemoveAt(index);
                        return found;
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{Name} generation {generation}: waiting for {typeof(T).Name}.{Environment.NewLine}{Transcript}");
        }
    }

    private async Task ReceiveAsync(NetClient peer, int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (peer.ReadMessage(out var packet))
                {
                    try
                    {
                        if (packet.MessageType == NetIncomingMessageType.StatusChanged)
                        {
                            var status = (NetConnectionStatus)packet.ReadByte();
                            var reason = packet.ReadString();
                            lock (_gate)
                            {
                                if (status == NetConnectionStatus.Disconnected && !cancellationToken.IsCancellationRequested)
                                {
                                    if (_expectedDisconnectReason == null || !_expectedDisconnectReason(reason))
                                        throw new InvalidOperationException($"Unexpected {Name} disconnect: {reason}");
                                    _expectedDisconnectReason = null;
                                }
                            }
                            Publish(new StatusSnapshot(generation, status, reason));
                        }
                        else if (packet.MessageType == NetIncomingMessageType.Data)
                        {
                            lock (_factoryGate)
                            {
                                var message = _incoming.Deserialize(packet, DateTime.UtcNow.Ticks);
                                try
                                {
                                    if (message.VersionMismatch) throw new InvalidOperationException($"{Name} protocol version mismatch");
                                    var snapshot = Snapshot(message.Data, generation);
                                    if (snapshot != null) Publish(snapshot);
                                }
                                finally { message.Recycle(); }
                            }
                        }
                        else if (packet.MessageType == NetIncomingMessageType.ErrorMessage)
                            throw new InvalidOperationException($"{Name} transport error: {packet.ReadString()}");
                    }
                    finally { peer.Recycle(packet); }
                }
                await Task.Delay(5, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) { _fault = error; Pulse(); }
            Record($"failure {error}");
        }
    }

    private static AgencySnapshot CopyAgency(AgencyInfo agency)
        => new(agency.Id, agency.Name, agency.IsSolo, Array.AsReadOnly(agency.MemberUniqueIds.ToArray()));
    private static VesselOwnerSnapshot CopyOwnership(VesselOwnershipRecord record)
        => new(record.VesselId, record.OwnerAgencyId, Array.AsReadOnly(record.CoOwnerAgencyIds.ToArray()), record.DockingPolicy);
    private static KerbalSnapshot CopyKerbal(KerbalInfo kerbal)
        => new(kerbal.KerbalName, Encoding.UTF8.GetString(kerbal.KerbalData, 0, kerbal.NumBytes));
    private static IReadOnlyList<KerbalSnapshot> CopyRoster(KerbalReplyMsgData roster)
    {
        if (roster.KerbalsCount < 0 || roster.KerbalsCount > roster.Kerbals.Length)
            throw new InvalidOperationException("Invalid kerbal reply count");
        // Factory arrays retain capacity across uses; only Count entries belong to this reply.
        return Array.AsReadOnly(roster.Kerbals.Take(roster.KerbalsCount).Select(CopyKerbal).ToArray());
    }
    private static BotSnapshot Snapshot(IMessageData data, int generation) => data switch
    {
        PlayerConnectionLeaveMsgData d => new PlayerLeftSnapshot(generation, d.PlayerName),
        LockAcquireMsgData d when d.Lock.Type == LockType.Control => new ControlSnapshot(generation, d.Lock.VesselId, d.Lock.PlayerName, true, string.Empty),
        LockAcquireDeniedMsgData d when d.Lock.Type == LockType.Control => new ControlSnapshot(generation, d.Lock.VesselId, d.Lock.PlayerName, false, d.Reason),
        LockReleaseMsgData d when d.Lock.Type == LockType.Control => new ControlSnapshot(generation, d.Lock.VesselId, d.Lock.PlayerName, false, "released"),
        AgencyVesselOwnershipResultMsgData d => new OwnershipResultSnapshot(generation, d.RequestId, d.VesselId, d.Success, d.Reason),
        AgencyDockStatusMsgData d => new DockStatusSnapshot(generation, d.RequestId, d.SourceVesselId, d.TargetVesselId, d.RequesterAgencyId, d.Status, d.ExpiresUtcTicks, d.Reason, d.OperationId, d.GrantId),
        AgencyVesselMapSyncMsgData d => new OwnershipMapSnapshot(generation, d.OwnershipSnapshotPresent, d.OwnershipRevision, Array.AsReadOnly(d.OwnershipRecords.Select(CopyOwnership).ToArray())),
        AgencyVesselMapEntryMsgData d => new OwnershipMapSnapshot(generation, d.OwnershipSnapshotPresent, d.OwnershipRevision, Array.AsReadOnly(d.OwnershipRecord == null ? Array.Empty<VesselOwnerSnapshot>() : new[] { CopyOwnership(d.OwnershipRecord) })),
        VesselProtoMsgData d => new VesselProtoSnapshot(generation, d.VesselId, Encoding.UTF8.GetString(d.Data, 0, d.NumBytes)),
        KerbalReplyMsgData d => new KerbalRosterSnapshot(generation, CopyRoster(d)),
        KerbalProtoMsgData d => new KerbalProtoSnapshot(generation, CopyKerbal(d.Kerbal)),
        HandshakeReplyMsgData d => new HandshakeSnapshot(generation, d.Response, d.Reason),
        AgencySyncAllMsgData d => new AgencySyncSnapshot(generation, d.MyAgencyId, Array.AsReadOnly(d.Agencies.Select(CopyAgency).ToArray()),
            d.LaunchSitesSnapshotPresent, d.LaunchSitesRevision,
            Array.AsReadOnly(d.LaunchSites.Select(site => new SiteAssignmentSnapshot(site.SiteId, site.AgencyId)).ToArray())),
        AgencyUpsertMsgData d => new AgencyUpsertSnapshot(generation, CopyAgency(d.Agency)),
        AgencyReplyMsgData d => new AgencyReplySnapshot(generation, d.Success, d.Message),
        AgencyJoinRequestPostedMsgData d => new JoinRequestSnapshot(generation, d.Request.AgencyId, d.Request.PlayerUniqueId),
        ChatMsgData d => new ChatSnapshot(generation, d.From, d.Text, d.Channel, d.AgencyId),
        _ => null
    };

    private void Publish(BotSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_pending.Count >= 4096 || _chatHistory.Count >= 4096 || _kerbalHistory.Count >= 4096)
                throw new InvalidOperationException("Bot snapshot capacity exceeded; refusing to lose negative assertion evidence");
            _pending.Add(snapshot);
            if (snapshot is ChatSnapshot chat) _chatHistory.Add(chat);
            if (snapshot is KerbalProtoSnapshot kerbal) _kerbalHistory.Add(kerbal);
            Record($"receive {snapshot}");
            Pulse();
        }
    }
    private void Pulse() { var signal = _changed; _changed = NewSignal(); signal.TrySetResult(true); }
    private void ThrowIfFaulted() { if (_fault != null) throw new InvalidOperationException($"{Name} receive pump failed", _fault); }
    private void Record(string line)
    {
        lock (_gate)
        {
            while (_transcript.Count >= 1000) _transcript.Dequeue();
            _transcript.Enqueue($"{DateTime.UtcNow:O} {Name}: {line}");
        }
    }

    private async Task StopPeerAsync()
    {
        if (_peer == null) return;
        var errors = new List<Exception>();
        const string reason = "Headless fixture cleanup";
        try
        {
            // Lidgren's peer shutdown resets reliable channels before its final ACK-processing
            // heartbeat. Let the normal loop remove the connection before shutting down.
            if (_peer.Status == NetPeerStatus.Running)
            {
                lock (_gate) _expectedDisconnectReason = received => received == reason;
                _peer.Disconnect(reason);
                using var disconnectDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (_peer.ConnectionsCount != 0)
                    await Task.Delay(10, disconnectDeadline.Token);
            }
        }
        catch (OperationCanceledException error)
        {
            errors.Add(new TimeoutException($"{Name}: connection removal timed out during cleanup", error));
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            // Pump cleanup must still run when disconnect fails or times out. Keep each step
            // independent so a cancellation/await failure cannot skip token disposal.
            try { if (_pumpCancellation != null) await _pumpCancellation.CancelAsync(); }
            catch (Exception error) { errors.Add(error); }
            try { if (_pump != null) await _pump.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                _pumpCancellation?.Dispose();
                _pumpCancellation = null;
                if (_pump?.IsCompleted != false) _pump = null;
            }
        }

        // Never fall back to the unsafe active-connection Shutdown path. Keep the peer
        // reference if removal failed, allowing a later DisposeAsync attempt to finish it.
        if (_peer.ConnectionsCount == 0)
        {
            try
            {
                _peer.Shutdown(reason);
                using var shutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (_peer.Status != NetPeerStatus.NotRunning)
                    await Task.Delay(10, shutdownDeadline.Token);
            }
            catch (OperationCanceledException error)
            {
                errors.Add(new TimeoutException($"{Name}: peer shutdown timed out during cleanup", error));
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (_peer.Status == NetPeerStatus.NotRunning)
            _peer = null;
        else
        {
            var retained = $"{Name}: transport retained for cleanup retry; status={_peer.Status} connections={_peer.ConnectionsCount}. Active connections cannot be forcibly shut down safely.";
            Record(retained);
            errors.Add(new InvalidOperationException(retained));
        }
        if (errors.Count != 0) throw new AggregateException($"{Name} cleanup failed", errors);
    }
    public async ValueTask DisposeAsync()
    {
        await StopPeerAsync();
        lock (_gate) ThrowIfFaulted();
    }
}
