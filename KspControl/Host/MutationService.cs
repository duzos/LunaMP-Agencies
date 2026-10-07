using System.Security.Cryptography;
using System.Text;
using KspControl.Contracts;
using KspControl.EditorModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;

/// <summary>
/// The journaled path of every mutation (plan R1-section 5): argument checks without a socket, then the lease and the grant's operation
/// family, then the journal admits the request (dedupe by request id, conflict on a different request, restart turns in-flight jobs
/// indeterminate), then the bridge is asked, and the job is followed until it ends or the wait runs out. The host never decides
/// whether the game changed: it records what the bridge reported. When it cannot tell (the connection broke, the bridge restarted),
/// the job is indeterminate and stays reserved until reconciled.
/// </summary>
public sealed partial class MutationService(BridgeClient bridge,LeaseKeeper keeper,JournalAccess journal)
{
 /// <summary>How often a running job is polled. Tests shorten it.</summary>
 public TimeSpan PollInterval { get; set; }=TimeSpan.FromMilliseconds(300);
 private const string Entity="editor:VAB";
 // Failures that prove the request never reached a point where it could act. Everything else that is not a bridge refusal is ambiguous.
 private static readonly HashSet<string> NeverDispatched=new(StringComparer.Ordinal)
 {
  "credential_not_configured","credential_invalid","bridge_configuration_invalid",ControlReasons.BridgeBusy,"queue_full","expired","stale_world","operation_unavailable","unauthorized","protocol_mismatch","invalid_request","bridge_stopped"
 };
 private static readonly HashSet<string> LeaseGone=new(StringComparer.Ordinal) { ControlReasons.LeaseInvalid,ControlReasons.LeaseExpired,ControlReasons.AuthorityRevoked };

 public static string Fail(string reason,string? detail=null,string? requestId=null,bool notDispatched=true)
 {
  var data=new JObject { ["phase"]="host_admission",["notDispatched"]=notDispatched,["dispatched"]=!notDispatched };
  if(requestId!=null) data["requestId"]=requestId;
  if(detail!=null) data["detail"]=detail;
  return JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode=reason,Data=data });
 }

 public Task<string> ApplyAsync(string requestId,string leaseId,string expectedRevision,string graph,string expectedPlanHash,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.Common(requestId,leaseId,expectedRevision);
  if(bad==null) bad=MutationArguments.PlanHash(expectedPlanHash);
  if(bad==null && CraftPlanService.ParseGraph(graph,out var error)==null) bad=error;
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["graph"]=graph,["expectedPlanHash"]=expectedPlanHash,["expectedRevision"]=expectedRevision };
  return RunAsync(OperationEffects.ReplaceCraft,EditorOperations.ApplyCraft,requestId,leaseId.ToLowerInvariant(),args,OperationLimits.WaitSecondsMax,cancellationToken);
 }

 public Task<string> RestoreAsync(string requestId,string leaseId,string expectedRevision,string snapshotId,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.Common(requestId,leaseId,expectedRevision);
  if(bad==null && !OperationLimits.IsSnapshotId(snapshotId)) bad="snapshotId must match [A-Za-z0-9_-]{8,128}";
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["snapshotId"]=snapshotId,["expectedRevision"]=expectedRevision };
  return RunAsync(OperationEffects.RestoreSnapshot,EditorOperations.RestoreSnapshot,requestId,leaseId.ToLowerInvariant(),args,OperationLimits.WaitSecondsMax,cancellationToken);
 }

 /// <summary>The ship name is a bare name (".craft" implied), the same rule the bridge applies to a save target.</summary>
 public static string? FileName(string? fileName)
 {
  if(fileName==null || !CraftPaths.IsName(fileName) || fileName.EndsWith(CraftPaths.CraftExtension,StringComparison.OrdinalIgnoreCase))
   return "fileName must be 1..64 characters of A-Z a-z 0-9 space . _ -, without a leading dot, '..' or a .craft suffix";
  return null;
 }

 public Task<string> SaveAsync(string requestId,string leaseId,string expectedRevision,string fileName,string? replaceExpectedSha256,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.Common(requestId,leaseId,expectedRevision) ?? FileName(fileName);
  var replace=string.IsNullOrEmpty(replaceExpectedSha256) ? null : replaceExpectedSha256;
  if(bad==null && replace!=null && !OperationLimits.IsSha256(replace)) bad=$"replaceExpectedSha256 must be {OperationLimits.Sha256Length} lower-case hex characters (the sha256 craft_list reports)";
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["fileName"]=fileName,["expectedRevision"]=expectedRevision };
  if(replace!=null) args["replaceExpectedSha256"]=replace;
  return RunAsync(OperationEffects.WriteCraft,EditorOperations.SaveCraft,requestId,leaseId.ToLowerInvariant(),args,OperationLimits.WaitSecondsMax,cancellationToken);
 }

 internal async Task<string> RunAsync(string effect,string bridgeOperation,string requestId,string leaseId,JObject args,int waitSeconds,CancellationToken cancellationToken,string entity=Entity,decimal reserve=0m)
 {
  var j=journal.TryGet();
  if(j==null) return Fail(journal.State=="locked_by_other_host" ? OperationReasons.JournalLockedByOtherHost : OperationReasons.JournalUnavailable,null,requestId);
  var lease=j.CurrentLease; var grant=journal.CurrentGrant;
  if(lease==null || keeper.TrackedLease==null) return Fail(ControlReasons.LeaseRequired,"acquire a lease with control_acquire_lease first",requestId);
  if(!string.Equals(lease.Id,leaseId,StringComparison.Ordinal)) return Fail(ControlReasons.LeaseInvalid,"the lease id is not the one this session holds",requestId);
  if(lease.ExpiresAt<=DateTimeOffset.UtcNow) return Fail(ControlReasons.LeaseExpired,null,requestId);
  if(grant==null) return Fail(ControlReasons.GrantMissing,null,requestId);
  if(!grant.Operations.Contains(effect,StringComparer.Ordinal)) return Fail(OperationReasons.GrantOperationDenied,"the grant does not list "+effect,requestId);
  Job job;
  try { job=j.Admit(requestId,grant.Id,grant.Generation,leaseId,lease.WorldEpoch,effect,entity,Canonical(args),reserve,DateTimeOffset.UtcNow); }
  catch(InvalidOperationException e) { return Fail(MapAdmission(e.Message),null,requestId); }
  catch(IOException) { return Fail(OperationReasons.JournalUnavailable,null,requestId); }
  catch(ArgumentException e) { return CraftPlanService.Invalid(e.Message); }
  if(job.Status!="accepted") return await FollowAsync(j,job,null,waitSeconds,cancellationToken); // a duplicate: answer from the journal or the running job
  try { j.Begin(requestId,DateTimeOffset.UtcNow); }
  catch(InvalidOperationException) { Safely(()=>j.CancelBeforeDispatch(requestId)); return Fail(ControlReasons.LeaseInvalid,"the lease or grant ended before dispatch",requestId); }
  catch(IOException) { return Fail(OperationReasons.JournalUnavailable,null,requestId,notDispatched:false); }
  JObject reply;
  try { reply=Parse(await bridge.MutateAsync(bridgeOperation,leaseId,args,cancellationToken)); }
  catch(OperationCanceledException) { return Indeterminate(j,j.Get(requestId),"request_cancelled","the call was cancelled after the request may have been sent"); }
  return await FollowAsync(j,j.Get(requestId),reply,waitSeconds,cancellationToken);
 }

 /// <summary>job_status: the journal's answer for a finished job, otherwise the bridge's current one.</summary>
 public async Task<string> StatusAsync(string requestId,int waitSeconds,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Wait(waitSeconds);
  if(bad!=null) return CraftPlanService.Invalid(bad);
  var j=journal.TryGet();
  if(j==null) return Fail(journal.State=="locked_by_other_host" ? OperationReasons.JournalLockedByOtherHost : OperationReasons.JournalUnavailable,null,requestId,notDispatched:false);
  Job job;
  try { job=j.Get(requestId); }
  catch(KeyNotFoundException) { return Fail(OperationReasons.JobUnknown,"this host has no job with that requestId",requestId,notDispatched:false); }
  catch(IOException) { return Fail(OperationReasons.JournalUnavailable,null,requestId,notDispatched:false); }
  return await FollowAsync(j,job,null,waitSeconds,cancellationToken);
 }

 /// <summary>Follows a job to its end or for at most <paramref name="waitSeconds"/>. <paramref name="reply"/> is the bridge's answer to the dispatch call that just ran, if any.</summary>
 private async Task<string> FollowAsync(ControlJournal j,Job job,JObject? reply,int waitSeconds,CancellationToken cancellationToken)
 {
  if(reply==null && job.Status is "completed" or "failed" or "cancelled") return StoredOrSynthesised(job);
  var deadline=DateTime.UtcNow.AddSeconds(waitSeconds); var pollFailures=0; var dispatchReply=reply!=null; var firstRead=true; string? lastFailure=null;
  while(true)
  {
   if(reply==null)
   {
    if(!firstRead)
    {
     if(DateTime.UtcNow>=deadline || cancellationToken.IsCancellationRequested) return lastFailure ?? Running(job,null);
     try { await Task.Delay(PollInterval,cancellationToken); } catch(OperationCanceledException) { return lastFailure ?? Running(job,null); }
    }
    firstRead=false;
    try { reply=Parse(await bridge.ReadAsync(StatusOperation(job.Operation),new JObject { ["requestId"]=job.RequestId },cancellationToken)); }
    catch(OperationCanceledException) { return lastFailure ?? Running(job,null); }
    if((string?)reply["Status"]=="failed" && (string?)reply["ReasonCode"]!=OperationReasons.JobUnknown && reply["Data"]?["requestId"]==null)
    {
     // A poll that does not get through says nothing about the job. A dead game does: after repeated failures (or at once if nothing listens) the outcome is unknown.
     var why=(string?)reply["ReasonCode"] ?? "unknown";
     if(++pollFailures>=3 || why=="bridge_unreachable") return Indeterminate(j,job,why,"the bridge stopped answering while the job ran");
     lastFailure=Render(reply); reply=null; continue;
    }
    pollFailures=0; lastFailure=null;
   }
   var verdict=Absorb(j,job,reply,dispatchReply);
   if(verdict!=null) return verdict;
   dispatchReply=false;
   if(DateTime.UtcNow>=deadline || cancellationToken.IsCancellationRequested) return Running(job,reply);
   reply=null;
  }
 }

 /// <summary>Applies one bridge reply to the journal. Returns the response text when the job has ended, or null to keep waiting.</summary>
 private string? Absorb(ControlJournal j,Job job,JObject reply,bool dispatchReply)
 {
  var status=(string?)reply["Status"]; var reason=(string?)reply["ReasonCode"]; var text=Render(reply);
  if(status is "accepted" or "running") return null;
  string Finish(string terminal,string why)
  {
   if(job.Operation==OperationEffects.Launch) SettleLaunch(j,job,reply,terminal,why,text);
   else Safely(()=>j.Finish(job.RequestId,terminal,why,text));
   if(reason!=null && (LeaseGone.Contains(reason) || reason==OperationReasons.HumanInputDuringOperation)) { keeper.Untrack(); journal.MirrorEnd(job.LeaseId); }
   return text;
  }
  if(status is "completed" or "cancelled" or "indeterminate") return Finish(status,reason ?? "observed");
  if(status=="failed")
  {
   if(reason==OperationReasons.JobUnknown) return Indeterminate(j,job,OperationReasons.ReconcileRequired,"the bridge no longer knows this job (it restarted)");
   if(!dispatchReply) return Finish("failed",reason ?? "unknown");
   // The reply to the dispatch itself: a refusal that never acted, a job that already failed, or a transport failure that proves nothing.
   if((bool?)reply["Data"]?["notDispatched"]==true || (reason!=null && NeverDispatched.Contains(reason))) return Finish("cancelled","not_dispatched");
   if(reply["Data"]?["requestId"]!=null) return Finish("failed",reason ?? "unknown");
   return Indeterminate(j,job,reason ?? "unknown","the host cannot tell whether the bridge acted");
  }
  return Indeterminate(j,job,reason ?? "unknown","the bridge sent an unexpected status");
 }

 private string Indeterminate(ControlJournal j,Job job,string reason,string detail)
 {
  Safely(()=>j.Finish(job.RequestId,"indeterminate",reason,""));
  var data=new JObject { ["operation"]=OperationLabel(job),["requestId"]=job.RequestId,["phase"]="unknown",["notDispatched"]=false,["detail"]=detail,["reconcile"]="read editor_state and the recent snapshots before retrying; this request id stays reserved" };
  return JsonConvert.SerializeObject(new BridgeResponse { Status="indeterminate",ReasonCode=reason,Data=data });
 }

 private static string Running(Job job,JObject? reply)
 {
  if(reply!=null && (string?)reply["Status"] is "accepted" or "running") return Render(reply);
  return JsonConvert.SerializeObject(new BridgeResponse { Status="running",Data=new JObject { ["requestId"]=job.RequestId,["phase"]="waiting",["detail"]="the job is still running; call job_status with this requestId" } });
 }

 private static string StoredOrSynthesised(Job job)
 {
  if(job.Result.Length>0) return job.Result;
  var data=new JObject { ["requestId"]=job.RequestId,["notDispatched"]=job.Status=="cancelled" && job.Reason=="not_dispatched",["detail"]="the journal holds the status but not the full envelope" };
  return JsonConvert.SerializeObject(new BridgeResponse { Status=job.Status,ReasonCode=job.Status=="completed" ? null : job.Reason,Data=data });
 }

 /// <summary>The bridge operation that reports a job of this effect.</summary>
 private static string StatusOperation(string effect) => effect==AutopilotOperations.Effect ? AutopilotOperations.Status : EditorOperations.OperationStatus;

 private static string MapAdmission(string code) => code switch
 {
  "request_id_conflict" => OperationReasons.RequestIdConflict,
  "lease_revoked_or_expired" => ControlReasons.LeaseInvalid,
  "grant_denied" => OperationReasons.GrantOperationDenied,
  "budget_exceeded" => OperationReasons.SpendCapExceeded,
  "journal_unavailable" => OperationReasons.JournalUnavailable,
  _ => OperationReasons.ReconcileRequired
 };

 private static void Safely(Action action) { try { action(); } catch(Exception e) when(e is InvalidOperationException or IOException or KeyNotFoundException) { } }
 private static JObject Parse(string text) { try { return JObject.Parse(text); } catch(JsonException) { return JObject.Parse(Fail("protocol_invalid")); } }
 private static string Render(JObject reply) { ControlTools.Scrub(reply["Data"]); return reply.ToString(Formatting.None); }
 /// <summary>The request fingerprint: everything the bridge would act on, so a retried request matches and a changed one conflicts.</summary>
 private static string Canonical(JObject args) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(args.ToString(Formatting.None))));
}

/// <summary>The shared argument rules of the mutation tools, each returning an invalid_argument detail or null.</summary>
public static class MutationArguments
{
 public static string? RequestId(string? requestId) => OperationLimits.IsRequestId(requestId) ? null : "requestId must match [A-Za-z0-9_-]{8,128}";
 public static string? Lease(string? leaseId) => ControlLimits.IsLeaseId(leaseId) ? null : $"leaseId must be {ControlLimits.LeaseIdLength} hex characters";
 public static string? Revision(string? revision)
 {
  if(string.IsNullOrEmpty(revision) || revision.Length>OperationLimits.ExpectedRevisionMax) return $"expectedRevision must be 1..{OperationLimits.ExpectedRevisionMax} characters (the token from editor_state)";
  foreach(var c in revision) if(!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return "expectedRevision must be the opaque token from editor_state";
  return null;
 }
 public static string? PlanHash(string? hash) => OperationLimits.IsPlanHash(hash) ? null : $"expectedPlanHash must be {OperationLimits.PlanHashLength} lower-case hex characters (the planHash from craft_plan)";
 public static string? LaunchSite(string? site) => OperationLimits.IsLaunchSite(site) ? null : $"launchSite must be 1..{OperationLimits.LaunchSiteMax} printable characters";
 public static string? MaxSpend(long funds) => funds<0 || funds>OperationLimits.MaxSpendFunds ? $"maxSpendFunds must be 0..{OperationLimits.MaxSpendFunds}" : null;
 public static string? Wait(int seconds) => seconds<0 || seconds>OperationLimits.WaitSecondsMax ? $"waitSeconds must be 0..{OperationLimits.WaitSecondsMax}" : null;
 public static string? Common(string? requestId,string? leaseId,string? revision) => RequestId(requestId) ?? Lease(leaseId) ?? Revision(revision);
}
