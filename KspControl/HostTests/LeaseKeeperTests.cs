using System.Diagnostics;
using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class LeaseKeeperTests
{
 private sealed class FakeSender : IBeatSender
 {
  public Func<string,CancellationToken,Task<BeatResult>> OnBeat=(_,_)=>Task.FromResult(new BeatResult(BeatOutcome.Alive));
  public int Beats; public readonly List<string> Released=new();
  public Task<BeatResult> BeatAsync(string leaseId,CancellationToken cancellationToken) { Interlocked.Increment(ref Beats); return OnBeat(leaseId,cancellationToken); }
  public Task ReleaseAsync(string leaseId,CancellationToken cancellationToken) { lock(Released) Released.Add(leaseId); return Task.CompletedTask; }
 }
 private const string Lease="0123456789abcdef0123456789abcdef";

 [TestMethod] public void DefaultsMatchThePlan()
 {
  var keeper=new LeaseKeeper(new FakeSender());
  Assert.AreEqual(1000,keeper.IntervalMilliseconds); Assert.AreEqual(800,keeper.TimeoutMilliseconds);
  Assert.AreEqual(1000,ControlLimits.HeartbeatIntervalMilliseconds); Assert.AreEqual(800,ControlLimits.HeartbeatTimeoutMilliseconds); Assert.AreEqual(3,ControlLimits.HeartbeatFailureThreshold);
 }
 [TestMethod] public async Task NothingIsSentWithoutALease()
 { var sender=new FakeSender(); var keeper=new LeaseKeeper(sender); keeper.Tick(); await keeper.WaitIdleAsync(); Assert.AreEqual(0,sender.Beats); Assert.AreEqual("none",keeper.Status().State); }
 [TestMethod] public async Task SuccessKeepsItOkAndFailuresMarkLeaseUnhealthyAfterThree()
 {
  var sender=new FakeSender(); var keeper=new LeaseKeeper(sender); keeper.Track(Lease);
  keeper.Tick(); await keeper.WaitIdleAsync(); Assert.AreEqual("ok",keeper.Status().State);
  sender.OnBeat=(_,_)=>Task.FromResult(new BeatResult(BeatOutcome.TransportFailure,"bridge_unreachable"));
  for(int i=1;i<=3;i++) { keeper.Tick(); await keeper.WaitIdleAsync(); Assert.AreEqual(i,keeper.Status().ConsecutiveFailures); Assert.AreEqual(i<3?"ok":"lease_unhealthy",keeper.Status().State); }
  sender.OnBeat=(_,_)=>Task.FromResult(new BeatResult(BeatOutcome.Alive)); keeper.Tick(); await keeper.WaitIdleAsync();
  Assert.AreEqual("ok",keeper.Status().State); Assert.AreEqual(0,keeper.Status().ConsecutiveFailures);
 }
 [TestMethod] public async Task ABeatStillInFlightIsSkippedNeverQueued()
 {
  var sender=new FakeSender(); var gate=new TaskCompletionSource<BeatResult>(); sender.OnBeat=(_,_)=>gate.Task;
  var keeper=new LeaseKeeper(sender,1000,5000,3); keeper.Track(Lease);
  keeper.Tick(); keeper.Tick(); keeper.Tick(); keeper.Tick();
  Assert.AreEqual(1,sender.Beats); Assert.AreEqual(3,keeper.Status().SkippedBeats);
  gate.SetResult(new BeatResult(BeatOutcome.Alive)); await keeper.WaitIdleAsync();
  keeper.Tick(); await keeper.WaitIdleAsync(); Assert.AreEqual(2,sender.Beats);
 }
 [TestMethod] public async Task ABeatThatIgnoresCancellationStillTimesOutAndFreesTheSlot()
 {
  var sender=new FakeSender(); sender.OnBeat=(_,_)=>new TaskCompletionSource<BeatResult>().Task; // never completes, never honours the token
  var keeper=new LeaseKeeper(sender,1000,120,3); keeper.Track(Lease);
  var clock=Stopwatch.StartNew(); keeper.Tick(); await keeper.WaitIdleAsync(3000);
  Assert.IsTrue(clock.ElapsedMilliseconds<1500,"timeout must release the beat slot, took "+clock.ElapsedMilliseconds);
  Assert.AreEqual(1,keeper.Status().ConsecutiveFailures);
  keeper.Tick(); await keeper.WaitIdleAsync(3000); Assert.AreEqual(2,sender.Beats,"next interval sends a fresh beat");
 }
 [TestMethod] public async Task SenderExceptionsCountAsTransportFailures()
 {
  var sender=new FakeSender(); sender.OnBeat=(_,_)=>throw new InvalidOperationException("boom");
  var keeper=new LeaseKeeper(sender); keeper.Track(Lease); keeper.Tick(); await keeper.WaitIdleAsync(); Assert.AreEqual(1,keeper.Status().ConsecutiveFailures);
 }
 [TestMethod] public async Task LeaseLostStopsBeatingAndRaisesTheEvent()
 {
  var sender=new FakeSender(); sender.OnBeat=(_,_)=>Task.FromResult(new BeatResult(BeatOutcome.LeaseLost,ControlReasons.LeaseExpired));
  var keeper=new LeaseKeeper(sender); string? seen=null; keeper.LeaseLost+=(id,reason)=>seen=id+":"+reason; keeper.Track(Lease);
  keeper.Tick(); await keeper.WaitIdleAsync();
  Assert.AreEqual(Lease+":lease_expired",seen); Assert.AreEqual("lease_lost",keeper.Status().State); Assert.IsNull(keeper.TrackedLease);
  keeper.Tick(); await keeper.WaitIdleAsync(); Assert.AreEqual(1,sender.Beats);
 }
 [TestMethod] public async Task ReleasedLeaseDuringABeatIsNotRevivedByItsResult()
 {
  var sender=new FakeSender(); var gate=new TaskCompletionSource<BeatResult>(); sender.OnBeat=(_,_)=>gate.Task;
  var keeper=new LeaseKeeper(sender,1000,5000,3); keeper.Track(Lease); keeper.Tick(); keeper.Untrack(Lease);
  gate.SetResult(new BeatResult(BeatOutcome.TransportFailure)); await keeper.WaitIdleAsync(); Assert.AreEqual("none",keeper.Status().State); Assert.AreEqual(0,keeper.Status().ConsecutiveFailures);
 }
 [TestMethod] public async Task HostedLoopBeatsAtTheIntervalAndStopsOnShutdownReleasingTheLease()
 {
  var sender=new FakeSender(); var keeper=new LeaseKeeper(sender,25,500,3); keeper.Track(Lease);
  await keeper.StartAsync(CancellationToken.None);
  var deadline=Environment.TickCount64+3000; while(sender.Beats<3 && Environment.TickCount64<deadline) await Task.Delay(10);
  Assert.IsTrue(sender.Beats>=3,"beats "+sender.Beats);
  await keeper.StopAsync(CancellationToken.None); int after=sender.Beats; await Task.Delay(120);
  Assert.AreEqual(after,sender.Beats,"no beats after the stdio session ends");
  CollectionAssert.AreEqual(new[]{Lease},sender.Released); Assert.IsNull(keeper.TrackedLease);
 }

 // ---- over a real socket: the beat has its own connection ----

 [TestMethod] public async Task ABlockedToolCallConnectionNeverDelaysABeat()
 {
  var blocked=new TaskCompletionSource<BridgeResponse>();
  using var bridge=new FakeBridge(async r => r.Operation==ControlOperations.Heartbeat ? FakeBridge.Ok(new JObject { ["alive"]=true }) : await blocked.Task);
  var client=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(client));
  var toolCall=client.ReadAsync("parts.list",null,CancellationToken.None); // sits on its own connection, never answered
  var deadline=Environment.TickCount64+2000; while(bridge.Connections<1 && Environment.TickCount64<deadline) await Task.Delay(5);
  keeper.Track(Lease); var clock=Stopwatch.StartNew(); keeper.Tick(); await keeper.WaitIdleAsync(2000);
  Assert.IsTrue(clock.ElapsedMilliseconds<400,"beat took "+clock.ElapsedMilliseconds); Assert.AreEqual("ok",keeper.Status().State); Assert.AreEqual(0,keeper.Status().ConsecutiveFailures);
  Assert.AreEqual(2,bridge.Connections,"one connection for the tool call, a separate one for the beat");
  var beat=bridge.Requests.Single(r=>r.Operation==ControlOperations.Heartbeat); Assert.AreEqual(Lease,beat.LeaseId);
  blocked.SetResult(FakeBridge.Ok(new JObject())); await toolCall;
 }
 [TestMethod] public async Task ASlowBridgeBeatTimesOutNearEightHundredMilliseconds()
 {
  using var bridge=new FakeBridge(async r => { await Task.Delay(5000); return FakeBridge.Ok(new JObject()); });
  var keeper=new LeaseKeeper(new BridgeBeatSender(new BridgeClient())); keeper.Track(Lease);
  var clock=Stopwatch.StartNew(); keeper.Tick(); await keeper.WaitIdleAsync(4000);
  Assert.IsTrue(clock.ElapsedMilliseconds>=700 && clock.ElapsedMilliseconds<1800,"took "+clock.ElapsedMilliseconds); Assert.AreEqual(1,keeper.Status().ConsecutiveFailures);
 }
 [TestMethod] public async Task BridgeReportingTheLeaseGoneEndsTrackingOtherFailuresDoNot()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Fail(ControlReasons.LeaseExpired));
  var keeper=new LeaseKeeper(new BridgeBeatSender(new BridgeClient())); keeper.Track(Lease); keeper.Tick(); await keeper.WaitIdleAsync();
  Assert.AreEqual("lease_lost",keeper.Status().State); Assert.AreEqual("lease_expired",keeper.Status().LeaseLostReason);
 }
 [TestMethod] public async Task UnreachableBridgeCountsAsTransportFailure()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); int port=bridge.Port; bridge.Dispose();
  string credential=Path.GetTempFileName(); File.WriteAllText(credential,new string('x',64));
  Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",credential); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",port.ToString());
  try { var keeper=new LeaseKeeper(new BridgeBeatSender(new BridgeClient())); keeper.Track(Lease); keeper.Tick(); await keeper.WaitIdleAsync(); Assert.AreEqual(1,keeper.Status().ConsecutiveFailures); }
  finally { Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",null); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",null); File.Delete(credential); }
 }
}
