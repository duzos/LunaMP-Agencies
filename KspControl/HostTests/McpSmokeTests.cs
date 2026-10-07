using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.HostTests;
[TestClass] public class McpSmokeTests
{
 [TestMethod] public async Task RealStdioInitializeListAndDisconnectedCall()
 {
  string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../Host/bin/Release/net10.0/KspControl.Host.dll"));
  var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? @"C:\Users\james\.dotnet\dotnet.exe") { RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true };
  string journal=Path.Combine(Path.GetTempPath(),"ksp-smoke-journal",Guid.NewGuid().ToString("N"));
  start.ArgumentList.Add(root); start.Environment.Remove("KSP_CONTROL_TOKEN_FILE"); start.Environment["KSP_CONTROL_JOURNAL_DIR"]=journal;
  using var process=Process.Start(start)!;
  var errors=process.StandardError.ReadToEndAsync();
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
  try {
   async Task<JsonElement> Request(object value,int id) {
    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value)); await process.StandardInput.FlushAsync();
    while(true) {
     string? line=await process.StandardOutput.ReadLineAsync(deadline.Token); Assert.IsNotNull(line);
     using var document=JsonDocument.Parse(line!); var element=document.RootElement;
     if(element.TryGetProperty("id",out var rid) && rid.GetInt32()==id) return element.Clone();
    }
   }
   var init=await Request(new { jsonrpc="2.0",id=1,method="initialize",@params=new { protocolVersion="2025-06-18",capabilities=new {},clientInfo=new { name="protocol-test",version="1" } } },1);
   Assert.IsTrue(init.TryGetProperty("result",out _));
   await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
   var list=await Request(new { jsonrpc="2.0",id=2,method="tools/list",@params=new {} },2);
   var names=list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t=>t.GetProperty("name").GetString()).ToArray();
   CollectionAssert.Contains(names,"context");
   foreach(var expected in new[]{"control_status","control_acquire_lease","control_renew_lease","control_release_lease","editor_state","editor_engineering","craft_plan","editor_apply_craft","editor_restore_snapshot","editor_launch","job_status"}) CollectionAssert.Contains(names,expected);
   // The mutation tools: exactly the documented arguments, bounded, and none that could carry grant content.
   JsonElement Schema(string tool)=>list.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t=>t.GetProperty("name").GetString()==tool).GetProperty("inputSchema");
   CollectionAssert.AreEquivalent(new[]{"requestId","leaseId","expectedRevision","graph","expectedPlanHash"},Schema("editor_apply_craft").GetProperty("properties").EnumerateObject().Select(p=>p.Name).ToArray());
   CollectionAssert.AreEquivalent(new[]{"requestId","leaseId","expectedRevision","snapshotId"},Schema("editor_restore_snapshot").GetProperty("properties").EnumerateObject().Select(p=>p.Name).ToArray());
   CollectionAssert.AreEquivalent(new[]{"requestId","leaseId","expectedRevision","launchSite","maxSpendFunds"},Schema("editor_launch").GetProperty("properties").EnumerateObject().Select(p=>p.Name).ToArray());
   Assert.AreEqual(1000000000,Schema("editor_launch").GetProperty("properties").GetProperty("maxSpendFunds").GetProperty("maximum").GetInt32());
   CollectionAssert.AreEquivalent(new[]{"requestId","waitSeconds"},Schema("job_status").GetProperty("properties").EnumerateObject().Select(p=>p.Name).ToArray());
   var applyProperties=Schema("editor_apply_craft").GetProperty("properties");
   Assert.AreEqual(262144,applyProperties.GetProperty("graph").GetProperty("maxLength").GetInt32()); Assert.AreEqual(64,applyProperties.GetProperty("expectedPlanHash").GetProperty("minLength").GetInt32()); Assert.AreEqual(64,applyProperties.GetProperty("expectedPlanHash").GetProperty("maxLength").GetInt32());
   Assert.AreEqual(32,applyProperties.GetProperty("leaseId").GetProperty("minLength").GetInt32()); Assert.AreEqual(128,applyProperties.GetProperty("expectedRevision").GetProperty("maxLength").GetInt32());
   var wait=Schema("job_status").GetProperty("properties").GetProperty("waitSeconds"); Assert.AreEqual(0,wait.GetProperty("minimum").GetInt32()); Assert.AreEqual(20,wait.GetProperty("maximum").GetInt32());
   CollectionAssert.AreEquivalent(new[]{"requestId","leaseId","expectedRevision","graph","expectedPlanHash"},Schema("editor_apply_craft").GetProperty("required").EnumerateArray().Select(p=>p.GetString()).ToArray());
   // Schema scan over the real tool list: nothing accepts grant payloads or key material.
   foreach(var tool in list.GetProperty("result").GetProperty("tools").EnumerateArray())
   {
    string name=tool.GetProperty("name").GetString()!;
    Assert.IsFalse(name.Contains("grant",StringComparison.OrdinalIgnoreCase),name);
    if(tool.TryGetProperty("inputSchema",out var schema) && schema.TryGetProperty("properties",out var properties))
     foreach(var property in properties.EnumerateObject())
      foreach(var word in new[]{"grant","payload","mac","key","secret","token","password","envelope"})
       Assert.IsFalse(property.Name.Contains(word,StringComparison.OrdinalIgnoreCase),$"{name}.{property.Name}");
   }
   // The editor observation tools are read-only: no lease, request id or revision argument, and only the documented properties.
   var stateSchema=list.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t=>t.GetProperty("name").GetString()=="editor_state").GetProperty("inputSchema");
   Assert.IsFalse(stateSchema.TryGetProperty("properties",out var stateProperties) && stateProperties.EnumerateObject().Any());
   var engineering=list.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t=>t.GetProperty("name").GetString()=="editor_engineering").GetProperty("inputSchema").GetProperty("properties");
   CollectionAssert.AreEquivalent(new[]{"offset","limit","includeDeltaV"},engineering.EnumerateObject().Select(p=>p.Name).ToArray());
   Assert.AreEqual(0,engineering.GetProperty("offset").GetProperty("minimum").GetInt32()); Assert.AreEqual(100000,engineering.GetProperty("offset").GetProperty("maximum").GetInt32());
   Assert.AreEqual(1,engineering.GetProperty("limit").GetProperty("minimum").GetInt32()); Assert.AreEqual(50,engineering.GetProperty("limit").GetProperty("maximum").GetInt32());
   var control=list.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t=>t.GetProperty("name").GetString()=="control_acquire_lease").GetProperty("inputSchema").GetProperty("properties");
   Assert.AreEqual(2,control.EnumerateObject().Count()); Assert.IsTrue(control.TryGetProperty("purpose",out _)); Assert.IsTrue(control.TryGetProperty("durationSeconds",out _));
   var call=await Request(new { jsonrpc="2.0",id=3,method="tools/call",@params=new { name="context",arguments=new {} } },3);
   string output=call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
   using var failure=JsonDocument.Parse(output); Assert.AreEqual("credential_not_configured",failure.RootElement.GetProperty("ReasonCode").GetString());
   var status=await Request(new { jsonrpc="2.0",id=4,method="tools/call",@params=new { name="control_status",arguments=new {} } },4);
   using var statusFailure=JsonDocument.Parse(status.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
   Assert.AreEqual("credential_not_configured",statusFailure.RootElement.GetProperty("ReasonCode").GetString());
   Assert.AreEqual("available",statusFailure.RootElement.GetProperty("Data").GetProperty("host").GetProperty("journal").GetString());
   var editorState=await Request(new { jsonrpc="2.0",id=6,method="tools/call",@params=new { name="editor_state",arguments=new {} } },6);
   using var editorStateResult=JsonDocument.Parse(editorState.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
   Assert.AreEqual("credential_not_configured",editorStateResult.RootElement.GetProperty("ReasonCode").GetString());
   var badEngineering=await Request(new { jsonrpc="2.0",id=7,method="tools/call",@params=new { name="editor_engineering",arguments=new { limit=0 } } },7);
   using var badEngineeringResult=JsonDocument.Parse(badEngineering.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
   Assert.AreEqual("invalid_argument",badEngineeringResult.RootElement.GetProperty("ReasonCode").GetString());
   var invalid=await Request(new { jsonrpc="2.0",id=5,method="tools/call",@params=new { name="control_acquire_lease",arguments=new { purpose="x",durationSeconds=5 } } },5);
   using var invalidResult=JsonDocument.Parse(invalid.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
   Assert.AreEqual("invalid_argument",invalidResult.RootElement.GetProperty("ReasonCode").GetString());
   // A mutation without a lease is refused before any socket: lease_required with the journal available, never a bridge error.
   var noLease=await Request(new { jsonrpc="2.0",id=8,method="tools/call",@params=new { name="editor_apply_craft",arguments=new { requestId="apply-0001",leaseId="0123456789abcdef0123456789abcdef",expectedRevision="abc",graph="{\"name\":\"n\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}",expectedPlanHash=new string('a',64) } } },8);
   using var noLeaseResult=JsonDocument.Parse(noLease.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
   Assert.AreEqual("lease_required",noLeaseResult.RootElement.GetProperty("ReasonCode").GetString());
   var unknownJob=await Request(new { jsonrpc="2.0",id=9,method="tools/call",@params=new { name="job_status",arguments=new { requestId="never-seen-1" } } },9);
   using var unknownJobResult=JsonDocument.Parse(unknownJob.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
   Assert.AreEqual("job_unknown",unknownJobResult.RootElement.GetProperty("ReasonCode").GetString());
   var badWait=await Request(new { jsonrpc="2.0",id=10,method="tools/call",@params=new { name="job_status",arguments=new { requestId="never-seen-1",waitSeconds=99 } } },10);
   using var badWaitResult=JsonDocument.Parse(badWait.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
   Assert.AreEqual("invalid_argument",badWaitResult.RootElement.GetProperty("ReasonCode").GetString());
  } finally { process.StandardInput.Close(); if(!process.WaitForExit(1000)) process.Kill(true); await errors; }
 }
}

