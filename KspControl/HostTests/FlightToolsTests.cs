using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class FlightToolsTests
{
 private const string Lease="0123456789abcdef0123456789abcdef";
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-flight-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ try { if(Directory.Exists(dir)) Directory.Delete(dir,true); } catch { } }

 private sealed record Rig(BridgeClient Bridge,LeaseKeeper Keeper,JournalAccess Journal,ControlTools Control,FlightService Service,FlightTools Tools);
 private Rig Make()
 {
  var bridge=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(bridge)); var journal=new JournalAccess(dir);
  var service=new FlightService(bridge,keeper,journal);
  return new Rig(bridge,keeper,journal,new ControlTools(bridge,keeper,journal),service,new FlightTools(bridge,service));
 }
 private static JObject Acquired()=>new() { ["leaseId"]=Lease,["purpose"]="fly probe",["expiresInSeconds"]=300,["grantId"]="grant-1",["generation"]=3,["epoch"]="epoch-9",["entity"]="vessel:11111111-1111-1111-1111-111111111111" };
 private static JObject StatusData(string[] operations,string[] facilities)=>new() { ["grant"]=new JObject { ["state"]="valid",["id"]="grant-1",["generation"]=3,["operations"]=new JArray(operations),["facilities"]=new JArray(facilities),["expiresUtc"]="2099-01-01T00:00:00.000Z" },["lease"]=new JObject { ["held"]=false },["cooldownSeconds"]=0 };
 private static readonly string[] FlightOps={ FlightEffects.Family };
 private static readonly string[] FlightFacility={ FlightEffects.Facility };
 private static BridgeResponse ControlOrElse(BridgeRequest r,Func<BridgeRequest,BridgeResponse> other,string[]? operations=null,string[]? facilities=null) => r.Operation switch
 {
  ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusData(operations ?? FlightOps,facilities ?? FlightFacility)), _=>other(r)
 };
 private async Task<Rig> Acquire(string[]? operations=null,string[]? facilities=null)
 {
  var rig=Make();
  var reply=JObject.Parse(await rig.Control.ControlAcquireLease("fly probe",300)); Assert.AreEqual("completed",(string?)reply["Status"]);
  Assert.AreEqual(Lease,rig.Keeper.TrackedLease); Assert.IsNotNull(rig.Journal.CurrentGrant); return rig;
 }
 private static BridgeResponse Done(string operation,string requestId,string status="completed",string? reason=null,bool dispatched=true)=>new()
 { Status=status,ReasonCode=reason,Data=new JObject { ["operation"]=operation,["requestId"]=requestId,["phase"]="done",["notDispatched"]=!dispatched,["dispatched"]=dispatched,["observed"]=new JObject { ["throttleFraction"]=0.5 } } };
 private static BridgeResponse Refused(string reason)=>new() { Status="failed",ReasonCode=reason,Data=new JObject { ["phase"]="admission",["notDispatched"]=true,["dispatched"]=false,["requestId"]="irrelevant" } };

 // ---- the read tool: no lease, no journal ----

 [TestMethod] public async Task FlightStateIsAReadThatNeedsNoLeaseAndNoJournal()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject { ["body"]="Kerbin" })); var rig=Make();
  var result=JObject.Parse(await rig.Tools.FlightState());
  Assert.AreEqual("completed",(string?)result["Status"]); Assert.AreEqual("Kerbin",(string?)result["Data"]!["body"]);
  var request=bridge.Requests.Single(); Assert.AreEqual(FlightOperations.State,request.Operation); Assert.IsNull(request.LeaseId);
  Assert.IsFalse(Directory.Exists(dir),"the journal was never opened");
 }

 [TestMethod] public void TheBridgeClientRoutesFlightOperationsByTheirKind()
 {
  CollectionAssert.Contains(BridgeClient.ReadOperations.ToArray(),FlightOperations.State);
  foreach(var operation in FlightOperations.Mutations)
  {
   Assert.IsTrue(BridgeClient.MutationOperations.Contains(operation),operation);
   Assert.IsFalse(BridgeClient.ReadOperations.Contains(operation),operation+" must not be reachable as a read");
  }
  Assert.ThrowsExceptionAsync<ArgumentException>(()=>new BridgeClient().ReadAsync(FlightOperations.SetControls,null,CancellationToken.None)).GetAwaiter().GetResult();
 }

 // ---- argument bounds: no socket, no journal ----

 [TestMethod] public async Task OutOfBoundsArgumentsAreInvalidWithoutASocketOrAJournal()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var results=new[]
  {
   await rig.Tools.FlightSetControls("short",Lease,throttle:0.5),
   await rig.Tools.FlightSetControls("flight-req-01","zz",throttle:0.5),
   await rig.Tools.FlightSetControls("flight-req-01",Lease),
   await rig.Tools.FlightSetControls("flight-req-01",Lease,throttle:1.5),
   await rig.Tools.FlightSetControls("flight-req-01",Lease,throttle:-0.5),
   await rig.Tools.FlightSetControls("flight-req-01",Lease,throttleDelta:2),
   await rig.Tools.FlightSetControls("flight-req-01",Lease,throttle:0.5,throttleDelta:0.1),
   await rig.Tools.FlightSetControls("flight-req-01",Lease,throttle:double.NaN),
   await rig.Tools.FlightStage("flight-req-01",Lease,-1),
   await rig.Tools.FlightStage("flight-req-01",Lease,1000),
   await rig.Tools.FlightActionGroup("flight-req-01",Lease,"Custom11"),
   await rig.Tools.FlightActionGroup("flight-req-01",Lease,"Abort"),
   await rig.Tools.FlightActionGroup("flight-req-01",Lease,"gear"),
   await rig.Tools.FlightAbort("x",Lease),
   await rig.Tools.FlightWarp("flight-req-01",Lease,-1),
   await rig.Tools.FlightWarp("flight-req-01",Lease,8),
  };
  foreach(var text in results) { var result=JObject.Parse(text); Assert.AreEqual("failed",(string?)result["Status"],text); Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"],text); }
  Assert.AreEqual(0,bridge.Connections); Assert.IsFalse(Directory.Exists(dir));
 }

 // ---- lease and grant, before any flight socket ----

 [TestMethod] public async Task WithoutALeaseNothingIsSent()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.FlightStage("flight-req-01",Lease,2));
  Assert.AreEqual("lease_required",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!); Assert.AreEqual(0,bridge.Connections);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnotherLeaseIdIsLeaseInvalid()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()))); var rig=await Acquire(); var before=bridge.Connections;
  var result=JObject.Parse(await rig.Tools.FlightWarp("flight-req-01",new string('b',32),2));
  Assert.AreEqual("lease_invalid",(string?)result["ReasonCode"]); Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AGrantWithoutTheFlightFamilyDeniesBeforeAnySocket()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),new[]{ OperationEffects.ReplaceCraft },FlightFacility)); var rig=await Acquire(); var before=bridge.Connections;
  var result=JObject.Parse(await rig.Tools.FlightAbort("flight-req-01",Lease));
  Assert.AreEqual("grant_operation_denied",(string?)result["ReasonCode"]); StringAssert.Contains((string?)result["Data"]!["detail"],FlightEffects.Family);
  Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AGrantWithoutTheFlightFacilityDeniesBeforeAnySocket()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),FlightOps,new[]{ "VAB" })); var rig=await Acquire(); var before=bridge.Connections;
  var result=JObject.Parse(await rig.Tools.FlightSetControls("flight-req-01",Lease,gear:true));
  Assert.AreEqual("grant_operation_denied",(string?)result["ReasonCode"]); StringAssert.Contains((string?)result["Data"]!["detail"],FlightEffects.Facility);
  Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ASecondHostWithoutTheJournalCannotFly()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject())));
  using var other=new ControlJournal(dir); var rig=Make();
  var result=JObject.Parse(await rig.Tools.FlightStage("flight-req-01",Lease,2));
  Assert.AreEqual("journal_locked_by_other_host",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections);
 }

 // ---- the journaled flow ----

 [TestMethod] public async Task ASetControlsRequestReachesTheBridgeWithItsLeaseAndExactArgumentsAndIsRecorded()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Done("set_controls",(string)q.Arguments["requestId"]!)));
  var rig=await Acquire();
  var result=JObject.Parse(await rig.Tools.FlightSetControls("flight-req-01",Lease.ToUpperInvariant(),throttle:0.5,sas:true,gear:false));
  Assert.AreEqual("completed",(string?)result["Status"]);
  var sent=bridge.Requests.Single(r=>r.Operation==FlightOperations.SetControls);
  Assert.AreEqual(Lease,sent.LeaseId);
  Assert.AreEqual("flight-req-01",(string?)sent.Arguments["requestId"]); Assert.AreEqual(0.5,(double)sent.Arguments["throttle"]!,1e-9);
  Assert.AreEqual(true,(bool)sent.Arguments["sas"]!); Assert.AreEqual(false,(bool)sent.Arguments["gear"]!);
  Assert.IsNull(sent.Arguments["rcs"]); Assert.IsNull(sent.Arguments["lights"]); Assert.IsNull(sent.Arguments["brakes"]); Assert.IsNull(sent.Arguments["throttleDelta"]);
  var job=rig.Journal.TryGet()!.Get("flight-req-01");
  Assert.AreEqual("completed",job.Status); Assert.AreEqual(FlightEffects.Family,job.Operation); Assert.AreEqual(FlightEffects.JournalEntity,job.EntityId); Assert.AreEqual("grant-1",job.GrantId); Assert.AreEqual("epoch-9",job.WorldEpoch);
  StringAssert.Contains(job.Result,"throttleFraction"); rig.Journal.Dispose();
 }

 [TestMethod] public async Task EachFlightToolSendsItsOwnBridgeOperation()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Done(q.Operation,(string)q.Arguments["requestId"]!))); var rig=await Acquire();
  await rig.Tools.FlightStage("flight-stage-1",Lease,3); await rig.Tools.FlightActionGroup("flight-group-1",Lease,"Custom04",true); await rig.Tools.FlightAbort("flight-abort-1",Lease); await rig.Tools.FlightWarp("flight-warp-1",Lease,2);
  var sent=bridge.Requests.Where(r=>r.Operation.StartsWith("flight.")).ToDictionary(r=>r.Operation);
  CollectionAssert.AreEquivalent(new[]{ FlightOperations.Stage,FlightOperations.ActionGroup,FlightOperations.Abort,FlightOperations.Warp },sent.Keys.ToArray());
  Assert.AreEqual(3,(int)sent[FlightOperations.Stage].Arguments["expectedStage"]!);
  Assert.AreEqual("Custom04",(string?)sent[FlightOperations.ActionGroup].Arguments["group"]); Assert.AreEqual(true,(bool)sent[FlightOperations.ActionGroup].Arguments["state"]!);
  Assert.AreEqual(2,(int)sent[FlightOperations.Warp].Arguments["rateIndex"]!);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task TheSameRequestAgainAnswersFromTheJournalAndNeverTogglesTwice()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Done("action_group",(string)q.Arguments["requestId"]!))); var rig=await Acquire();
  var first=JObject.Parse(await rig.Tools.FlightActionGroup("flight-req-01",Lease,"Gear")); var second=JObject.Parse(await rig.Tools.FlightActionGroup("flight-req-01",Lease,"Gear"));
  Assert.AreEqual("completed",(string?)first["Status"]); Assert.AreEqual("completed",(string?)second["Status"]);
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==FlightOperations.ActionGroup)); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ADifferentRequestUnderTheSameIdIsAConflictWithoutASocket()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Done(q.Operation,(string)q.Arguments["requestId"]!))); var rig=await Acquire();
  await rig.Tools.FlightActionGroup("flight-req-01",Lease,"Gear"); var calls=bridge.Requests.Count;
  Assert.AreEqual("request_id_conflict",(string?)JObject.Parse(await rig.Tools.FlightActionGroup("flight-req-01",Lease,"Light"))["ReasonCode"]);
  Assert.AreEqual("request_id_conflict",(string?)JObject.Parse(await rig.Tools.FlightActionGroup("flight-req-01",Lease,"Gear",true))["ReasonCode"]);
  Assert.AreEqual("request_id_conflict",(string?)JObject.Parse(await rig.Tools.FlightAbort("flight-req-01",Lease))["ReasonCode"],"another operation under the same id");
  Assert.AreEqual(calls,bridge.Requests.Count); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ABridgeRefusalBeforeDispatchCancelsTheJobAndKeepsTheReason()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Refused("stage_mismatch"))); var rig=await Acquire();
  var result=JObject.Parse(await rig.Tools.FlightStage("flight-req-01",Lease,2));
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("stage_mismatch",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!);
  var job=rig.Journal.TryGet()!.Get("flight-req-01"); Assert.AreEqual("cancelled",job.Status); Assert.AreEqual("not_dispatched",job.Reason);
  var again=JObject.Parse(await rig.Tools.FlightStage("flight-req-01",Lease,2)); Assert.AreEqual("stage_mismatch",(string?)again["ReasonCode"]);
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==FlightOperations.Stage)); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ADispatchedFailureIsRecordedAsFailedNotCancelled()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Done("warp",(string)q.Arguments["requestId"]!,"failed","warp_denied"))); var rig=await Acquire();
  var result=JObject.Parse(await rig.Tools.FlightWarp("flight-req-01",Lease,4));
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("warp_denied",(string?)result["ReasonCode"]);
  var job=rig.Journal.TryGet()!.Get("flight-req-01"); Assert.AreEqual("failed",job.Status); Assert.AreEqual("warp_denied",job.Reason); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnIndeterminateBridgeAnswerStaysIndeterminateAndReservesTheId()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Done("stage",(string)q.Arguments["requestId"]!,"indeterminate","state_not_confirmed"))); var rig=await Acquire();
  var result=JObject.Parse(await rig.Tools.FlightStage("flight-req-01",Lease,2));
  Assert.AreEqual("indeterminate",(string?)result["Status"]); Assert.AreEqual("state_not_confirmed",(string?)result["ReasonCode"]);
  var again=JObject.Parse(await rig.Tools.FlightStage("flight-req-01",Lease,2)); Assert.AreEqual("indeterminate",(string?)again["Status"]);
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==FlightOperations.Stage),"an indeterminate stage is never sent again"); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ATransportFailureAfterSendingIsIndeterminateAndNeverReplayed()
 {
  using var bridge=new FakeBridge(r=>r.Operation==FlightOperations.Abort ? throw new IOException("connection reset") : ControlOrElse(r,_=>FakeBridge.Ok(new JObject()))); var rig=await Acquire();
  var result=JObject.Parse(await rig.Tools.FlightAbort("flight-req-01",Lease));
  Assert.AreEqual("indeterminate",(string?)result["Status"]); StringAssert.Contains((string?)result["Data"]!["reconcile"],"flight_state");
  Assert.AreEqual("indeterminate",rig.Journal.TryGet()!.Get("flight-req-01").Status);
  var again=JObject.Parse(await rig.Tools.FlightAbort("flight-req-01",Lease)); Assert.AreEqual("indeterminate",(string?)again["Status"]);
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==FlightOperations.Abort)); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ALeaseTheBridgeRevokedEndsTheSessionsLeaseTracking()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Refused("authority_revoked"))); var rig=await Acquire();
  var result=JObject.Parse(await rig.Tools.FlightSetControls("flight-req-01",Lease,throttle:0));
  Assert.AreEqual("authority_revoked",(string?)result["ReasonCode"]);
  Assert.IsNull(rig.Keeper.TrackedLease); Assert.IsNull(rig.Journal.TryGet()!.CurrentLease);
  Assert.AreEqual("lease_required",(string?)JObject.Parse(await rig.Tools.FlightStage("flight-req-02",Lease,2))["ReasonCode"]); rig.Journal.Dispose();
 }

 [TestMethod] public async Task TheFlightFacilityIsMirroredAsTheFlightJournalEntity()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),FlightOps,new[]{ "VAB",FlightEffects.Facility })); var rig=await Acquire();
  CollectionAssert.AreEquivalent(new[]{ "editor:VAB",FlightEffects.JournalEntity },rig.Journal.CurrentGrant!.Entities); rig.Journal.Dispose();
 }

 // ---- the human-run grant CLI ----

 private (int Code,string Out,string Err) Cli(params string[] extra)
 {
  var output=new StringWriter(); var error=new StringWriter();
  var args=new[]{"issue","--trust-dir",dir,"--ksp-root",@"C:\Games\KSP","--save","Sandbox"}.Concat(extra).ToArray();
  int code=GrantCli.Run(args,output,error,_=>null,()=>new DateTime(2026,3,1,12,0,0,DateTimeKind.Utc));
  return (code,output.ToString(),error.ToString());
 }

 [TestMethod] public void TheGrantCliIssuesTheFlightFamilyOnTheFlightFacilityAndKeepsEditorDefaults()
 {
  var flight=Cli("--ops",FlightEffects.Family,"--facilities",FlightEffects.Facility); Assert.AreEqual(GrantCli.Ok,flight.Code,flight.Err);
  var verified=GrantCodec.Verify(File.ReadAllText(Path.Combine(dir,"grant.json")),File.ReadAllBytes(Path.Combine(dir,"grant.key"))); Assert.IsTrue(verified.Ok,verified.State);
  CollectionAssert.AreEqual(new[]{ FlightEffects.Family },verified.Payload.Operations); CollectionAssert.AreEqual(new[]{ "FLIGHT" },verified.Payload.Facilities);
  CollectionAssert.DoesNotContain(GrantCli.DefaultOperations,FlightEffects.Family);
  CollectionAssert.Contains(GrantCli.AllowedOperations,FlightEffects.Family);
 }

 [TestMethod] public void TheGrantCliRefusesAnOperationFamilyTheBridgeDoesNotKnow()
 {
  var bad=Cli("--ops","flight.control,flight.teleport"); Assert.AreEqual(GrantCli.Usage,bad.Code); StringAssert.Contains(bad.Err,"flight.teleport");
  Assert.IsFalse(File.Exists(Path.Combine(dir,"grant.json")));
 }
}
