using System.Net;
using System.Net.Sockets;
using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] [DoNotParallelize] public class BridgeClientTests
{
 [TestMethod] public async Task AuthenticatedLoopbackRoundtripPreservesResponse()
 {
  string credential=Path.GetTempFileName(); await File.WriteAllTextAsync(credential,new string('x',64));
  string? oldFile=Environment.GetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE"),oldPort=Environment.GetEnvironmentVariable("KSP_CONTROL_PORT");
  using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
  Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",credential); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",((IPEndPoint)listener.LocalEndpoint).Port.ToString());
  try {
   var server=Task.Run(async()=>{ using var client=await listener.AcceptTcpClientAsync(); using var stream=client.GetStream(); var req=BridgeFrames.Read<BridgeRequest>(stream); Assert.AreEqual(new string('x',64),req.Token); Assert.AreEqual("parts.list",req.Operation); Assert.AreEqual(5,(int)req.Arguments["limit"]!); BridgeFrames.Write(stream,new BridgeResponse { RequestId=req.RequestId,Status="completed",WorldEpoch="session",Data=new JObject { ["count"]=5 } }); });
   var result=JObject.Parse(await new BridgeClient().ReadAsync("parts.list",new JObject{["limit"]=5},CancellationToken.None)); Assert.AreEqual("session",(string?)result["WorldEpoch"]); await server;
  } finally { Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",oldFile); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",oldPort); File.Delete(credential); }
 }
 [TestMethod] public async Task ArbitraryOperationRefusedBeforeConnection()
 { await Assert.ThrowsExceptionAsync<ArgumentException>(()=>new BridgeClient().ReadAsync("launch",null,CancellationToken.None)); }
}
