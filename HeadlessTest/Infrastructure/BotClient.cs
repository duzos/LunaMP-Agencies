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
using LmpCommon.Message.Interface;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessTest.Infrastructure;

internal abstract record BotSnapshot(int Generation);
internal sealed record StatusSnapshot(int Generation, NetConnectionStatus Status, string Reason) : BotSnapshot(Generation);
internal sealed record HandshakeSnapshot(int Generation, HandshakeReply Response, string Reason) : BotSnapshot(Generation);
internal sealed record AgencySnapshot(Guid Id, string Name, bool IsSolo, IReadOnlyList<string> Members);
internal sealed record AgencySyncSnapshot(int Generation, Guid MyAgencyId, IReadOnlyList<AgencySnapshot> Agencies) : BotSnapshot(Generation);
internal sealed record AgencyUpsertSnapshot(int Generation, AgencySnapshot Agency) : BotSnapshot(Generation);
internal sealed record AgencyReplySnapshot(int Generation, bool Success, string Message) : BotSnapshot(Generation);
internal sealed record JoinRequestSnapshot(int Generation, Guid AgencyId, string PlayerIdentity) : BotSnapshot(Generation);
internal sealed record ChatSnapshot(int Generation, string From, string Text, ChatChannel Channel, Guid AgencyId) : BotSnapshot(Generation);

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
    private static BotSnapshot Snapshot(IMessageData data, int generation) => data switch
    {
        HandshakeReplyMsgData d => new HandshakeSnapshot(generation, d.Response, d.Reason),
        AgencySyncAllMsgData d => new AgencySyncSnapshot(generation, d.MyAgencyId, Array.AsReadOnly(d.Agencies.Select(CopyAgency).ToArray())),
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
            if (_pending.Count >= 4096 || _chatHistory.Count >= 4096)
                throw new InvalidOperationException("Bot snapshot capacity exceeded; refusing to lose negative assertion evidence");
            _pending.Add(snapshot);
            if (snapshot is ChatSnapshot chat) _chatHistory.Add(chat);
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
        if (_pumpCancellation != null) await _pumpCancellation.CancelAsync();
        _peer.Shutdown("Headless fixture cleanup");
        if (_pump != null) await _pump.WaitAsync(TimeSpan.FromSeconds(5));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_peer.Status != NetPeerStatus.NotRunning) await Task.Delay(10, deadline.Token);
        _pumpCancellation?.Dispose();
        _pumpCancellation = null;
        _peer = null;
        _pump = null;
    }
    public async ValueTask DisposeAsync()
    {
        await StopPeerAsync();
        lock (_gate) ThrowIfFaulted();
    }
}
