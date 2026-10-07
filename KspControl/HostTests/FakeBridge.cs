using System.Net;
using System.Net.Sockets;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
/// <summary>A scriptable loopback bridge: one task per accepted connection, like the real per-connection workers.</summary>
internal sealed class FakeBridge : IDisposable
{
 private readonly TcpListener listener=new(IPAddress.Loopback,0);
 private readonly Func<BridgeRequest,Task<BridgeResponse>> handler;
 private readonly CancellationTokenSource stop=new();
 private readonly List<BridgeRequest> seen=new();
 private readonly string? oldFile,oldPort; private readonly string credential;
 public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
 public string Token { get; }=new string('x',64);
 public int Connections;
 public IReadOnlyList<BridgeRequest> Requests { get { lock(seen) return seen.ToArray(); } }
 public FakeBridge(Func<BridgeRequest,Task<BridgeResponse>> handler,bool setEnvironment=true)
 {
  this.handler=handler; listener.Start();
  credential=Path.GetTempFileName(); File.WriteAllText(credential,Token);
  oldFile=Environment.GetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE"); oldPort=Environment.GetEnvironmentVariable("KSP_CONTROL_PORT");
  if(setEnvironment) { Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",credential); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",Port.ToString()); }
  _=Task.Run(AcceptLoop);
 }
 public FakeBridge(Func<BridgeRequest,BridgeResponse> handler,bool setEnvironment=true) : this(r=>Task.FromResult(handler(r)),setEnvironment) { }
 private async Task AcceptLoop()
 {
  try { while(!stop.IsCancellationRequested) { var client=await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref Connections); _=Task.Run(()=>Serve(client)); } } catch(Exception) { }
 }
 private async Task Serve(TcpClient client)
 {
  try
  {
   using(client) {
    var stream=client.GetStream(); var request=BridgeFrames.Read<BridgeRequest>(stream);
    lock(seen) seen.Add(request);
    var response=await handler(request); response.RequestId=request.RequestId;
    BridgeFrames.Write(stream,response);
   }
  } catch(Exception) { }
 }
 public static BridgeResponse Ok(JObject data) => new() { Status="completed",Data=data };
 public static BridgeResponse Fail(string reason) => new() { Status="failed",ReasonCode=reason };
 public void Dispose()
 {
  stop.Cancel(); listener.Stop();
  Environment.SetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE",oldFile); Environment.SetEnvironmentVariable("KSP_CONTROL_PORT",oldPort);
  try { File.Delete(credential); } catch { }
 }
}
