using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using KspControl.Contracts;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;
/// <summary>
/// Lease tools. None accepts or returns grant content or key material: the grant is issued by a human-run CLI and
/// verified only inside the game, and the model only ever sees its state, id, generation, facilities and expiry.
/// </summary>
[McpServerToolType]
public sealed class ControlTools(BridgeClient bridge,LeaseKeeper keeper,JournalAccess journal)
{
 private static readonly TimeSpan CallTimeout=TimeSpan.FromSeconds(5);
 private static readonly HashSet<string> LeaseGoneReasons=new(StringComparer.Ordinal) { ControlReasons.LeaseInvalid,ControlReasons.LeaseExpired,ControlReasons.AuthorityRevoked };
 private static readonly string[] Forbidden={ "payload","mac","key","secret","token","password" };
 private static Task<string> Invalid(string detail) => Task.FromResult(JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode=ControlReasons.InvalidArgument,Data=new JObject { ["detail"]=detail } }));

 [McpServerTool, Description("Report whether this session may control the editor: grant state (valid, missing, invalid_mac, malformed, expired, revoked, suspended, binding_mismatch, not_yet_applicable) with id, generation, operations, facilities and expiry; the current lease (held, purpose, expiresInSeconds); the post-takeover cooldown; and host-local lease keeper and journal state. Read-only; never returns grant contents or keys. Returns bridge_unreachable when the game is not connected.")]
 public async Task<string> ControlStatus(CancellationToken cancellationToken)
 {
  var reply=await Call(ControlOperations.Status,null,null,cancellationToken);
  var keeperStatus=keeper.Status();
  var host=new JObject { ["journal"]=journal.State,["leaseKeeper"]=new JObject { ["state"]=keeperStatus.State,["consecutiveFailures"]=keeperStatus.ConsecutiveFailures } };
  if(keeperStatus.LeaseLostReason!=null) host["leaseKeeper"]!["lostReason"]=keeperStatus.LeaseLostReason;
  var data=(reply["Data"] as JObject) ?? new JObject(); data["host"]=host; reply["Data"]=data;
  return Render(reply);
 }

 [McpServerTool, Description("Acquire the single editor control lease. purpose is a human-visible reason of 1..128 characters; durationSeconds is 30..300. Requires a valid human-issued grant and an editor scene that is ready. Returns reasonCode grant_missing, grant_expired, grant_revoked, grant_suspended, grant_binding_mismatch, control_busy, human_activity_cooldown or editor_unavailable when refused. The lease is kept alive by the host while this session runs. Invalid arguments return invalid_argument.")]
 public async Task<string> ControlAcquireLease(
  [Description("Why control is needed, 1..128 characters, no control characters.")] [StringLength(ControlLimits.PurposeMax,MinimumLength=ControlLimits.PurposeMin)] string purpose,
  [Description("Requested lease length in seconds, 30..300.")] [Range(ControlLimits.DurationMinSeconds,ControlLimits.DurationMaxSeconds)] int durationSeconds,
  CancellationToken cancellationToken=default)
 {
  var bad=CheckPurpose(purpose) ?? CheckDuration(durationSeconds);
  if(bad!=null) return await Invalid(bad);
  var reply=await Call(ControlOperations.Acquire,null,new JObject { ["purpose"]=purpose.Trim(),["durationSeconds"]=durationSeconds },cancellationToken);
  if((string?)reply["Status"]=="completed" && reply["Data"] is JObject data && (string?)data["leaseId"] is { } leaseId)
  {
   keeper.Track(leaseId);
   JObject? status=null;
   try { status=JObject.Parse(await bridge.ControlAsync(ControlOperations.Status,null,null,CallTimeout,cancellationToken))["Data"] as JObject; } catch(Exception) { }
   journal.MirrorAcquire(data,status);
  }
  return Render(reply);
 }

 [McpServerTool, Description("Extend the held editor lease from now by durationSeconds (30..300), never past the grant expiry. leaseId is the 32 hex characters returned by control_acquire_lease. Returns lease_expired, lease_invalid or authority_revoked when the lease is gone. Invalid arguments return invalid_argument.")]
 public async Task<string> ControlRenewLease(
  [Description("Lease id, 32 hex characters.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("New lease length in seconds from now, 30..300.")] [Range(ControlLimits.DurationMinSeconds,ControlLimits.DurationMaxSeconds)] int durationSeconds,
  CancellationToken cancellationToken=default)
 {
  var bad=CheckLease(leaseId) ?? CheckDuration(durationSeconds);
  if(bad!=null) return await Invalid(bad);
  leaseId=leaseId.ToLowerInvariant();
  var reply=await Call(ControlOperations.Renew,leaseId,new JObject { ["durationSeconds"]=durationSeconds },cancellationToken);
  if((string?)reply["Status"]=="completed") journal.MirrorRenew(leaseId,(int?)reply["Data"]?["expiresInSeconds"]??durationSeconds);
  else Ended(leaseId,reply);
  return Render(reply);
 }

 [McpServerTool, Description("Release the held editor lease. leaseId is the 32 hex characters returned by control_acquire_lease. Invalid arguments return invalid_argument.")]
 public async Task<string> ControlReleaseLease(
  [Description("Lease id, 32 hex characters.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  CancellationToken cancellationToken=default)
 {
  var bad=CheckLease(leaseId);
  if(bad!=null) return await Invalid(bad);
  leaseId=leaseId.ToLowerInvariant();
  var reply=await Call(ControlOperations.Release,leaseId,null,cancellationToken);
  if((string?)reply["Status"]=="completed") { keeper.Untrack(leaseId); journal.MirrorEnd(leaseId); }
  else Ended(leaseId,reply);
  return Render(reply);
 }

 private void Ended(string leaseId,JObject reply)
 { if(LeaseGoneReasons.Contains((string?)reply["ReasonCode"]??"")) { keeper.Untrack(leaseId); journal.MirrorEnd(leaseId); } }
 private async Task<JObject> Call(string operation,string? leaseId,JObject? arguments,CancellationToken cancellationToken)
  => JObject.Parse(await bridge.ControlAsync(operation,leaseId,arguments,CallTimeout,cancellationToken));
 private static string Render(JObject reply) { Scrub(reply["Data"]); return reply.ToString(Formatting.None); }
 /// <summary>Defence in depth: nothing resembling grant content or key material ever leaves through a response.</summary>
 private static void Scrub(JToken? token)
 {
  if(token is JObject obj)
  {
   foreach(var property in obj.Properties().ToArray())
   {
    if(Forbidden.Any(f=>string.Equals(property.Name,f,StringComparison.OrdinalIgnoreCase))) property.Remove(); else Scrub(property.Value);
   }
  }
  else if(token is JArray array) foreach(var item in array) Scrub(item);
 }
 private static string? CheckPurpose(string? purpose)
 {
  if(purpose==null) return "purpose is required";
  var text=purpose.Trim();
  if(text.Length<ControlLimits.PurposeMin||text.Length>ControlLimits.PurposeMax) return $"purpose must be {ControlLimits.PurposeMin}..{ControlLimits.PurposeMax} characters";
  return text.Any(char.IsControl) ? "purpose must not contain control characters" : null;
 }
 private static string? CheckDuration(int seconds) =>
  seconds<ControlLimits.DurationMinSeconds||seconds>ControlLimits.DurationMaxSeconds ? $"durationSeconds must be {ControlLimits.DurationMinSeconds}..{ControlLimits.DurationMaxSeconds}" : null;
 private static string? CheckLease(string? leaseId) =>
  ControlLimits.IsLeaseId(leaseId) ? null : $"leaseId must be {ControlLimits.LeaseIdLength} hex characters";
}
