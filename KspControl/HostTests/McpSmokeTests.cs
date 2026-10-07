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
  start.ArgumentList.Add(root); start.Environment.Remove("KSP_CONTROL_TOKEN_FILE");
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
   var call=await Request(new { jsonrpc="2.0",id=3,method="tools/call",@params=new { name="context",arguments=new {} } },3);
   Assert.IsTrue(call.GetProperty("result").GetProperty("isError").GetBoolean());
  } finally { process.StandardInput.Close(); if(!process.WaitForExit(1000)) process.Kill(true); await errors; }
 }
}
