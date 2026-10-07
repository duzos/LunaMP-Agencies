using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class ControlToolsTests
{
 private const string Lease="0123456789abcdef0123456789abcdef";
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-control-tool-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ try { if(Directory.Exists(dir)) Directory.Delete(dir,true); } catch { } }
 private (ControlTools Tools,LeaseKeeper Keeper,JournalAccess Journal) Make()
 { var bridge=new BridgeClient(); var keeper=new LeaseKeeper(new BridgeBeatSender(bridge)); var journal=new JournalAccess(dir); return (new ControlTools(bridge,keeper,journal),keeper,journal); }
 private static JObject Acquired(string lease=Lease)=>new() { ["leaseId"]=lease,["purpose"]="build probe",["expiresInSeconds"]=60,["grantId"]="grant-1",["generation"]=3,["epoch"]="epoch-9",["entity"]="editor:VAB" };
 private static JObject StatusData()=>new() { ["grant"]=new JObject { ["state"]="valid",["id"]="grant-1",["generation"]=3,["operations"]=new JArray("editor.replace_craft"),["facilities"]=new JArray("VAB"),["expiresUtc"]="2099-01-01T00:00:00.000Z" },["lease"]=new JObject { ["held"]=false },["cooldownSeconds"]=0 };

 [DataTestMethod]
 [DataRow("acquire","",60,"purpose")] [DataRow("acquire","   ",60,"purpose")] [DataRow("acquire","x",29,"durationSeconds")] [DataRow("acquire","x",301,"durationSeconds")]
 [DataRow("acquire","bad\u0007control",60,"purpose")]
 [DataRow("renew","short",60,"leaseId")] [DataRow("renew","0123456789abcdef0123456789abcdeg",60,"leaseId")] [DataRow("renew",Lease,29,"durationSeconds")] [DataRow("renew",Lease,301,"durationSeconds")]
 [DataRow("release","",0,"leaseId")] [DataRow("release",Lease+"0",0,"leaseId")]
 public async Task InvalidArgumentsReturnStructuredFailureWithoutContactingTheBridge(string tool,string text,int number,string field)
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var t=Make();
  string json=tool switch { "acquire"=>await t.Tools.ControlAcquireLease(text,number), "renew"=>await t.Tools.ControlRenewLease(text,number), _=>await t.Tools.ControlReleaseLease(text) };
  var result=JObject.Parse(json);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); StringAssert.Contains((string?)result["Data"]!["detail"],field);
  Assert.AreEqual(0,bridge.Connections,"no socket may open"); Assert.IsNull(t.Keeper.TrackedLease);
 }
 [TestMethod] public async Task AcquireOverlongPurposeIsRejected()
 { using var bridge=new FakeBridge(r=>FakeBridge.Ok(new JObject())); var result=JObject.Parse(await Make().Tools.ControlAcquireLease(new string('p',129),60)); Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]); Assert.AreEqual(0,bridge.Connections); }

 [TestMethod] public async Task AcquireTracksTheLeaseAndMirrorsBridgeIdsIntoTheJournal()
 {
  using var bridge=new FakeBridge(r=>r.Operation==ControlOperations.Acquire ? FakeBridge.Ok(Acquired()) : FakeBridge.Ok(StatusData()));
  var t=Make(); var result=JObject.Parse(await t.Tools.ControlAcquireLease("  build probe  ",60));
  Assert.AreEqual("completed",(string?)result["Status"]); Assert.AreEqual(Lease,(string?)result["Data"]!["leaseId"]);
  var request=bridge.Requests.First(r=>r.Operation==ControlOperations.Acquire);
  Assert.AreEqual(bridge.Token,request.Token); Assert.AreEqual("build probe",(string?)request.Arguments["purpose"]); Assert.AreEqual(60,(int?)request.Arguments["durationSeconds"]);
  Assert.AreEqual(Lease,t.Keeper.TrackedLease);
  var lease=t.Journal.TryGet()!.CurrentLease!; Assert.AreEqual(Lease,lease.Id,"journal mirrors the bridge lease id"); Assert.AreEqual("epoch-9",lease.WorldEpoch); Assert.AreEqual("build probe",lease.Owner);
  t.Journal.Dispose();
 }
 [TestMethod] public async Task RefusedAcquireTracksNothingAndPassesTheReasonThrough()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Fail(ControlReasons.GrantMissing)); var t=Make();
  var result=JObject.Parse(await t.Tools.ControlAcquireLease("x",60)); Assert.AreEqual("grant_missing",(string?)result["ReasonCode"]); Assert.IsNull(t.Keeper.TrackedLease);
 }
 [TestMethod] public async Task RenewSendsTheLeaseOnTheRequestAndReleaseUntracks()
 {
  using var bridge=new FakeBridge(r=>r.Operation switch { ControlOperations.Renew=>FakeBridge.Ok(new JObject { ["expiresInSeconds"]=120 }), ControlOperations.Release=>FakeBridge.Ok(new JObject { ["released"]=true }), _=>FakeBridge.Ok(Acquired()) });
  var t=Make(); await t.Tools.ControlAcquireLease("x",60);
  var renewed=JObject.Parse(await t.Tools.ControlRenewLease(Lease.ToUpperInvariant(),120)); Assert.AreEqual("completed",(string?)renewed["Status"]);
  var renew=bridge.Requests.Single(r=>r.Operation==ControlOperations.Renew); Assert.AreEqual(Lease,renew.LeaseId,"normalised to lower case"); Assert.AreEqual(120,(int?)renew.Arguments["durationSeconds"]);
  var released=JObject.Parse(await t.Tools.ControlReleaseLease(Lease)); Assert.AreEqual("completed",(string?)released["Status"]);
  Assert.IsNull(t.Keeper.TrackedLease); Assert.IsNull(t.Journal.TryGet()!.CurrentLease); t.Journal.Dispose();
 }
 [TestMethod] public async Task ALeaseGoneReplyStopsTheKeeperAndClearsTheMirror()
 {
  using var bridge=new FakeBridge(r=>r.Operation==ControlOperations.Renew ? FakeBridge.Fail(ControlReasons.LeaseExpired) : FakeBridge.Ok(Acquired()));
  var t=Make(); await t.Tools.ControlAcquireLease("x",60);
  var result=JObject.Parse(await t.Tools.ControlRenewLease(Lease,60)); Assert.AreEqual("lease_expired",(string?)result["ReasonCode"]);
  Assert.IsNull(t.Keeper.TrackedLease); Assert.IsNull(t.Journal.TryGet()!.CurrentLease); t.Journal.Dispose();
 }
 [TestMethod] public async Task StatusReflectsBridgeStateAndAddsHostLocalState()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(StatusData())); var t=Make();
  var result=JObject.Parse(await t.Tools.ControlStatus(CancellationToken.None));
  Assert.AreEqual("valid",(string?)result["Data"]!["grant"]!["state"]); Assert.AreEqual("available",(string?)result["Data"]!["host"]!["journal"]); Assert.AreEqual("none",(string?)result["Data"]!["host"]!["leaseKeeper"]!["state"]);
  t.Journal.Dispose();
 }
 [TestMethod] public async Task StatusReturnsBridgeUnreachableCleanly()
 {
  int port; using(var probe=new TcpListener(IPAddress.Loopback,0)) { probe.Start(); port=((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
  string credential=Path.GetTempFileName(); File.WriteAllText(credential,new string('x',64));
  Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",credential); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",port.ToString());
  try
  {
   var t=Make(); var result=JObject.Parse(await t.Tools.ControlStatus(CancellationToken.None));
   Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("bridge_unreachable",(string?)result["ReasonCode"]); Assert.AreEqual("available",(string?)result["Data"]!["host"]!["journal"]); t.Journal.Dispose();
  }
  finally { Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",null); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",null); File.Delete(credential); }
 }
 [TestMethod] public async Task StatusReportsAJournalHeldByAnotherHost()
 {
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(StatusData()));
  using var other=new ControlJournal(dir); var t=Make();
  var result=JObject.Parse(await t.Tools.ControlStatus(CancellationToken.None)); Assert.AreEqual("locked_by_other_host",(string?)result["Data"]!["host"]!["journal"]);
 }
 [TestMethod] public async Task NoResponseEverCarriesGrantPayloadMacOrKeyMaterial()
 {
  var hostile=StatusData(); hostile["payload"]="AAAA"; hostile["grant"]!["mac"]="BBBB"; hostile["grant"]!["key"]="CCCC"; hostile["lease"]!["secret"]="DDDD"; hostile["nested"]=new JArray(new JObject { ["Payload"]="EEEE" });
  using var bridge=new FakeBridge(r=>FakeBridge.Ok(hostile)); var t=Make();
  string text=await t.Tools.ControlStatus(CancellationToken.None);
  foreach(var needle in new[]{"AAAA","BBBB","CCCC","DDDD","EEEE","\"payload\"","\"mac\"","\"key\"","\"secret\""}) Assert.IsFalse(text.Contains(needle,StringComparison.OrdinalIgnoreCase),needle);
  t.Journal.Dispose();
 }
}
[TestClass] public class BridgeClientAllowlistTests
{
 [TestMethod] public void ReadControlAndMutationAllowlistsAreDisjointAndMutationsAreExactlyApplyRestoreAndSave()
 {
  CollectionAssert.AreEquivalent(new[]{ "editor.apply_craft","editor.restore_snapshot","editor.save_craft" },BridgeClient.MutationOperations.ToArray());
  Assert.IsFalse(BridgeClient.MutationOperations.Overlaps(BridgeClient.ReadOperations)); Assert.IsFalse(BridgeClient.MutationOperations.Overlaps(BridgeClient.ControlOperationSet));
  Assert.IsFalse(BridgeClient.ReadOperations.Overlaps(BridgeClient.ControlOperationSet)); Assert.IsFalse(BridgeClient.ReadOperations.Any(ControlOperations.IsControl));
  CollectionAssert.AreEquivalent(ControlOperations.All,BridgeClient.ControlOperationSet.ToArray());
 }
 [TestMethod] public async Task EachEntryPointRefusesTheOtherAllowlistsBeforeConnecting()
 {
  var client=new BridgeClient();
  foreach(var control in ControlOperations.All) await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.ReadAsync(control,null,CancellationToken.None));
  foreach(var read in BridgeClient.ReadOperations) await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.ControlAsync(read,null,null,TimeSpan.FromSeconds(1),CancellationToken.None));
  foreach(var mutation in new[]{"editor.apply_craft","editor.save_craft","editor.restore_snapshot","craft.write","launch"})
  { await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.ReadAsync(mutation,null,CancellationToken.None)); await Assert.ThrowsExceptionAsync<ArgumentException>(()=>client.ControlAsync(mutation,null,null,TimeSpan.FromSeconds(1),CancellationToken.None)); }
 }
}
[TestClass] public class ToolSurfaceTests
{
 private static readonly string[] Forbidden={ "grant","payload","mac","key","secret","token","password","envelope","signature" };
 private static IEnumerable<MethodInfo> Tools()=>typeof(BridgeClient).Assembly.GetTypes().Where(t=>t.GetCustomAttribute<McpServerToolTypeAttribute>()!=null).SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly)).Where(m=>m.GetCustomAttribute<McpServerToolAttribute>()!=null);
 [TestMethod] public void ControlToolsExistAndNoToolIsNamedForGrants()
 {
  var names=Tools().Select(m=>m.Name).ToArray();
  foreach(var expected in new[]{"ControlStatus","ControlAcquireLease","ControlRenewLease","ControlReleaseLease"}) CollectionAssert.Contains(names,expected);
  foreach(var name in names) foreach(var word in Forbidden) Assert.IsFalse(name.Contains(word,StringComparison.OrdinalIgnoreCase),$"tool {name} must not mention {word}");
 }
 [TestMethod] public void NoToolParameterCouldCarryGrantContentOrKeyMaterial()
 {
  foreach(var tool in Tools()) foreach(var parameter in tool.GetParameters())
  {
   if(parameter.ParameterType!=typeof(CancellationToken)) foreach(var word in Forbidden) Assert.IsFalse(parameter.Name!.Contains(word,StringComparison.OrdinalIgnoreCase),$"{tool.Name}({parameter.Name}) looks like grant input"); // the SDK injects CancellationToken; it is not model-facing
   Assert.IsTrue(parameter.ParameterType==typeof(string)||parameter.ParameterType==typeof(int)||parameter.ParameterType==typeof(bool)||parameter.ParameterType==typeof(CancellationToken),$"{tool.Name}({parameter.Name}) has an unexpected type {parameter.ParameterType}");
  }
  foreach(var tool in Tools()) Assert.AreEqual(typeof(Task<string>),tool.ReturnType,tool.Name+" returns a JSON string only");
 }
 [TestMethod] public void StringParametersOnControlToolsAreBounded()
 {
  foreach(var name in new[]{"ControlAcquireLease","ControlRenewLease","ControlReleaseLease"})
   foreach(var p in typeof(ControlTools).GetMethod(name)!.GetParameters().Where(p=>p.ParameterType==typeof(string)))
    Assert.IsNotNull(p.GetCustomAttribute<System.ComponentModel.DataAnnotations.StringLengthAttribute>(),$"{name}.{p.Name} needs a length bound");
 }
 [TestMethod] public void GrantCliIsNotReachableFromAnyMcpTool()
 {
  Assert.IsNull(typeof(GrantCli).GetCustomAttribute<McpServerToolTypeAttribute>());
  Assert.IsFalse(typeof(GrantCli).GetMethods().Any(m=>m.GetCustomAttribute<McpServerToolAttribute>()!=null));
  foreach(var type in new[]{typeof(ControlTools),typeof(ObservationTools),typeof(LeaseKeeper),typeof(BridgeClient),typeof(JournalAccess),typeof(BridgeBeatSender)})
  {
   var members=type.GetMembers(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly).OfType<MethodBase>().SelectMany(m=>m.GetParameters().Select(p=>p.ParameterType).Append(m is MethodInfo mi?mi.ReturnType:typeof(void)));
   Assert.IsFalse(members.Any(t=>t==typeof(GrantPayload)||t==typeof(GrantEnvelope)||t==typeof(GrantVerification)),type.Name+" must not handle grant payloads");
  }
 }
 [TestMethod] public void HostSourcesNeverReferenceKeyPathsOutsideTheCli()
 {
  string host=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../Host"));
  foreach(var file in Directory.GetFiles(host,"*.cs").Where(f=>Path.GetFileName(f)!="GrantCli.cs"))
  {
   string text=File.ReadAllText(file);
   Assert.IsFalse(text.Contains("GRANT_KEY",StringComparison.Ordinal)||text.Contains("grant.key",StringComparison.OrdinalIgnoreCase)||text.Contains("GrantCodec",StringComparison.Ordinal),Path.GetFileName(file)+" must not touch the grant key or codec");
  }
 }
}
