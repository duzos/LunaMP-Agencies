using KspControl.Contracts;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;
public enum BeatOutcome { Alive, LeaseLost, TransportFailure }
public readonly record struct BeatResult(BeatOutcome Outcome,string? Reason=null);
public interface IBeatSender
{
 /// <summary>Sends one heartbeat on its own connection. Must honour cancellation.</summary>
 Task<BeatResult> BeatAsync(string leaseId,CancellationToken cancellationToken);
 Task ReleaseAsync(string leaseId,CancellationToken cancellationToken);
}
public sealed record LeaseKeeperStatus(string State,int ConsecutiveFailures,string? LeaseLostReason,long Beats,long SkippedBeats);
/// <summary>
/// Sends an inline control.heartbeat every second while a lease is tracked and the host process (stdio session) is alive.
/// Every beat uses its own connection, never shares a queue or semaphore with tool calls, times out after 800 ms,
/// and a beat still in flight is skipped rather than queued. The bridge watchdog stays authoritative.
/// </summary>
public sealed class LeaseKeeper : BackgroundService
{
 private readonly IBeatSender sender; private readonly int intervalMs,timeoutMs,threshold;
 private readonly object gate=new(); private string? leaseId; private string? lostReason; private int failures; private int inFlight; private long beats,skipped;
 private CancellationToken stopping;
 /// <summary>Raised with (leaseId, reason) when the bridge reports the lease is gone.</summary>
 public event Action<string,string>? LeaseLost;
 public LeaseKeeper(IBeatSender sender) : this(sender,ControlLimits.HeartbeatIntervalMilliseconds,ControlLimits.HeartbeatTimeoutMilliseconds,ControlLimits.HeartbeatFailureThreshold) { }
 public LeaseKeeper(IBeatSender sender,int intervalMilliseconds,int timeoutMilliseconds,int failureThreshold)
 { this.sender=sender; intervalMs=intervalMilliseconds; timeoutMs=timeoutMilliseconds; threshold=failureThreshold; }
 public int IntervalMilliseconds=>intervalMs; public int TimeoutMilliseconds=>timeoutMs;
 public void Track(string id) { lock(gate) { leaseId=id; failures=0; lostReason=null; } }
 public void Untrack(string? id=null) { lock(gate) { if(id==null||leaseId==id) leaseId=null; } }
 public string? TrackedLease { get { lock(gate) return leaseId; } }
 public LeaseKeeperStatus Status()
 {
  lock(gate) {
   string state=lostReason!=null ? "lease_lost" : leaseId==null ? "none" : failures>=threshold ? "lease_unhealthy" : "ok";
   return new LeaseKeeperStatus(state,failures,lostReason,Interlocked.Read(ref beats),Interlocked.Read(ref skipped));
  }
 }
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  stopping=stoppingToken;
  using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
  try { while(await timer.WaitForNextTickAsync(stoppingToken)) Tick(); } catch(OperationCanceledException) { }
 }
 public override async Task StopAsync(CancellationToken cancellationToken)
 {
  await base.StopAsync(cancellationToken);
  string? id; lock(gate) { id=leaseId; leaseId=null; }
  if(id==null) return;
  // Best effort so the bridge frees the lease at once instead of waiting for its watchdog.
  try { using var release=new CancellationTokenSource(timeoutMs); await sender.ReleaseAsync(id,release.Token); } catch { }
 }
 /// <summary>One scheduler step. Exposed for tests; the hosted loop calls it every interval.</summary>
 public void Tick()
 {
  string? id; lock(gate) id=leaseId;
  if(id==null) return;
  if(Interlocked.CompareExchange(ref inFlight,1,0)!=0) { Interlocked.Increment(ref skipped); return; }
  _=RunBeat(id);
 }
 /// <summary>Test hook: waits until no beat is in flight.</summary>
 public async Task WaitIdleAsync(int milliseconds=5000)
 { var end=Environment.TickCount64+milliseconds; while(Volatile.Read(ref inFlight)!=0 && Environment.TickCount64<end) await Task.Delay(5); }
 private async Task RunBeat(string id)
 {
  try
  {
   Interlocked.Increment(ref beats);
   using var cts=CancellationTokenSource.CreateLinkedTokenSource(stopping);
   BeatResult result;
   try
   {
    var beat=sender.BeatAsync(id,cts.Token);
    if(await Task.WhenAny(beat,Task.Delay(timeoutMs))==beat) result=await beat;
    else { cts.Cancel(); result=new BeatResult(BeatOutcome.TransportFailure,"timeout"); _=beat.ContinueWith(t=>_=t.Exception,TaskContinuationOptions.OnlyOnFaulted); }
   }
   catch(Exception) { result=new BeatResult(BeatOutcome.TransportFailure,"exception"); }
   Apply(id,result);
  }
  finally { Volatile.Write(ref inFlight,0); }
 }
 private void Apply(string id,BeatResult result)
 {
  string? lost=null;
  lock(gate)
  {
   if(leaseId!=id) return; // released or replaced while the beat was in flight
   switch(result.Outcome)
   {
    case BeatOutcome.Alive: failures=0; break;
    case BeatOutcome.LeaseLost: leaseId=null; lostReason=result.Reason??ControlReasons.LeaseInvalid; lost=lostReason; break;
    default: failures++; break;
   }
  }
  if(lost!=null) LeaseLost?.Invoke(id,lost);
 }
}
/// <summary>Heartbeat and release over the shared bridge client, one fresh connection each.</summary>
public sealed class BridgeBeatSender(BridgeClient bridge) : IBeatSender
{
 private static readonly HashSet<string> LeaseReasons=new(StringComparer.Ordinal) { ControlReasons.LeaseInvalid,ControlReasons.LeaseExpired,ControlReasons.AuthorityRevoked };
 public async Task<BeatResult> BeatAsync(string leaseId,CancellationToken cancellationToken)
 {
  var text=await bridge.ControlAsync(ControlOperations.Heartbeat,leaseId,null,TimeSpan.FromMilliseconds(ControlLimits.HeartbeatTimeoutMilliseconds),cancellationToken);
  var reply=JObject.Parse(text);
  if((string?)reply["Status"]=="completed") return new BeatResult(BeatOutcome.Alive);
  var reason=(string?)reply["ReasonCode"]??"unknown";
  return LeaseReasons.Contains(reason) ? new BeatResult(BeatOutcome.LeaseLost,reason) : new BeatResult(BeatOutcome.TransportFailure,reason);
 }
 public Task ReleaseAsync(string leaseId,CancellationToken cancellationToken)
  => bridge.ControlAsync(ControlOperations.Release,leaseId,null,TimeSpan.FromMilliseconds(ControlLimits.HeartbeatTimeoutMilliseconds),cancellationToken);
}
