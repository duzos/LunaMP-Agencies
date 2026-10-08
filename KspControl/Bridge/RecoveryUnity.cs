using System;
using System.Collections.Generic;
using System.Linq;
using KSP.UI.Screens;

namespace KspControl.Bridge
{
    /// <summary>
    /// The KSP half of the recovery job: reads the recovered vessel by id, stages through the flight_stage port, arms parachutes and holds the throttle
    /// through the bridge's fly-by-wire guard. Main thread only. Not linked into the unit tests; every member used is listed in the README's
    /// live-verification notes.
    /// </summary>
    internal sealed class UnityRecoveryPort : IRecoveryVesselPort
    {
        private static readonly string[] PropellantNames = { "LiquidFuel", "Oxidizer", "SolidFuel" };
        private readonly IFlightPort flight;
        private readonly FlightControlGuard guard;

        public UnityRecoveryPort(IFlightPort flight, FlightControlGuard guard)
        {
            this.flight = flight ?? throw new ArgumentNullException(nameof(flight));
            this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
        }

        public RecoveryReading Read(string vesselId)
        {
            var vessel = MechJebSources.FindVessel(vesselId);
            if (vessel == null || !vessel.loaded || vessel.parts == null) return null;
            var body = vessel.mainBody; var orbit = vessel.orbit;
            var active = ReferenceEquals(vessel, FlightGlobals.ActiveVessel);
            var reading = new RecoveryReading
            {
                VesselId = vessel.id.ToString(), Body = body == null ? null : body.bodyName, Situation = vessel.situation.ToString(),
                HasAtmosphere = body != null && body.atmosphere, AtmosphereTopMeters = body != null && body.atmosphere ? body.atmosphereDepth : 0,
                AltitudeMeters = vessel.altitude,
                // radarAltitude is altitude minus the terrain height, or the altitude itself over the sea (KSP clamps the PQS height at sea level).
                HeightAboveTerrainMeters = double.IsNaN(vessel.radarAltitude) || vessel.radarAltitude < 0 ? vessel.altitude : vessel.radarAltitude,
                SurfaceSpeed = vessel.srfSpeed, VerticalSpeed = vessel.verticalSpeed,
                ApoapsisMeters = orbit == null ? double.NaN : orbit.ApA, PeriapsisMeters = orbit == null ? double.NaN : orbit.PeA,
                TimeToApoapsis = orbit == null || orbit.eccentricity >= 1 ? double.NaN : orbit.timeToAp,
                Orbiting = vessel.situation == Vessel.Situations.ORBITING,
                CurrentStage = active ? StageManager.CurrentStage : vessel.currentStage,
                WarpIndex = TimeWarp.CurrentRateIndex,
                StagingLocked = active && flight.StagingLocked
            };
            var crew = vessel.GetVesselCrew();
            reading.CrewCount = crew == null ? 0 : crew.Count(c => c != null && c.rosterStatus != ProtoCrewMember.RosterStatus.Dead && c.rosterStatus != ProtoCrewMember.RosterStatus.Missing);
            foreach (var part in vessel.parts)
            {
                if (part == null) continue;
                var info = Describe(part);
                reading.Parts.Add(info);
                if (ReferenceEquals(part, vessel.rootPart)) { reading.RootIsCommand = info.Command || info.Crewed; reading.RootPartId = info.PartId; }
                foreach (PartModule module in part.Modules)
                {
                    var chute = module as ModuleParachute;
                    if (chute == null) continue;
                    reading.Chutes.Add(new RecoveryChute
                    {
                        PartId = info.PartId, Name = info.Name, State = chute.deploymentState.ToString(), Safety = chute.deploymentSafeState.ToString(),
                        AutomateSafeDeploy = chute.automateSafeDeploy
                    });
                }
            }
            return reading;
        }

        private static RecoveryPart Describe(Part part)
        {
            var info = new RecoveryPart
            {
                PartId = part.persistentId.ToString(), Name = part.partInfo == null ? part.name : part.partInfo.name,
                ParentId = part.parent == null ? null : part.parent.persistentId.ToString(),
                InverseStage = part.inverseStage, StagingOn = part.stagingOn, Crewed = part.protoModuleCrew != null && part.protoModuleCrew.Count > 0
            };
            var engine = false;
            foreach (PartModule module in part.Modules)
            {
                if (module == null) continue;
                if (module is ModuleCommand) info.Command = true;
                else if (module is ModuleParachute) info.Parachute = true;
                else if (module is ModuleAblator) info.HeatShield = true;
                else if (module is ModuleDecouplerBase) info.Separator = true;
                else if (module is ModuleEngines) engine = true;
            }
            var propellant = false;
            foreach (PartResource resource in part.Resources)
                if (resource != null && resource.maxAmount > 0 && Array.IndexOf(PropellantNames, resource.resourceName) >= 0) { propellant = true; break; }
            info.Engine = engine;
            info.Propulsion = engine || (propellant && !info.Command && !info.Crewed);
            return info;
        }

        // The runner checks every frame that the recovered vessel is the active one, so the flight_stage port (active vessel) addresses it.
        public IList<FlightPartAction> StageActions(int stage) { return flight.PartsInStage(stage); }
        public IList<FlightPartAction> StageGroupBindings() { return flight.GroupBindings("Stage"); }
        public void ActivateNextStage() { flight.ActivateNextStage(); }

        public bool ArmChute(string vesselId, string partId, int automateSafeDeploy)
        {
            var vessel = MechJebSources.FindVessel(vesselId);
            if (vessel == null || vessel.parts == null) return false;
            var part = vessel.parts.FirstOrDefault(p => p != null && p.persistentId.ToString() == partId);
            if (part == null) return false;
            var found = false;
            foreach (PartModule module in part.Modules)
            {
                var chute = module as ModuleParachute;
                if (chute == null) continue;
                found = true;
                // Stock opens an ACTIVE chute only while automateSafeDeploy >= (int)deploymentSafeState (SAFE 0, RISKY 1, UNSAFE 2, NONE 3 in vacuum), so this
                // decides when it may open. Deploy() arms a STOWED chute (state ACTIVE) and refuses while it is shielded from the airstream.
                chute.automateSafeDeploy = Math.Max(0, Math.Min(2, automateSafeDeploy));
                if (chute.deploymentState == ModuleParachute.deploymentStates.STOWED) chute.Deploy();
            }
            return found;
        }

        public bool SetThrottle(float value) { return guard.Engage(value); }

        /// <summary>Any reason other than "released" or "human_takeover" makes the guard neutralise: throttle zero, then the callback is removed.</summary>
        public void ReleaseThrottle(string vesselId) { guard.Release("autopilot_recover_released"); }
    }
}
