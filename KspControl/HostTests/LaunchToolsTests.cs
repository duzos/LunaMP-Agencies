using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class LaunchToolsTests
{
 private const string Lease="0123456789abcdef0123456789abcdef";
 private const string Token="djF8ZXBvY2gtOXwxfDR8YWJjZGVmMDEyMzQ1";
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-launch-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ try { if(Directory.Exists(dir)) Directory.Delete(dir,true); } catch { } }

 private sealed record Rig(BridgeClient Bridge,LeaseKeeper Keeper,JournalAccess Journal,ControlTools Control,MutationService Service,LaunchTools Tools);
 private Rig Make()
 {
  var bridge=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(bridge)); var journal=new JournalAccess(dir);
  var service=new MutationService(bridge,keeper,journal) { PollInterval=TimeSpan.FromMilliseconds(5) };
  return new Rig(bridge,keeper,journal,new ControlTools(bridge,keeper,journal),service,new LaunchTools(service));
 }
 private static JObject Acquired()=>new() { ["leaseId"]=Lease,["purpose"]="launch probe",["expiresInSeconds"]=300,["grantId"]="grant-1",["generation"]=3,["epoch"]="epoch-9",["entity"]="editor:VAB" };
 private static JObject StatusData(long? spend,params string[] operations)
 {
  var grant=new JObject { ["state"]="valid",["id"]="grant-1",["generation"]=3,["operations"]=new JArray(operations.Length==0 ? new[]{ OperationEffects.Launch } : operations),["facilities"]=new JArray("VAB"),["expiresUtc"]="2099-01-01T00:00:00.000Z" };
  if(spend.HasValue) { grant["spendLimitFunds"]=spend.Value; grant["effectiveSpendCap"]=spend.Value; }
  return new JObject { ["grant"]=grant,["lease"]=new JObject { ["held"]=false },["cooldownSeconds"]=0 };
 }
 private static BridgeResponse Running(string requestId)=>new() { Status="running",Data=new JObject { ["operation"]="launch",["requestId"]=requestId,["phase"]="await_flight",["notDispatched"]=false } };
 private static BridgeResponse Done(string requestId,double? charge=1200,bool released=true,string epoch="epoch-10")=>new()
 {
  Status="completed",Data=new JObject { ["operation"]="launch",["requestId"]=requestId,["phase"]="done",["notDispatched"]=false,["charge"]=charge==null ? null : charge,["chargeSource"]="tooling_result",["vesselId"]="11111111-2222-3333-4444-555555555555",["leaseReleased"]=released,["leaseContinues"]=!released,["epoch"]=epoch }
 };
 private static string RequestIdOf(BridgeRequest r)=>(string)r.Arguments["requestId"]!;
 private static BridgeResponse Control(BridgeRequest r,long? spend,Func<BridgeRequest,BridgeResponse> other,params string[] operations)=>r.Operation switch
 {
  ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusData(spend,operations)), _=>other(r)
 };
 private async Task<Rig> Acquire(long? spend,params string[] operations)
 {
  var rig=Make();
  var reply=JObject.Parse(await rig.Control.ControlAcquireLease("launch probe",300)); Assert.AreEqual("completed",(string?)reply["Status"]);
  Assert.IsNotNull(rig.Journal.CurrentGrant); return rig;
 }
 private static async Task<JObject> Launch(Rig rig,string requestId="launch-0001",int max=5000,string site="LaunchPad",string lease=Lease)
 => JObject.Parse(await rig.Tools.EditorLaunch(requestId,lease,Token,site,max));
 private static (decimal Charged,decimal Reserved) Spend(Rig rig)=>rig.Journal.TryGet()!.SpendFor("grant-1");
 private static int LaunchCalls(FakeBridge bridge)=>bridge.Requests.Count(r=>r.Operation==EditorOperations.Launch);

 // ---- argument bounds and grant family, before any mutation socket ----

 [DataTestMethod]
 [DataRow("short","LaunchPad",5000)] [DataRow("launch-0001","",5000)] [DataRow("launch-0001","Launch\u0001Pad",5000)] [DataRow("launch-0001","LaunchPad",-1)] [DataRow("launch-0001","LaunchPad",1000000001)]
 public async Task OutOfBoundsArgumentsAreInvalidWithoutASocket(string requestId,string site,int max)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Launch(rig,requestId,max,site);
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections); Assert.IsFalse(Directory.Exists(dir),"the journal was never opened");
 }

 [TestMethod] public async Task AnOverlongSiteNameIsInvalid()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  Assert.AreEqual("invalid_argument",(string?)(await Launch(rig,site:new string('s',OperationLimits.LaunchSiteMax+1)))["ReasonCode"]); Assert.AreEqual(0,bridge.Connections);
 }

 [TestMethod] public async Task WithoutALeaseTheLaunchIsLeaseRequiredAndNothingIsSent()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var rig=Make();
  var result=await Launch(rig);
  Assert.AreEqual("lease_required",(string?)result["ReasonCode"]); Assert.AreEqual(true,(bool)result["Data"]!["notDispatched"]!); Assert.AreEqual(0,bridge.Connections);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AGrantWithoutTheLaunchFamilyDeniesBeforeAnySocket()
 {
  using var bridge=new FakeBridge(r=>Control(r,9000,_=>FakeBridge.Ok(new JObject()),OperationEffects.ReplaceCraft)); var rig=await Acquire(9000,OperationEffects.ReplaceCraft); var before=bridge.Connections;
  var result=await Launch(rig);
  Assert.AreEqual("grant_operation_denied",(string?)result["ReasonCode"]); Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnotherLeaseIdIsLeaseInvalid()
 {
  using var bridge=new FakeBridge(r=>Control(r,9000,_=>FakeBridge.Ok(new JObject()))); var rig=await Acquire(9000); var before=bridge.Connections;
  var result=await Launch(rig,lease:new string('b',32));
  Assert.AreEqual("lease_invalid",(string?)result["ReasonCode"]); Assert.AreEqual(before,bridge.Connections); rig.Journal.Dispose();
 }

 // ---- the spend cap and the reservation ----

 [TestMethod] public async Task TheBridgeIsAskedWithTheSiteAndTheReservedCeiling()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>Done(RequestIdOf(x)))); var rig=await Acquire(8000);
  var result=await Launch(rig,max:5000,site:"Runway");
  Assert.AreEqual("completed",(string?)result["Status"],result.ToString());
  var sent=bridge.Requests.Single(r=>r.Operation==EditorOperations.Launch);
  Assert.AreEqual(Lease,sent.LeaseId); Assert.AreEqual("Runway",(string?)sent.Arguments["launchSite"]); Assert.AreEqual(5000L,(long?)sent.Arguments["maxSpendFunds"]); Assert.AreEqual(Token,(string?)sent.Arguments["expectedRevision"]);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task GrossSpendAccumulatesAcrossRetriesAndTheCapIsNeverExceeded()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>Done(RequestIdOf(x),1200,released:false))); var rig=await Acquire(8000);
  Assert.AreEqual("completed",(string?)(await Launch(rig,"launch-0001",5000))["Status"]);
  Assert.AreEqual((1200m,0m),Spend(rig),"the unspent part of the reservation is released, the charge is gross");
  var over=await Launch(rig,"launch-0002",7000);
  Assert.AreEqual("spend_cap_exceeded",(string?)over["ReasonCode"]); Assert.AreEqual(true,(bool)over["Data"]!["notDispatched"]!);
  Assert.AreEqual(1,LaunchCalls(bridge),"the refusal never reached the bridge");
  Assert.AreEqual("completed",(string?)(await Launch(rig,"launch-0003",6800))["Status"]);
  Assert.AreEqual((2400m,0m),Spend(rig));
  var last=await Launch(rig,"launch-0004",5601);
  Assert.AreEqual("spend_cap_exceeded",(string?)last["ReasonCode"],"2400 spent plus 5601 would pass the 8000 cap");
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AGrantWithoutASpendLimitAuthorisesNoSpend()
 {
  using var bridge=new FakeBridge(r=>Control(r,null,x=>Done(RequestIdOf(x)))); var rig=await Acquire(null);
  Assert.AreEqual("spend_cap_exceeded",(string?)(await Launch(rig,max:1))["ReasonCode"]);
  Assert.AreEqual(0,LaunchCalls(bridge)); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ARefusalThatNeverActedReleasesTheWholeReservation()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>new BridgeResponse { Status="failed",ReasonCode="spend_exceeds_max",Data=new JObject { ["requestId"]=RequestIdOf(x),["phase"]="quote",["notDispatched"]=true } })); var rig=await Acquire(8000);
  var result=await Launch(rig,max:5000);
  Assert.AreEqual("spend_exceeds_max",(string?)result["ReasonCode"]);
  Assert.AreEqual("cancelled",rig.Journal.TryGet()!.Get("launch-0001").Status); Assert.AreEqual((0m,0m),Spend(rig));
  Assert.AreEqual("spend_exceeds_max",(string?)(await Launch(rig,"launch-0002",8000))["ReasonCode"],"the full cap is available again, so this one reaches the bridge");
  Assert.AreEqual(2,LaunchCalls(bridge)); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ACancelledLaunchAfterAConfirmedRefundReleasesTheReservationWithoutCharge()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>new BridgeResponse { Status="cancelled",ReasonCode="authority_revoked",Data=new JObject { ["requestId"]=RequestIdOf(x),["phase"]="done",["notDispatched"]=false,["refundConfirmed"]=true,["charge"]=1000 } })); var rig=await Acquire(8000);
  var result=await Launch(rig,max:5000);
  Assert.AreEqual("cancelled",(string?)result["Status"]); Assert.AreEqual((0m,0m),Spend(rig),"a refunded reservation costs nothing, whatever the envelope says about the interim charge");
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnIndeterminateLaunchKeepsItsReservationHeld()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>FakeBridge.Fail("bridge_unreachable"))); var rig=await Acquire(8000);
  var result=await Launch(rig,max:5000);
  Assert.AreEqual("indeterminate",(string?)result["Status"],result.ToString()); Assert.AreEqual("launch",(string?)result["Data"]!["operation"]);
  Assert.AreEqual((0m,5000m),Spend(rig));
  Assert.AreEqual("spend_cap_exceeded",(string?)(await Launch(rig,"launch-0002",3500))["ReasonCode"],"the held 5000 plus 3500 passes the 8000 cap");
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task ARunningLaunchIsFollowedThroughJobStatusAndSettlesOnCompletion()
 {
  var finished=false;
  using var bridge=new FakeBridge(r=>Control(r,8000,x=> x.Operation==EditorOperations.Launch || !finished ? Running(RequestIdOf(x)) : Done(RequestIdOf(x),1150)));
  var rig=await Acquire(8000);
  using var shortWait=new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
  var first=JObject.Parse(await rig.Tools.EditorLaunch("launch-0001",Lease,Token,"LaunchPad",5000,shortWait.Token));
  // The short wait may end on a cancelled poll, which is reported as that poll's failure; what matters is that the launch has not completed and its reservation is held.
  Assert.AreNotEqual("completed",(string?)first["Status"],first.ToString()); Assert.AreEqual((0m,5000m),Spend(rig),"reserved while running");
  finished=true;
  var status=JObject.Parse(await new MutationTools(rig.Service).JobStatus("launch-0001",5));
  Assert.AreEqual("completed",(string?)status["Status"],status.ToString());
  Assert.AreEqual((1150m,0m),Spend(rig)); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ARetriedRequestIdAnswersFromTheJournalAndNeitherLaunchesNorReservesAgain()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>Done(RequestIdOf(x),1200,released:false))); var rig=await Acquire(8000);
  var first=await Launch(rig); var again=await Launch(rig);
  Assert.AreEqual("completed",(string?)again["Status"]); Assert.AreEqual((string?)first["Data"]!["vesselId"],(string?)again["Data"]!["vesselId"]);
  Assert.AreEqual(1,LaunchCalls(bridge)); Assert.AreEqual((1200m,0m),Spend(rig));
  var conflict=await Launch(rig,max:4000);
  Assert.AreEqual("request_id_conflict",(string?)conflict["ReasonCode"]); Assert.AreEqual(1,LaunchCalls(bridge)); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ACompletionWithoutAChargeIsTreatedAsTheFullReservation()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>Done(RequestIdOf(x),null))); var rig=await Acquire(8000);
  Assert.AreEqual("completed",(string?)(await Launch(rig,max:5000))["Status"]);
  Assert.AreEqual((5000m,0m),Spend(rig),"never less than was reserved when the actual charge is unknown"); rig.Journal.Dispose();
 }

 [TestMethod] public async Task AChargeAboveTheReservationIsStillRecordedGross()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>Done(RequestIdOf(x),5300.4))); var rig=await Acquire(8000);
  Assert.AreEqual("completed",(string?)(await Launch(rig,max:5000))["Status"]);
  Assert.AreEqual((5300.4m,0m),Spend(rig)); rig.Journal.Dispose();
 }

 // ---- indeterminate launches settle from the bridge's own envelope ----

 private static BridgeResponse Env(string status,string requestId,string? reason=null,bool? refund=null,bool notDispatched=false,double? charge=null)
 {
  var data=new JObject { ["operation"]="launch",["requestId"]=requestId,["phase"]="done",["notDispatched"]=notDispatched };
  if(refund.HasValue) data["refundConfirmed"]=refund.Value; if(charge.HasValue) data["charge"]=charge.Value;
  return new BridgeResponse { Status=status,ReasonCode=reason,Data=data };
 }

 [TestMethod] public async Task AnIndeterminateLaunchSettlesAsCompletedFromTheBridgesOwnEnvelope()
 {
  BridgeResponse? status=null;
  using var bridge=new FakeBridge(r=>Control(r,8000,x=> x.Operation==EditorOperations.Launch ? FakeBridge.Fail("bridge_unreachable") : status!));
  var rig=await Acquire(8000);
  Assert.AreEqual("indeterminate",(string?)(await Launch(rig,max:5000))["Status"]);
  status=Env("completed","launch-0001",charge:1300);
  var result=JObject.Parse(await new MutationTools(rig.Service).JobStatus("launch-0001",2));
  Assert.AreEqual("completed",(string?)result["Status"]); Assert.AreEqual((1300m,0m),Spend(rig)); Assert.AreEqual("completed",rig.Journal.TryGet()!.Get("launch-0001").Status);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnIndeterminateLaunchReleasesOnlyOnAConfirmedRefundOrProofItNeverRan()
 {
  BridgeResponse? status=null;
  using var bridge=new FakeBridge(r=>Control(r,8000,x=> x.Operation==EditorOperations.Launch ? FakeBridge.Fail("bridge_unreachable") : status!));
  var rig=await Acquire(8000);
  Assert.AreEqual("indeterminate",(string?)(await Launch(rig,max:5000))["Status"]);
  status=Env("cancelled","launch-0001","authority_revoked",refund:false);
  await new MutationTools(rig.Service).JobStatus("launch-0001",2);
  Assert.AreEqual((0m,5000m),Spend(rig),"an unconfirmed refund keeps the hold");
  status=Env("cancelled","launch-0001","authority_revoked",refund:true);
  await new MutationTools(rig.Service).JobStatus("launch-0001",2);
  Assert.AreEqual((0m,0m),Spend(rig)); Assert.AreEqual("cancelled",rig.Journal.TryGet()!.Get("launch-0001").Status);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task AnEnvelopeForAnotherRequestOrAStillIndeterminateOneNeverSettles()
 {
  BridgeResponse? status=null;
  using var bridge=new FakeBridge(r=>Control(r,8000,x=> x.Operation==EditorOperations.Launch ? FakeBridge.Fail("bridge_unreachable") : status!));
  var rig=await Acquire(8000);
  Assert.AreEqual("indeterminate",(string?)(await Launch(rig,max:5000))["Status"]);
  status=Env("completed","launch-9999",charge:1);
  await new MutationTools(rig.Service).JobStatus("launch-0001",2);
  status=Env("indeterminate","launch-0001","disconnected");
  await new MutationTools(rig.Service).JobStatus("launch-0001",2);
  Assert.AreEqual((0m,5000m),Spend(rig)); Assert.AreEqual("indeterminate",rig.Journal.TryGet()!.Get("launch-0001").Status);
  rig.Journal.Dispose();
 }

 [TestMethod] public async Task ANotDispatchedFailureReleasesAnIndeterminateHold()
 {
  BridgeResponse? status=null;
  using var bridge=new FakeBridge(r=>Control(r,8000,x=> x.Operation==EditorOperations.Launch ? FakeBridge.Fail("bridge_unreachable") : status!));
  var rig=await Acquire(8000);
  Assert.AreEqual("indeterminate",(string?)(await Launch(rig,max:5000))["Status"]);
  status=Env("failed","launch-0001","launch_not_started",notDispatched:true);
  await new MutationTools(rig.Service).JobStatus("launch-0001",2);
  Assert.AreEqual((0m,0m),Spend(rig)); rig.Journal.Dispose();
 }

 // ---- the standing cap the bridge reports ----

 private static JObject StatusWithCap(long spend,long? effective,params string[] operations)
 {
  var data=StatusData(spend,operations); var grant=(JObject)data["grant"]!; grant.Remove("effectiveSpendCap"); if(effective.HasValue) grant["effectiveSpendCap"]=effective.Value; return data;
 }

 [TestMethod] public async Task TheHostUsesTheSmallerOfTheSignedLimitAndTheBridgesStandingCap()
 {
  using var bridge=new FakeBridge(r=>r.Operation switch { ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusWithCap(90000,20000)), _=>Done(RequestIdOf(r),100,released:false) });
  var rig=await Acquire(90000);
  Assert.AreEqual("spend_cap_exceeded",(string?)(await Launch(rig,max:20001))["ReasonCode"]);
  Assert.AreEqual("completed",(string?)(await Launch(rig,"launch-0002",20000))["Status"]); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ALaunchGrantWithoutAStandingCapAuthorisesNothing()
 {
  using var bridge=new FakeBridge(r=>r.Operation switch { ControlOperations.Acquire=>FakeBridge.Ok(Acquired()), ControlOperations.Status=>FakeBridge.Ok(StatusWithCap(90000,null)), _=>Done(RequestIdOf(r),100) });
  var rig=await Acquire(90000);
  Assert.AreEqual("spend_cap_exceeded",(string?)(await Launch(rig,max:1))["ReasonCode"]); Assert.AreEqual(0,LaunchCalls(bridge)); rig.Journal.Dispose();
 }

 // ---- the lease after the launch ----

 [TestMethod] public async Task ASuccessThatReleasedTheLeaseEndsTheHostSessionLease()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>Done(RequestIdOf(x),1200,released:true))); var rig=await Acquire(8000);
  Assert.AreEqual(Lease,rig.Keeper.TrackedLease);
  Assert.AreEqual("completed",(string?)(await Launch(rig))["Status"]);
  Assert.IsNull(rig.Keeper.TrackedLease); Assert.IsNull(rig.Journal.CurrentGrant); Assert.IsNull(rig.Journal.TryGet()!.CurrentLease); rig.Journal.Dispose();
 }

 [TestMethod] public async Task ASuccessThatKeepsTheLeaseAdoptsTheNewWorldEpoch()
 {
  using var bridge=new FakeBridge(r=>Control(r,8000,x=>Done(RequestIdOf(x),1200,released:false,epoch:"epoch-10"))); var rig=await Acquire(8000);
  Assert.AreEqual("completed",(string?)(await Launch(rig))["Status"]);
  Assert.AreEqual(Lease,rig.Keeper.TrackedLease); Assert.AreEqual("epoch-10",rig.Journal.TryGet()!.CurrentLease!.WorldEpoch); rig.Journal.Dispose();
 }
}
