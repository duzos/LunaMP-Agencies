using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using KspControl.Contracts;
using ModelContextProtocol.Server;
namespace KspControl.Host;

/// <summary>The launch tool (plan 32, flight and multiplayer strategy). It needs the lease, a grant listing the launch family and a spend limit, and goes through the journal.</summary>
[McpServerToolType]
public sealed class LaunchTools(MutationService service)
{
 [McpServerTool, Description("Launch the craft now in the editor through the normal editor launch path: stock pre-flight checks, the agency tooling quote and the server's funds reservation, then the flight scene. Needs a held lease (control_acquire_lease), a grant that lists the launch family (editor.launch) and sets a spend limit, and the editorRevision token from a fresh editor_state. maxSpendFunds is the most this launch may cost: it is reserved against the grant's gross spend cap (spendLimitFunds, set by the human, never raised by retries or income) before anything happens, and a quote above it is refused with spend_exceeds_max. The job quotes, begins the launch, then waits for the reservation, the flight scene and a new vessel owned by your agency; it returns completed with vesselId, the actual charge and whether the lease continues (it continues onto vessel:<id> only if the grant allows flight operations, otherwise it is released). A launch aborted before the flight scene loads is cancelled through the normal flow and ends only after the refund shows; one that cannot be cancelled, or a disconnect, ends indeterminate and keeps its reservation: inspect game state before retrying. After 20 seconds a running job is returned as running: poll job_status with the same requestId. Refusals include lease_required, lease_invalid, grant_operation_denied, spend_cap_exceeded, stale_revision, editor_busy, facade_unavailable, launch_not_allowed, launch_pending, quote_unavailable, spend_exceeds_max, economy_not_ready, insufficient_funds and launch_site_invalid. Invalid arguments return invalid_argument.")]
 public Task<string> EditorLaunch(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -. Reuse it only to ask about the same request again; a retry with a new id is a new launch with a new reservation.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("The editorRevision token from editor_state, at most 128 characters, passed back unchanged.")] [StringLength(OperationLimits.ExpectedRevisionMax,MinimumLength=1)] string expectedRevision,
  [Description("The launch site name, for example LaunchPad or Runway, as the editor lists it for your agency. At most 128 characters.")] [StringLength(OperationLimits.LaunchSiteMax,MinimumLength=1)] string launchSite,
  [Description("The most this launch may cost, in whole funds, 0..1000000000. Reserved before the launch and never exceeded.")] [Range(0,1000000000)] int maxSpendFunds,
  CancellationToken cancellationToken=default) => service.LaunchAsync(requestId,leaseId,expectedRevision,launchSite,maxSpendFunds,cancellationToken);
}
