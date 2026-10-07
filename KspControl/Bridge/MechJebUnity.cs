using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KspControl.Bridge
{
    /// <summary>
    /// The Unity and KSP half of the autopilot layer: telemetry, human-input detection, the throttle and the two stock maneuver-node plans.
    /// Main thread only. Not linked into the unit-test assembly; everything with a decision in it lives in the pure files.
    /// </summary>
    internal sealed class UnityFlightPort : IAutopilotFlightPort
    {
        private const double AxisThreshold = 0.05;

        public bool InFlight { get { return HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null; } }

        public FlightTelemetry Read()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (!HighLogic.LoadedSceneIsFlight || vessel == null) return null;
            var body = vessel.mainBody; var orbit = vessel.orbit;
            var nodes = vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes == null ? 0 : vessel.patchedConicSolver.maneuverNodes.Count;
            return new FlightTelemetry
            {
                VesselId = vessel.id.ToString(), BodyName = body == null ? null : body.bodyName,
                AltitudeMeters = vessel.altitude, ApoapsisMeters = orbit == null ? double.NaN : orbit.ApA, PeriapsisMeters = orbit == null ? double.NaN : orbit.PeA,
                AtmosphereTopMeters = body != null && body.atmosphere ? body.atmosphereDepth : 0,
                Situation = vessel.situation.ToString(), Orbiting = vessel.situation == Vessel.Situations.ORBITING && orbit != null && orbit.eccentricity < 1,
                ManeuverNodes = nodes, UniversalTime = Planetarium.GetUniversalTime()
            };
        }

        /// <summary>Physical keys and axes bound to attitude, throttle and translation. SAS toggles, camera and warp keys are not control input.</summary>
        public bool HumanInputDetected()
        {
            try
            {
                if (Key(GameSettings.PITCH_UP) || Key(GameSettings.PITCH_DOWN) || Key(GameSettings.YAW_LEFT) || Key(GameSettings.YAW_RIGHT) || Key(GameSettings.ROLL_LEFT) || Key(GameSettings.ROLL_RIGHT)
                    || Key(GameSettings.THROTTLE_UP) || Key(GameSettings.THROTTLE_DOWN) || Key(GameSettings.THROTTLE_FULL) || Key(GameSettings.THROTTLE_CUTOFF)
                    || Key(GameSettings.TRANSLATE_UP) || Key(GameSettings.TRANSLATE_DOWN) || Key(GameSettings.TRANSLATE_LEFT) || Key(GameSettings.TRANSLATE_RIGHT) || Key(GameSettings.TRANSLATE_FWD) || Key(GameSettings.TRANSLATE_BACK))
                    return true;
                return Moved(GameSettings.AXIS_PITCH) || Moved(GameSettings.AXIS_YAW) || Moved(GameSettings.AXIS_ROLL)
                    || Moved(GameSettings.AXIS_TRANSLATE_X) || Moved(GameSettings.AXIS_TRANSLATE_Y) || Moved(GameSettings.AXIS_TRANSLATE_Z) || Moved(GameSettings.AXIS_THROTTLE_INC);
            }
            catch (Exception) { return false; }
        }

        private static bool Key(KeyBinding binding) { return binding != null && binding.GetKey(); }
        private static bool Moved(AxisBinding axis) { return axis != null && !axis.IsNeutral() && Math.Abs(axis.GetAxis()) > AxisThreshold; }

        public void CutThrottle()
        {
            if (FlightInputHandler.state != null) FlightInputHandler.state.mainThrottle = 0f;
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel != null && vessel.ctrlState != null) vessel.ctrlState.mainThrottle = 0f;
        }

        // ---------------------------------------------------------------- stock maneuver-node plans

        public PlanOutcome PlanCircularize()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.patchedConicSolver == null || vessel.orbit == null) return PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "no vessel with a patched-conic solver (tracking station level or career restrictions)");
            var orbit = vessel.orbit;
            if (orbit.eccentricity >= 1 || double.IsNaN(orbit.ApR) || orbit.ApR <= 0) return PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "the orbit has no apoapsis");
            if (vessel.situation != Vessel.Situations.ORBITING && vessel.situation != Vessel.Situations.SUB_ORBITAL) return PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "the vessel is not on a free orbit");
            var mu = orbit.referenceBody.gravParameter;
            var dv = ManeuverMath.CircularizeAtApoapsis(mu, orbit.ApR, orbit.semiMajorAxis);
            var now = Planetarium.GetUniversalTime();
            var ut = now + orbit.timeToAp;
            var node = Create(vessel, ut, new Vector3d(0, 0, dv));
            if (node == null) return PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "the maneuver node could not be created");
            var data = Describe("circularize_at_apoapsis", "stock vis-viva delta-v at the apoapsis; exact for the current orbit, ignores later perturbations", ut, now, dv);
            data["apoapsisMeters"] = orbit.ApA;
            return new PlanOutcome { Ok = true, Data = data };
        }

        public PlanOutcome PlanHohmann(string targetBodyName)
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.patchedConicSolver == null || vessel.orbit == null) return PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "no vessel with a patched-conic solver (tracking station level or career restrictions)");
            var orbit = vessel.orbit; var parent = orbit.referenceBody;
            var target = FlightGlobals.Bodies == null ? null : FlightGlobals.Bodies.FirstOrDefault(b => b != null && string.Equals(b.bodyName, targetBodyName, StringComparison.OrdinalIgnoreCase));
            if (target == null) return PlanOutcome.Fail(AutopilotReasons.TargetUnavailable, "no body named " + targetBodyName);
            if (target.referenceBody != parent || target == parent) return PlanOutcome.Fail(AutopilotReasons.TargetUnavailable, target.bodyName + " does not orbit " + parent.bodyName + "; only a moon of the current body is supported");
            if (vessel.situation != Vessel.Situations.ORBITING || orbit.eccentricity > 0.1) return PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "the vessel needs a near-circular orbit (eccentricity at most 0.1); plan_circularize first");
            if (target.orbit == null || target.orbit.eccentricity > 0.2) return PlanOutcome.Fail(AutopilotReasons.TargetUnavailable, "the target orbit is not near-circular");
            var now = Planetarium.GetUniversalTime();
            // Lead angle of the target over the vessel, measured along the direction of motion (frame independent): the angle is in [0, pi] to
            // the half-plane the velocity points into, otherwise it is behind by that angle.
            var rv = orbit.getRelativePositionAtUT(now); var rt = target.orbit.getRelativePositionAtUT(now);
            var cosine = Vector3d.Dot(rv.normalized, rt.normalized);
            var angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosine)));
            var ahead = Vector3d.Dot(rt, orbit.getOrbitalVelocityAtUT(now)) > 0;
            var lead = ahead ? angle : 2 * Math.PI - angle;
            ManeuverMath.HohmannPlan plan;
            try { plan = ManeuverMath.Hohmann(parent.gravParameter, orbit.semiMajorAxis, target.orbit.semiMajorAxis, lead); }
            catch (ArgumentException) { return PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "the vessel and the target orbits are too close to plan a transfer"); }
            var ut = now + plan.WaitSeconds;
            var node = Create(vessel, ut, new Vector3d(0, 0, plan.DepartureDeltaV));
            if (node == null) return PlanOutcome.Fail(AutopilotReasons.PlanUnavailable, "the maneuver node could not be created");
            var data = Describe("hohmann_phase_wait_estimate", "ESTIMATE: stock Hohmann departure burn at the next phase-angle window, assuming circular coplanar orbits; check the encounter on the map and refine before relying on it", ut, now, plan.DepartureDeltaV);
            data["targetBody"] = target.bodyName;
            data["leadAngleNowDegrees"] = lead * 180 / Math.PI; data["requiredLeadAngleDegrees"] = ManeuverMath.Mod(plan.RequiredPhaseRadians, 2 * Math.PI) * 180 / Math.PI;
            data["transferSeconds"] = plan.TransferSeconds;
            return new PlanOutcome { Ok = true, Data = data };
        }

        private static JObject Describe(string kind, string label, double ut, double now, double dv)
        {
            return new JObject
            {
                ["source"] = "stock_math", ["kind"] = kind, ["label"] = label, ["nodeUniversalTime"] = ut, ["secondsFromNow"] = ut - now,
                ["deltaVMetersPerSecond"] = dv, ["frame"] = "prograde"
            };
        }

        private static ManeuverNode Create(Vessel vessel, double ut, Vector3d deltaV)
        {
            var solver = vessel.patchedConicSolver;
            var node = solver.AddManeuverNode(ut);
            if (node == null) return null;
            node.DeltaV = deltaV; node.UT = ut;
            solver.UpdateFlightPlan();
            return node;
        }
    }

    /// <summary>Where the adapter gets the live objects. Re-evaluated on every call: nothing is held between frames.</summary>
    internal static class MechJebSources
    {
        public static object Core()
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || vessel.parts == null) return null;
            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                foreach (PartModule module in part.Modules)
                    if (module != null && module.GetType().FullName == "MuMech.MechJebCore") return module;
            }
            return null;
        }

        public static object Vessel() { return HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null; }
    }
}
