using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;

/// <summary>editor_save_craft and craft_list on the host: argument bounds, the craft.write family, the journal path and the read-only listing.</summary>
[TestClass] [DoNotParallelize] public class SaveToolsTests
{
 private const string Lease="0123456789abcdef0123456789abcdef";
 private const string Token="djF8ZXBvY2gtOXwxfDR8YWJjZGVmMDEyMzQ1";
 private static readonly string Hash=new('a',64);
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-save-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ try { if(Directory.Exists(dir)) Directory.Delete(dir,true); } catch { } }

 private sealed record Rig(BridgeClient Bridge,LeaseKeeper Keeper,JournalAccess Journal,ControlTools Control,MutationService Service,MutationTools Tools,ObservationTools Observe);
 private Rig Make()
 {
  var bridge=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(bridge)); var journal=new JournalAccess(dir);
  var service=new MutationService(bridge,keeper,journal) { PollInterval=TimeSpan.FromMilliseconds(5) };
  return new Rig(bridge,keeper,journal,new ControlTools(bridge,keeper,journal),service,new MutationTools(service),new ObservationTools(bridge));
 }
 private static JObject Acquired()=>new() { ["leaseId"]=Lease,["purpose"]="save probe",["expiresInSeconds"]=300,["grantId"]="grant-1",["generation"]=3,["epoch"]="epoch-9",["entity"]="editor:VAB" };
 private static JObject StatusData(params string[] operations)=>new() { ["grant"]=new JObject { ["state"]="valid",["id"]="grant-1",["generation"]=3,["operations"]=new JArray(operations.Length==0 ? new[]{ OperationEffects.ReplaceCraft,OperationEffects.RestoreSnapshot,OperationEffects.WriteCraft } : operations),["facilities"]=new JArray("VAB"),["expiresUtc"]="2099-01-01T00:00:00.000Z" },["lease"]=new JObject { ["held"]=false },["cooldownSeconds"]=0 };
 private static BridgeResponse Running(string id)=>new() { Status="running",Data=new JObject { ["operation"]="save_craft",["requestId"]=id,["phase"]="save",["notDispatched"]=false } };
 private static BridgeResponse Done(string id)=>new() { Status="completed",Data=new JObject { ["operation"]="save_craft",["requestId"]=id,["phase"]="done",["notDispatched"]=false,["savedPath"]="C:\\ksp\\saves\\S\\Ships\\VAB\\Probe.craft",["sha256"]=Hash,["editorRevision"]="newtoken0123" } };
 private static BridgeResponse ControlOrElse(BridgeRequest r,Func<BridgeRequest,BridgeResponse> other,params string[] operations)=>r.Operation switch { ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusData(operations)), _=>other(r) };
 private async Task<Rig> Acquire(params string[] operations)
 {
  var rig=Make(); var reply=JObject.Parse(await rig.Control.ControlAcquireLease("save probe",300)); Assert.AreEqual("completed",(string?)reply["Status"]); return rig;
 }
 private static async Task<JObject> Save(Rig rig,string requestId="save-0001",string name="Probe One",string? replace=null,string revision=Token)=>JObject.Parse(await rig.Tools.EditorSaveCraft(requestId,Lease,revision,name,replace));

 [DataTestMethod]
 [DataRow("short",Lease,Token,"Probe")] [DataRow("save-0001","short",Token,"Probe")] [DataRow("save-0001",Lease,"has space","Probe")] [DataRow("save-0001",Lease,"","Probe")]
 [DataRow("save-0001",Lease,Token,"")] [DataRow("save-0001",Lease,Token,"Probe.craft")] [DataRow("save-0001",Lease,Token,"..\\evil")] [DataRow("save-0001",Lease,Token,"a/b")] [DataRow("save-0001",Lease,Token,".hidden")] [DataRow("save-0001",Lease,Token,"CON")]
 public async Task SaveArgumentsOutOfBoundsAreInvalidWithoutASocketOrAJournal(string requestId,string leaseId,string revision,string name)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=JObject.Parse(await rig.Tools.EditorSaveCraft(requestId,leaseId,revision,name));
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections); Assert.IsFalse(Directory.Exists(dir));
 }

 [DataTestMethod] [DataRow("ABC")] [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] [DataRow("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
 public async Task ABadReplaceHashIsInvalid(string hash)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Save(rig,replace:hash);
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); StringAssert.Contains((string?)result["Data"]!["detail"],"replaceExpectedSha256"); Assert.AreEqual(0,bridge.Connections);
 }

 [TestMethod] public async Task WithoutALeaseTheSaveIsLeaseRequired()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Save(rig);
  Assert.AreEqual("lease_required",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!); Assert.AreEqual(0,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ASaveNeedsTheCraftWriteFamilyAndAnApplyDoesNot()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>FakeBridge.Ok(new JObject()),OperationEffects.ReplaceCraft)); var rig=await Acquire(OperationEffects.ReplaceCraft); var before=bridge.Connections;
  var result=await Save(rig);
  Assert.AreEqual("grant_operation_denied",(string?)result["ReasonCode"]); StringAssert.Contains((string?)result["Data"]!["detail"],"craft.write"); Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ASuccessfulSaveIsJournaledUnderCraftWriteAndSendsOnlyTheDocumentedArguments()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation switch
  {
   EditorOperations.SaveCraft=>Running((string)q.Arguments["requestId"]!),
   EditorOperations.OperationStatus=>Done((string)q.Arguments["requestId"]!),
   _=>FakeBridge.Fail("operation_unavailable")
  }));
  var rig=await Acquire();
  var result=await Save(rig);
  Assert.AreEqual("completed",(string?)result["Status"]); Assert.AreEqual("save_craft",(string?)result["Data"]!["operation"]); Assert.AreEqual(Hash,(string?)result["Data"]!["sha256"]);
  var sent=bridge.Requests.Single(r=>r.Operation==EditorOperations.SaveCraft);
  Assert.AreEqual(Lease,sent.LeaseId);
  CollectionAssert.AreEquivalent(new[]{ "requestId","fileName","expectedRevision" },sent.Arguments.Properties().Select(p=>p.Name).ToArray());
  Assert.AreEqual("Probe One",(string?)sent.Arguments["fileName"]);
  var job=rig.Journal.TryGet()!.Get("save-0001");
  Assert.AreEqual("completed",job.Status); Assert.AreEqual(OperationEffects.WriteCraft,job.Operation); StringAssert.Contains(job.Result,"savedPath");
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AReplaceHashIsForwardedAndEmptyMeansNone()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.SaveCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire();
  await Save(rig,"save-0001",replace:Hash);
  Assert.AreEqual(Hash,(string?)bridge.Requests.Single(r=>r.Operation==EditorOperations.SaveCraft && (string?)r.Arguments["requestId"]=="save-0001").Arguments["replaceExpectedSha256"]);
  await Save(rig,"save-0002",replace:"");
  Assert.IsNull(bridge.Requests.Single(r=>r.Operation==EditorOperations.SaveCraft && (string?)r.Arguments["requestId"]=="save-0002").Arguments["replaceExpectedSha256"]);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task TheSameSaveAgainAnswersFromTheJournalAndADifferentOneConflicts()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.SaveCraft ? Running((string)q.Arguments["requestId"]!) : Done((string)q.Arguments["requestId"]!)));
  var rig=await Acquire();
  await Save(rig); var again=await Save(rig); var other=await Save(rig,name:"Another");
  Assert.AreEqual("completed",(string?)again["Status"]); Assert.AreEqual(1,bridge.Requests.Count(r=>r.Operation==EditorOperations.SaveCraft),"the retry did not reach the game");
  Assert.AreEqual("request_id_conflict",(string?)other["ReasonCode"]); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ABridgeRefusalBeforeTheWriteIsACancelledNotDispatchedJob()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,_=>new BridgeResponse { Status="failed",ReasonCode="file_exists",Data=new JObject { ["requestId"]="save-0001",["phase"]="admission",["notDispatched"]=true } }));
  var rig=await Acquire();
  var result=await Save(rig);
  Assert.AreEqual("file_exists",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!);
  Assert.AreEqual("cancelled",rig.Journal.TryGet()!.Get("save-0001").Status); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnIndeterminateSaveNamesTheOperation()
 {
  using var bridge=new FakeBridge(r=>ControlOrElse(r,q=> q.Operation==EditorOperations.SaveCraft ? Running((string)q.Arguments["requestId"]!) : FakeBridge.Fail("bridge_unreachable")));
  var rig=await Acquire();
  var result=await Save(rig);
  Assert.AreEqual("indeterminate",(string?)result["Status"]); Assert.AreEqual("save_craft",(string?)result["Data"]!["operation"]); rig.Journal.Dispose();
 }

 // ---- craft_list ----

 [DataTestMethod]
 [DataRow("")] [DataRow("LAUNCHPAD")] [DataRow("vab")]
 public async Task CraftListNeedsAShipFacility(string facility)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  Assert.AreEqual("invalid_argument",(string?)JObject.Parse(await rig.Observe.CraftList(facility))["ReasonCode"]); Assert.AreEqual(0,bridge.Connections);
 }

 [DataTestMethod] [DataRow(-1,10)] [DataRow(0,0)] [DataRow(0,51)] [DataRow(100001,10)]
 public async Task CraftListPagingIsBoundedWithoutASocket(int offset,int limit)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  Assert.AreEqual("invalid_argument",(string?)JObject.Parse(await rig.Observe.CraftList("VAB",offset,limit))["ReasonCode"]);
  Assert.AreEqual("invalid_argument",(string?)JObject.Parse(await rig.Observe.CraftList("VAB",0,10,new string('a',129)))["ReasonCode"]); Assert.AreEqual(0,bridge.Connections);
 }

 [TestMethod] public async Task CraftListIsAReadOperationThatSendsNoLease()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject { ["crafts"]=new JArray() })); var rig=Make();
  var result=JObject.Parse(await rig.Observe.CraftList("VAB",5,7,"probe"));
  Assert.AreEqual("completed",(string?)result["Status"]);
  var sent=bridge.Requests.Single(); Assert.AreEqual(CraftOperations.List,sent.Operation); Assert.IsNull(sent.LeaseId);
  Assert.AreEqual("VAB",(string?)sent.Arguments["facility"]); Assert.AreEqual(5,(int)sent.Arguments["offset"]!); Assert.AreEqual(7,(int)sent.Arguments["limit"]!); Assert.AreEqual("probe",(string?)sent.Arguments["filter"]);
  Assert.IsTrue(BridgeClient.ReadOperations.Contains(CraftOperations.List)); Assert.IsFalse(BridgeClient.MutationOperations.Contains(CraftOperations.List));
 }
}
