using System;
using System.Collections.Generic;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace KspControl.Bridge
{
    /// <summary>
    /// The only flight code that touches KSP. Thin on purpose: every decision (disclosure aside, which the facade makes) lives in the pure
    /// services. Main thread only. Not linked into the unit tests, so every member is covered by the live-verification list.
    /// </summary>
    internal sealed class UnityFlightPort : IFlightPort, IFlightInputPort, IFlightHumanInput
    {
        private readonly Func<Vessel, bool> mayInspect;
        private Vessel attached;
        private FlightInputCallback callback;
        private Func<float?> tickFunction;
        private Guid mechJebVessel;
        private bool mechJebPresent;
        private float mechJebCheckedAt = -100f;

        public UnityFlightPort(Func<Vessel, bool> mayInspect) { this.mayInspect = mayInspect ?? (v => false); }

        // ---------------------------------------------------------------- IFlightPort

        public bool InFlight { get { return HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null; } }

        public FlightSnapshot Read()
        {
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return null;
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return null;
            // Disclosure first: an unowned vessel yields an empty, unowned snapshot and nothing else is read.
            if (!mayInspect(vessel)) return new FlightSnapshot { Owned = false };
            var snapshot = new FlightSnapshot
            {
                Owned = true, VesselId = vessel.id.ToString(), VesselName = vessel.vesselName, Controllable = vessel.IsControllable,
                Situation = vessel.situation.ToString(), Body = vessel.mainBody == null ? null : vessel.mainBody.bodyName,
                UniversalTime = Planetarium.GetUniversalTime(), Altitude = vessel.altitude, VerticalSpeed = vessel.verticalSpeed,
                SurfaceSpeed = vessel.srfSpeed, OrbitalSpeed = vessel.obt_speed, CrewCount = vessel.GetCrewCount(), PartCount = vessel.parts.Count
            };
            var orbit = vessel.orbit;
            if (orbit != null)
            {
                var encounter = orbit.patchEndTransition == Orbit.PatchTransitionType.ENCOUNTER || orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE;
                snapshot.Orbit = new FlightOrbit
                {
                    ReferenceBody = orbit.referenceBody == null ? null : orbit.referenceBody.bodyName,
                    ApoapsisAltitude = orbit.ApA, PeriapsisAltitude = orbit.PeA, InclinationDegrees = orbit.inclination, Eccentricity = orbit.eccentricity,
                    SemiMajorAxis = orbit.semiMajorAxis, PeriodSeconds = orbit.period, TimeToApoapsis = orbit.timeToAp, TimeToPeriapsis = orbit.timeToPe,
                    PatchEndTransition = orbit.patchEndTransition.ToString(),
                    PredictedNextBody = encounter && orbit.nextPatch != null && orbit.nextPatch.referenceBody != null ? orbit.nextPatch.referenceBody.bodyName : null,
                    TimeToSoiChange = encounter ? orbit.EndUT - snapshot.UniversalTime : double.NaN
                };
            }
            ReadNextNode(vessel, snapshot);
            var groups = vessel.ActionGroups;
            snapshot.Controls = new FlightControlStates
            {
                Throttle = vessel.ctrlState == null ? 0 : vessel.ctrlState.mainThrottle,
                Sas = groups[KSPActionGroup.SAS], Rcs = groups[KSPActionGroup.RCS], Gear = groups[KSPActionGroup.Gear],
                Lights = groups[KSPActionGroup.Light], Brakes = groups[KSPActionGroup.Brakes],
                CurrentStage = StageManager.CurrentStage, StageCount = StageManager.StageCount
            };
            ReadDeltaV(vessel, snapshot);
            ReadResources(vessel, snapshot);
            snapshot.Warp = ReadWarp(vessel);
            snapshot.MechJebPresent = MechJeb(vessel);
            return snapshot;
        }

        private static void ReadDeltaV(Vessel vessel, FlightSnapshot snapshot)
        {
            try
            {
                var calculator = vessel.VesselDeltaV;
                if (calculator == null || !calculator.IsReady) return;
                snapshot.DeltaVReady = true; snapshot.TotalDeltaVActual = calculator.TotalDeltaVActual;
                foreach (var info in calculator.OperatingStageInfo)
                    snapshot.Stages.Add(new FlightStageDeltaV
                    {
                        Stage = info.stage, DeltaVActual = info.deltaVActual, DeltaVVacuum = info.deltaVinVac, DeltaVSeaLevel = info.deltaVatASL,
                        BurnTimeSeconds = info.stageBurnTime, ThrustToWeightActual = info.TWRActual, MassKilograms = info.stageMass * 1000.0
                    });
            }
            catch (Exception) { snapshot.DeltaVReady = false; snapshot.Stages.Clear(); }
        }

        private static void ReadNextNode(Vessel vessel, FlightSnapshot snapshot)
        {
            try
            {
                var solver = vessel.patchedConicSolver;
                if (solver == null || solver.maneuverNodes == null || solver.maneuverNodes.Count == 0) return;
                snapshot.NodeCount = solver.maneuverNodes.Count;
                ManeuverNode next = null;
                foreach (var node in solver.maneuverNodes) if (node != null && (next == null || node.UT < next.UT)) next = node;
                if (next != null) snapshot.NextNode = UnityNavigationPort.Describe(next);
            }
            catch (Exception) { snapshot.NextNode = null; }
        }

        private static void ReadResources(Vessel vessel, FlightSnapshot snapshot)
        {
            var totals = new Dictionary<string, FlightResource>(StringComparer.Ordinal);
            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                foreach (PartResource resource in part.Resources)
                {
                    FlightResource total;
                    if (!totals.TryGetValue(resource.resourceName, out total)) totals[resource.resourceName] = total = new FlightResource { Name = resource.resourceName };
                    total.Amount += resource.amount; total.Capacity += resource.maxAmount;
                }
            }
            snapshot.Resources = totals.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        }

        private static FlightWarpInfo ReadWarp(Vessel vessel)
        {
            var rails = TimeWarp.WarpMode == TimeWarp.Modes.HIGH;
            var fetch = TimeWarp.fetch;
            var info = new FlightWarpInfo
            {
                Mode = rails ? "rails" : "physics", CurrentIndex = TimeWarp.CurrentRateIndex, CurrentRate = TimeWarp.CurrentRate,
                Rates = fetch == null ? new float[0] : (float[])(rails ? fetch.warpRates : fetch.physicsWarpRates).Clone()
            };
            if (rails && fetch != null && vessel.mainBody != null) { try { info.AltitudeLimitIndex = fetch.GetMaxRateForAltitude(vessel.altitude, vessel.mainBody); } catch (Exception) { info.AltitudeLimitIndex = -1; } }
            return info;
        }

        private bool MechJeb(Vessel vessel)
        {
            if (mechJebVessel == vessel.id && Time.unscaledTime - mechJebCheckedAt < 5f) return mechJebPresent;
            mechJebVessel = vessel.id; mechJebCheckedAt = Time.unscaledTime;
            mechJebPresent = vessel.parts.Any(p => p != null && p.Modules.Cast<PartModule>().Any(m => m != null && m.moduleName == "MechJebCore"));
            return mechJebPresent;
        }

        private static KSPActionGroup Group(string name) { return (KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), name); }

        public bool GetGroup(string group) { return FlightGlobals.ActiveVessel.ActionGroups[Group(group)]; }

        public bool SetGroup(string group, bool desired)
        {
            var vessel = FlightGlobals.ActiveVessel; var g = Group(group);
            // Built-in groups have a state setter (the one MechJeb uses). A custom group only toggles, so reconcile to the desired state.
            if (group.StartsWith("Custom", StringComparison.Ordinal)) { if (vessel.ActionGroups[g] != desired) vessel.ActionGroups.ToggleGroup(g); }
            else vessel.ActionGroups.SetGroup(g, desired);
            return vessel.ActionGroups[g];
        }

        public bool ToggleGroup(string group)
        {
            var vessel = FlightGlobals.ActiveVessel; var g = Group(group);
            vessel.ActionGroups.ToggleGroup(g);
            return vessel.ActionGroups[g];
        }

        public bool StagingLocked { get { return InputLockManager.IsLocked(ControlTypes.STAGING) || InputLockManager.IsLocked(ControlTypes.GROUP_STAGE); } }

        public IList<FlightPartAction> PartsInStage(int stage)
        {
            var result = new List<FlightPartAction>();
            var vessel = FlightGlobals.ActiveVessel;
            foreach (var part in vessel.parts)
            {
                if (part == null || !part.stagingOn || part.inverseStage != stage) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null) continue;
                    var hook = module.GetType().GetMethod("OnActive");
                    result.Add(new FlightPartAction
                    {
                        PartId = part.persistentId.ToString(), PartName = part.partInfo == null ? part.name : part.partInfo.name, Module = module.moduleName,
                        ActsOnStaging = module is IStageSeparator || (hook != null && hook.DeclaringType != typeof(PartModule))
                    });
                }
            }
            return result;
        }

        public IList<FlightPartAction> GroupBindings(string group)
        {
            var result = new List<FlightPartAction>();
            var g = Group(group);
            foreach (var part in FlightGlobals.ActiveVessel.parts)
            {
                if (part == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    if (module == null) continue;
                    foreach (BaseAction action in module.Actions)
                        if (action != null && (action.actionGroup & g) != KSPActionGroup.None)
                            result.Add(new FlightPartAction { PartId = part.persistentId.ToString(), PartName = part.partInfo == null ? part.name : part.partInfo.name, Module = module.moduleName, Action = action.name });
                }
            }
            return result;
        }

        public void ActivateNextStage() { StageManager.ActivateNextStage(); }

        public void FireAbort() { FlightGlobals.ActiveVessel.ActionGroups.SetGroup(KSPActionGroup.Abort, true); }

        /// <summary>The stock setter, never the fields: LunaMP's Harmony prefix on TimeWarp.SetRate applies the server's warp rules and may veto it.</summary>
        public void SetWarpIndex(int index) { TimeWarp.SetRate(index, false, false); }

        // ---------------------------------------------------------------- IFlightInputPort

        public bool Attach(Func<float?> tick)
        {
            var vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || tick == null) return false;
            Detach();
            attached = vessel; tickFunction = tick;
            callback = OnFlyByWire;
            vessel.OnFlyByWire += callback;
            return true;
        }

        private void OnFlyByWire(FlightCtrlState state)
        {
            float? throttle = null;
            try { throttle = tickFunction == null ? (float?)null : tickFunction(); } catch (Exception) { throttle = null; }
            if (throttle.HasValue) state.mainThrottle = throttle.Value;
            else Detach();
        }

        public void Detach()
        {
            if (attached != null && callback != null) attached.OnFlyByWire -= callback;
            attached = null; callback = null; tickFunction = null;
        }

        public void WriteThrottle(float value)
        {
            var vessel = attached;
            if (vessel != null && vessel.ctrlState != null) vessel.ctrlState.mainThrottle = value;
            // The stock input state belongs to whichever vessel is active now: touch it only when that is the controlled one.
            if (FlightInputHandler.state != null && (vessel == null || FlightGlobals.ActiveVessel == vessel)) FlightInputHandler.state.mainThrottle = value;
        }

        public void CancelWarp() { if (TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true, false); }

        // ---------------------------------------------------------------- IFlightHumanInput

        private const float AxisChange = 0.1f;
        private readonly float[] lastAxes = new float[4];
        private int lastAxisFrame = -100;
        private int inputLatch;

        private static IEnumerable<KeyBinding> HeldKeys()
        {
            yield return GameSettings.PITCH_UP; yield return GameSettings.PITCH_DOWN; yield return GameSettings.YAW_LEFT; yield return GameSettings.YAW_RIGHT;
            yield return GameSettings.ROLL_LEFT; yield return GameSettings.ROLL_RIGHT; yield return GameSettings.THROTTLE_UP; yield return GameSettings.THROTTLE_DOWN;
            yield return GameSettings.THROTTLE_FULL; yield return GameSettings.THROTTLE_CUTOFF; yield return GameSettings.SAS_HOLD;
            yield return GameSettings.TRANSLATE_UP; yield return GameSettings.TRANSLATE_DOWN; yield return GameSettings.TRANSLATE_LEFT; yield return GameSettings.TRANSLATE_RIGHT;
            yield return GameSettings.TRANSLATE_FWD; yield return GameSettings.TRANSLATE_BACK;
            yield return GameSettings.WHEEL_STEER_LEFT; yield return GameSettings.WHEEL_STEER_RIGHT; yield return GameSettings.WHEEL_THROTTLE_UP; yield return GameSettings.WHEEL_THROTTLE_DOWN;
        }

        /// <summary>Single-press keys: staging, toggles, groups, abort and vessel switching. A press lasts one frame, so it is latched.</summary>
        private static IEnumerable<KeyBinding> PressKeys()
        {
            yield return GameSettings.LAUNCH_STAGES; yield return GameSettings.SAS_TOGGLE; yield return GameSettings.RCS_TOGGLE; yield return GameSettings.BRAKES;
            yield return GameSettings.LANDING_GEAR; yield return GameSettings.HEADLIGHT_TOGGLE; yield return GameSettings.AbortActionGroup;
            yield return GameSettings.CustomActionGroup1; yield return GameSettings.CustomActionGroup2; yield return GameSettings.CustomActionGroup3;
            yield return GameSettings.CustomActionGroup4; yield return GameSettings.CustomActionGroup5; yield return GameSettings.CustomActionGroup6;
            yield return GameSettings.CustomActionGroup7; yield return GameSettings.CustomActionGroup8; yield return GameSettings.CustomActionGroup9;
            yield return GameSettings.CustomActionGroup10; yield return GameSettings.FOCUS_NEXT_VESSEL; yield return GameSettings.FOCUS_PREV_VESSEL;
        }

        /// <summary>
        /// Axes count only when they change by more than 0.1 since the previous frame: a stick resting off-centre or a throttle axis parked at a value
        /// is not input. The first call after a gap in frames only sets the baseline.
        /// </summary>
        public bool PlayerIsInputting()
        {
            var readings = new[] { GameSettings.AXIS_PITCH.GetAxis(), GameSettings.AXIS_ROLL.GetAxis(), GameSettings.AXIS_YAW.GetAxis(), GameSettings.AXIS_THROTTLE.GetAxis() };
            var gap = Time.frameCount - lastAxisFrame > 1;
            var changed = false;
            for (var i = 0; i < readings.Length; i++)
            {
                if (!gap && Mathf.Abs(readings[i] - lastAxes[i]) > AxisChange) changed = true;
                lastAxes[i] = readings[i];
            }
            lastAxisFrame = Time.frameCount;
            if (changed) inputLatch = 4;
            foreach (var key in HeldKeys()) if (key != null && key.GetKey(false)) return true;
            foreach (var key in PressKeys()) if (key != null && key.GetKeyDown(false)) inputLatch = 4;
            if (inputLatch > 0) { inputLatch--; return true; }
            return false;
        }
    }
}
