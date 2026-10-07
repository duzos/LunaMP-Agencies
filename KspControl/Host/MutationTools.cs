using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using KspControl.Contracts;
using ModelContextProtocol.Server;
namespace KspControl.Host;

/// <summary>
/// The mutation tools (plan R1-section 4). Each needs the lease from control_acquire_lease and a grant that lists the operation, goes
/// through the journal, and waits up to 20 seconds before answering "running". None accepts or returns grant content: the grant is
/// issued by a human-run CLI and verified only inside the game.
/// </summary>
[McpServerToolType]
public sealed class MutationTools(MutationService service)
{
 [McpServerTool, Description("Replace the editor craft with a planned rocket. Needs a held lease (control_acquire_lease), a grant that lists editor.replace_craft, the editorRevision token from a fresh editor_state, and the graph and planHash from craft_plan for the same graph. The bridge re-plans against the live catalog and refuses with plan_changed if the planHash differs. Takes a verified snapshot of a non-empty editor first, loads the structural craft through the normal editor load, verifies it against the plan, and on any failure reloads the snapshot (restore.result reports it). Returns the job envelope: status running, completed, failed, cancelled or indeterminate, phase, notDispatched (true only for a guaranteed no-effect failure before the load), snapshotId, effects, restore, declaredOutputs and editorRevision (the new token, set once the editor has settled). After 20 seconds a running job is returned as running: poll job_status with the same requestId. Reuse of a requestId with the same arguments returns the same job; with different arguments it returns request_id_conflict. Refusals include lease_required, lease_invalid, grant_operation_denied, stale_revision, editor_busy, unsaved_human_craft, invalid_graph (with issues), plan_changed, part_locked, snapshot_unverified and save_overwrite_guard_unavailable. Invalid arguments return invalid_argument.")]
 public Task<string> EditorApplyCraft(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -. Reuse it only to ask about the same request again.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("The editorRevision token from editor_state, at most 128 characters. It is opaque: pass it back unchanged.")] [StringLength(OperationLimits.ExpectedRevisionMax,MinimumLength=1)] string expectedRevision,
  [Description("The same graph JSON object (as a string, at most 262144 bytes) that was given to craft_plan.")] [StringLength(ConstructionLimits.MaxGraphBytes)] string graph,
  [Description("The planHash returned by craft_plan for this graph, 64 lower-case hex characters.")] [StringLength(OperationLimits.PlanHashLength,MinimumLength=OperationLimits.PlanHashLength)] string expectedPlanHash,
  CancellationToken cancellationToken=default) => service.ApplyAsync(requestId,leaseId,expectedRevision,graph,expectedPlanHash,cancellationToken);

 [McpServerTool, Description("Reload a recovery snapshot into the editor (the snapshotId from editor_state recentSnapshots or from an apply job). Needs a held lease, a grant that lists editor.restore_snapshot, and the editorRevision token from a fresh editor_state. Verifies the snapshot file against its recorded hash, loads it through the normal editor load, checks the fingerprint, then writes back the ship name, description, flag, the save-name fields and the unsaved marker. Returns the same job envelope as editor_apply_craft. Refusals include snapshot_not_found, snapshot_unverified, stale_revision and editor_busy. Invalid arguments return invalid_argument.")]
 public Task<string> EditorRestoreSnapshot(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("The editorRevision token from editor_state, at most 128 characters, passed back unchanged.")] [StringLength(OperationLimits.ExpectedRevisionMax,MinimumLength=1)] string expectedRevision,
  [Description("The snapshot id, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string snapshotId,
  CancellationToken cancellationToken=default) => service.RestoreAsync(requestId,leaseId,expectedRevision,snapshotId,cancellationToken);

 [McpServerTool, Description("Read the job envelope of an earlier editor_apply_craft or editor_restore_snapshot, optionally waiting for it to end. A finished job is answered from the host journal; a running one is read from the game. status stays running (phase post_unlock_grace, thumbnail_settle) until the editor has settled and the locks are released, and editorRevision is final only once status is terminal. If the game restarted and no longer knows the job, status is indeterminate with reconcile guidance. waitSeconds 0..20. Invalid arguments return invalid_argument; an unknown requestId returns job_unknown.")]
 public Task<string> JobStatus(
  [Description("The requestId of the job.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("How long to wait for the job to end, 0..20 seconds. Default 0.")] [Range(0,OperationLimits.WaitSecondsMax)] int waitSeconds=0,
  CancellationToken cancellationToken=default) => service.StatusAsync(requestId,waitSeconds,cancellationToken);
}
