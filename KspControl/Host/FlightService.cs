using System.Security.Cryptography;
using System.Text;
using KspControl.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;

/// <summary>
/// The journaled path of every flight mutation. Same discipline as <see cref="MutationService"/> (arguments checked without a socket, then the lease
/// and the grant's flight.control family, then the journal admits the request, then the bridge is asked), but a flight request is one bridge round trip:
/// the bridge executes it inside a single main-thread drain and answers with the terminal envelope, so there is nothing to poll. The host records what the
/// bridge reported. When it cannot tell (the connection broke after the request may have been sent) the job is indeterminate and the request id stays
/// reserved: a retry never replays a toggle, a stage or an abort.
/// </summary>
public sealed class FlightService(BridgeClient bridge,LeaseKeeper keeper,JournalAccess journal)
{
 private static readonly HashSet<string> NeverDispatched=new(StringComparer.Ordinal)
 {
  "credential_not_configured","credential_invalid","bridge_configuration_invalid",ControlReasons.BridgeBusy,"queue_full","expired","stale_world","operation_unavailable","unauthorized","protocol_mismatch","invalid_request","bridge_stopped"
 };
 private static readonly HashSet<string> LeaseGone=new(StringComparer.Ordinal) { ControlReasons.LeaseInvalid,ControlReasons.LeaseExpired,ControlReasons.AuthorityRevoked };
 private static readonly string[] SetControlFlags={ "sas","rcs","gear","lights","brakes" };

 public Task<string> SetControlsAsync(string requestId,string leaseId,double? throttle,double? throttleDelta,bool? sas,bool? rcs,bool? gear,bool? lights,bool? brakes,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad==null && throttle is { } t && (double.IsNaN(t)||t<0||t>1)) bad="throttle must be 0..1";
  if(bad==null && throttleDelta is { } d && (double.IsNaN(d)||d<-1||d>1)) bad="throttleDelta must be -1..1";
  if(bad==null && throttle.HasValue && throttleDelta.HasValue) bad="throttle and throttleDelta cannot both be given";
  if(bad==null && !throttle.HasValue && !throttleDelta.HasValue && sas==null && rcs==null && gear==null && lights==null && brakes==null) bad="give at least one of throttle, throttleDelta, sas, rcs, gear, lights, brakes";
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId };
  if(throttle.HasValue) args["throttle"]=throttle.Value;
  if(throttleDelta.HasValue) args["throttleDelta"]=throttleDelta.Value;
  var flags=new[]{ sas,rcs,gear,lights,brakes };
  for(int i=0;i<flags.Length;i++) if(flags[i].HasValue) args[SetControlFlags[i]]=flags[i]!.Value;
  return RunAsync(FlightOperations.SetControls,requestId,leaseId.ToLowerInvariant(),args,cancellationToken);
 }

 public Task<string> StageAsync(string requestId,string leaseId,int expectedStage,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad==null && (expectedStage<0||expectedStage>FlightLimits.MaxStageNumber)) bad=$"expectedStage must be 0..{FlightLimits.MaxStageNumber}";
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  return RunAsync(FlightOperations.Stage,requestId,leaseId.ToLowerInvariant(),new JObject { ["requestId"]=requestId,["expectedStage"]=expectedStage },cancellationToken);
 }

 public Task<string> ActionGroupAsync(string requestId,string leaseId,string group,bool? state,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad==null && !FlightLimits.IsActionGroup(group)) bad="group must be one of "+string.Join(", ",FlightLimits.ActionGroups);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["group"]=group };
  if(state.HasValue) args["state"]=state.Value;
  return RunAsync(FlightOperations.ActionGroup,requestId,leaseId.ToLowerInvariant(),args,cancellationToken);
 }

 public Task<string> AbortAsync(string requestId,string leaseId,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  return RunAsync(FlightOperations.Abort,requestId,leaseId.ToLowerInvariant(),new JObject { ["requestId"]=requestId },cancellationToken);
 }

 public Task<string> WarpAsync(string requestId,string leaseId,int rateIndex,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad==null && (rateIndex<0||rateIndex>FlightLimits.MaxRateIndex)) bad=$"rateIndex must be 0..{FlightLimits.MaxRateIndex}";
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  return RunAsync(FlightOperations.Warp,requestId,leaseId.ToLowerInvariant(),new JObject { ["requestId"]=requestId,["rateIndex"]=rateIndex },cancellationToken);
 }

 private async Task<string> RunAsync(string bridgeOperation,string requestId,string leaseId,JObject args,CancellationToken cancellationToken)
 {
  var j=journal.TryGet();
  if(j==null) return MutationService.Fail(journal.State=="locked_by_other_host" ? OperationReasons.JournalLockedByOtherHost : OperationReasons.JournalUnavailable,null,requestId);
  var lease=j.CurrentLease; var grant=journal.CurrentGrant;
  if(lease==null || keeper.TrackedLease==null) return MutationService.Fail(ControlReasons.LeaseRequired,"acquire a lease with control_acquire_lease first",requestId);
  if(!string.Equals(lease.Id,leaseId,StringComparison.Ordinal)) return MutationService.Fail(ControlReasons.LeaseInvalid,"the lease id is not the one this session holds",requestId);
  if(lease.ExpiresAt<=DateTimeOffset.UtcNow) return MutationService.Fail(ControlReasons.LeaseExpired,null,requestId);
  if(grant==null) return MutationService.Fail(ControlReasons.GrantMissing,null,requestId);
  if(!grant.Operations.Contains(FlightEffects.Family,StringComparer.Ordinal)) return MutationService.Fail(OperationReasons.GrantOperationDenied,"the grant does not list "+FlightEffects.Family,requestId);
  if(!grant.Entities.Contains(FlightEffects.JournalEntity,StringComparer.Ordinal)) return MutationService.Fail(OperationReasons.GrantOperationDenied,"the grant does not include the "+FlightEffects.Facility+" facility",requestId);
  Job job;
  try { job=j.Admit(requestId,grant.Id,grant.Generation,leaseId,lease.WorldEpoch,FlightEffects.Family,FlightEffects.JournalEntity,Canonical(bridgeOperation,args),0m,DateTimeOffset.UtcNow); }
  catch(InvalidOperationException e) { return MutationService.Fail(MapAdmission(e.Message),null,requestId); }
  catch(IOException) { return MutationService.Fail(OperationReasons.JournalUnavailable,null,requestId); }
  catch(ArgumentException e) { return CraftPlanService.Invalid(e.Message); }
  // A duplicate answers from the journal, never from a second call: the bridge may already have acted on the first.
  if(job.Status!="accepted") return Stored(job);
  try { j.Begin(requestId,DateTimeOffset.UtcNow); }
  catch(InvalidOperationException) { Safely(()=>j.CancelBeforeDispatch(requestId)); return MutationService.Fail(ControlReasons.LeaseInvalid,"the lease or grant ended before dispatch",requestId); }
  catch(IOException) { return MutationService.Fail(OperationReasons.JournalUnavailable,null,requestId,notDispatched:false); }
  JObject reply;
  try { reply=Parse(await bridge.MutateAsync(bridgeOperation,leaseId,args,cancellationToken)); }
  catch(OperationCanceledException) { return Indeterminate(j,job,bridgeOperation,"request_cancelled","the call was cancelled after the request may have been sent"); }
  return Absorb(j,job,bridgeOperation,reply);
 }

 private string Absorb(ControlJournal j,Job job,string bridgeOperation,JObject reply)
 {
  var status=(string?)reply["Status"]; var reason=(string?)reply["ReasonCode"]; var text=Render(reply);
  string Finish(string terminal,string why)
  {
   Safely(()=>j.Finish(job.RequestId,terminal,why,text));
   if(reason!=null && (LeaseGone.Contains(reason) || reason==OperationReasons.SceneChanged)) { keeper.Untrack(); journal.MirrorEnd(job.LeaseId); }
   return text;
  }
  if(status is "completed") return Finish("completed",reason ?? "observed");
  if(status is "indeterminate" or "cancelled") return Finish(status,reason ?? "unknown");
  if(status=="failed")
  {
   if((bool?)reply["Data"]?["notDispatched"]==true || (reason!=null && NeverDispatched.Contains(reason))) return Finish("cancelled","not_dispatched");
   // The bridge answered with its own envelope for a request it acted on: the outcome is known (for example warp_denied).
   if(reply["Data"]?["requestId"]!=null) return Finish("failed",reason ?? "unknown");
   return Indeterminate(j,job,bridgeOperation,reason ?? "unknown","the host cannot tell whether the bridge acted");
  }
  return Indeterminate(j,job,bridgeOperation,reason ?? "unknown","the bridge sent an unexpected status");
 }

 private static string Indeterminate(ControlJournal j,Job job,string bridgeOperation,string reason,string detail)
 {
  Safely(()=>j.Finish(job.RequestId,"indeterminate",reason,""));
  var data=new JObject { ["operation"]=bridgeOperation.Substring("flight.".Length),["requestId"]=job.RequestId,["phase"]="unknown",["notDispatched"]=false,["detail"]=detail,["reconcile"]="read flight_state and compare it with what was requested before any retry; this request id stays reserved" };
  return JsonConvert.SerializeObject(new BridgeResponse { Status="indeterminate",ReasonCode=reason,Data=data });
 }

 private static string Stored(Job job)
 {
  if(job.Result.Length>0) return job.Result;
  var running=job.Status is "accepted" or "running";
  var data=new JObject { ["requestId"]=job.RequestId,["notDispatched"]=job.Status=="cancelled" && job.Reason=="not_dispatched",["detail"]=running ? "the request is still being sent; read flight_state before asking again" : "the journal holds the status but not the full envelope" };
  if(job.Status=="indeterminate") data["reconcile"]="read flight_state and compare it with what was requested before any retry; this request id stays reserved";
  return JsonConvert.SerializeObject(new BridgeResponse { Status=job.Status,ReasonCode=job.Status=="completed" ? null : job.Reason,Data=data });
 }

 private static string MapAdmission(string code) => code switch
 {
  "request_id_conflict" => OperationReasons.RequestIdConflict,
  "lease_revoked_or_expired" => ControlReasons.LeaseInvalid,
  "grant_denied" => OperationReasons.GrantOperationDenied,
  "budget_exceeded" => OperationReasons.GrantOperationDenied,
  "journal_unavailable" => OperationReasons.JournalUnavailable,
  _ => OperationReasons.ReconcileRequired
 };

 private static void Safely(Action action) { try { action(); } catch(Exception e) when(e is InvalidOperationException or IOException or KeyNotFoundException) { } }
 private static JObject Parse(string text) { try { return JObject.Parse(text); } catch(JsonException) { return JObject.Parse(MutationService.Fail("protocol_invalid")); } }
 private static string Render(JObject reply) { ControlTools.Scrub(reply["Data"]); return reply.ToString(Formatting.None); }
 /// <summary>The fingerprint: the bridge operation and everything it would act on, so a retried request matches and a changed one conflicts.</summary>
 private static string Canonical(string bridgeOperation,JObject args) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bridgeOperation+"|"+args.ToString(Formatting.None))));
}
