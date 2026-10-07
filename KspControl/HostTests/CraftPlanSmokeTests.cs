using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.HostTests;
/// <summary>Real stdio host: craft_plan is listed with a single string graph argument, validates without a socket and fails structured when disconnected.</summary>
[TestClass] public class CraftPlanSmokeTests
{
 [TestMethod] public async Task RealStdioListsCraftPlanAndValidatesGraph()
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
   await Request(new { jsonrpc="2.0",id=1,method="initialize",@params=new { protocolVersion="2025-06-18",capabilities=new {},clientInfo=new { name="protocol-test",version="1" } } },1);
   await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
   var list=await Request(new { jsonrpc="2.0",id=2,method="tools/list",@params=new {} },2);
   var tools=list.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
   var tool=tools.Single(t => t.GetProperty("name").GetString()=="craft_plan");
   var schema=tool.GetProperty("inputSchema"); var properties=schema.GetProperty("properties");
   Assert.AreEqual(1,properties.EnumerateObject().Count()); Assert.AreEqual("string",properties.GetProperty("graph").GetProperty("type").GetString());
   Assert.IsTrue(schema.GetProperty("required").EnumerateArray().Any(r => r.GetString()=="graph"));
   StringAssert.Contains(tool.GetProperty("description").GetString(),"planHash");
   Assert.IsFalse(tools.Any(t => t.GetProperty("name").GetString()!.Contains("catalog",StringComparison.Ordinal)),"the catalog fetch is folded into craft_plan");
   async Task<JsonDocument> Call(string graph,int id) {
    var r=await Request(new { jsonrpc="2.0",id,method="tools/call",@params=new { name="craft_plan",arguments=new { graph } } },id);
    return JsonDocument.Parse(r.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
   }
   using(var invalid=await Call("{",3)) Assert.AreEqual("invalid_argument",invalid.RootElement.GetProperty("ReasonCode").GetString());
   const string valid="{\"name\":\"Probe\",\"facility\":\"VAB\",\"root\":\"core\",\"parts\":[{\"id\":\"core\",\"part\":\"probeCoreOcto.v2\"}]}";
   using(var disconnected=await Call(valid,4)) Assert.AreEqual("credential_not_configured",disconnected.RootElement.GetProperty("ReasonCode").GetString());
   const string shapeOnly="{\"name\":\"Probe\",\"facility\":\"SPH\",\"root\":\"core\",\"parts\":[{\"id\":\"core\",\"part\":\"probeCoreOcto.v2\"}]}";
   using(var shape=await Call(shapeOnly,5)) {
    Assert.AreEqual("completed",shape.RootElement.GetProperty("Status").GetString());
    Assert.AreEqual("facility_mismatch",shape.RootElement.GetProperty("Data").GetProperty("issues")[0].GetProperty("code").GetString());
   }
  } finally { process.StandardInput.Close(); if(!process.WaitForExit(1000)) process.Kill(true); await errors; }
 }
}
