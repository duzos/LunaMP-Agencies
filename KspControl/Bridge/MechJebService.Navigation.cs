using System;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// The stock navigation mutations of the flight.autopilot family: maneuver-node edits (finished inside admission, like the plans) and the warp_to job
    /// (advanced by the runner, polled with flight.autopilot_status). Admission is <see cref="Mutate"/>'s: request id, lease, fingerprint, the grant's
    /// family, a free runner and the lease vessel being the active one.
    /// </summary>
    internal sealed partial class MechJebService
    {
        private BridgeResponse Navigating(BridgeRequest request, AutopilotKind kind)
        {
            if (navigation == null) return Refuse(request, ControlReasons.OperationUnavailable, "the navigation layer is not wired");
            return Mutate(request, kind);
        }

        // ---------------------------------------------------------------- maneuver nodes

        private BridgeResponse EditNodes(BridgeRequest request, AutopilotJob job)
        {
            var nav = job.Navigation;
            TrajectoryReading before;
            try { before = navigation.ReadTrajectory(NavigationLimits.MaxPatches); } catch (Exception) { before = null; }
            if (before == null) return Refuse(request, AutopilotReasons.FlightUnavailable, "there is no active vessel");
            if (!before.Owned) return Refuse(request, FlightReasons.VesselNotOwned, null);
            if (!string.Equals(before.VesselId, job.VesselId, StringComparison.Ordinal)) return Refuse(request, AutopilotReasons.VesselChanged, "the active vessel changed");
            if (!before.SolverAvailable) return Refuse(request, AutopilotReasons.PlanUnavailable, "the vessel has no patched-conic solver, so it cannot carry maneuver nodes");
            if (!before.FlightPlanningUnlocked) return Refuse(request, NavigationReasons.FlightPlanningLocked, "Mission Control is not upgraded far enough for flight planning");

            Func<NodeEditOutcome> edit;
            string effect;
            var node = new JObject();
            switch (job.Kind)
            {
                case AutopilotKind.NodeCreate:
                {
                    if (before.NodeCount >= NavigationLimits.MaxNodes) return Refuse(request, NavigationReasons.TooManyNodes, "the vessel already has " + before.NodeCount + " maneuver nodes (at most " + NavigationLimits.MaxNodes + ")");
                    var time = NavigationMath.ResolveTime(nav.TimeReference, nav.TimeSeconds, before.UniversalTime, before.TimeToApoapsis, before.TimeToPeriapsis, before.Eccentricity);
                    if (!time.Ok) return Refuse(request, time.Reason, time.Detail);
                    double ut = time.UniversalTime, p = nav.Prograde ?? 0, n = nav.Normal ?? 0, r = nav.Radial ?? 0;
                    node["universalTimeSeconds"] = ut; node["deltaV"] = DeltaV(p, n, r);
                    edit = () => navigation.AddNode(ut, p, n, r); effect = "maneuver_node_created";
                    break;
                }
                case AutopilotKind.NodeUpdate:
                {
                    if (nav.NodeIndex >= before.Nodes.Count) return Refuse(request, before.NodeCount == 0 ? AutopilotReasons.NoManeuverNode : NavigationReasons.NodeNotFound, NodeRange(before));
                    var current = before.Nodes[nav.NodeIndex];
                    var ut = current.UniversalTime;
                    if (nav.TimeReference != null)
                    {
                        var time = NavigationMath.ResolveTime(nav.TimeReference, nav.TimeSeconds, before.UniversalTime, before.TimeToApoapsis, before.TimeToPeriapsis, before.Eccentricity);
                        if (!time.Ok) return Refuse(request, time.Reason, time.Detail);
                        ut = time.UniversalTime;
                    }
                    else
                    {
                        // Only the delta-v changes: a node already in the past cannot be edited back into a valid plan.
                        var window = NavigationMath.WindowProblem(ut, before.UniversalTime);
                        if (window != null) return Refuse(request, ControlReasons.InvalidArgument, "the node's time is no longer valid: " + window);
                    }
                    double p = nav.Prograde ?? current.Prograde, n = nav.Normal ?? current.Normal, r = nav.Radial ?? current.Radial;
                    var bounds = NavigationLimits.CheckDeltaV(p, n, r);
                    if (bounds != null) return Refuse(request, ControlReasons.InvalidArgument, bounds);
                    var index = nav.NodeIndex;
                    node["before"] = OrbitPrediction.Node(current, index, before.UniversalTime);
                    node["universalTimeSeconds"] = ut; node["deltaV"] = DeltaV(p, n, r);
                    edit = () => navigation.UpdateNode(index, ut, p, n, r); effect = "maneuver_node_updated";
                    break;
                }
                default:
                {
                    if (before.NodeCount == 0) return Refuse(request, AutopilotReasons.NoManeuverNode, "the active vessel has no maneuver node");
                    if (!nav.All && nav.NodeIndex >= before.Nodes.Count) return Refuse(request, NavigationReasons.NodeNotFound, NodeRange(before));
                    var index = nav.All ? -1 : nav.NodeIndex;
                    node["deleted"] = nav.All ? (JToken)"all" : OrbitPrediction.Node(before.Nodes[index], index, before.UniversalTime);
                    node["nodesBefore"] = before.NodeCount;
                    edit = () => navigation.DeleteNodes(index); effect = nav.All ? "maneuver_nodes_deleted" : "maneuver_node_deleted";
                    break;
                }
            }

            jobs.Add(job);
            job.Phase = "done";
            NodeEditOutcome outcome;
            try { outcome = edit(); }
            catch (Exception) { outcome = null; }
            job.UpdatedUtc = utcNow(); job.CompletedUtc = job.UpdatedUtc;
            if (outcome == null)
            {
                // The call may have changed the flight plan before it failed: never report that as a clean refusal.
                job.Dispatched = true; job.Status = JobStatuses.Indeterminate; job.ReasonCode = NavigationReasons.NodeEditFailed;
                job.Detail = "the game failed during the node edit; read flight_orbit_prediction before retrying";
                return Envelope(request, job);
            }
            if (!outcome.Ok)
            {
                job.Status = JobStatuses.Failed; job.ReasonCode = NavigationReasons.NodeEditFailed; job.Detail = outcome.Detail ?? "the node edit was not applied";
                return Envelope(request, job);
            }
            job.Dispatched = true; job.Status = JobStatuses.Completed; job.EffectsApplied.Add(effect);
            if (job.Kind != AutopilotKind.NodeDelete) node["index"] = outcome.Index;
            TrajectoryReading after;
            try { after = navigation.ReadTrajectory(NavigationLimits.MaxPatches); } catch (Exception) { after = null; }
            job.Plan = new JObject
            {
                ["source"] = "stock_flight_plan", ["node"] = node,
                ["prediction"] = after == null || !after.Owned ? JValue.CreateNull() : (JToken)OrbitPrediction.Build(after)
            };
            if (job.Kind == AutopilotKind.NodeDelete && nav.All)
            {
                // KSP re-plans after the removal on a later frame: the withoutNodes chain read now can still be the old one.
                job.Plan["predictionStale"] = true; job.Plan["predictionNote"] = "re-read flight_orbit_prediction next frame";
            }
            try { job.Last = flight.Read() ?? job.Last; } catch (Exception) { }
            return Envelope(request, job);
        }

        private static JObject DeltaV(double prograde, double normal, double radial)
        {
            return new JObject
            {
                ["progradeMetresPerSecond"] = prograde, ["normalMetresPerSecond"] = normal, ["radialMetresPerSecond"] = radial,
                ["deltaVMagnitudeMetresPerSecond"] = Math.Sqrt(prograde * prograde + normal * normal + radial * radial)
            };
        }

        private static string NodeRange(TrajectoryReading t)
        {
            return t.NodeCount == 0 ? "the active vessel has no maneuver node" : "nodeIndex must be 0.." + (Math.Min(t.NodeCount, NavigationLimits.MaxNodes) - 1) + " (the vessel has " + t.NodeCount + " nodes, sorted by time)";
        }

        // ---------------------------------------------------------------- warp_to

        /// <summary>Checks a warp_to request against the game and fixes its target time. Null admits it.</summary>
        private BridgeResponse AdmitWarp(BridgeRequest request, AutopilotJob job)
        {
            var nav = job.Navigation;
            WarpReading warp; TrajectoryReading trajectory;
            try { warp = navigation.ReadWarp(); trajectory = navigation.ReadTrajectory(NavigationLimits.MaxPatches); }
            catch (Exception) { warp = null; trajectory = null; }
            if (warp == null || trajectory == null) return Refuse(request, AutopilotReasons.FlightUnavailable, "there is no active vessel");
            if (!trajectory.Owned) return Refuse(request, FlightReasons.VesselNotOwned, null);
            if (!string.Equals(warp.VesselId, job.VesselId, StringComparison.Ordinal) || !string.Equals(trajectory.VesselId, job.VesselId, StringComparison.Ordinal))
                return Refuse(request, AutopilotReasons.VesselChanged, "the active vessel changed");
            if (!warp.Rails) return Refuse(request, NavigationReasons.WarpModePhysics, "time warp is in physics mode; return it to rails mode (index 0, then the rails warp keys) before warp_to");
            if (warp.Throttle > 0.001) return Refuse(request, FlightReasons.WarpThrottleActive, "the throttle is above zero; cut it before warping");
            var flying = warp.Situation == "FLYING" || warp.Situation == "SUB_ORBITAL";
            if (warp.AltitudeLimitIndex == 0 || (flying && warp.AtmosphereTopMeters > 0 && warp.AltitudeMeters < warp.AtmosphereTopMeters))
                return Refuse(request, NavigationReasons.WarpNotAllowedHere, "the vessel is too low for rails warp (in the atmosphere or below the first altitude limit)",
                    new JObject { ["altitudeMeters"] = OrbitPrediction.Finite(warp.AltitudeMeters), ["atmosphereTopMeters"] = OrbitPrediction.Finite(warp.AtmosphereTopMeters), ["altitudeLimitIndex"] = warp.AltitudeLimitIndex });

            double target;
            switch (nav.WarpTarget)
            {
                case "node":
                    if (trajectory.NodeCount == 0) return Refuse(request, AutopilotReasons.NoManeuverNode, "the active vessel has no maneuver node");
                    if (nav.NodeIndex >= trajectory.Nodes.Count) return Refuse(request, NavigationReasons.NodeNotFound, NodeRange(trajectory));
                    target = trajectory.Nodes[nav.NodeIndex].UniversalTime;
                    break;
                case "soi":
                {
                    var soi = trajectory.Coast.FirstOrDefault(p => p != null && (p.EndTransition == "ENCOUNTER" || p.EndTransition == "ESCAPE"));
                    if (soi == null) return Refuse(request, NavigationReasons.NoSoiChange, "the current trajectory has no sphere-of-influence change" + (trajectory.NodeCount > 0 ? " before the first maneuver node" : ""));
                    target = soi.EndUniversalTime;
                    break;
                }
                default:
                {
                    var time = NavigationMath.ResolveTime(nav.WarpTarget, nav.TimeSeconds, warp.UniversalTime, trajectory.TimeToApoapsis, trajectory.TimeToPeriapsis, trajectory.Eccentricity);
                    if (!time.Ok) return Refuse(request, time.Reason, time.Detail);
                    target = time.UniversalTime;
                    break;
                }
            }
            var stop = target - nav.LeadSeconds;
            var ahead = stop - warp.UniversalTime;
            if (!NavigationLimits.IsFinite(ahead) || ahead < NavigationLimits.MinLeadSeconds)
                return Refuse(request, NavigationReasons.WarpTargetReached, "the target minus the lead is already reached (" + (NavigationLimits.IsFinite(ahead) ? Math.Round(ahead, 1) + " s" : "unknown") + "); nothing to warp",
                    new JObject { ["targetUniversalTimeSeconds"] = OrbitPrediction.Finite(target), ["leadSeconds"] = nav.LeadSeconds });
            if (ahead > NavigationLimits.MaxHorizonSeconds) return Refuse(request, ControlReasons.InvalidArgument, "the target is more than " + NavigationLimits.MaxHorizonSeconds + " s ahead");
            job.TargetUniversalTime = target; job.StopUniversalTime = stop; job.LastWarp = warp;
            return null;
        }
    }
}
