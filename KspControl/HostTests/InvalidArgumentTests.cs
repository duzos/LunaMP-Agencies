using System.Net;
using System.Net.Sockets;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class InvalidArgumentTests
{
 private static async Task<string> WithListener(Func<ObservationTools,Task<string>> call)
 {
  string credential=Path.GetTempFileName(); await File.WriteAllTextAsync(credential,new string('x',64));
  string? oldFile=Environment.GetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE"),oldPort=Environment.GetEnvironmentVariable("KSP_CONTROL_PORT");
  using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
  Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",credential); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",((IPEndPoint)listener.LocalEndpoint).Port.ToString());
  try { var result=await call(new ObservationTools(new BridgeClient())); Assert.IsFalse(listener.Pending(),"bridge must not be contacted"); return result; }
  finally { Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",oldFile); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",oldPort); File.Delete(credential); }
 }
 [DataTestMethod]
 [DataRow("parts",1000,"limit")] [DataRow("parts",100000,"offset")] [DataRow("parts",-1,"offset")]
 [DataRow("editor",0,"limit")] [DataRow("vessel",51,"limit")] [DataRow("snapshot",21,"limit")]
 [DataRow("controls",-5,"offset")] [DataRow("controlsId",0,"partId")] [DataRow("scienceId",0,"partId")]
 [DataRow("definition",0,"partName")] [DataRow("filter",0,"filter")]
 public async Task OutOfRangeReturnsStructuredFailureWithoutBridge(string tool,int value,string field)
 {
  string json=await WithListener(t => tool switch {
   "parts" => t.Parts(field=="offset"?(value==100000?100001:value):0,field=="limit"?value:50),
   "editor" => t.Editor(0,value), "vessel" => t.Vessel(0,value), "snapshot" => t.EditorSnapshot(0,value),
   "controls" => t.PartControls("1",value), "controlsId" => t.PartControls("abc"), "scienceId" => t.Science("-1"),
   "definition" => t.PartDefinition(" "), _ => t.Parts(0,10,new string('a',129)) });
  var result=JObject.Parse(json);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]);
  StringAssert.Contains((string?)result["Data"]!["detail"],field);
 }
 [DataTestMethod]
 [DataRow(-1,20,"offset")] [DataRow(100001,20,"offset")] [DataRow(0,0,"limit")] [DataRow(0,51,"limit")] [DataRow(0,-4,"limit")]
 public async Task EditorEngineeringOutOfRangeReturnsStructuredFailureWithoutBridge(int offset,int limit,string field)
 {
  string json=await WithListener(t => t.EditorEngineering(offset,limit));
  var result=JObject.Parse(json);
  Assert.AreEqual("failed",(string?)result["Status"]); Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]);
  StringAssert.Contains((string?)result["Data"]!["detail"],field);
 }
 [TestMethod] public async Task EditorEngineeringBoundaryValuesReachTheBridge()
 {
  // Valid arguments must pass host validation and open a socket (the fake listener never answers, so the call times out or fails to read).
  string credential=Path.GetTempFileName(); await File.WriteAllTextAsync(credential,new string('x',64));
  string? oldFile=Environment.GetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE"),oldPort=Environment.GetEnvironmentVariable("KSP_CONTROL_PORT");
  using var bridge=new FakeBridge(r => FakeBridge.Ok(new JObject { ["echo"]=r.Operation }));
  try {
   foreach(var (offset,limit) in new[] { (0,1),(100000,50) }) {
    var result=JObject.Parse(await new ObservationTools(new BridgeClient()).EditorEngineering(offset,limit,false));
    Assert.AreEqual("completed",(string?)result["Status"]);
   }
   var arguments=bridge.Requests.Last().Arguments;
   Assert.AreEqual(100000,(int)arguments["offset"]!); Assert.AreEqual(50,(int)arguments["limit"]!); Assert.AreEqual(false,(bool)arguments["includeDeltaV"]!);
   Assert.AreEqual("editor.engineering",bridge.Requests.Last().Operation);
   var state=JObject.Parse(await new ObservationTools(new BridgeClient()).EditorState());
   Assert.AreEqual("completed",(string?)state["Status"]); Assert.AreEqual("editor.state",bridge.Requests.Last().Operation);
   Assert.IsTrue(bridge.Requests.All(r => string.IsNullOrEmpty(r.LeaseId)),"read-only tools never carry a lease");
  } finally { Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",oldFile); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",oldPort); File.Delete(credential); }
 }
 [TestMethod] public async Task PartsLimit100ReportsInvalidArgumentNotUnreachable()
 {
  var result=JObject.Parse(await new ObservationTools(new BridgeClient()).Parts(0,100));
  Assert.AreEqual("invalid_argument",(string?)result["ReasonCode"]);
 }
}
