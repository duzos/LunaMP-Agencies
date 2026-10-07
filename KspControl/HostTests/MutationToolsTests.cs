using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class MutationToolsTests
{
 private const string Lease="0123456789abcdef0123456789abcdef";
 private const string Token="djF8ZXBvY2gtOXwxfDR8YWJjZGVmMDEyMzQ1";
 private static readonly string Hash=new('a',64);
 private const string Graph="{\"name\":\"Probe One\",\"facility\":\"VAB\",\"root\":\"pod\",\"parts\":[{\"id\":\"pod\",\"part\":\"mk1pod.v2\"}]}";
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-mutation-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ try { if(Directory.Exists(dir)) Directory.Delete(dir,true); } catch { } }

 private sealed record Rig(BridgeClient Bridge,LeaseKeeper Keeper,JournalAccess Journal,ControlTools Control,MutationService Service,MutationTools Tools);
 private Rig Make()
 {
  var bridge=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(bridge)); var journal=new JournalAccess(dir);
  var service=new MutationService(bridge,keeper,journal) { PollInterval=TimeSpan.FromMilliseconds(5) };
  return new Rig(bridge,keeper,journal,new ControlTools(bridge,keeper,journal),service,new MutationTools(service));
 }
 private static JObject Acquired()=>new() { ["leaseId"]=Lease,["purpose"]="build probe",["expiresInSeconds"]=300,["grantId"]="grant-1",["generation"]=3,["epoch"]="epoch-9",["entity"]="editor:VAB" };
 private static JObject StatusData(params string[] operations)=>new() { ["grant"]=new JObject { ["state"]="valid",["id"]="grant-1",["generation"]=3,["operations"]=new JArray(operations.Length==0 ? new[]{ OperationEffects.ReplaceCraft,OperationEffects.RestoreSnapshot } : operations),["facilities"]=new JArray("VAB"),["expiresUtc"]="2099-01-01T00:00:00.000Z" },["lease"]=new JObject { ["held"]=false },["cooldownSeconds"]=0 };
 private static BridgeResponse Running(string requestId,string phase="settle")=>new() { Status="running",Data=new JObject { ["operation"]="apply_craft",["requestId"]=requestId,["phase"]=phase,["notDispatched"]=false } };
 private static BridgeResponse Done(string requestId,string status="completed",string? reason=null)=>new() { Status=status,ReasonCode=reason,Data=new JObject { ["operation"]="apply_craft",["requestId"]=requestId,["phase"]="done",["notDispatched"]=false,["editorRevision"]="newtoken0123" } };
 private async Task<Rig> Acquire(FakeBridge bridge,params string[] operations)
 {
  var rig=Make(); var status=StatusData(operations);
  var reply=JObject.Parse(await rig.Control.ControlAcquireLease("build probe",300)); Assert.AreEqual("completed",(string?)reply["Status"]);
  Assert.AreEqual(Lease,rig.Keeper.TrackedLease); Assert.IsNotNull(rig.Journal.CurrentGrant); return rig;
 }
 private static BridgeResponse ControlOrElse(BridgeRequest r,Func<BridgeRequest,BridgeResponse> other,params string[] operations) => r.Operation switch
 {
  ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusData(operations)), _=>other(r)
 };
 private static async Task<JObject> Apply(Rig rig,string requestId="apply-0001",string revision=Token,string graph=Graph,string? hash=null)
 => JObject.Parse(await rig.Tools.EditorApplyCraft(requestId,Lease,revision,graph,hash ?? Hash));

 // ---- argument bounds: no socket, no journal ----

 [DataTestMethod]
 [DataRow("short",Lease,Token)] [DataRow("has space in it",Lease,Token)] [DataRow("apply-0001","short",Token)] [DataRow("apply-0001","0123456789abcdef0123456789abcdeg",Token)]
 [DataRow("apply-0001",Lease,"")] [DataRow("apply-0001",Lease,"has space")] [DataRow("apply-0001",Lease,"x+y/z=")]
 public async Task ApplyArgumentsOutOfBoundsAreInvalidWithoutASocket(string requestId,string leaseId,string revision)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.EditorApplyCraft(requestId,leaseId,revision,Graph,Hash));
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]);
  Assert.AreEqual(0,bridge.Connections); Assert.IsFalse(Directory.Exists(dir),"the journal was never opened");
 }

 [DataTestMethod]
 [DataRow("")] [DataRow("ABC")] [DataRow("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")] [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
 public async Task ApplyPlanHashMustBe64LowerCaseHex(string hash)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.EditorApplyCraft("apply-0001",Lease,Token,Graph,hash));
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); StringAssert.Contains((string?)result["Data"]!["detail"],"expectedPlanHash"); Assert.AreEqual(0,bridge.Connections);
 }

 [DataTestMethod]
 [DataRow("")] [DataRow("   ")] [DataRow("[]")] [DataRow("{")] [DataRow("{\"name\":\"a\",\"name\":\"b\"}")] [DataRow("{\"name\":\"a\",\"parts\":[]}")]
 public async Task ApplyGraphMustBeAStrictlyValidObjectBeforeAnySocketOpens(string graph)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.EditorApplyCraft("apply-0001",Lease,Token,graph,Hash));
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections);
 }

 [TestMethod] public async Task ApplyOversizeGraphIsInvalid()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.EditorApplyCraft("apply-0001",Lease,Token,"{\"name\":\""+new string('x',ConstructionLimits.MaxGraphBytes)+"\"}",Hash));
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections);
 }

 [DataTestMethod] [DataRow("short")] [DataRow("has space in it")] [DataRow("")]
 public async Task RestoreAndStatusValidateTheirIds(string id)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  Assert.AreEqual("invalid_argument",(string?)JObject.Parse(await rig.Tools.EditorRestoreSnapshot("restore-0001",Lease,Token,id))["ReasonCode"]);
  Assert.AreEqual("invalid_argument",(string?)JObject.Parse(await rig.Tools.EditorRestoreSnapshot(id,Lease,Token,"snapshot0001"))["ReasonCode"]);
  Assert.AreEqual("invalid_argument",(string?)JObject.Parse(await rig.Tools.JobStatus(id))["ReasonCode"]);
  Assert.AreEqual(0,bridge.Connections);
 }

 [DataTestMethod] [DataRow(-1)] [DataRow(21)] [DataRow(1000)]
 public async Task JobStatusWaitIsBounded(int wait)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.JobStatus("apply-0001",wait));
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); StringAssert.Contains((string?)result["Data"]!["detail"],"waitSeconds"); Assert.AreEqual(0,bridge.Connections);
 }

 // ---- lease and grant, before any mutation socket ----

 [TestMethod] public async Task WithoutALeaseTheMutationIsLeaseRequiredAndNothingIsSent()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Apply(rig);
  Assert.AreEqual("lease_required",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!);
  Assert.AreEqual(0,bridge.Connections);
  Assert.AreEqual("lease_required",(string?)JObject.Parse(await rig.Tools.EditorRestoreSnapshot("restore-0001",Lease,Token,"snapshot0001"))["ReasonCode"]);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnotherLeaseIdIsLeaseInvalid()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()))); var rig=await Acquire(bridge); var before=bridge.Connections;
  var result=JObject.Parse(await rig.Tools.EditorApplyCraft("apply-0001",new string('b',32),Token,Graph,Hash));
  Assert.AreEqual("lease_invalid",(string?)result["ReasonCode"]); Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AGrantWithoutTheOperationFamilyDeniesBeforeAnySocket()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),OperationEffects.RestoreSnapshot)); var rig=await Acquire(bridge,OperationEffects.RestoreSnapshot); var before=bridge.Connections;
  var result=await Apply(rig);
  Assert.AreEqual("grant_operation_denied",(string?)result["ReasonCode"]); Assert.AreEqual(before,bridge.Connections);
  Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ARestoreNeedsItsOwnOperationFamily()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),OperationEffects.ReplaceCraft)); var rig=await Acquire(bridge,OperationEffects.ReplaceCraft); var before=bridge.Connections;
  var result=JObject.Parse(await rig.Tools.EditorRestoreSnapshot("restore-0001",Lease,Token,"snapshot0001"));
  Assert.AreEqual("grant_operation_denied",(string?)result["ReasonCode"]); Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ASecondHostWithoutTheJournalCannotMutate()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject())));
  using var other=new ControlJournal(dir); var rig=Make();
  var result=await Apply(rig);
  Assert.AreEqual("journal_locked_by_other_host",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections);
 }

 // ---- the journaled flow ----

 [TestMethod] public async Task ASuccessfulApplyIsRecordedAndReturnsTheEnvelope()
 {
  var polls=0;
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation switch
  {
   EditorOperations.ApplyCraft=>Running((string)q.Arguments["requestId"]!,"locking"),
   EditorOperations.OperationStatus=>++polls<3 ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!),
   _=>FakeBridge.Fail("operation_unavailable")
  }));
  var rig=await Acquire(bridge);
  var result=await Apply(rig);
  Assert.AreEqual("completed",(string?)result["Status"]); Assert.AreEqual("newtoken0123",(string?)result["Data"]!["editorRevision"]);
  var apply=bridge.Requests.Single(r=>r.Operation==EditorOperations.ApplyCraft);
  Assert.AreEqual(Lease,apply.LeaseId); Assert.AreEqual("apply-0001",(string?)apply.Arguments["requestId"]); Assert.AreEqual(Token,(string?)apply.Arguments["expectedRevision"]); Assert.AreEqual(Hash,(string?)apply.Arguments["expectedPlanHash"]);
  Assert.AreEqual(Graph,(string?)apply.Arguments["graph"]);
  var job=rig.Journal.TryGet()!.Get("apply-0001");
  Assert.AreEqual("completed",job.Status); Assert.AreEqual(OperationEffects.ReplaceCraft,job.Operation); Assert.AreEqual("grant-1",job.GrantId); Assert.AreEqual(3,job.GrantGeneration); Assert.AreEqual("epoch-9",job.WorldEpoch);
  StringAssert.Contains(job.Result,"newtoken0123");
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task TheSameRequestAgainAnswersFromTheJournalWithoutAskingTheGameToApplyTwice()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire(bridge);
  var first=await Apply(rig); var second=await Apply(rig);
  Assert.AreEqual("completed",(string?)first["Status"]); Assert.AreEqual("completed",(string?)second["Status"]);
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==EditorOperations.ApplyCraft),"the retry did not reach the game again");
  Assert.AreEqual((string?)first["Data"]!["editorRevision"],(string?)second["Data"]!["editorRevision"]); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ADifferentRequestUnderTheSameIdIsAConflictWithoutASocket()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire(bridge); await Apply(rig); var calls=bridge.Requests.Count;
  var other=await Apply(rig,revision:Token+"x");
  Assert.AreEqual("request_id_conflict",(string?)other["ReasonCode"]); Assert.AreEqual(calls,bridge.Requests.Count);
  var changedGraph=await Apply(rig,graph:Graph.Replace("Probe One","Other"));
  Assert.AreEqual("request_id_conflict",(string?)changedGraph["ReasonCode"]); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ABridgeRefusalBeforeDispatchCancelsTheJobAndKeepsTheReason()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>new BridgeResponse { Status="failed",ReasonCode="stale_revision",Data=new JObject { ["phase"]="admission",["notDispatched"]=true } }));
  var rig=await Acquire(bridge);
  var result=await Apply(rig);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("stale_revision",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!);
  var job=rig.Journal.TryGet()!.Get("apply-0001"); Assert.AreEqual("cancelled",job.Status); Assert.AreEqual("not_dispatched",job.Reason);
  var again=await Apply(rig); Assert.AreEqual("stale_revision",(string?)again["ReasonCode"],"the retry is answered from the journal");
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==EditorOperations.ApplyCraft)); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AFailedJobIsRecordedAsFailedWithItsRestoreInfo()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!)
   : new BridgeResponse { Status="failed",ReasonCode="structure_mismatch_after_load",Data=new JObject { ["requestId"]=(string)q.Arguments["requestId"]!,["notDispatched"]=false,["restore"]=new JObject { ["attempted"]=true,["result"]="restored" } } }));
  var rig=await Acquire(bridge); var result=await Apply(rig);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("restored",(string?)result["Data"]!["restore"]!["result"]);
  var job=rig.Journal.TryGet()!.Get("apply-0001"); Assert.AreEqual("failed",job.Status); Assert.AreEqual("structure_mismatch_after_load",job.Reason); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ALostConnectionAfterDispatchIsIndeterminateAndStaysReserved()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!) : FakeBridge.Fail("bridge_unreachable")));
  var rig=await Acquire(bridge);
  var result=await Apply(rig);
  Assert.AreEqual("indeterminate",(string?)result["Status"]); Assert.AreEqual(false,(bool)result["Data"]!["notDispatched"]!); StringAssert.Contains((string?)result["Data"]!["reconcile"],"editor_state");
  Assert.AreEqual("indeterminate",rig.Journal.TryGet()!.Get("apply-0001").Status); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnAmbiguousDispatchFailureIsIndeterminateWhileAMissingCredentialIsNot()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>FakeBridge.Fail("simulation_not_responding")));
  var rig=await Acquire(bridge);
  var result=await Apply(rig); Assert.AreEqual("indeterminate",(string?)result["Status"]); Assert.AreEqual("simulation_not_responding",(string?)result["ReasonCode"]);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task TheBridgeForgettingARunningJobIsIndeterminateWithReconcileGuidance()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!) : FakeBridge.Fail(OperationReasons.JobUnknown)));
  var rig=await Acquire(bridge);
  var result=await Apply(rig);
  Assert.AreEqual("indeterminate",(string?)result["Status"]); Assert.AreEqual("reconcile_required",(string?)result["ReasonCode"]);
  Assert.AreEqual("indeterminate",rig.Journal.TryGet()!.Get("apply-0001").Status); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AHumanTakeoverEndsTheLeaseOnTheHostToo()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!,"indeterminate",OperationReasons.HumanInputDuringOperation)));
  var rig=await Acquire(bridge);
  var result=await Apply(rig);
  Assert.AreEqual("indeterminate",(string?)result["Status"]); Assert.AreEqual("human_input_during_operation",(string?)result["ReasonCode"]);
  Assert.IsNull(rig.Keeper.TrackedLease); Assert.IsNull(rig.Journal.TryGet()!.CurrentLease); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ARunningJobIsReturnedAsRunningWhenTheWaitRunsOut()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!,"locking") : Running((string)q.Arguments["requestId"]!,"post_unlock_grace")));
  var rig=await Acquire(bridge);
  using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
  var result=JObject.Parse(await rig.Tools.EditorApplyCraft("apply-0001",Lease,Token,Graph,Hash,cancel.Token));
  Assert.AreEqual("running",(string?)result["Status"]);
  Assert.AreEqual("running",rig.Journal.TryGet()!.Get("apply-0001").Status); rig.Journal.Dispose();
 }

 // ---- job_status ----

 [TestMethod] public async Task JobStatusOfAFinishedJobNeedsNoGame()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire(bridge); await Apply(rig); var calls=bridge.Requests.Count;
  var status=JObject.Parse(await rig.Tools.JobStatus("apply-0001",5));
  Assert.AreEqual("completed",(string?)status["Status"]); Assert.AreEqual(calls,bridge.Requests.Count); rig.Journal.Dispose();
 }

 [TestMethod] public async Task JobStatusOfARunningJobAsksTheGame()
 {
  var phase="settle";
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!,"locking") : phase=="done" ? Done((string)q.Arguments["requestId"]!) : Running((string)q.Arguments["requestId"]!,phase)));
  var rig=await Acquire(bridge);
  using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(60))) await rig.Tools.EditorApplyCraft("apply-0001",Lease,Token,Graph,Hash,cancel.Token);
  var running=JObject.Parse(await rig.Tools.JobStatus("apply-0001",0)); Assert.AreEqual("running",(string?)running["Status"]); Assert.AreEqual("settle",(string?)running["Data"]!["phase"]);
  phase="done";
  var finished=JObject.Parse(await rig.Tools.JobStatus("apply-0001",2)); Assert.AreEqual("completed",(string?)finished["Status"]);
  Assert.AreEqual("completed",rig.Journal.TryGet()!.Get("apply-0001").Status); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnUnknownRequestIdIsJobUnknown()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.JobStatus("never-seen-1")); Assert.AreEqual("job_unknown",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AJobLeftInFlightByARestartedHostIsIndeterminateAndReportedFromTheGameIfItKnowsIt()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.ApplyCraft ? Running((string)q.Arguments["requestId"]!) : FakeBridge.Fail(OperationReasons.JobUnknown)));
  var first=await Acquire(bridge);
  using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(60))) await first.Tools.EditorApplyCraft("apply-0001",Lease,Token,Graph,Hash,cancel.Token);
  first.Journal.Dispose();
  var second=Make();
  Assert.AreEqual("indeterminate",second.Journal.TryGet()!.Get("apply-0001").Status,"never replayed after a restart");
  var status=JObject.Parse(await second.Tools.JobStatus("apply-0001",1)); Assert.AreEqual("indeterminate",(string?)status["Status"]);
  second.Journal.Dispose();
 }

 // ---- restore ----

 [TestMethod] public async Task ARestoreGoesThroughTheSameJournalAndCarriesTheSnapshotId()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.RestoreSnapshot ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire(bridge);
  var result=JObject.Parse(await rig.Tools.EditorRestoreSnapshot("restore-0001",Lease,Token,"snapshot0001"));
  Assert.AreEqual("completed",(string?)result["Status"]);
  var request=bridge.Requests.Single(r=>r.Operation==EditorOperations.RestoreSnapshot); Assert.AreEqual("snapshot0001",(string?)request.Arguments["snapshotId"]); Assert.AreEqual(Lease,request.LeaseId);
  var job=rig.Journal.TryGet()!.Get("restore-0001"); Assert.AreEqual(OperationEffects.RestoreSnapshot,job.Operation); Assert.AreEqual("completed",job.Status); rig.Journal.Dispose();
 }

 // ---- the allowlists ----

 [TestMethod] public async Task MutationOperationsAreOnlyReachableThroughTheMutationEntryPoint()
 {
  var client=new BridgeClient();
  foreach(var operation in EditorOperations.Mutations)
  {
   Assert.IsTrue(BridgeClient.MutationOperations.Contains(operation));
   Assert.IsFalse(BridgeClient.ReadOperations.Contains(operation)); Assert.IsFalse(BridgeClient.ControlOperationSet.Contains(operation));
   await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.ReadAsync(operation,null,CancellationToken.None));
   await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.ControlAsync(operation,Lease,null,TimeSpan.FromSeconds(1),CancellationToken.None));
  }
  await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.MutateAsync("editor.state",Lease,null!,CancellationToken.None));
  await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.MutateAsync("launch",Lease,null!,CancellationToken.None));
  await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.MutateAsync(EditorOperations.ApplyCraft,"notalease",null!,CancellationToken.None));
  Assert.IsTrue(BridgeClient.ReadOperations.Contains(EditorOperations.OperationStatus));
 }
}
[TestClass] public class MutationJournalTests
{
 private string directory=null!; private readonly DateTimeOffset now=DateTimeOffset.UtcNow;
 [TestInitialize] public void Init()=>directory=Path.Combine(Path.GetTempPath(),"ksp-mutation-journal-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ if(Directory.Exists(directory)) Directory.Delete(directory,true); }
 private (ControlJournal j,ControlLease l) Setup()
 {
  var j=new ControlJournal(directory); j.ProvisionGrant(new MissionGrant("grant",1,"bridge_reported",now.AddHours(1),0,new[]{ OperationEffects.ReplaceCraft },new[]{ "editor:VAB" }));
  return (j,j.Acquire("bridge-lease-1","tester","world",now,TimeSpan.FromMinutes(2)));
 }
 private Job Admit(ControlJournal j,ControlLease l,string id="req00001",string args="{}")=>j.Admit(id,"grant",1,l.Id,"world",OperationEffects.ReplaceCraft,"editor:VAB",args,0m,now);
 [TestMethod] public void FinishRecordsEveryTerminalOutcomeAndItsEnvelope()
 {
  foreach(var status in new[]{"completed","failed","cancelled","indeterminate"})
  { var(j,l)=Setup(); using(j) { var id="job-"+status; Admit(j,l,id); j.Begin(id,now); var done=j.Finish(id,status,"why","{\"x\":1}"); Assert.AreEqual(status,done.Status); Assert.AreEqual("why",done.Reason); Assert.AreEqual("{\"x\":1}",done.Result); } Cleanup(); Init(); }
 }
 [TestMethod] public void ATerminalJobCannotBeFinishedAgainAndAnUnknownStatusIsRefused()
 {
  var(j,l)=Setup(); using(j) { Admit(j,l); j.Begin("req00001",now); j.Finish("req00001","completed","observed","{}"); Assert.ThrowsException<InvalidOperationException>(()=>j.Finish("req00001","failed","x","{}")); Assert.ThrowsException<ArgumentException>(()=>j.Finish("req00001","weird","x","{}")); }
 }
 [TestMethod] public void ACancelledJobReleasesItsReservationButAnIndeterminateOneKeepsIt()
 {
  var(j,l)=Setup(); using(j) { Admit(j,l,"a1234567"); j.Begin("a1234567",now); j.Finish("a1234567","cancelled","not_dispatched","{}"); Admit(j,l,"b1234567"); j.Begin("b1234567",now); j.Finish("b1234567","indeterminate","socket","{}"); Assert.IsNotNull(Admit(j,l,"c1234567")); }
 }
 [TestMethod] public void TheEnvelopeAndStatusSurviveARestartAndInFlightJobsBecomeIndeterminate()
 {
  var(j,l)=Setup(); using(j) { Admit(j,l,"done1234"); j.Begin("done1234",now); j.Finish("done1234","completed","observed","{\"editorRevision\":\"tok\"}"); Admit(j,l,"live1234"); j.Begin("live1234",now); Admit(j,l,"fresh123"); }
  using var restarted=new ControlJournal(directory);
  Assert.AreEqual("{\"editorRevision\":\"tok\"}",restarted.Get("done1234").Result); Assert.AreEqual("completed",restarted.Get("done1234").Status);
  Assert.AreEqual("indeterminate",restarted.Get("live1234").Status,"a running job is never replayed"); Assert.AreEqual("indeterminate",restarted.Get("fresh123").Status,"nor an accepted one");
  Assert.AreEqual("host_restart_reconcile_required",restarted.Get("live1234").Reason);
 }
 [TestMethod] public void ADuplicateAdmitReturnsTheStoredJobIncludingItsResult()
 {
  var(j,l)=Setup(); using(j) { Admit(j,l); j.Begin("req00001",now); j.Finish("req00001","failed","load_failed","{\"r\":1}"); var again=Admit(j,l); Assert.AreEqual("failed",again.Status); Assert.AreEqual("{\"r\":1}",again.Result); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"req00001","{\"different\":true}")); }
 }
 [TestMethod] public void GrantGenerationReuseStaysRefused()
 {
  var(j,l)=Setup(); using(j) { Assert.ThrowsException<InvalidOperationException>(()=>j.ProvisionGrant(new MissionGrant("grant",1,"bridge_reported",now.AddHours(1),0,new[]{ OperationEffects.ReplaceCraft },new[]{ "editor:VAB" }))); j.RevokeGrant("grant"); Assert.ThrowsException<InvalidOperationException>(()=>j.ProvisionGrant(new MissionGrant("grant",1,"bridge_reported",now.AddHours(1),0,new[]{ OperationEffects.ReplaceCraft },new[]{ "editor:VAB" }))); }
 }
}
