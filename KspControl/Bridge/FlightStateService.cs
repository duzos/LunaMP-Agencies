using System;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// flight.state: a bounded, read-only report of the active vessel. It answers only for a vessel this agency owns (the existing facade v1
    /// decision, made by the port), and every number carries its unit in its name.
    /// </summary>
    internal sealed class FlightStateResult
    {
        public string Reason;
        public JObject Data;
    }

    internal sealed class FlightStateService
    {
        internal const int MaxStages = 32;
        internal const int MaxResources = 32;
        private readonly IFlightPort port;
        private readonly Func<string> worldEpoch;
        public FlightStateService(IFlightPort port, Func<string> worldEpoch) { this.port = port; this.worldEpoch = worldEpoch; }

        public FlightStateResult State()
        {
            if (!port.InFlight) return new FlightStateResult { Reason = FlightReasons.FlightUnavailable };
            var s = port.Read();
            if (s == null) return new FlightStateResult { Reason = FlightReasons.FlightUnavailable };
            // The disclosure gate: nothing about a vessel the agency does not own leaves the bridge, not even its name.
            if (!s.Owned) return new FlightStateResult { Reason = FlightReasons.VesselNotOwned };
            return new FlightStateResult { Data = Build(s) };
        }

        internal JObject Build(FlightSnapshot s)
        {
            var c = s.Controls ?? new FlightControlStates();
            var data = new JObject
            {
                ["vessel"] = new JObject { ["id"] = s.VesselId, ["name"] = Text(s.VesselName), ["entity"] = FlightEffectClassifier.EntityOf(s.VesselId), ["controllable"] = s.Controllable, ["partCount"] = s.PartCount },
                ["situation"] = s.Situation,
                ["body"] = Text(s.Body),
                ["universalTimeSeconds"] = Finite(s.UniversalTime),
                ["altitudeAboveSeaLevelMetres"] = Finite(s.Altitude),
                ["verticalSpeedMetresPerSecond"] = Finite(s.VerticalSpeed),
                ["surfaceSpeedMetresPerSecond"] = Finite(s.SurfaceSpeed),
                ["orbitalSpeedMetresPerSecond"] = Finite(s.OrbitalSpeed),
                ["orbit"] = Orbit(s.Orbit),
                ["stage"] = new JObject
                {
                    ["current"] = c.CurrentStage, ["count"] = c.StageCount, ["deltaVReady"] = s.DeltaVReady,
                    ["totalDeltaVActualMetresPerSecond"] = s.DeltaVReady ? Finite(s.TotalDeltaVActual) : JValue.CreateNull(),
                    ["perStage"] = new JArray(s.Stages.Take(MaxStages).Select(Stage)), ["truncated"] = s.Stages.Count > MaxStages
                },
                ["resources"] = new JObject
                {
                    ["totals"] = new JArray(s.Resources.Take(MaxResources).Select(r => (JToken)new JObject { ["name"] = Text(r.Name), ["amount"] = Finite(r.Amount), ["capacity"] = Finite(r.Capacity) })),
                    ["truncated"] = s.Resources.Count > MaxResources
                },
                ["controls"] = new JObject
                {
                    ["throttleFraction"] = Finite(c.Throttle), ["sas"] = c.Sas, ["rcs"] = c.Rcs, ["gear"] = c.Gear, ["lights"] = c.Lights, ["brakes"] = c.Brakes
                },
                ["warp"] = new JObject
                {
                    ["mode"] = s.Warp.Mode, ["rateIndex"] = s.Warp.CurrentIndex, ["effectiveRate"] = Finite(s.Warp.CurrentRate),
                    ["capEffectiveRate"] = FlightLimits.MaxWarpRate, ["capPhysicsRate"] = FlightLimits.MaxPhysicsWarpRate,
                    ["altitudeLimitIndex"] = s.Warp.AltitudeLimitIndex < 0 ? JValue.CreateNull() : new JValue(s.Warp.AltitudeLimitIndex)
                },
                ["crew"] = new JObject { ["count"] = s.CrewCount },
                ["maneuver"] = new JObject
                {
                    ["nodeCount"] = s.NodeCount,
                    ["nextNode"] = s.NextNode == null ? JValue.CreateNull() : (JToken)OrbitPrediction.Node(s.NextNode, 0, s.UniversalTime),
                    ["detail"] = "flight_orbit_prediction reports every node and the trajectory with and without them"
                },
                ["controlState"] = new JObject { ["mechJebPresent"] = s.MechJebPresent, ["detail"] = "presence only; no MechJeb state is read or controlled" },
                ["epoch"] = worldEpoch()
            };
            return data;
        }

        private static JToken Orbit(FlightOrbit o)
        {
            if (o == null) return JValue.CreateNull();
            return new JObject
            {
                ["referenceBody"] = Text(o.ReferenceBody),
                ["apoapsisAltitudeMetres"] = Finite(o.ApoapsisAltitude), ["periapsisAltitudeMetres"] = Finite(o.PeriapsisAltitude),
                ["inclinationDegrees"] = Finite(o.InclinationDegrees), ["eccentricity"] = Finite(o.Eccentricity), ["semiMajorAxisMetres"] = Finite(o.SemiMajorAxis),
                ["periodSeconds"] = Finite(o.PeriodSeconds), ["timeToApoapsisSeconds"] = Finite(o.TimeToApoapsis), ["timeToPeriapsisSeconds"] = Finite(o.TimeToPeriapsis),
                ["predictedNextBody"] = o.PredictedNextBody == null ? JValue.CreateNull() : new JValue(Text(o.PredictedNextBody)),
                ["patchEndTransition"] = o.PatchEndTransition,
                ["timeToSoiChangeSeconds"] = Finite(o.TimeToSoiChange),
                ["predictedNote"] = "referenceBody is the sphere of influence the vessel is in now; predictedNextBody is a forecast, never proof of an encounter"
            };
        }

        private static JToken Stage(FlightStageDeltaV d)
        {
            return new JObject
            {
                ["stage"] = d.Stage, ["deltaVActualMetresPerSecond"] = Finite(d.DeltaVActual), ["deltaVVacuumMetresPerSecond"] = Finite(d.DeltaVVacuum),
                ["deltaVSeaLevelMetresPerSecond"] = Finite(d.DeltaVSeaLevel), ["burnTimeSeconds"] = Finite(d.BurnTimeSeconds),
                ["thrustToWeightActual"] = Finite(d.ThrustToWeightActual), ["massKilograms"] = Finite(d.MassKilograms)
            };
        }

        private static string Text(string value) { return value == null ? null : value.Substring(0, Math.Min(256, value.Length)); }
        internal static JToken Finite(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? JValue.CreateNull() : new JValue(value); }
    }
}
