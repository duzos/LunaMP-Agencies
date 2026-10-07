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
        private int stageLatch;

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
                    PredictedNextBody = encounter && orbit.nextPatch != null && orbit.nextPatch.referenceBody != null ? orbit.nextPatch.referenceBody.bodyName : null
                };
            }
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

        public bool PlayerIsInputting()
        {
            if (Mathf.Abs(GameSettings.AXIS_PITCH.GetAxis()) > 0.1f || Mathf.Abs(GameSettings.AXIS_ROLL.GetAxis()) > 0.1f || Mathf.Abs(GameSettings.AXIS_YAW.GetAxis()) > 0.1f
                || Mathf.Abs(GameSettings.AXIS_THROTTLE.GetAxis()) > 0.1f) return true;
            if (GameSettings.PITCH_UP.GetKey(false) || GameSettings.PITCH_DOWN.GetKey(false) || GameSettings.YAW_LEFT.GetKey(false) || GameSettings.YAW_RIGHT.GetKey(false)
                || GameSettings.ROLL_LEFT.GetKey(false) || GameSettings.ROLL_RIGHT.GetKey(false) || GameSettings.THROTTLE_UP.GetKey(false) || GameSettings.THROTTLE_DOWN.GetKey(false)
                || GameSettings.THROTTLE_FULL.GetKey(false) || GameSettings.THROTTLE_CUTOFF.GetKey(false)) return true;
            // A stage key press lasts one frame, so it is latched for a few frames to survive the watcher's debounce.
            if (GameSettings.LAUNCH_STAGES.GetKeyDown(false)) stageLatch = 4;
            if (stageLatch > 0) { stageLatch--; return true; }
            return false;
        }
    }
}
