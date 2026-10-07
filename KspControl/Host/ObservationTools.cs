using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
  try { return await ReadCore(operation,arguments,cancellationToken); }
  catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested) { return Failure("bridge_timeout"); }
  catch(SocketException) { return Failure("bridge_unreachable"); }
  catch(ObjectDisposedException) { return Failure("bridge_timeout"); }
  catch(InvalidDataException) { return Failure("protocol_invalid"); }
  catch(Newtonsoft.Json.JsonException) { return Failure("protocol_invalid"); }
  catch(IOException) { return Failure("bridge_io_failure"); }
 }
 private static string Failure(string reason) => JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode=reason });
 private static async Task<string> ReadCore(string operation,JObject? arguments,CancellationToken cancellationToken)
 {
  string? tokenPath = Environment.GetEnvironmentVariable("KSP_CONTROL_TOKEN_FILE");
  if(string.IsNullOrWhiteSpace(tokenPath)) return Failure("credential_not_configured");
  string token;
  try {
   if(new FileInfo(tokenPath).Length>512) return Failure("credential_invalid");
   token=(await File.ReadAllTextAsync(tokenPath,cancellationToken)).Trim();
  } catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException) { return Failure("credential_invalid"); }
  if(token.Length < 32 || token.Length > 256) return Failure("credential_invalid");
  int port = int.TryParse(Environment.GetEnvironmentVariable("KSP_CONTROL_PORT"),out int configured) ? configured : BridgeFrames.DefaultPort;
  if(port<1024||port>65535) return Failure("bridge_configuration_invalid");
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
 private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "bridge.capabilities","game.context","parts.list","editor.inspect","vessel.inspect","part.controls","science.inspect","parts.definition","editor.snapshot" };
}
[McpServerToolType]
public sealed class ObservationTools(BridgeClient bridge)
{
 private const string PageNote="Offsets are positions within the filtered list; follow nextOffset to continue.";
 private static Task<string> Invalid(string detail) => Task.FromResult(JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode="invalid_argument",Data=new JObject { ["detail"]=detail } }));
 private static string? CheckPage(int offset,int limit,int maxLimit) =>
  offset<0||offset>ObservationLimits.MaxOffset ? $"offset must be 0..{ObservationLimits.MaxOffset}" :
  limit<1||limit>maxLimit ? $"limit must be 1..{maxLimit}" : null;
 [McpServerTool, Description("Query implemented and unavailable capabilities of the connected game bridge. Returns a structured failure when disconnected.")]
 public Task<string> Capabilities(CancellationToken cancellationToken) => bridge.ReadAsync("bridge.capabilities",null,cancellationToken);
 [McpServerTool, Description("Read current game scene and session context without mutation.")]
 public Task<string> Context(CancellationToken cancellationToken) => bridge.ReadAsync("game.context",null,cancellationToken);
 [McpServerTool, Description("Inspect a bounded page of the loaded part catalog (limit 1..50, offset 0..100000, filter up to 128 chars). Each item has category and buildable; pseudo-parts (kerbalEVA, flag, hidden or unresearchable parts) are omitted unless includeNonBuildable=true. " + PageNote + " Invalid arguments return reasonCode invalid_argument. Read-only.")]
 public Task<string> Parts(
  [Description("Position in the filtered list, 0..100000.")] [Range(0,ObservationLimits.MaxOffset)] int offset=0,
  [Description("Page size, 1..50.")] [Range(1,ObservationLimits.MaxPage)] int limit=ObservationLimits.DefaultPartsPage,
  [Description("Case-insensitive name/title substring, up to 128 chars.")] [StringLength(ObservationLimits.MaxFilter)] string filter="",
  [Description("Include pseudo-parts and non-buildable parts. Default false.")] bool includeNonBuildable=false,
  CancellationToken cancellationToken=default)
 {
  var bad=CheckPage(offset,limit,ObservationLimits.MaxPage) ?? (filter==null||filter.Length>ObservationLimits.MaxFilter ? $"filter must be at most {ObservationLimits.MaxFilter} characters" : null);
  if(bad!=null) return Invalid(bad);
  return bridge.ReadAsync("parts.list",new JObject { ["offset"]=offset,["limit"]=limit,["query"]=filter,["includeNonBuildable"]=includeNonBuildable },cancellationToken);
 }
 [McpServerTool, Description("Inspect the current editor craft without changing it. limit 1..50, offset 0..100000. Invalid arguments return reasonCode invalid_argument.")]
 public Task<string> Editor(
  [Description("Part position, 0..100000.")] [Range(0,ObservationLimits.MaxOffset)] int offset=0,
  [Description("Page size, 1..50.")] [Range(1,ObservationLimits.MaxPage)] int limit=ObservationLimits.DefaultPage,
  CancellationToken cancellationToken=default) => Page(offset,limit,ObservationLimits.MaxPage,"editor.inspect",null,cancellationToken);
 [McpServerTool, Description("Inspect available part right-click controls (offset 0..100000 is a module index, 4 modules per page). Discovery only; no invocation. partId must be a numeric persistent part id.")]
 public Task<string> PartControls(
  [Description("Numeric persistent part id from an editor or vessel observation.")] string partId,
  [Description("Module index, 0..100000.")] [Range(0,ObservationLimits.MaxOffset)] int offset=0,
  CancellationToken cancellationToken=default) => Part(partId,offset,"part.controls",cancellationToken);
 [McpServerTool, Description("Inspect science experiment state without running or transmitting experiments. partId must be a numeric persistent part id.")]
 public Task<string> Science(
  [Description("Numeric persistent part id from an editor or vessel observation.")] string partId,
  CancellationToken cancellationToken=default) => Part(partId,0,"science.inspect",cancellationToken);
 [McpServerTool, Description("Inspect the active vessel through bridge disclosure policy. No foreign vessel lookup. limit 1..50, offset 0..100000.")]
 public Task<string> Vessel(
  [Description("Part position, 0..100000.")] [Range(0,ObservationLimits.MaxOffset)] int offset=0,
  [Description("Page size, 1..50.")] [Range(1,ObservationLimits.MaxPage)] int limit=ObservationLimits.DefaultPage,
  CancellationToken cancellationToken=default) => Page(offset,limit,ObservationLimits.MaxPage,"vessel.inspect",null,cancellationToken);
 [McpServerTool, Description("Inspect a loaded part definition including configured attachment nodes (kind stack/surface/dock) and attachRules. Optional native config is bounded and is not a saved live module state. partName is the internal name (up to 256 chars).")]
 public Task<string> PartDefinition(
  [Description("Internal part name, for example mk1pod_v2, up to 256 chars.")] [StringLength(ObservationLimits.MaxPartName)] string partName,
  [Description("Include bounded native config text. Default false.")] bool includeNative=false,
  CancellationToken cancellationToken=default)
 {
  if(string.IsNullOrWhiteSpace(partName)||partName.Length>ObservationLimits.MaxPartName) return Invalid($"partName must be 1..{ObservationLimits.MaxPartName} non-blank characters");
  return bridge.ReadAsync("parts.definition",new JObject { ["partName"]=partName,["includeNative"]=includeNative },cancellationToken);
 }
 [McpServerTool, Description("Snapshot the current editor craft without changing it. limit 1..20, offset 0..100000. Optional native craft text is bounded; oversize snapshots fail instead of truncating.")]
 public Task<string> EditorSnapshot(
  [Description("Part position, 0..100000.")] [Range(0,ObservationLimits.MaxOffset)] int offset=0,
  [Description("Page size, 1..20.")] [Range(1,ObservationLimits.MaxSnapshotPage)] int limit=ObservationLimits.DefaultPage,
  [Description("Include bounded native craft text. Default false.")] bool includeNative=false,
  CancellationToken cancellationToken=default) => Page(offset,limit,ObservationLimits.MaxSnapshotPage,"editor.snapshot",new JObject { ["includeNative"]=includeNative },cancellationToken);
 private Task<string> Page(int offset,int limit,int maxLimit,string operation,JObject? extra,CancellationToken cancellationToken)
 {
  var bad=CheckPage(offset,limit,maxLimit);
  if(bad!=null) return Invalid(bad);
  var args=new JObject { ["offset"]=offset,["limit"]=limit };
  if(extra!=null) args.Merge(extra);
  return bridge.ReadAsync(operation,args,cancellationToken);
 }
 private Task<string> Part(string partId,int offset,string operation,CancellationToken cancellationToken)
 {
  if(!uint.TryParse(partId,out _)) return Invalid("partId must be a numeric persistent part id");
  var bad=CheckPage(offset,ObservationLimits.PartControlsPage,ObservationLimits.PartControlsPage);
  if(bad!=null) return Invalid(bad);
  var args=new JObject { ["offset"]=offset,["limit"]=ObservationLimits.PartControlsPage,["partId"]=partId };
  return bridge.ReadAsync(operation,args,cancellationToken);
 }
}
