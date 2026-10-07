using KspControl.Contracts;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;

/// <summary>
/// The launch half of the journaled mutation path. The host reserves the caller's maxSpendFunds against the grant's gross spend cap before the
/// bridge is asked (so concurrent launches and retries can never overspend), the bridge quotes and refuses a quote above it, and when the job
/// ends the journal settles it: a completed launch records the charge the bridge confirmed and releases the unspent reservation, a launch that
/// was refunded or never started releases all of it, and an indeterminate one keeps it held until reconciled.
/// </summary>
public sealed partial class MutationService
{
 public Task<string> LaunchAsync(string requestId,string leaseId,string expectedRevision,string launchSite,long maxSpendFunds,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.Common(requestId,leaseId,expectedRevision) ?? MutationArguments.LaunchSite(launchSite) ?? MutationArguments.MaxSpend(maxSpendFunds);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["launchSite"]=launchSite,["maxSpendFunds"]=maxSpendFunds,["expectedRevision"]=expectedRevision };
  return RunAsync(OperationEffects.Launch,EditorOperations.Launch,requestId,leaseId.ToLowerInvariant(),args,OperationLimits.WaitSecondsMax,cancellationToken,maxSpendFunds);
 }

 private static string OperationLabel(string operation) => operation switch
 {
  OperationEffects.RestoreSnapshot => "restore_snapshot",
  OperationEffects.Launch => "launch",
  _ => "apply_craft"
 };

 /// <summary>Records a launch job's end in the journal and follows a continuing or released lease.</summary>
 private void SettleLaunch(ControlJournal j,Job job,JObject reply,string terminal,string why,string text)
 {
  var data=reply["Data"] as JObject; decimal charge=0m;
  if(terminal=="completed")
  {
   // The bridge reports what the server charged. Without it the whole reservation is assumed spent, never less.
   var reported=data?["charge"];
   charge=reported is { Type: JTokenType.Integer or JTokenType.Float } ? Math.Ceiling(Math.Max(0m,(decimal)reported)*100m)/100m : job.ReservedCost;
  }
  Safely(()=>j.Settle(job.RequestId,terminal,why,text,charge));
  if(terminal!="completed") return;
  if((bool?)data?["leaseReleased"]==true) { keeper.Untrack(job.LeaseId); journal.MirrorEnd(job.LeaseId); }
  else if((bool?)data?["leaseContinues"]==true && (string?)data?["epoch"] is { Length: >0 } epoch) Safely(()=>j.RebaseLease(job.LeaseId,epoch));
 }
}
