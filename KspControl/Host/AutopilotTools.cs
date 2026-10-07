using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using KspControl.Contracts;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;

/// <summary>
/// The MechJeb flight tools' argument checks and journaled dispatch. Every mutation needs the lease and a grant that lists flight.autopilot, goes
/// through the same journal as the editor mutations, and is followed with job_status. The bridge owns every decision about the game.
/// </summary>
public sealed class AutopilotService(MutationService mutations)
{
 public Task<string> AscentAsync(string requestId,string leaseId,int targetAltitudeMeters,double inclinationDegrees,bool autostage,bool autoWarp,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad==null && (targetAltitudeMeters<AutopilotLimits.AltitudeMinMeters || targetAltitudeMeters>AutopilotLimits.AltitudeMaxMeters)) bad=$"targetAltitudeMeters must be {AutopilotLimits.AltitudeMinMeters}..{AutopilotLimits.AltitudeMaxMeters}";
  if(bad==null && (double.IsNaN(inclinationDegrees) || inclinationDegrees<AutopilotLimits.InclinationMinDegrees || inclinationDegrees>AutopilotLimits.InclinationMaxDegrees)) bad=$"inclinationDegrees must be {AutopilotLimits.InclinationMinDegrees}..{AutopilotLimits.InclinationMaxDegrees}";
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["targetAltitudeMeters"]=targetAltitudeMeters,["inclinationDegrees"]=inclinationDegrees,["autostage"]=autostage,["autoWarp"]=autoWarp };
  return Run(AutopilotOperations.Ascent,requestId,leaseId,args,cancellationToken);
 }

 public Task<string> ExecuteNodeAsync(string requestId,string leaseId,bool all,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  return Run(AutopilotOperations.ExecuteNode,requestId,leaseId,new JObject { ["requestId"]=requestId,["all"]=all },cancellationToken);
 }

 public Task<string> PlanCircularizeAsync(string requestId,string leaseId,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  return Run(AutopilotOperations.PlanCircularize,requestId,leaseId,new JObject { ["requestId"]=requestId },cancellationToken);
 }

 public Task<string> PlanHohmannAsync(string requestId,string leaseId,string targetBodyName,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad==null && !AutopilotLimits.IsBodyName(targetBodyName)) bad=$"targetBodyName must be 1..{AutopilotLimits.BodyNameMax} letters, digits, spaces, apostrophes, dashes or underscores";
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  return Run(AutopilotOperations.PlanHohmann,requestId,leaseId,new JObject { ["requestId"]=requestId,["targetBodyName"]=targetBodyName },cancellationToken);
 }

 private Task<string> Run(string bridgeOperation,string requestId,string leaseId,JObject args,CancellationToken cancellationToken)
  => mutations.RunAsync(AutopilotOperations.Effect,bridgeOperation,requestId,leaseId.ToLowerInvariant(),args,OperationLimits.WaitSecondsMax,cancellationToken,AutopilotOperations.JournalEntity);
}

/// <summary>
/// MechJeb flight tools (plan P3b). The bridge drives the installed MechJeb through a guarded reflection adapter, refuses when another controller is
/// engaged, and disengages at once on Stop, a lost lease, a vessel or scene change, or human input.
/// </summary>
[McpServerToolType]
public sealed class AutopilotTools(BridgeClient bridge,AutopilotService service)
{
 private const string Lifecycle=" Returns the job envelope (status running, completed, failed, cancelled or indeterminate; phase; telemetry; notDispatched). After 20 seconds a running job is returned as running: poll job_status with the same requestId. Releasing the lease (control_release_lease) or the Stop button ends the job and releases MechJeb at once. Needs a held lease taken in the flight scene and a grant that lists flight.autopilot.";

 [McpServerTool, Description("Read the MechJeb adapter state without changing anything: whether MechJeb is installed, its version and whether this adapter supports it, which modules were found, which autopilots are engaged on the active vessel, the ascent settings now configured (target altitude, inclination, autostage), the node executor state, competing controllers (other MechJeb autopilots or AtmosphereAutopilot), flight telemetry and the last autopilot job. No lease needed.")]
 public Task<string> MechjebStatus(CancellationToken cancellationToken) => bridge.ReadAsync(AutopilotOperations.MechJebStatus,null,cancellationToken);

 [McpServerTool, Description("Fly the active vessel to orbit with MechJeb's ascent guidance. Configures the target orbit altitude, inclination and autostage on MechJeb, engages it with a bridge-owned user, and reports progress (phase, altitude, apoapsis, periapsis) until periapsis clears the atmosphere of the current body, or it fails or times out (20 minutes). Refuses with competing_controller if another MechJeb autopilot or AtmosphereAutopilot is engaged, mechjeb_unavailable if the vessel has no MechJeb core part, and never fights manual input: using the flight controls ends the job and starts the takeover cooldown. autoWarp true is refused (unsupported_option)."+Lifecycle)]
 public Task<string> MechjebAscent(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease taken in the flight scene.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("Target circular orbit altitude in metres, 70000..500000.")] [Range(AutopilotLimits.AltitudeMinMeters,AutopilotLimits.AltitudeMaxMeters)] int targetAltitudeMeters,
  [Description("Target inclination in degrees, -180..180. 0 is equatorial.")] [Range(AutopilotLimits.InclinationMinDegrees,AutopilotLimits.InclinationMaxDegrees)] double inclinationDegrees,
  [Description("Let MechJeb stage automatically when a stage burns out.")] bool autostage,
  [Description("Must be false (the default): MechJeb 2.15 has no warp setting for the ascent autopilot.")] bool autoWarp=false,
  CancellationToken cancellationToken=default) => service.AscentAsync(requestId,leaseId,targetAltitudeMeters,inclinationDegrees,autostage,autoWarp,cancellationToken);

 [McpServerTool, Description("Execute the active vessel's next maneuver node (all=false) or every node (all=true) with MechJeb's node executor, engaged with a bridge-owned user. The executor's own auto-warp is forced off: it waits in real time. Ends when the node is consumed, or fails with node_execution_ended_early or a timeout (60 minutes). Refuses with no_maneuver_node when there is none and competing_controller when another controller is engaged."+Lifecycle)]
 public Task<string> MechjebExecuteNode(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease taken in the flight scene.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("false executes the next node only, true executes all nodes.")] bool all,
  CancellationToken cancellationToken=default) => service.ExecuteNodeAsync(requestId,leaseId,all,cancellationToken);

 [McpServerTool, Description("Create a stock maneuver node that circularizes the orbit at the apoapsis. Computed by stock vis-viva arithmetic (plan.source is stock_math), not by MechJeb's planner, and exact for the current orbit. Changes the flight plan only; it burns nothing. Follow with mechjeb_execute_node."+Lifecycle)]
 public Task<string> MechjebPlanCircularize(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease taken in the flight scene.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  CancellationToken cancellationToken=default) => service.PlanCircularizeAsync(requestId,leaseId,cancellationToken);

 [McpServerTool, Description("Create a stock maneuver node for the departure burn of a Hohmann transfer to a moon of the current body (for example Mun from Kerbin orbit). This is a clearly labelled ESTIMATE (plan.kind hohmann_phase_wait_estimate, plan.source stock_math): it assumes near-circular coplanar orbits, waits for the phase-angle window and does not refine the encounter. Needs a near-circular orbit (eccentricity at most 0.1). Check the encounter on the map and correct it before relying on it. Changes the flight plan only."+Lifecycle)]
 public Task<string> MechjebPlanHohmannToTarget(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease taken in the flight scene.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("Name of a moon of the current body, for example Mun.")] [StringLength(AutopilotLimits.BodyNameMax,MinimumLength=1)] string targetBodyName,
  CancellationToken cancellationToken=default) => service.PlanHohmannAsync(requestId,leaseId,targetBodyName,cancellationToken);
}
