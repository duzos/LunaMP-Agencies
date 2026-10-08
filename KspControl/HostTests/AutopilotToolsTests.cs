using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class AutopilotToolsTests
{
 private const string Lease="0123456789abcdef0123456789abcdef";
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-autopilot-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ try { if(Directory.Exists(dir)) Directory.Delete(dir,true); } catch { } }

 private sealed record Rig(BridgeClient Bridge,LeaseKeeper Keeper,JournalAccess Journal,ControlTools Control,MutationService Mutations,AutopilotTools Tools,MutationTools Jobs);
 private Rig Make()
 {
  var bridge=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(bridge)); var journal=new JournalAccess(dir);
  var mutations=new MutationService(bridge,keeper,journal) { PollInterval=TimeSpan.FromMilliseconds(5) };
  return new Rig(bridge,keeper,journal,new ControlTools(bridge,keeper,journal),mutations,new AutopilotTools(bridge,new AutopilotService(mutations)),new MutationTools(mutations));
 }
 private static JObject Acquired()=>new() { ["leaseId"]=Lease,["purpose"]="fly to orbit",["expiresInSeconds"]=300,["grantId"]="grant-1",["generation"]=3,["epoch"]="epoch-9",["entity"]=FlightEffects.EntityPrefix+"vessel-1" };
 private static JObject StatusData(params string[] operations)=>new() { ["grant"]=new JObject { ["state"]="valid",["id"]="grant-1",["generation"]=3,["operations"]=new JArray(operations.Length==0 ? new[]{ AutopilotOperations.Effect } : operations),["facilities"]=new JArray(operations.Contains(AutopilotOperations.Effect)||operations.Length==0 ? "FLIGHT" : "VAB"),["expiresUtc"]="2099-01-01T00:00:00.000Z" },["lease"]=new JObject { ["held"]=false },["cooldownSeconds"]=0 };
 private static BridgeResponse Running(string requestId,string phase="ascending")=>new() { Status="running",Data=new JObject { ["operation"]="autopilot_ascent",["requestId"]=requestId,["phase"]=phase,["notDispatched"]=false } };
 private static BridgeResponse Done(string requestId,string status="completed",string? reason=null)=>new() { Status=status,ReasonCode=reason,Data=new JObject { ["operation"]="autopilot_ascent",["requestId"]=requestId,["phase"]="done",["notDispatched"]=false,["orbitReached"]=status=="completed" } };
 private static BridgeResponse ControlOrElse(BridgeRequest r,Func<BridgeRequest,BridgeResponse> other,params string[] operations)=>r.Operation switch
 {
  ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusData(operations)), _=>other(r)
 };
 private async Task<Rig> Acquire(params string[] operations)
 {
  var rig=Make(); var reply=JObject.Parse(await rig.Control.ControlAcquireLease("fly to orbit",300)); Assert.AreEqual("completed",(string?)reply["Status"]);
  Assert.AreEqual(Lease,rig.Keeper.TrackedLease); return rig;
 }
 private static async Task<JObject> Ascent(Rig rig,string requestId="ascent-0001",int altitude=100000,double inclination=0,bool autostage=true,bool autoWarp=false)
  =>JObject.Parse(await rig.Tools.MechjebAscent(requestId,Lease,altitude,inclination,autostage,autoWarp));

 // ---- argument bounds: no socket, no journal ----

 [DataTestMethod]
 [DataRow("short",100000,0.0)] [DataRow("ascent-0001",69999,0.0)] [DataRow("ascent-0001",500001,0.0)] [DataRow("ascent-0001",100000,180.5)] [DataRow("ascent-0001",100000,-181.0)] [DataRow("ascent-0001",100000,double.NaN)]
 public async Task AscentArgumentsOutOfBoundsAreInvalidWithoutASocket(string requestId,int altitude,double inclination)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Ascent(rig,requestId,altitude,inclination);
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections); Assert.IsFalse(Directory.Exists(dir),"the journal was never opened");
 }

 [TestMethod] public async Task OtherToolsValidateTheirArgumentsToo()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  foreach(var result in new[]
  {
   JObject.Parse(await rig.Tools.MechjebExecuteNode("short",Lease,false)), JObject.Parse(await rig.Tools.MechjebExecuteNode("node-0001","short",false)),
   JObject.Parse(await rig.Tools.MechjebPlanCircularize("short",Lease)), JObject.Parse(await rig.Tools.MechjebPlanCircularize("circ-0001",new string('g',32))),
   JObject.Parse(await rig.Tools.MechjebPlanHohmannToTarget("hohm-0001",Lease,"Mun; rm")), JObject.Parse(await rig.Tools.MechjebPlanHohmannToTarget("hohm-0001",Lease,"")),
   JObject.Parse(await rig.Tools.MechjebPlanHohmannToTarget("hohm-0001",Lease,new string('a',65)))
  }) Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]);
  Assert.AreEqual(0,bridge.Connections);
 }

 // ---- lease and grant, before any mutation socket ----

 [TestMethod] public async Task WithoutALeaseTheMutationIsLeaseRequiredAndNothingIsSent()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Ascent(rig);
  Assert.AreEqual("lease_required",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!); Assert.AreEqual(0,bridge.Connections);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AGrantWithoutTheFlightFamilyDeniesEveryAutopilotTool()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),OperationEffects.ReplaceCraft)); var rig=await Acquire(OperationEffects.ReplaceCraft); var before=bridge.Connections;
  Assert.AreEqual("grant_operation_denied",(string?)(await Ascent(rig))["ReasonCode"]);
  Assert.AreEqual("grant_operation_denied",(string?)JObject.Parse(await rig.Tools.MechjebExecuteNode("node-0001",Lease,false))["ReasonCode"]);
  Assert.AreEqual("grant_operation_denied",(string?)JObject.Parse(await rig.Tools.MechjebPlanCircularize("circ-0001",Lease))["ReasonCode"]);
  Assert.AreEqual("grant_operation_denied",(string?)JObject.Parse(await rig.Tools.MechjebPlanHohmannToTarget("hohm-0001",Lease,"Mun"))["ReasonCode"]);
  Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnEditorOnlyGrantMirrorHasNoFlightEntity()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),OperationEffects.ReplaceCraft)); var rig=await Acquire(OperationEffects.ReplaceCraft);
  CollectionAssert.DoesNotContain(rig.Journal.CurrentGrant!.Entities,FlightEffects.JournalEntity); rig.Journal.Dispose();
 }

 [TestMethod] public async Task TheFlightFamilyAddsTheFlightSceneEntityToTheMirror()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()))); var rig=await Acquire();
  CollectionAssert.Contains(rig.Journal.CurrentGrant!.Entities,FlightEffects.JournalEntity); CollectionAssert.Contains(rig.Journal.CurrentGrant.Operations,AutopilotOperations.Effect); rig.Journal.Dispose();
 }

 // ---- the journaled flow ----

 [TestMethod] public async Task AnAscentIsJournaledDispatchedAndFollowedWithTheAutopilotStatusOperation()
 {
  var polls=0;
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation switch
  {
   AutopilotOperations.Ascent=>Running((string)q.Arguments["requestId"]!,"engaging"),
   AutopilotOperations.Status=>++polls<3 ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!),
   _=>FakeBridge.Fail("operation_unavailable")
  }));
  var rig=await Acquire();
  var result=await Ascent(rig,altitude:120000,inclination:28.5,autostage:true);
  Assert.AreEqual("completed",(string?)result["Status"]); Assert.AreEqual(true,(bool)result["Data"]!["orbitReached"]!);
  var ascent=bridge.Requests.Single(r=>r.Operation==AutopilotOperations.Ascent);
  Assert.AreEqual(Lease,ascent.LeaseId); Assert.AreEqual("ascent-0001",(string?)ascent.Arguments["requestId"]); Assert.AreEqual(120000,(int)ascent.Arguments["targetAltitudeMeters"]!);
  Assert.AreEqual(28.5,(double)ascent.Arguments["inclinationDegrees"]!); Assert.AreEqual(true,(bool)ascent.Arguments["autostage"]!); Assert.AreEqual(false,(bool)ascent.Arguments["autoWarp"]!);
  Assert.AreEqual(true,(bool)ascent.Arguments["ignite"]!,"ignite defaults to true");
  Assert.IsFalse(bridge.Requests.Any(r=>r.Operation==EditorOperations.OperationStatus),"an autopilot job is never polled through the editor status operation");
  var job=rig.Journal.TryGet()!.Get("ascent-0001");
  Assert.AreEqual("completed",job.Status); Assert.AreEqual(AutopilotOperations.Effect,job.Operation); Assert.AreEqual(FlightEffects.JournalEntity,job.EntityId); Assert.AreEqual("grant-1",job.GrantId); Assert.AreEqual("epoch-9",job.WorldEpoch);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task EveryMutationToolReachesItsOwnBridgeOperation()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>new BridgeResponse { Status="completed",Data=new JObject { ["operation"]="x",["requestId"]=(string)q.Arguments["requestId"]!,["phase"]="done" } }));
  var rig=await Acquire();
  Assert.AreEqual("completed",(string?)JObject.Parse(await rig.Tools.MechjebExecuteNode("node-0001",Lease,true))["Status"]);
  Assert.AreEqual("completed",(string?)JObject.Parse(await rig.Tools.MechjebPlanCircularize("circ-0001",Lease))["Status"]);
  Assert.AreEqual("completed",(string?)JObject.Parse(await rig.Tools.MechjebPlanHohmannToTarget("hohm-0001",Lease,"Mun"))["Status"]);
  Assert.AreEqual(true,(bool)bridge.Requests.Single(r=>r.Operation==AutopilotOperations.ExecuteNode).Arguments["all"]!);
  Assert.AreEqual("Mun",(string?)bridge.Requests.Single(r=>r.Operation==AutopilotOperations.PlanHohmann).Arguments["targetBodyName"]);
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==AutopilotOperations.PlanCircularize));
  Assert.AreEqual("completed",(string?)JObject.Parse(await rig.Tools.FlightRecover("recover-0001",Lease))["Status"]);
  var recover=bridge.Requests.Single(r=>r.Operation==AutopilotOperations.Recover);
  Assert.AreEqual(30000,(int)recover.Arguments["targetPeriapsisMeters"]!); Assert.AreEqual("now",(string?)recover.Arguments["burnAt"]); Assert.AreEqual(10000,(int)recover.Arguments["armAltitudeMeters"]!);
  Assert.AreEqual(AutopilotOperations.Effect,rig.Journal.TryGet()!.Get("recover-0001").Operation);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task RecoverArgumentsOutOfBoundsAreInvalidWithoutASocket()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  foreach(var result in new[]
  {
   JObject.Parse(await rig.Tools.FlightRecover("short",Lease)), JObject.Parse(await rig.Tools.FlightRecover("recover-0001","short")),
   JObject.Parse(await rig.Tools.FlightRecover("recover-0001",Lease,targetPeriapsisMeters:-50001)), JObject.Parse(await rig.Tools.FlightRecover("recover-0001",Lease,targetPeriapsisMeters:60001)),
   JObject.Parse(await rig.Tools.FlightRecover("recover-0001",Lease,burnAt:"periapsis")), JObject.Parse(await rig.Tools.FlightRecover("recover-0001",Lease,armAltitudeMeters:999)),
   JObject.Parse(await rig.Tools.FlightRecover("recover-0001",Lease,armAltitudeMeters:30001))
  }) Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]);
  Assert.AreEqual(0,bridge.Connections); Assert.IsFalse(Directory.Exists(dir),"the journal was never opened");
 }

 [TestMethod] public async Task ARecoveryIsDeniedWithoutTheAutopilotFamily()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),FlightEffects.Family)); var rig=await Acquire(FlightEffects.Family); var before=bridge.Connections;
  Assert.AreEqual("grant_operation_denied",(string?)JObject.Parse(await rig.Tools.FlightRecover("recover-0001",Lease,burnAt:"apoapsis"))["ReasonCode"]);
  Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task TheSameRequestAgainAnswersFromTheJournalAndADifferentOneConflicts()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==AutopilotOperations.Ascent ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire();
  var first=await Ascent(rig); var second=await Ascent(rig);
  Assert.AreEqual("completed",(string?)first["Status"]); Assert.AreEqual("completed",(string?)second["Status"]);
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==AutopilotOperations.Ascent),"the retry did not reach the game again");
  var other=await Ascent(rig,altitude:200000); Assert.AreEqual("request_id_conflict",(string?)other["ReasonCode"]); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ABridgeRefusalBeforeDispatchCancelsTheJobAndKeepsTheReason()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>new BridgeResponse { Status="failed",ReasonCode="competing_controller",Data=new JObject { ["phase"]="admission",["notDispatched"]=true,["competitors"]=new JArray("mechjeb.landing") } }));
  var rig=await Acquire();
  var result=await Ascent(rig);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("competing_controller",(string?)result["ReasonCode"]); Assert.AreEqual("mechjeb.landing",(string?)result["Data"]!["competitors"]![0]);
  var job=rig.Journal.TryGet()!.Get("ascent-0001"); Assert.AreEqual("cancelled",job.Status); Assert.AreEqual("not_dispatched",job.Reason); rig.Journal.Dispose();
 }

 [TestMethod] public async Task StopOrLeaseLossEndsTheHostLeaseAndTheJournalRecordsCancelled()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==AutopilotOperations.Ascent ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!,"cancelled","authority_revoked")));
  var rig=await Acquire();
  var result=await Ascent(rig);
  Assert.AreEqual("cancelled",(string?)result["Status"]); Assert.AreEqual("authority_revoked",(string?)result["ReasonCode"]);
  Assert.IsNull(rig.Keeper.TrackedLease); Assert.AreEqual("cancelled",rig.Journal.TryGet()!.Get("ascent-0001").Status); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AHumanTakeoverDuringAnAscentEndsTheLeaseOnTheHostToo()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==AutopilotOperations.Ascent ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!,"cancelled",OperationReasons.HumanInputDuringOperation)));
  var rig=await Acquire();
  var result=await Ascent(rig);
  Assert.AreEqual("human_input_during_operation",(string?)result["ReasonCode"]); Assert.IsNull(rig.Keeper.TrackedLease); Assert.IsNull(rig.Journal.TryGet()!.CurrentLease); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ALostConnectionAfterDispatchIsIndeterminateAndLabelledAsAnAutopilotJob()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==AutopilotOperations.Ascent ? Running((string)q.Arguments["requestId"]!) : FakeBridge.Fail("bridge_unreachable")));
  var rig=await Acquire();
  var result=await Ascent(rig);
  Assert.AreEqual("indeterminate",(string?)result["Status"]); Assert.AreEqual("autopilot",(string?)result["Data"]!["operation"]); Assert.AreEqual(false,(bool)result["Data"]!["notDispatched"]!);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnAscentStillRunningAfterTheWaitIsRunningAndJobStatusAsksTheAutopilotStatusOperation()
 {
  var phase="ascending";
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==AutopilotOperations.Ascent ? Running((string)q.Arguments["requestId"]!,"engaging") : phase=="done" ? Done((string)q.Arguments["requestId"]!) : Running((string)q.Arguments["requestId"]!,phase)));
  var rig=await Acquire();
  using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(80))) await rig.Tools.MechjebAscent("ascent-0001",Lease,100000,0,true,false,cancellationToken:cancel.Token);
  var running=JObject.Parse(await rig.Jobs.JobStatus("ascent-0001",0)); Assert.AreEqual("running",(string?)running["Status"]); Assert.AreEqual("ascending",(string?)running["Data"]!["phase"]);
  phase="done";
  var finished=JObject.Parse(await rig.Jobs.JobStatus("ascent-0001",2)); Assert.AreEqual("completed",(string?)finished["Status"]);
  Assert.IsFalse(bridge.Requests.Any(r=>r.Operation==EditorOperations.OperationStatus)); Assert.AreEqual("completed",rig.Journal.TryGet()!.Get("ascent-0001").Status); rig.Journal.Dispose();
 }

 // ---- reads and allowlists ----

 [TestMethod] public async Task MechjebStatusIsAReadWithNoLeaseOrJournal()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject { ["mechjeb"]="available",["version"]="2.15.2.0" })); var rig=Make();
  var result=JObject.Parse(await rig.Tools.MechjebStatus(CancellationToken.None));
  Assert.AreEqual("available",(string?)result["Data"]!["mechjeb"]); Assert.AreEqual(AutopilotOperations.MechJebStatus,bridge.Requests.Single().Operation); Assert.IsNull(bridge.Requests.Single().LeaseId); Assert.IsFalse(Directory.Exists(dir));
 }

 [TestMethod] public async Task AutopilotMutationsAreNotReadableAndReadsAreNotMutable()
 {
  var client=new BridgeClient();
  foreach(var operation in AutopilotOperations.Mutations) await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.ReadAsync(operation,null,CancellationToken.None));
  foreach(var operation in AutopilotOperations.Reads) await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.MutateAsync(operation,Lease,new JObject(),CancellationToken.None));
  CollectionAssert.IsSubsetOf(AutopilotOperations.Reads,BridgeClient.ReadOperations.ToArray()); CollectionAssert.IsSubsetOf(AutopilotOperations.Mutations,BridgeClient.MutationOperations.ToArray());
 }
}
[TestClass] public class AutopilotGrantCliTests
{
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-grant-autopilot",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ if(Directory.Exists(dir)) Directory.Delete(dir,true); }
 private (int Code,string Err) Issue(params string[] extra)
 {
  var error=new StringWriter();
  int code=GrantCli.Run(new[]{"issue","--trust-dir",dir,"--ksp-root",@"C:\Games\KSP","--save","Sandbox"}.Concat(extra).ToArray(),new StringWriter(),error,_=>null,()=>new DateTime(2026,3,1,12,0,0,DateTimeKind.Utc));
  return (code,error.ToString());
 }
 private GrantPayload Read()
 { var v=GrantCodec.Verify(File.ReadAllText(Path.Combine(dir,"grant.json")),File.ReadAllBytes(Path.Combine(dir,"grant.key"))); Assert.IsTrue(v.Ok,v.State); return v.Payload; }

 [TestMethod] public void TheFlightFamilyIsKnownButNeverGrantedByDefault()
 {
  CollectionAssert.Contains(GrantCli.AllowedOperations,"flight.autopilot"); CollectionAssert.DoesNotContain(GrantCli.DefaultOperations,"flight.autopilot");
  Assert.AreEqual(0,Issue().Code); CollectionAssert.DoesNotContain(Read().Operations,"flight.autopilot");
 }

 [TestMethod] public void OpsCanAskForTheFlightFamily()
 {
  Assert.AreEqual(0,Issue("--ops","editor.replace_craft,flight.autopilot").Code);
  CollectionAssert.AreEqual(new[]{"editor.replace_craft","flight.autopilot"},Read().Operations);
 }

 [TestMethod] public void AnUnknownFamilyIsRejectedAndNothingIsWritten()
 {
  var result=Issue("--ops","flight.autopilot,flight.teleport");
  Assert.AreEqual(GrantCli.Usage,result.Code); StringAssert.Contains(result.Err,"flight.teleport"); Assert.IsFalse(File.Exists(Path.Combine(dir,"grant.json")));
 }
}
