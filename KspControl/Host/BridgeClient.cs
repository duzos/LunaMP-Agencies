using System.Net;
using System.Net.Sockets;
using KspControl.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;
/// <summary>
/// Opens one fresh loopback connection per call. Operations are split into three disjoint allowlists:
/// read (queued observations, including the status of a mutation job), control (inline lease operations) and
/// mutation (queued, lease-bound; only the mutation service calls them, and only after the journal admitted the request).
/// </summary>
public sealed class BridgeClient
{
 public static readonly IReadOnlySet<string> ReadOperations = new HashSet<string>(StringComparer.Ordinal) { "bridge.capabilities","game.context","parts.list","editor.inspect","vessel.inspect","part.controls","science.inspect","parts.definition","editor.snapshot",EditorOperations.State,EditorOperations.Engineering,ConstructionOperations.Catalog,EditorOperations.OperationStatus };
 public static readonly IReadOnlySet<string> ControlOperationSet = new HashSet<string>(ControlOperations.All,StringComparer.Ordinal);
 /// <summary>Mutations reach the bridge only through <see cref="MutateAsync"/>, which the journaled mutation service calls.</summary>
 public static readonly IReadOnlySet<string> MutationOperations = new HashSet<string>(EditorOperations.Mutations,StringComparer.Ordinal);
 private static readonly TimeSpan DefaultTimeout=TimeSpan.FromSeconds(10);

 public async Task<string> ReadAsync(string operation, JObject? arguments, CancellationToken cancellationToken, string? expectedWorldEpoch = null)
 {
  if (!ReadOperations.Contains(operation)) throw new ArgumentException("unsupported_operation");
  return await Call(operation,null,arguments,DefaultTimeout,cancellationToken,expectedWorldEpoch);
 }
 /// <summary>Runs an inline control operation. <paramref name="timeout"/> bounds connect, write and read together.</summary>
 public async Task<string> ControlAsync(string operation, string? leaseId, JObject? arguments, TimeSpan timeout, CancellationToken cancellationToken)
 {
  if (!ControlOperationSet.Contains(operation)) throw new ArgumentException("unsupported_operation");
  return await Call(operation,leaseId,arguments,timeout,cancellationToken);
 }
 /// <summary>Sends one mutation with its lease. The bridge answers within its queue deadline: either a refusal or a running job to poll with editor.operation_status.</summary>
 public async Task<string> MutateAsync(string operation, string leaseId, JObject arguments, CancellationToken cancellationToken)
 {
  if (!MutationOperations.Contains(operation)) throw new ArgumentException("unsupported_operation");
  if (!ControlLimits.IsLeaseId(leaseId)) throw new ArgumentException("invalid_lease");
  return await Call(operation,leaseId,arguments,DefaultTimeout,cancellationToken);
 }
 private async Task<string> Call(string operation,string? leaseId,JObject? arguments,TimeSpan timeout,CancellationToken cancellationToken,string? expectedWorldEpoch=null)
 {
  try { return await Roundtrip(operation,leaseId,arguments,timeout,cancellationToken,expectedWorldEpoch); }
  catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested) { return Failure("bridge_timeout"); }
  catch(SocketException) { return Failure("bridge_unreachable"); }
  catch(ObjectDisposedException) { return Failure("bridge_timeout"); }
  catch(InvalidDataException) { return Failure("protocol_invalid"); }
  catch(Newtonsoft.Json.JsonException) { return Failure("protocol_invalid"); }
  catch(IOException) { return Failure("bridge_io_failure"); }
 }
 internal static string Failure(string reason) => JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode=reason });
 private static async Task<string> Roundtrip(string operation,string? leaseId,JObject? arguments,TimeSpan timeout,CancellationToken cancellationToken,string? expectedWorldEpoch=null)
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
  using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
  linked.CancelAfter(timeout);
  using var client = new TcpClient();
  using var close = linked.Token.Register(() => client.Dispose());
  await client.ConnectAsync(IPAddress.Loopback,port,linked.Token);
  var request = new BridgeRequest { RequestId=Guid.NewGuid().ToString("N"),Token=token,Operation=operation,LeaseId=leaseId,ExpectedWorldEpoch=expectedWorldEpoch,Arguments=arguments ?? new JObject() };
  // Blocking framing runs off the MCP dispatch thread. Cancellation disposes the socket.
  return await Task.Run(() => {
   var stream=client.GetStream(); BridgeFrames.Write(stream,request);
   var reply=BridgeFrames.Read<BridgeResponse>(stream);
   if(reply==null || reply.ProtocolVersion!=1 || reply.RequestId!=request.RequestId) throw new InvalidDataException("invalid_bridge_response");
   return JsonConvert.SerializeObject(reply);
  },linked.Token);
 }
}
