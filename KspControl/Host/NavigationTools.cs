using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using KspControl.Contracts;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;

/// <summary>
/// The navigation tools' argument checks and journaled dispatch. The mutations belong to the flight.autopilot family (a grant that may have MechJeb burn may also
/// edit the flight plan and warp to the burn) and go through the same journal and job registry as the MechJeb tools, so one job runs at a time.
/// </summary>
public sealed class NavigationService(MutationService mutations)
{
 public Task<string> NodeCreateAsync(string requestId,string leaseId,string timeReference,double? timeSeconds,double? prograde,double? normal,double? radial,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId) ?? NavigationLimits.CheckTime(timeReference,timeSeconds)
   ?? NavigationLimits.CheckDeltaV(prograde ?? 0,normal ?? 0,radial ?? 0);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["timeReference"]=timeReference };
  Put(args,"timeSeconds",timeSeconds); Put(args,"prograde",prograde); Put(args,"normal",normal); Put(args,"radial",radial);
  return Run(AutopilotOperations.NodeCreate,requestId,leaseId,args,cancellationToken);
 }

 public Task<string> NodeUpdateAsync(string requestId,string leaseId,int nodeIndex,string? timeReference,double? timeSeconds,double? prograde,double? normal,double? radial,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId) ?? Index(nodeIndex);
  if(bad==null && timeReference!=null) bad=NavigationLimits.CheckTime(timeReference,timeSeconds);
  if(bad==null && timeReference==null && timeSeconds.HasValue) bad="timeSeconds needs timeReference";
  if(bad==null && timeReference==null && prograde==null && normal==null && radial==null) bad="give timeReference or at least one of prograde, normal, radial";
  if(bad==null) bad=Component("prograde",prograde) ?? Component("normal",normal) ?? Component("radial",radial);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["nodeIndex"]=nodeIndex };
  if(timeReference!=null) args["timeReference"]=timeReference;
  Put(args,"timeSeconds",timeSeconds); Put(args,"prograde",prograde); Put(args,"normal",normal); Put(args,"radial",radial);
  return Run(AutopilotOperations.NodeUpdate,requestId,leaseId,args,cancellationToken);
 }

 public Task<string> NodeDeleteAsync(string requestId,string leaseId,int? nodeIndex,bool all,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad==null && all==nodeIndex.HasValue) bad="give exactly one of nodeIndex and all=true";
  if(bad==null && nodeIndex is { } i) bad=Index(i);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId };
  if(nodeIndex.HasValue) args["nodeIndex"]=nodeIndex.Value; else args["all"]=true;
  return Run(AutopilotOperations.NodeDelete,requestId,leaseId,args,cancellationToken);
 }

 public Task<string> WarpToAsync(string requestId,string leaseId,string target,double? timeSeconds,double? leadSeconds,int? nodeIndex,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.RequestId(requestId) ?? MutationArguments.Lease(leaseId);
  if(bad==null && !NavigationLimits.IsWarpTarget(target)) bad="target must be one of "+string.Join(", ",NavigationLimits.WarpTargets);
  if(bad==null && target is "node" or "soi" && timeSeconds.HasValue) bad="timeSeconds is not used with target "+target;
  if(bad==null && target is not ("node" or "soi")) bad=NavigationLimits.CheckTime(target,timeSeconds);
  if(bad==null && leadSeconds is { } lead && (!NavigationLimits.IsFinite(lead) || lead<0 || lead>NavigationLimits.MaxLeadSeconds)) bad=$"leadSeconds must be 0..{NavigationLimits.MaxLeadSeconds}";
  if(bad==null && nodeIndex.HasValue && target!="node") bad="nodeIndex is only used with target node";
  if(bad==null && nodeIndex is { } i) bad=Index(i);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["target"]=target };
  Put(args,"timeSeconds",timeSeconds); Put(args,"leadSeconds",leadSeconds);
  if(nodeIndex.HasValue) args["nodeIndex"]=nodeIndex.Value;
  return Run(AutopilotOperations.WarpTo,requestId,leaseId,args,cancellationToken);
 }

 private static string? Index(int index) => index<0 || index>NavigationLimits.MaxNodeIndex ? $"nodeIndex must be 0..{NavigationLimits.MaxNodeIndex}" : null;
 private static string? Component(string name,double? value) => value is { } v && (!NavigationLimits.IsFinite(v) || Math.Abs(v)>NavigationLimits.MaxDeltaVMetersPerSecond) ? $"{name} must be -{NavigationLimits.MaxDeltaVMetersPerSecond}..{NavigationLimits.MaxDeltaVMetersPerSecond} m/s" : null;
 private static void Put(JObject args,string name,double? value) { if(value.HasValue) args[name]=value.Value; }

 private Task<string> Run(string bridgeOperation,string requestId,string leaseId,JObject args,CancellationToken cancellationToken)
  => mutations.RunAsync(AutopilotOperations.Effect,bridgeOperation,requestId,leaseId.ToLowerInvariant(),args,OperationLimits.WaitSecondsMax,cancellationToken,FlightEffects.JournalEntity);
}

/// <summary>
/// Navigation for a crewed Mun free return: read the patched-conic prediction, edit stock maneuver nodes until the prediction shows the encounter and the return
/// periapsis, then warp on rails to the burn. The prediction is read-only; the mutations need the lease and a grant that lists flight.autopilot.
/// </summary>
[McpServerToolType]
public sealed class NavigationTools(BridgeClient bridge,NavigationService service)
{
 private const string Lease=" Needs a held lease (control_acquire_lease, taken in the flight scene, bound to the active vessel) and a grant that lists flight.autopilot with the FLIGHT facility. Refused with autopilot_busy while a MechJeb job or a warp runs.";
 private const string NodeResult=" Takes effect at once (the solver's flight plan is updated in the same frame) and returns a completed envelope whose plan holds the node (index, universalTimeSeconds, deltaV) and plan.prediction, the same report as flight_orbit_prediction, so the result of the change can be judged without another call. Reuse of a requestId with the same arguments returns the same result without acting again; with different arguments it is request_id_conflict. Invalid arguments return invalid_argument.";
 private const string Time=" timeReference: absolute (timeSeconds is a universal time), in_seconds (timeSeconds from now), apoapsis or periapsis (the next apsis of the current orbit; timeSeconds is an optional offset, -86400..86400). The time must be 1 s .. 10000000 s ahead.";

 [McpServerTool, Description("Read the active vessel's patched-conic trajectory without a lease or any change. Answers only for a vessel this agency owns (owned_active_vessel_unavailable otherwise, flight_unavailable outside flight). Returns nodes [{index (sorted by time; the index the node tools take), universalTimeSeconds, secondsFromNow, deltaV {prograde, normal, radial m/s}, deltaVMagnitudeMetresPerSecond}], withoutNodes (the vessel's own orbit chain; KSP ends it at the first node) and withNodes (the solver's flight plan with every node applied, null without nodes), each with up to 6 patches {referenceBody, start/end universal time, apoapsis (null when open) and periapsis altitude in metres, inclinationDegrees, eccentricity, start/endTransition (INITIAL, ENCOUNTER, ESCAPE, MANEUVER, FINAL, IMPACT)} and an assessment; timeToSoiChangeSeconds of the current coast; and assessment (from withNodes when there are nodes) {munEncounter, munClosestApproachAltitudeMetres (periapsis of the last, post-burn, Mun patch; negative is an impact), returnPeriapsisKerbin (periapsis of the last patch of the first Kerbin run after the Mun), returnsIntoAtmosphere, timeToSoiChangeSeconds, finalTransition}. A free return needs munEncounter true and returnPeriapsisKerbin about 20000..40000 m. A forecast, never proof of an encounter. No arguments.")]
 public Task<string> FlightOrbitPrediction(CancellationToken cancellationToken=default) => bridge.ReadAsync(FlightOperations.OrbitPrediction,null,cancellationToken);

 [McpServerTool, Description("Add a stock maneuver node to the active vessel at a time and with a delta-v in the node frame (prograde, normal, radial in m/s; each -3000..3000 and the magnitude at most 3000; omitted components are 0). At most 16 nodes. Refusals include flight_planning_locked (career Mission Control level), time_unavailable (apoapsis of an open orbit), too_many_nodes and plan_unavailable (no patched-conic solver)."+Time+NodeResult+Lease)]
 public Task<string> FlightNodeCreate(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease taken in the flight scene.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("absolute, in_seconds, apoapsis or periapsis.")] string timeReference,
  [Description("Universal time (absolute), seconds from now (in_seconds) or an offset from the apsis (apoapsis/periapsis, optional).")] double? timeSeconds=null,
  [Description("Prograde delta-v in m/s, -3000..3000.")] [Range(-NavigationLimits.MaxDeltaVMetersPerSecond,NavigationLimits.MaxDeltaVMetersPerSecond)] double? prograde=null,
  [Description("Normal delta-v in m/s, -3000..3000.")] [Range(-NavigationLimits.MaxDeltaVMetersPerSecond,NavigationLimits.MaxDeltaVMetersPerSecond)] double? normal=null,
  [Description("Radial (outward) delta-v in m/s, -3000..3000.")] [Range(-NavigationLimits.MaxDeltaVMetersPerSecond,NavigationLimits.MaxDeltaVMetersPerSecond)] double? radial=null,
  CancellationToken cancellationToken=default) => service.NodeCreateAsync(requestId,leaseId,timeReference,timeSeconds,prograde,normal,radial,cancellationToken);

 [McpServerTool, Description("Change the time and/or the delta-v of an existing maneuver node by its index (from flight_orbit_prediction nodes, sorted by time). Each given delta-v component replaces that component; omitted ones are kept; the merged node must stay within 3000 m/s. With timeReference the node moves; without it the node keeps its time (which must still be in the future). Use it to iterate a free-return burn: adjust prograde (and the time) until plan.prediction.assessment shows munEncounter and returnPeriapsisKerbin about 20000..40000 m. Refusals include no_maneuver_node and node_not_found."+Time+NodeResult+Lease)]
 public Task<string> FlightNodeUpdate(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease taken in the flight scene.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("The node's index, 0..15 (sorted by time).")] [Range(0,NavigationLimits.MaxNodeIndex)] int nodeIndex,
  [Description("Optional new time: absolute, in_seconds, apoapsis or periapsis.")] string? timeReference=null,
  [Description("With timeReference: universal time, seconds from now, or an apsis offset.")] double? timeSeconds=null,
  [Description("New prograde delta-v in m/s, -3000..3000.")] [Range(-NavigationLimits.MaxDeltaVMetersPerSecond,NavigationLimits.MaxDeltaVMetersPerSecond)] double? prograde=null,
  [Description("New normal delta-v in m/s, -3000..3000.")] [Range(-NavigationLimits.MaxDeltaVMetersPerSecond,NavigationLimits.MaxDeltaVMetersPerSecond)] double? normal=null,
  [Description("New radial (outward) delta-v in m/s, -3000..3000.")] [Range(-NavigationLimits.MaxDeltaVMetersPerSecond,NavigationLimits.MaxDeltaVMetersPerSecond)] double? radial=null,
  CancellationToken cancellationToken=default) => service.NodeUpdateAsync(requestId,leaseId,nodeIndex,timeReference,timeSeconds,prograde,normal,radial,cancellationToken);

 [McpServerTool, Description("Delete one maneuver node by its index (sorted by time) or every node (all=true); give exactly one. After all=true the plan.prediction is marked predictionStale=true (re-read flight_orbit_prediction next frame). Refusals include no_maneuver_node and node_not_found."+NodeResult+Lease)]
 public Task<string> FlightNodeDelete(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease taken in the flight scene.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("The node's index, 0..15. Not with all.")] [Range(0,NavigationLimits.MaxNodeIndex)] int? nodeIndex=null,
  [Description("true deletes every node. Not with nodeIndex.")] bool all=false,
  CancellationToken cancellationToken=default) => service.NodeDeleteAsync(requestId,leaseId,nodeIndex,all,cancellationToken);

 [McpServerTool, Description("Time-warp on rails to a target time minus a lead, then return to real time. target: node (the node at nodeIndex, default 0, the next one), soi (the next sphere-of-influence change of the current coast), absolute, in_seconds, apoapsis or periapsis (timeSeconds as for the node tools). leadSeconds (0..3600, default 60) is how far before the target the warp ends, before a node burn use half the burn time plus 30 s. Every rate change goes through the stock TimeWarp.SetRate, so the multiplayer server's warp rules still apply (warp_denied if refused) and the stock altitude limits clamp it; the rate steps down early so it never overshoots, up to 100000x (flight_warp stays capped at 1000x). Job: returns running; poll job_status with the same requestId (envelope warp {targetUniversalTimeSeconds, stopUniversalTimeSeconds, remainingSeconds, rateIndex, effectiveRate}). Ends completed at the stop time; failed when the throttle rises (warp_while_thrusting), the mode leaves rails, a rate is refused, the drop to real time is refused 5 times in a row (warp_drop_refused) or after 30 minutes; cancelled when a person drops warp to real time (warp_stopped_externally), on Stop, lease loss or flight-control input (takeover). Every ending returns warp to real time. Refusals include warp_mode_physics, warp_while_thrusting, warp_not_allowed_here (in the atmosphere or below the first altitude limit), no_maneuver_node, node_not_found, no_soi_change and warp_target_reached. Invalid arguments return invalid_argument."+Lease)]
 public Task<string> FlightWarpTo(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease taken in the flight scene.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("node, soi, absolute, in_seconds, apoapsis or periapsis.")] string target,
  [Description("absolute: universal time; in_seconds: seconds from now; apoapsis/periapsis: optional offset. Not with node or soi.")] double? timeSeconds=null,
  [Description("Seconds before the target at which the warp ends, 0..3600 (default 60).")] [Range(0.0,NavigationLimits.MaxLeadSeconds)] double? leadSeconds=null,
  [Description("With target node: the node index, 0..15 (default 0, the next node).")] [Range(0,NavigationLimits.MaxNodeIndex)] int? nodeIndex=null,
  CancellationToken cancellationToken=default) => service.WarpToAsync(requestId,leaseId,target,timeSeconds,leadSeconds,nodeIndex,cancellationToken);
}
