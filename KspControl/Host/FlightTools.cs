using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using KspControl.Contracts;
using ModelContextProtocol.Server;
namespace KspControl.Host;

/// <summary>
/// Flight telemetry and the lease-bound flight controls. flight_state is read-only and needs no lease. Every other tool needs the lease from
/// control_acquire_lease and a grant that lists flight.control on the FLIGHT facility, goes through the journal, and answers with a terminal
/// envelope observed in the game (completed, failed, cancelled or indeterminate). None accepts or returns grant content.
/// </summary>
[McpServerToolType]
public sealed class FlightTools(BridgeClient bridge,FlightService service)
{
 [McpServerTool, Description("Read the active vessel's flight state without a lease or any change. Answers only for a vessel this agency owns (reasonCode owned_active_vessel_unavailable otherwise, flight_unavailable outside the flight scene). Returns vessel {id, name, entity}, situation, body, universalTimeSeconds, altitudeAboveSeaLevelMetres, vertical/surface/orbital speed in metres per second, orbit {referenceBody (the sphere of influence the vessel is in now: use it to confirm a Mun encounter), apoapsis/periapsis altitude in metres, inclinationDegrees, eccentricity, periodSeconds, timeToApoapsis/Periapsis in seconds, predictedNextBody (a forecast, never proof)}, stage {current, count, per-stage delta-V in metres per second from the stock calculation, deltaVReady}, resources totals, controls {throttleFraction, sas, rcs, gear, lights, brakes}, warp {mode, rateIndex, effectiveRate and the caps}, crew count and controlState.mechJebPresent (presence only). Bounded: at most 32 stages and 32 resources. No arguments.")]
 public Task<string> FlightState(CancellationToken cancellationToken=default) => bridge.ReadAsync(FlightOperations.State,null,cancellationToken);

 [McpServerTool, Description("Set flight controls on the active vessel in one request. Needs a held lease (control_acquire_lease) bound to the active vessel and a grant that lists flight.control with the FLIGHT facility. Give at least one of: throttle (0..1 fraction; 1 is full, 0 is cut), throttleDelta (-1..1 added to the current throttle, for increase or decrease; not with throttle), sas, rcs, gear, lights, brakes (true or false desired states: the setter is used, never a blind toggle, and a state that already matches is not touched). The throttle is held by the bridge through a fly-by-wire hook for as long as the lease lasts; Stop, lease loss or a vessel switch cuts it to zero. Every part action bound to the requested groups is classified first; an unknown module is reported in unclassified[] and consequential[] but does not refuse the request. Returns the envelope: status, applied[], consequential[], observed state read back from the game, notDispatched. A state the game does not confirm is indeterminate: read flight_state, do not repeat blindly. Reuse of a requestId with the same arguments returns the same result without acting again. Invalid arguments return invalid_argument.")]
 public Task<string> FlightSetControls(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("Explicit throttle fraction 0..1.")] [Range(0.0,1.0)] double? throttle=null,
  [Description("Throttle change -1..1 added to the current throttle (increase or decrease). Not with throttle.")] [Range(-1.0,1.0)] double? throttleDelta=null,
  [Description("Desired SAS state.")] bool? sas=null,
  [Description("Desired RCS state.")] bool? rcs=null,
  [Description("Desired landing gear state (true is deployed).")] bool? gear=null,
  [Description("Desired lights state.")] bool? lights=null,
  [Description("Desired brakes state.")] bool? brakes=null,
  CancellationToken cancellationToken=default) => service.SetControlsAsync(requestId,leaseId,throttle,throttleDelta,sas,rcs,gear,lights,brakes,cancellationToken);

 [McpServerTool, Description("Activate the next stage of the active vessel, only if the vessel is still at expectedStage (the stage.current from flight_state); otherwise stage_mismatch and nothing happens. Consequential: the modules on the parts of that stage (engines, decouplers, parachutes, docking nodes, clamps, fairings) are classified first and reported in consequential[]; a module the classifier does not know is reported in unclassified[] and consequential[] but does not refuse the request. Needs a held lease and a grant that lists flight.control with the FLIGHT facility. The result is confirmed by reading the stage number back; an unconfirmed stage is indeterminate and must be reconciled with flight_state, never retried blindly. Refusals include lease_required, lease_invalid, grant_operation_denied, stage_mismatch, staging_locked, too_many_effects and owned_active_vessel_unavailable. Invalid arguments return invalid_argument.")]
 public Task<string> FlightStage(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("The stage number the vessel must be at now (stage.current from flight_state), 0..999.")] [Range(0,FlightLimits.MaxStageNumber)] int expectedStage,
  CancellationToken cancellationToken=default) => service.StageAsync(requestId,leaseId,expectedStage,cancellationToken);

 [McpServerTool, Description("Trigger an action group on the active vessel. group is one of Gear, Light, Brakes, SAS, RCS, Custom01..Custom10. With state (true or false) the group is driven to that desired state and a state that already matches is a no-op; without state the group is toggled once and the new state is read back. Every part action bound to the group is classified first and reported in consequential[]; a bound module the classifier does not know (including mod actions) is reported in unclassified[] and consequential[] but does not refuse the request. Needs a held lease and a grant that lists flight.control with the FLIGHT facility. An unconfirmed state is indeterminate: read flight_state, do not repeat blindly. Invalid arguments return invalid_argument.")]
 public Task<string> FlightActionGroup(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("Gear, Light, Brakes, SAS, RCS or Custom01..Custom10.")] string group,
  [Description("Desired state. Omit to toggle once.")] bool? state=null,
  CancellationToken cancellationToken=default) => service.ActionGroupAsync(requestId,leaseId,group,state,cancellationToken);

 [McpServerTool, Description("Fire the abort action group of the active vessel. Consequential (launch escape and any other part bound to Abort): the bound actions are classified first and reported in consequential[]; an unknown module refuses the whole request. Needs a held lease and a grant that lists flight.control with the FLIGHT facility. Does not change the throttle. The result is confirmed by reading the abort group back; unconfirmed is indeterminate. Invalid arguments return invalid_argument.")]
 public Task<string> FlightAbort(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  CancellationToken cancellationToken=default) => service.AbortAsync(requestId,leaseId,cancellationToken);

 [McpServerTool, Description("Set the time-warp rate index of the current warp mode through the stock rate setter (the multiplayer warp rules still apply and may refuse: warp_denied, rate unchanged). Capped: an effective rate above 1000x or a physics warp above 1x is refused (warp_above_cap), as is an index the current altitude does not allow; warping up while the throttle is above zero is refused (warp_while_thrusting). Index 0 is real time and is always allowed. Needs a held lease and a grant that lists flight.control with the FLIGHT facility. rateIndex is 0..7 into the rate table reported by flight_state warp. Stop and lease loss return warp to real time. Invalid arguments return invalid_argument.")]
 public Task<string> FlightWarp(
  [Description("Unique id for this request, 8..128 characters of A-Z a-z 0-9 _ -.")] [StringLength(OperationLimits.SnapshotIdMax,MinimumLength=OperationLimits.SnapshotIdMin)] string requestId,
  [Description("Lease id, 32 hex characters, from control_acquire_lease.")] [StringLength(ControlLimits.LeaseIdLength,MinimumLength=ControlLimits.LeaseIdLength)] string leaseId,
  [Description("Rate index in the current mode's table, 0..7.")] [Range(0,FlightLimits.MaxRateIndex)] int rateIndex,
  CancellationToken cancellationToken=default) => service.WarpAsync(requestId,leaseId,rateIndex,cancellationToken);
}
