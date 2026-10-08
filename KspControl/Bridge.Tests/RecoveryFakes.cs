using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;

namespace KspControl.BridgeTests
{
    /// <summary>
    /// A scriptable recovered vessel. Staging decrements the stage and drops the parts registered for that stage in <see cref="Drops"/>; arming a chute
    /// turns it ACTIVE unless it is shielded. Every call is recorded.
    /// </summary>
    internal sealed class FakeRecoveryPort : IRecoveryVesselPort
    {
        public RecoveryReading Reading;
        public bool Gone, StageIgnored, ThrottleRefused;
        public readonly HashSet<string> Shielded = new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<int, List<string>> Drops = new Dictionary<int, List<string>>();
        public readonly List<string> Calls = new List<string>();
        public float Throttle;
        public int Reads;

        /// <summary>
        /// Mk1 pod (root, crewed, command) with a Mk16 chute and a heat shield in stage 0, a decoupler in stage 1 and a tank and engine below it (engine
        /// stage 2 already fired). 80 km circular orbit around a Kerbin-like body (70 km atmosphere).
        /// </summary>
        public static FakeRecoveryPort Capsule(string vesselId)
        {
            var port = new FakeRecoveryPort
            {
                Reading = new RecoveryReading
                {
                    VesselId = vesselId, Body = "Kerbin", Situation = "ORBITING", HasAtmosphere = true, AtmosphereTopMeters = 70000,
                    AltitudeMeters = 80000, HeightAboveTerrainMeters = 80000, SurfaceSpeed = 2200, VerticalSpeed = 0, ApoapsisMeters = 81000, PeriapsisMeters = 79000,
                    TimeToApoapsis = 600, Orbiting = true, CurrentStage = 2, CrewCount = 1, RootIsCommand = true,
                    Parts = new List<RecoveryPart>
                    {
                        new RecoveryPart { PartId = "1", Name = "mk1pod.v2", Crewed = true, Command = true },
                        new RecoveryPart { PartId = "2", Name = "parachuteSingle", Parachute = true, StagingOn = true, InverseStage = 0 },
                        new RecoveryPart { PartId = "3", Name = "HeatShield1", HeatShield = true, Separator = true, StagingOn = true, InverseStage = 0 },
                        new RecoveryPart { PartId = "4", Name = "Decoupler.1", Separator = true, StagingOn = true, InverseStage = 1 },
                        new RecoveryPart { PartId = "5", Name = "fuelTankSmallFlat", Propulsion = true },
                        new RecoveryPart { PartId = "6", Name = "liquidEngine3.v2", Propulsion = true, StagingOn = true, InverseStage = 2 }
                    },
                    Chutes = new List<RecoveryChute> { new RecoveryChute { PartId = "2", Name = "parachuteSingle", State = "STOWED", Safety = "UNSAFE", AutomateSafeDeploy = 0 } }
                }
            };
            port.Drops[1] = new List<string> { "4", "5", "6" };
            return port;
        }

        public RecoveryChute Chute(string partId = "2") { return Reading.Chutes.First(c => c.PartId == partId); }
        public int Count(string prefix) { return Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal)); }

        /// <summary>Puts the vessel at that altitude and periapsis (situation follows), terrain at sea level.</summary>
        public void Fly(double altitude, double periapsis, double surfaceSpeed = 2000, string situation = null)
        {
            Reading.AltitudeMeters = altitude; Reading.HeightAboveTerrainMeters = altitude; Reading.PeriapsisMeters = periapsis; Reading.SurfaceSpeed = surfaceSpeed;
            Reading.Orbiting = periapsis > Reading.AtmosphereTopMeters;
            Reading.Situation = situation ?? (Reading.Orbiting ? "ORBITING" : altitude > Reading.AtmosphereTopMeters ? "SUB_ORBITAL" : "FLYING");
        }

        public RecoveryReading Read(string vesselId) { Reads++; return Gone || vesselId != Reading.VesselId ? null : Reading; }

        public IList<FlightPartAction> StageActions(int stage)
        {
            return Reading.Parts.Where(p => p.StagingOn && p.InverseStage == stage)
                .Select(p => new FlightPartAction { PartId = p.PartId, PartName = p.Name, Module = p.Separator ? "ModuleDecouple" : p.Parachute ? "ModuleParachute" : "ModuleEngines", ActsOnStaging = true }).ToList();
        }

        public IList<FlightPartAction> StageGroupBindings() { return new List<FlightPartAction>(); }

        public void ActivateNextStage()
        {
            Calls.Add("stage:" + (Reading.CurrentStage - 1));
            if (StageIgnored) return;
            Reading.CurrentStage--;
            List<string> dropped;
            if (Drops.TryGetValue(Reading.CurrentStage, out dropped)) Reading.Parts.RemoveAll(p => dropped.Contains(p.PartId));
        }

        public bool ArmChute(string vesselId, string partId)
        {
            Calls.Add("arm:" + partId);
            var chute = Reading.Chutes.FirstOrDefault(c => c.PartId == partId);
            if (chute == null) return false;
            if (!Shielded.Contains(partId) && chute.State == "STOWED") chute.State = "ACTIVE";
            return true;
        }

        public bool SetThrottle(float value)
        {
            Calls.Add("throttle:" + value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            if (ThrottleRefused) return false;
            Throttle = value; return true;
        }

        public void ReleaseThrottle(string vesselId) { Calls.Add("throttle_release"); Throttle = 0; }
    }
}
