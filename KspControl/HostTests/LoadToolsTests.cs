using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
/// <summary>editor_load_craft on the host: argument bounds without a socket, the lease and grant checks, and the journaled flow with its refusals.</summary>
[TestClass] [DoNotParallelize] public class LoadToolsTests
{
 private const string Lease="0123456789abcdef0123456789abcdef";
 private const string Token="djF8ZXBvY2gtOXwxfDR8YWJjZGVmMDEyMzQ1";
 private static readonly string Hash=new('a',64);
 private const string File="Orbiter One.craft";
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-load-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ try { if(Directory.Exists(dir)) Directory.Delete(dir,true); } catch { } }

 private sealed record Rig(BridgeClient Bridge,LeaseKeeper Keeper,JournalAccess Journal,ControlTools Control,LoadTools Tools);
 private Rig Make()
 {
  var bridge=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(bridge)); var journal=new JournalAccess(dir);
  var service=new MutationService(bridge,keeper,journal) { PollInterval=TimeSpan.FromMilliseconds(5) };
  return new Rig(bridge,keeper,journal,new ControlTools(bridge,keeper,journal),new LoadTools(service));
 }
 private static JObject Acquired()=>new() { ["leaseId"]=Lease,["purpose"]="build probe",["expiresInSeconds"]=300,["grantId"]="grant-1",["generation"]=3,["epoch"]="epoch-9",["entity"]="editor:VAB" };
 private static JObject StatusData(params string[] operations)=>new() { ["grant"]=new JObject { ["state"]="valid",["id"]="grant-1",["generation"]=3,["operations"]=new JArray(operations.Length==0 ? new[]{ OperationEffects.ReplaceCraft,OperationEffects.RestoreSnapshot } : operations),["facilities"]=new JArray("VAB"),["expiresUtc"]="2099-01-01T00:00:00.000Z" },["lease"]=new JObject { ["held"]=false },["cooldownSeconds"]=0 };
 private static BridgeResponse Running(string requestId,string phase="settle")=>new() { Status="running",Data=new JObject { ["operation"]="load_craft",["requestId"]=requestId,["phase"]=phase,["notDispatched"]=false } };
 private static BridgeResponse Done(string requestId)=>new() { Status="completed",Data=new JObject { ["operation"]="load_craft",["requestId"]=requestId,["phase"]="done",["notDispatched"]=false,["upgradedOnLoad"]=false,["editorRevision"]="newtoken0123",["comparison"]=new JObject { ["equal"]=true } } };
 private static BridgeResponse Refused(string reason)=>new() { Status="failed",ReasonCode=reason,Data=new JObject { ["phase"]="admission",["notDispatched"]=true,["dispatched"]=false } };
 private static BridgeResponse ControlOrElse(BridgeRequest r,Func<BridgeRequest,BridgeResponse> other,params string[] operations) => r.Operation switch
 {
  ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusData(operations)), _=>other(r)
 };
 private async Task<Rig> Acquire(params string[] operations)
 {
  var rig=Make();
  var reply=JObject.Parse(await rig.Control.ControlAcquireLease("build probe",300)); Assert.AreEqual("completed",(string?)reply["Status"]);
  return rig;
 }
 private static async Task<JObject> Load(Rig rig,string requestId="load-0001",string facility="VAB",string fileName=File,string? hash=null,bool allowUpgrade=false,string leaseId=Lease)
 => JObject.Parse(await rig.Tools.EditorLoadCraft(requestId,leaseId,Token,facility,fileName,hash ?? Hash,allowUpgrade));

 [DataTestMethod]
 [DataRow("short","VAB",File,"a")] [DataRow("load-0001","LAUNCHPAD",File,"a")] [DataRow("load-0001","vab",File,"a")]
 [DataRow("load-0001","VAB","..\\x.craft","a")] [DataRow("load-0001","VAB","../x.craft","a")] [DataRow("load-0001","VAB","sub/x.craft","a")] [DataRow("load-0001","VAB","C:\\x.craft","a")]
 [DataRow("load-0001","VAB","no extension","a")] [DataRow("load-0001","VAB",".craft","a")] [DataRow("load-0001","VAB","bad*name.craft","a")] [DataRow("load-0001","VAB",File,"ABC")]
 [DataRow("load-0001","VAB",File,"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
 public async Task ArgumentsOutOfBoundsAreInvalidWithoutASocket(string requestId,string facility,string fileName,string hashKind)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Load(rig,requestId,facility,fileName,hashKind=="a" ? null : hashKind);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]);
  Assert.AreEqual(0,bridge.Connections); Assert.IsFalse(Directory.Exists(dir),"the journal was never opened");
 }

 [TestMethod] public async Task WithoutALeaseNothingIsSent()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Load(rig);
  Assert.AreEqual("lease_required",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!); Assert.AreEqual(0,bridge.Connections);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AGrantWithoutTheReplaceFamilyDeniesBeforeAnySocket()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),OperationEffects.RestoreSnapshot)); var rig=await Acquire(OperationEffects.RestoreSnapshot); var before=bridge.Connections;
  var result=await Load(rig);
  Assert.AreEqual("grant_operation_denied",(string?)result["ReasonCode"]); Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ASuccessfulLoadForwardsEveryArgumentAndIsJournaledUnderTheReplaceEffect()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.LoadCraft ? Running((string)q.Arguments["requestId"]!,"locking") : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire();
  var result=await Load(rig,allowUpgrade:true);
  Assert.AreEqual("completed",(string?)result["Status"]); Assert.AreEqual(true,(bool)result["Data"]!["comparison"]!["equal"]!);
  var request=bridge.Requests.Single(r=>r.Operation==EditorOperations.LoadCraft);
  Assert.AreEqual(Lease,request.LeaseId); Assert.AreEqual("load-0001",(string?)request.Arguments["requestId"]); Assert.AreEqual(Token,(string?)request.Arguments["expectedRevision"]);
  Assert.AreEqual("VAB",(string?)request.Arguments["facility"]); Assert.AreEqual(File,(string?)request.Arguments["fileName"]); Assert.AreEqual(Hash,(string?)request.Arguments["expectedSha256"]); Assert.AreEqual(true,(bool)request.Arguments["allowUpgrade"]!);
  var job=rig.Journal.TryGet()!.Get("load-0001"); Assert.AreEqual("completed",job.Status); Assert.AreEqual(OperationEffects.ReplaceCraft,job.Operation);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AllowUpgradeDefaultsToFalse()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.LoadCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire();
  var result=JObject.Parse(await rig.Tools.EditorLoadCraft("load-0001",Lease,Token,"VAB",File,Hash));
  Assert.AreEqual("completed",(string?)result["Status"]);
  Assert.AreEqual(false,(bool)bridge.Requests.Single(r=>r.Operation==EditorOperations.LoadCraft).Arguments["allowUpgrade"]!); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ARequiresUpgradeRefusalIsAGuaranteedNoEffectAndIsAnsweredFromTheJournalOnRetry()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.LoadCraft ? Running((string)q.Arguments["requestId"]!,"staging")
   : new BridgeResponse { Status="failed",ReasonCode=LoadReasons.CraftRequiresUpgrade,Data=new JObject { ["requestId"]=(string)q.Arguments["requestId"]!,["notDispatched"]=true,["upgradedOnLoad"]=true,["pipelineDifferences"]=new JArray(new JObject { ["path"]="x" }) } }));
  var rig=await Acquire();
  var result=await Load(rig);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("craft_requires_upgrade",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!);
  Assert.AreEqual(1,((JArray)result["Data"]!["pipelineDifferences"]!).Count);
  var again=await Load(rig); Assert.AreEqual("craft_requires_upgrade",(string?)again["ReasonCode"]);
  Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==EditorOperations.LoadCraft),"the retry did not reach the game again"); rig.Journal.Dispose();
 }

 [DataTestMethod] [DataRow("craft_invalid_links")] [DataRow("module_not_installed")] [DataRow("craft_parts_missing")] [DataRow("craft_upgrade_failed")] [DataRow("file_changed")]
 public async Task PreDispatchRefusalsFromTheBridgeCancelTheJobWithTheirReason(string reason)
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=>Refused(reason)));
  var rig=await Acquire();
  var result=await Load(rig);
  Assert.AreEqual(reason,(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!);
  var job=rig.Journal.TryGet()!.Get("load-0001"); Assert.AreEqual("cancelled",job.Status); Assert.AreEqual("not_dispatched",job.Reason); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ADifferentRequestUnderTheSameIdIsAConflictWithoutASocket()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.LoadCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire(); await Load(rig); var calls=bridge.Requests.Count;
  Assert.AreEqual("request_id_conflict",(string?)(await Load(rig,allowUpgrade:true))["ReasonCode"]);
  Assert.AreEqual("request_id_conflict",(string?)(await Load(rig,hash:new string('b',64)))["ReasonCode"]);
  Assert.AreEqual(calls,bridge.Requests.Count); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ALoadAndAnApplyShareNoRequestId()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.LoadCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire(); await Load(rig);
  var apply=JObject.Parse(await new MutationTools(new MutationService(rig.Bridge,rig.Keeper,rig.Journal)).EditorApplyCraft("load-0001",Lease,Token,"{\"name\":\"Probe One\",\"facility\":\"VAB\",\"root\":\"pod\",\"parts\":[{\"id\":\"pod\",\"part\":\"mk1pod.v2\"}]}",Hash));
  Assert.AreEqual("request_id_conflict",(string?)apply["ReasonCode"]); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ALostConnectionAfterDispatchIsIndeterminateAndNamesTheLoad()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.LoadCraft ? Running((string)q.Arguments["requestId"]!) : FakeBridge.Fail("bridge_unreachable")));
  var rig=await Acquire();
  var result=await Load(rig);
  Assert.AreEqual("indeterminate",(string?)result["Status"]); Assert.AreEqual("load_craft",(string?)result["Data"]!["operation"]); Assert.AreEqual(false,(bool)result["Data"]!["notDispatched"]!);
  Assert.AreEqual("indeterminate",rig.Journal.TryGet()!.Get("load-0001").Status); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AFailedAfterDispatchLoadKeepsItsEnvelope()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.LoadCraft ? Running((string)q.Arguments["requestId"]!)
   : new BridgeResponse { Status="failed",ReasonCode=LoadReasons.SourceChangedDuringLoad,Data=new JObject { ["requestId"]=(string)q.Arguments["requestId"]!,["notDispatched"]=false,["load"]=new JObject { ["sourceUnchanged"]=false } } }));
  var rig=await Acquire();
  var result=await Load(rig);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("source_changed_during_load",(string?)result["ReasonCode"]); Assert.AreEqual(false,(bool)result["Data"]!["load"]!["sourceUnchanged"]!);
  Assert.AreEqual("failed",rig.Journal.TryGet()!.Get("load-0001").Status); rig.Journal.Dispose();
 }

 [TestMethod] public async Task JobStatusAnswersALoadFromTheJournal()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.LoadCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire(); await Load(rig); var calls=bridge.Requests.Count;
  var status=JObject.Parse(await new MutationTools(new MutationService(rig.Bridge,rig.Keeper,rig.Journal)).JobStatus("load-0001",1));
  Assert.AreEqual("completed",(string?)status["Status"]); Assert.AreEqual(calls,bridge.Requests.Count); rig.Journal.Dispose();
 }

 [TestMethod] public void TheLoadOperationIsAMutationOnlyReachableThroughTheJournal()
 {
  CollectionAssert.Contains(EditorOperations.Mutations,EditorOperations.LoadCraft);
  Assert.IsTrue(BridgeClient.MutationOperations.Contains(EditorOperations.LoadCraft));
  Assert.IsFalse(BridgeClient.ReadOperations.Contains(EditorOperations.LoadCraft));
 }
}
