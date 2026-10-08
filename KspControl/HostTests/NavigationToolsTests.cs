using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class NavigationToolsTests
{
 private const string Lease="0123456789abcdef0123456789abcdef";
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-navigation-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ try { if(Directory.Exists(dir)) Directory.Delete(dir,true); } catch { } }

 private sealed record Rig(BridgeClient Bridge,LeaseKeeper Keeper,JournalAccess Journal,ControlTools Control,NavigationTools Tools,MutationTools Jobs);
 private Rig Make()
 {
  var bridge=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(bridge)); var journal=new JournalAccess(dir);
  var mutations=new MutationService(bridge,keeper,journal) { PollInterval=TimeSpan.FromMilliseconds(5) };
  return new Rig(bridge,keeper,journal,new ControlTools(bridge,keeper,journal),new NavigationTools(bridge,new NavigationService(mutations)),new MutationTools(mutations));
 }
 private static JObject Acquired()=>new() { ["leaseId"]=Lease,["purpose"]="mun free return",["expiresInSeconds"]=300,["grantId"]="grant-1",["generation"]=3,["epoch"]="epoch-9",["entity"]=FlightEffects.EntityPrefix+"vessel-1" };
 private static JObject StatusData(string operation)=>new() { ["grant"]=new JObject { ["state"]="valid",["id"]="grant-1",["generation"]=3,["operations"]=new JArray(operation),["facilities"]=new JArray("FLIGHT"),["expiresUtc"]="2099-01-01T00:00:00.000Z" },["lease"]=new JObject { ["held"]=false },["cooldownSeconds"]=0 };
 private static BridgeResponse Envelope(BridgeRequest q,string status,string operation,string phase="done")=>new() { Status=status,Data=new JObject { ["operation"]=operation,["requestId"]=(string)q.Arguments["requestId"]!,["phase"]=phase,["notDispatched"]=false } };
 private static Func<BridgeRequest,BridgeResponse> Control(Func<BridgeRequest,BridgeResponse> other,string operation=AutopilotOperations.Effect)=>r=>r.Operation switch
 {
  ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusData(operation)), _=>other(r)
 };
 private async Task<Rig> Acquire()
 {
  var rig=Make(); var reply=JObject.Parse(await rig.Control.ControlAcquireLease("mun free return",300)); Assert.AreEqual("completed",(string?)reply["Status"]); return rig;
 }

 // ---- argument bounds: no socket, no journal ----

 [TestMethod] public async Task NodeAndWarpArgumentsAreCheckedWithoutASocket()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  foreach(var text in new[]
  {
   await rig.Tools.FlightNodeCreate("short",Lease,"apoapsis"),
   await rig.Tools.FlightNodeCreate("node-c-0001","short","apoapsis"),
   await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"soon"),
   await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"absolute"),
   await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"in_seconds",0.5),
   await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"apoapsis",90000),
   await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"apoapsis",null,3001),
   await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"apoapsis",null,2000,2000,2000),
   await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"apoapsis",null,double.NaN),
   await rig.Tools.FlightNodeUpdate("node-u-0001",Lease,0),
   await rig.Tools.FlightNodeUpdate("node-u-0001",Lease,16,null,null,10),
   await rig.Tools.FlightNodeUpdate("node-u-0001",Lease,0,null,30),
   await rig.Tools.FlightNodeUpdate("node-u-0001",Lease,0,null,null,-3001),
   await rig.Tools.FlightNodeDelete("node-d-0001",Lease),
   await rig.Tools.FlightNodeDelete("node-d-0001",Lease,0,true),
   await rig.Tools.FlightNodeDelete("node-d-0001",Lease,-1),
   await rig.Tools.FlightWarpTo("warp-0001",Lease,"later"),
   await rig.Tools.FlightWarpTo("warp-0001",Lease,"node",5),
   await rig.Tools.FlightWarpTo("warp-0001",Lease,"soi",null,null,0),
   await rig.Tools.FlightWarpTo("warp-0001",Lease,"node",null,3601),
   await rig.Tools.FlightWarpTo("warp-0001",Lease,"node",null,-1),
   await rig.Tools.FlightWarpTo("warp-0001",Lease,"absolute"),
   await rig.Tools.FlightWarpTo("warp-0001",Lease,"node",null,null,16)
  }) Assert.AreEqual("invalid_argument",(string?)JObject.Parse(text)["ReasonCode"],text);
  Assert.AreEqual(0,bridge.Connections); Assert.IsFalse(Directory.Exists(dir),"the journal was never opened");
 }

 // ---- lease and grant ----

 [TestMethod] public async Task WithoutALeaseNothingIsSent()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"apoapsis",null,860));
  Assert.AreEqual("lease_required",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AFlightControlOnlyGrantDeniesTheNavigationMutations()
 {
  using var bridge=new FakeBridge(Control(_=>FakeBridge.Ok(new JObject()),FlightEffects.Family)); var rig=await Acquire(); var before=bridge.Connections;
  foreach(var text in new[]
  {
   await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"apoapsis",null,860), await rig.Tools.FlightNodeUpdate("node-u-0001",Lease,0,null,null,850),
   await rig.Tools.FlightNodeDelete("node-d-0001",Lease,null,true), await rig.Tools.FlightWarpTo("warp-0001",Lease,"node")
  }) Assert.AreEqual("grant_operation_denied",(string?)JObject.Parse(text)["ReasonCode"],text);
  Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 // ---- the journaled flow ----

 [TestMethod] public async Task NodeEditsAreJournaledUnderTheAutopilotFamilyAndCarryTheirArguments()
 {
  using var bridge=new FakeBridge(Control(q=>Envelope(q,"completed",q.Operation.Substring("flight.".Length))));
  var rig=await Acquire();
  Assert.AreEqual("completed",(string?)JObject.Parse(await rig.Tools.FlightNodeCreate("node-c-0001",Lease,"apoapsis",-10,860,0,5))["Status"]);
  Assert.AreEqual("completed",(string?)JObject.Parse(await rig.Tools.FlightNodeUpdate("node-u-0001",Lease,0,"in_seconds",900,858))["Status"]);
  Assert.AreEqual("completed",(string?)JObject.Parse(await rig.Tools.FlightNodeDelete("node-d-0001",Lease,0))["Status"]);
  Assert.AreEqual("completed",(string?)JObject.Parse(await rig.Tools.FlightNodeDelete("node-d-0002",Lease,null,true))["Status"]);
  var create=bridge.Requests.Single(r=>r.Operation==AutopilotOperations.NodeCreate).Arguments;
  Assert.AreEqual("apoapsis",(string?)create["timeReference"]); Assert.AreEqual(-10.0,(double)create["timeSeconds"]!); Assert.AreEqual(860.0,(double)create["prograde"]!); Assert.AreEqual(5.0,(double)create["radial"]!);
  var update=bridge.Requests.Single(r=>r.Operation==AutopilotOperations.NodeUpdate).Arguments;
  Assert.AreEqual(0,(int)update["nodeIndex"]!); Assert.AreEqual(858.0,(double)update["prograde"]!); Assert.IsNull(update["normal"],"an omitted component is not sent, so the bridge keeps it");
  var deletes=bridge.Requests.Where(r=>r.Operation==AutopilotOperations.NodeDelete).Select(r=>r.Arguments).ToArray();
  Assert.AreEqual(0,(int)deletes[0]["nodeIndex"]!); Assert.AreEqual(true,(bool)deletes[1]["all"]!);
  var job=rig.Journal.TryGet()!.Get("node-c-0001");
  Assert.AreEqual("completed",job.Status); Assert.AreEqual(AutopilotOperations.Effect,job.Operation); Assert.AreEqual(FlightEffects.JournalEntity,job.EntityId);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AWarpIsAJobFollowedWithTheAutopilotStatusOperation()
 {
  var phase="warping";
  using var bridge=new FakeBridge(Control(q=> q.Operation==AutopilotOperations.WarpTo ? Envelope(q,"running","warp_to","warping") : phase=="done" ? Envelope(q,"completed","warp_to") : Envelope(q,"running","warp_to",phase)));
  var rig=await Acquire();
  using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(80))) await rig.Tools.FlightWarpTo("warp-0001",Lease,"node",null,120,0,cancel.Token);
  var warp=bridge.Requests.Single(r=>r.Operation==AutopilotOperations.WarpTo).Arguments;
  Assert.AreEqual("node",(string?)warp["target"]); Assert.AreEqual(120.0,(double)warp["leadSeconds"]!); Assert.AreEqual(0,(int)warp["nodeIndex"]!);
  var running=JObject.Parse(await rig.Jobs.JobStatus("warp-0001",0)); Assert.AreEqual("running",(string?)running["Status"]);
  phase="done";
  var finished=JObject.Parse(await rig.Jobs.JobStatus("warp-0001",2)); Assert.AreEqual("completed",(string?)finished["Status"]);
  Assert.IsTrue(bridge.Requests.Any(r=>r.Operation==AutopilotOperations.Status)); Assert.IsFalse(bridge.Requests.Any(r=>r.Operation==EditorOperations.OperationStatus));
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task ABridgeRefusalCancelsTheJobBeforeDispatch()
 {
  using var bridge=new FakeBridge(Control(q=>new BridgeResponse { Status="failed",ReasonCode=NavigationReasons.WarpModePhysics,Data=new JObject { ["phase"]="admission",["notDispatched"]=true } }));
  var rig=await Acquire();
  var result=JObject.Parse(await rig.Tools.FlightWarpTo("warp-0001",Lease,"soi"));
  Assert.AreEqual(NavigationReasons.WarpModePhysics,(string?)result["ReasonCode"]);
  var job=rig.Journal.TryGet()!.Get("warp-0001"); Assert.AreEqual("cancelled",job.Status); Assert.AreEqual("not_dispatched",job.Reason); rig.Journal.Dispose();
 }

 // ---- the read ----

 [TestMethod] public async Task ThePredictionIsAReadWithNoLeaseOrJournal()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject { ["assessment"]=new JObject { ["munEncounter"]=true } })); var rig=Make();
  var result=JObject.Parse(await rig.Tools.FlightOrbitPrediction());
  Assert.AreEqual(true,(bool)result["Data"]!["assessment"]!["munEncounter"]!);
  Assert.AreEqual(FlightOperations.OrbitPrediction,bridge.Requests.Single().Operation); Assert.IsNull(bridge.Requests.Single().LeaseId); Assert.IsFalse(Directory.Exists(dir));
 }

 [TestMethod] public async Task TheAllowlistsKeepTheReadAndTheMutationsApart()
 {
  var client=new BridgeClient();
  CollectionAssert.Contains(BridgeClient.ReadOperations.ToArray(),FlightOperations.OrbitPrediction);
  Assert.IsFalse(BridgeClient.MutationOperations.Contains(FlightOperations.OrbitPrediction));
  foreach(var operation in new[]{ AutopilotOperations.NodeCreate,AutopilotOperations.NodeUpdate,AutopilotOperations.NodeDelete,AutopilotOperations.WarpTo })
  {
   Assert.IsTrue(BridgeClient.MutationOperations.Contains(operation),operation);
   await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.ReadAsync(operation,null,CancellationToken.None));
  }
  await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.MutateAsync(FlightOperations.OrbitPrediction,Lease,new JObject(),CancellationToken.None));
  Assert.IsFalse(FlightOperations.IsMutation(FlightOperations.OrbitPrediction));
 }
}
