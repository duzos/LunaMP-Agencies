using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using KspControl.Contracts;
using ModelContextProtocol.Server;
namespace KspControl.Host;

/// <summary>
/// editor_load_craft (plan R3-section 4, R4-section 6.4). Like the other mutation tools it needs the lease and a grant listing
/// editor.replace_craft, goes through the journal and waits up to 20 seconds before answering "running".
/// </summary>
[McpServerToolType]
public sealed class LoadTools(MutationService service)
{
 [McpServerTool, Description("Load an existing craft file from the save's Ships folder into the editor. Needs a held lease (control_acquire_lease), a grant that lists editor.replace_craft, the editorRevision token from a fresh editor_state, and the sha256 of the file (from craft_list). The file is never loaded in place and never modified: the bridge copies it to a private staging folder, checks the copy's hash, refuses broken links, parts that are not installed and MODULE names the part prefabs lack (craft_invalid_links, craft_parts_missing, module_not_installed) before touching the editor, and runs KSP's own craft upgrade pipeline on the copy. If the pipeline would change the craft, the job is refused with craft_requires_upgrade (notDispatched=true, nothing changed) unless allowUpgrade is true; then the upgraded craft is loaded, upgradedOnLoad is true, pipelineDifferences lists what changed and the file on disk stays as it was. A pipeline failure is craft_upgrade_failed (notDispatched=true). After a verified snapshot of a non-empty editor, the staged craft is loaded through the normal editor load and compared with the editor's own save of it: comparison {equal, differences, exclusionsApplied}. The file and its sidecars are hashed again after the load; a change is source_changed_during_load. Returns the same job envelope as editor_apply_craft, plus upgradedOnLoad, pipelineDifferences, scriptsApplied (or unavailable), comparison and load. After 20 seconds a running job is returned as running: poll job_status with the same requestId. Other refusals include file_changed (the file does not hash to expectedSha256), craft_not_found, craft_unreadable, craft_too_large, facility_mismatch, path_outside_save, stale_revision, editor_busy and unsaved_human_craft. Invalid arguments return invalid_argument.")]
 public Task<string> EditorLoadCraft(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -. Reuse it only to ask about the same request again.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("The editorRevision token from editor_state, at most 128 characters, passed back unchanged.")] [StringLength(OperationLimits.ExpectedRevisionMax,MinimumLength=1)] string expectedRevision,
  [Description("VAB or SPH. Must be the facility the editor is in.")] [StringLength(3,MinimumLength=3)] string facility,
  [Description("The craft file name with its .craft extension, as listed by craft_list. No folder part.")] [StringLength(LoadLimits.FileNameMax,MinimumLength=7)] string fileName,
  [Description("The sha256 of the file, 64 lower-case hex characters, as listed by craft_list.")] [StringLength(64,MinimumLength=64)] string expectedSha256,
  [Description("Load the craft even if KSP's upgrade pipeline changes it. Default false: such a craft is refused with craft_requires_upgrade and the differences are reported.")] bool allowUpgrade=false,
  CancellationToken cancellationToken=default) => service.LoadAsync(requestId,leaseId,expectedRevision,facility,fileName,expectedSha256,allowUpgrade,cancellationToken);
}
