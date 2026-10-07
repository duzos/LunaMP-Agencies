using System.Net;
using System.Net.Sockets;
using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class BridgeClientTests
{
 [DataTestMethod] [DataRow("parts.list")] [DataRow("editor.inspect")] [DataRow("vessel.inspect")] [DataRow("part.controls")] [DataRow("science.inspect")] [DataRow("parts.definition")] [DataRow("editor.snapshot")] [DataRow("editor.state")] [DataRow("editor.engineering")] public async Task AuthenticatedLoopbackRoundtripPreservesResponse(string operation)
 {
  string credential=Path.GetTempFileName(); await File.WriteAllTextAsync(credential,new string('x',64));
  string? oldFile=Environment.GetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE"),oldPort=Environment.GetEnvironmentVariable("KSP_CONTROL_PORT");
  using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
  Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",credential); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",((IPEndPoint)listener.LocalEndpoint).Port.ToString());
  try {
   var server=Task.Run(async()=>{ using var client=await listener.AcceptTcpClientAsync(); using var stream=client.GetStream(); var req=BridgeFrames.Read<BridgeRequest>(stream); Assert.AreEqual(new string('x',64),req.Token); Assert.AreEqual(operation,req.Operation); if(operation=="parts.list") Assert.AreEqual("probe",(string?)req.Arguments["query"]); if(operation is "part.controls" or "science.inspect") Assert.AreEqual("42",(string?)req.Arguments["partId"]); if(operation=="parts.definition") Assert.AreEqual("probe",(string?)req.Arguments["partName"]); if(operation is "parts.definition" or "editor.snapshot") Assert.AreEqual(true,(bool)req.Arguments["includeNative"]!); if(operation=="editor.engineering") Assert.AreEqual(false,(bool)req.Arguments["includeDeltaV"]!); if(operation!="science.inspect" && operation!="parts.definition" && operation!="editor.state") Assert.AreEqual(8,(int)req.Arguments["offset"]!); BridgeFrames.Write(stream,new BridgeResponse { RequestId=req.RequestId,Status="completed",WorldEpoch="session",Data=new JObject { ["count"]=5 } }); });
   var tools=new ObservationTools(new BridgeClient()); var result=JObject.Parse(await (operation switch { "parts.list" => tools.Parts(8,5,"probe"), "editor.inspect" => tools.Editor(8,5), "vessel.inspect" => tools.Vessel(8,5), "part.controls" => tools.PartControls("42",8), "parts.definition" => tools.PartDefinition("probe",true), "editor.snapshot" => tools.EditorSnapshot(8,5,true), "editor.state" => tools.EditorState(), "editor.engineering" => tools.EditorEngineering(8,5,false), _ => tools.Science("42") })); Assert.AreEqual("session",(string?)result["WorldEpoch"]); await server;
  } finally { Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",oldFile); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",oldPort); File.Delete(credential); }
 }
 [TestMethod] public void EditorObservationsAreReadOperationsAndNeverControlOrMutation()
 {
  foreach(var operation in new[] { "editor.state","editor.engineering" })
  {
   Assert.IsTrue(BridgeClient.ReadOperations.Contains(operation)); Assert.IsFalse(BridgeClient.MutationOperations.Contains(operation)); Assert.IsFalse(BridgeClient.ControlOperationSet.Contains(operation));
  }
 }
 [TestMethod] public async Task ACallerCancellingMidReadIsACancellationNotABridgeFailure()
 {
  // The bridge has the request and is still working when the caller gives up: cancelling disposes the socket under the blocking read.
  var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var never=new TaskCompletionSource<BridgeResponse>();
  using var bridge=new FakeBridge(async r=>{ received.TrySetResult(); return await never.Task; });
  using var cancel=new CancellationTokenSource();
  var call=new BridgeClient().ReadAsync(EditorOperations.OperationStatus,new JObject { ["requestId"]="apply-0001" },cancel.Token);
  await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
  string? reply=null;
  try { reply=await call.WaitAsync(TimeSpan.FromSeconds(5)); } catch(OperationCanceledException) { return; }
  Assert.Fail("a cancelled call must not be reported as a bridge answer: "+reply);
 }
 [TestMethod] public async Task ArbitraryOperationRefusedBeforeConnection()
 { await Assert.ThrowsExceptionAsync<ArgumentException>(()=>new BridgeClient().ReadAsync("launch",null,CancellationToken.None)); }
}


