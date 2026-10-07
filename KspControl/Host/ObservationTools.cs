using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using KspControl.Contracts;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;
public sealed class BridgeClient
{
 public async Task<string> ReadAsync(string operation, JObject? arguments, CancellationToken cancellationToken)
 {
  if (!Allowed.Contains(operation)) throw new ArgumentException("unsupported_operation");
  string tokenPath = Environment.GetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE") ?? throw new InvalidOperationException("Set KSP_CONTROL_TOKEN_FILE to the bridge credential file.");
  var token = (await File.ReadAllTextAsync(tokenPath,cancellationToken)).Trim();
  if(token.Length < 32 || token.Length > 256) throw new InvalidOperationException("invalid_credential_file");
  int port = int.TryParse(Environment.GetEnvironmentVariable("KSP_CONTROL_PORT"),out int configured) ? configured : BridgeFrames.DefaultPort;
  using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
  timeout.CancelAfter(TimeSpan.FromSeconds(10));
  using var client = new TcpClient();
  using var close = timeout.Token.Register(() => client.Dispose());
  await client.ConnectAsync(IPAddress.Loopback,port,timeout.Token);
  var request = new BridgeRequest { RequestId=Guid.NewGuid().ToString("N"),Token=token,Operation=operation,Arguments=arguments ?? new JObject() };
  // Blocking framing runs off the MCP dispatch thread. Cancellation disposes the socket.
  return await Task.Run(() => {
   var stream=client.GetStream(); BridgeFrames.Write(stream,request);
   var reply=BridgeFrames.Read<BridgeResponse>(stream);
   if(reply==null || reply.ProtocolVersion!=1 || reply.RequestId!=request.RequestId) throw new InvalidDataException("invalid_bridge_response");
   return JsonConvert.SerializeObject(reply);
  },timeout.Token);
 }
 private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "bridge.capabilities","game.context","parts.list","editor.inspect","vessel.inspect","part.controls","science.inspect" };
}
[McpServerToolType]
public sealed class ObservationTools(BridgeClient bridge)
{
 [McpServerTool, Description("Report implemented and unavailable bridge capabilities. Never assumes the game is running.")]
 public Task<string> Capabilities(CancellationToken cancellationToken) => bridge.ReadAsync("bridge.capabilities",null,cancellationToken);
 [McpServerTool, Description("Read current game scene and session context without mutation.")]
 public Task<string> Context(CancellationToken cancellationToken) => bridge.ReadAsync("game.context",null,cancellationToken);
 [McpServerTool, Description("Inspect a bounded page of loaded parts. Read-only.")]
 public Task<string> Parts(int offset=0,int limit=50,string filter="",CancellationToken cancellationToken=default)
 {
  if(offset<0 || offset>100000 || limit<1 || limit>50 || filter==null || filter.Length>128) throw new ArgumentOutOfRangeException(nameof(limit));
  return bridge.ReadAsync("parts.list",new JObject { ["offset"]=offset,["limit"]=limit,["query"]=filter },cancellationToken);
 }
 [McpServerTool, Description("Inspect the current editor craft without changing it.")]
 public Task<string> Editor(int offset=0,int limit=20,CancellationToken cancellationToken=default) => bridge.ReadAsync("editor.inspect",Page(offset,limit),cancellationToken);
 [McpServerTool, Description("Inspect available part right-click controls. Discovery only; no invocation.")]
 public Task<string> PartControls(string partId,int offset=0,CancellationToken cancellationToken=default) => bridge.ReadAsync("part.controls",Part(partId,offset),cancellationToken);
 [McpServerTool, Description("Inspect science experiment state without running or transmitting experiments.")]
 public Task<string> Science(string partId,CancellationToken cancellationToken=default) => bridge.ReadAsync("science.inspect",Part(partId,0),cancellationToken);
 [McpServerTool, Description("Inspect the active vessel through bridge disclosure policy. No foreign vessel lookup.")]
 public Task<string> Vessel(int offset=0,int limit=20,CancellationToken cancellationToken=default) => bridge.ReadAsync("vessel.inspect",Page(offset,limit),cancellationToken);
 private static JObject Page(int offset,int limit)
 {
  if(offset<0||offset>100000||limit<1||limit>50) throw new ArgumentOutOfRangeException(nameof(offset));
  return new JObject { ["offset"]=offset,["limit"]=limit };
 }
 private static JObject Part(string partId,int offset)
 {
  if(!uint.TryParse(partId,out _)) throw new ArgumentException("invalid_part_id",nameof(partId));
  var args=Page(offset,4); args["partId"]=partId; return args;
 }
}


