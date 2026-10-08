using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>The pure arithmetic of the navigation tools: node time resolution, warp rate choice and the free-return assessment.</summary>
    internal static class NavigationMath
    {
        internal sealed class TimeResolution
        {
            public string Reason;
            public string Detail;
            public double UniversalTime;
            public bool Ok { get { return Reason == null; } }
        }

        /// <summary>
        /// Resolves a node or warp time. absolute: <paramref name="seconds"/> is a universal time; in_seconds: seconds from now; apoapsis/periapsis: the next
        /// apsis of the current orbit plus an optional offset. The result must lie <see cref="NavigationLimits.MinLeadSeconds"/>..<see cref="NavigationLimits.MaxHorizonSeconds"/> ahead.
        /// </summary>
        public static TimeResolution ResolveTime(string reference, double? seconds, double now, double timeToApoapsis, double timeToPeriapsis, double eccentricity)
        {
            var problem = NavigationLimits.CheckTime(reference, seconds);
            if (problem != null) return new TimeResolution { Reason = ControlReasons.InvalidArgument, Detail = problem };
            if (!NavigationLimits.IsFinite(now)) return new TimeResolution { Reason = NavigationReasons.TimeUnavailable, Detail = "the universal time is unknown" };
            double ut;
            switch (reference)
            {
                case "absolute": ut = seconds.Value; break;
                case "in_seconds": ut = now + seconds.Value; break;
                case "apoapsis":
                    if (!(eccentricity < 1) || !NavigationLimits.IsFinite(timeToApoapsis) || timeToApoapsis < 0)
                        return new TimeResolution { Reason = NavigationReasons.TimeUnavailable, Detail = "the current orbit is open and has no apoapsis" };
                    ut = now + timeToApoapsis + (seconds ?? 0); break;
                default:
                    if (!NavigationLimits.IsFinite(timeToPeriapsis))
                        return new TimeResolution { Reason = NavigationReasons.TimeUnavailable, Detail = "the periapsis time of the current orbit is unknown" };
                    ut = now + timeToPeriapsis + (seconds ?? 0); break;
            }
            var window = WindowProblem(ut, now);
            return window == null ? new TimeResolution { UniversalTime = ut } : new TimeResolution { Reason = ControlReasons.InvalidArgument, Detail = window };
        }

        /// <summary>Null when <paramref name="ut"/> is far enough ahead and not too far, else the detail.</summary>
        public static string WindowProblem(double ut, double now)
        {
            var ahead = ut - now;
            if (!NavigationLimits.IsFinite(ahead)) return "the resolved time is not a finite number";
            if (ahead < NavigationLimits.MinLeadSeconds) return "the resolved time is " + Seconds(ahead) + " s from now; it must be at least " + NavigationLimits.MinLeadSeconds + " s in the future";
            if (ahead > NavigationLimits.MaxHorizonSeconds) return "the resolved time is " + Seconds(ahead) + " s from now; it must be at most " + NavigationLimits.MaxHorizonSeconds + " s ahead";
            return null;
        }

        /// <summary>
        /// The rails rate index to use with <paramref name="remainingSeconds"/> of game time left: the highest index whose rate is within the cap, the altitude
        /// limit and the ceiling, and that needs at least <paramref name="marginRealSeconds"/> of real time to use up what is left. 0 (real time) at the least.
        /// </summary>
        public static int ChooseWarpIndex(float[] rates, double remainingSeconds, int ceiling, float cap, int altitudeLimit, double marginRealSeconds)
        {
            if (rates == null || rates.Length == 0 || !(remainingSeconds > 0)) return 0;
            var best = 0;
            for (var i = 1; i < rates.Length; i++)
            {
                if (i > ceiling) break;
                if (altitudeLimit >= 0 && i > altitudeLimit) break;
                var rate = rates[i];
                if (!(rate > 0) || rate > cap) break;
                if (rate * marginRealSeconds > remainingSeconds) break;
                best = i;
            }
            return best;
        }

        private static string Seconds(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    /// <summary>What a trajectory means for a free-return flyby of a moon (by default the Mun of Kerbin).</summary>
    internal sealed class TrajectoryAssessment
    {
        public string TargetBody, HomeBody;
        public bool Encounter;
        public int EncounterPatchIndex = -1;
        public double EncounterEntryUniversalTime = double.NaN;
        /// <summary>The periapsis altitude of the last (post-burn) patch around the target: the closest approach of the flyby.</summary>
        public double EncounterPeriapsisAltitude = double.NaN;
        public int ReturnPatchIndex = -1;
        /// <summary>The periapsis altitude of the last patch of the first home-body run after the encounter (the post-burn patch when a correction node sits on the return): what a free return needs (about 20..40 km for Kerbin).</summary>
        public double ReturnPeriapsisAltitude = double.NaN;
        public double ReturnAtmosphereTop = double.NaN;
        /// <summary>Seconds from now to the first sphere-of-influence change (ENCOUNTER or ESCAPE) on the trajectory.</summary>
        public double TimeToSoiChange = double.NaN;
        public string FinalTransition;

        public static TrajectoryAssessment Of(IList<OrbitPatch> patches, double now, string targetBody, string homeBody)
        {
            var a = new TrajectoryAssessment { TargetBody = targetBody, HomeBody = homeBody };
            if (patches == null) return a;
            // A node inside a body's sphere of influence splits the body into before/after-node patches, so the answer is the LAST patch of the final
            // unbroken run of target patches (the post-burn one), and the last patch of the first home run after it.
            var runStart = -1; var runEnd = -1;
            for (var i = 0; i < patches.Count; i++)
            {
                var p = patches[i];
                if (p == null) continue;
                if (string.Equals(p.ReferenceBody, targetBody, StringComparison.Ordinal))
                {
                    if (runEnd != i - 1 || runStart < 0 || runEnd < 0) runStart = i;
                    runEnd = i;
                }
            }
            if (runEnd >= 0)
            {
                var last = patches[runEnd];
                a.Encounter = true; a.EncounterPatchIndex = runEnd; a.EncounterEntryUniversalTime = patches[runStart].StartUniversalTime;
                a.EncounterPeriapsisAltitude = last.PeriapsisAltitude;
                var inReturn = false;
                for (var i = runEnd + 1; i < patches.Count; i++)
                {
                    var p = patches[i];
                    if (p == null) continue;
                    if (string.Equals(p.ReferenceBody, homeBody, StringComparison.Ordinal))
                    { inReturn = true; a.ReturnPatchIndex = i; a.ReturnPeriapsisAltitude = p.PeriapsisAltitude; a.ReturnAtmosphereTop = p.AtmosphereTopMeters; }
                    else if (inReturn) break;
                }
            }
            for (var i = 0; i < patches.Count; i++)
            {
                var p = patches[i];
                if (p == null) continue;
                if (double.IsNaN(a.TimeToSoiChange) && (p.EndTransition == "ENCOUNTER" || p.EndTransition == "ESCAPE")) a.TimeToSoiChange = p.EndUniversalTime - now;
                a.FinalTransition = p.EndTransition;
            }
            return a;
        }

        public JObject ToJson()
        {
            JToken returnsIntoAtmosphere = JValue.CreateNull();
            if (ReturnPatchIndex >= 0 && NavigationLimits.IsFinite(ReturnPeriapsisAltitude) && ReturnAtmosphereTop > 0) returnsIntoAtmosphere = ReturnPeriapsisAltitude < ReturnAtmosphereTop;
            return new JObject
            {
                ["targetBody"] = TargetBody, ["homeBody"] = HomeBody,
                ["munEncounter"] = Encounter,
                ["encounterPatchIndex"] = EncounterPatchIndex < 0 ? JValue.CreateNull() : new JValue(EncounterPatchIndex),
                ["encounterEntryUniversalTimeSeconds"] = OrbitPrediction.Finite(EncounterEntryUniversalTime),
                ["munClosestApproachAltitudeMetres"] = OrbitPrediction.Finite(EncounterPeriapsisAltitude),
                ["munImpact"] = Encounter && NavigationLimits.IsFinite(EncounterPeriapsisAltitude) && EncounterPeriapsisAltitude < 0,
                ["returnPatchIndex"] = ReturnPatchIndex < 0 ? JValue.CreateNull() : new JValue(ReturnPatchIndex),
                ["returnPeriapsisKerbin"] = OrbitPrediction.Finite(ReturnPeriapsisAltitude),
                ["returnsIntoAtmosphere"] = returnsIntoAtmosphere,
                ["timeToSoiChangeSeconds"] = OrbitPrediction.Finite(TimeToSoiChange),
                ["finalTransition"] = FinalTransition
            };
        }
    }

    /// <summary>Turns a <see cref="TrajectoryReading"/> into the bounded flight.orbit_prediction report. Pure; every number carries its unit in its name.</summary>
    internal static class OrbitPrediction
    {
        public const string TargetBody = "Mun";
        public const string HomeBody = "Kerbin";

        public static JObject Build(TrajectoryReading r)
        {
            var now = r.UniversalTime;
            var withoutNodes = Trajectory(r.Coast, r.CoastTruncated, now);
            withoutNodes["endsAtFirstNode"] = r.NodeCount > 0;
            JToken withNodes = JValue.CreateNull();
            if (r.NodeCount > 0 && r.Planned.Count > 0) withNodes = Trajectory(r.Planned, r.PlannedTruncated, now);
            var basis = withNodes.Type == JTokenType.Null ? r.Coast : r.Planned;
            var assessment = TrajectoryAssessment.Of(basis, now, TargetBody, HomeBody).ToJson();
            assessment["basis"] = withNodes.Type == JTokenType.Null ? "withoutNodes" : "withNodes";
            var coast = TrajectoryAssessment.Of(r.Coast, now, TargetBody, HomeBody);
            return new JObject
            {
                ["vessel"] = new JObject { ["id"] = r.VesselId, ["entity"] = FlightEffectClassifier.EntityOf(r.VesselId) },
                ["body"] = Text(r.Body), ["situation"] = r.Situation, ["universalTimeSeconds"] = Finite(now),
                ["solverAvailable"] = r.SolverAvailable, ["flightPlanningUnlocked"] = r.FlightPlanningUnlocked,
                ["nodes"] = new JArray(r.Nodes.Take(NavigationLimits.MaxNodes).Select((n, i) => (JToken)Node(n, i, now))),
                ["nodeCount"] = r.NodeCount, ["nodesTruncated"] = r.NodeCount > NavigationLimits.MaxNodes,
                ["timeToSoiChangeSeconds"] = Finite(coast.TimeToSoiChange),
                ["withoutNodes"] = withoutNodes, ["withNodes"] = withNodes,
                ["assessment"] = assessment,
                ["notes"] = "withoutNodes is the vessel's own orbit chain (KSP ends it at the first node); withNodes is the solver's flight plan with every node applied. "
                    + "The assessment uses withNodes when there are nodes. A free return needs munEncounter true and returnPeriapsisKerbin about 20000..40000 m. Predictions are forecasts, not achieved encounters; the patch count is limited by the game's conic patch limit."
            };
        }

        public static JObject Node(ManeuverNodeInfo n, int index, double now)
        {
            var magnitude = Math.Sqrt(n.Prograde * n.Prograde + n.Normal * n.Normal + n.Radial * n.Radial);
            return new JObject
            {
                ["index"] = index, ["universalTimeSeconds"] = Finite(n.UniversalTime), ["secondsFromNow"] = Finite(n.UniversalTime - now),
                ["deltaV"] = new JObject { ["progradeMetresPerSecond"] = Finite(n.Prograde), ["normalMetresPerSecond"] = Finite(n.Normal), ["radialMetresPerSecond"] = Finite(n.Radial) },
                ["deltaVMagnitudeMetresPerSecond"] = Finite(magnitude)
            };
        }

        private static JObject Trajectory(IList<OrbitPatch> patches, bool truncated, double now)
        {
            var list = (patches ?? new List<OrbitPatch>()).Where(p => p != null).Take(NavigationLimits.MaxPatches).ToList();
            return new JObject
            {
                ["patches"] = new JArray(list.Select((p, i) => (JToken)Patch(p, i))),
                ["truncated"] = truncated || (patches != null && patches.Count > NavigationLimits.MaxPatches),
                ["assessment"] = TrajectoryAssessment.Of(list, now, TargetBody, HomeBody).ToJson()
            };
        }

        private static JObject Patch(OrbitPatch p, int index)
        {
            var open = !(p.Eccentricity < 1);
            return new JObject
            {
                ["index"] = index, ["referenceBody"] = Text(p.ReferenceBody),
                ["startUniversalTimeSeconds"] = Finite(p.StartUniversalTime),
                // KSP stores the period in EndUT of an open final patch: it is not a time, so it is not reported.
                ["endUniversalTimeSeconds"] = open && p.EndTransition == "FINAL" ? JValue.CreateNull() : Finite(p.EndUniversalTime),
                ["apoapsisAltitudeMetres"] = open ? JValue.CreateNull() : Finite(p.ApoapsisAltitude),
                ["periapsisAltitudeMetres"] = Finite(p.PeriapsisAltitude),
                ["inclinationDegrees"] = Finite(p.InclinationDegrees), ["eccentricity"] = Finite(p.Eccentricity),
                ["startTransition"] = p.StartTransition, ["endTransition"] = p.EndTransition,
                ["periapsisBelowSurface"] = NavigationLimits.IsFinite(p.PeriapsisAltitude) && p.PeriapsisAltitude < 0
            };
        }

        internal static JToken Finite(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? JValue.CreateNull() : new JValue(value); }
        private static string Text(string value) { return value == null ? null : value.Substring(0, Math.Min(256, value.Length)); }
    }

    /// <summary>flight.orbit_prediction: read-only, no lease, only for an active vessel the agency owns.</summary>
    internal sealed class OrbitPredictionService
    {
        private readonly INavigationPort port;
        private readonly Func<string> worldEpoch;
        public OrbitPredictionService(INavigationPort port, Func<string> worldEpoch) { this.port = port; this.worldEpoch = worldEpoch; }

        public FlightStateResult Predict()
        {
            if (!port.InFlight) return new FlightStateResult { Reason = FlightReasons.FlightUnavailable };
            TrajectoryReading reading;
            try { reading = port.ReadTrajectory(NavigationLimits.MaxPatches); } catch (Exception) { reading = null; }
            if (reading == null) return new FlightStateResult { Reason = FlightReasons.FlightUnavailable };
            if (!reading.Owned) return new FlightStateResult { Reason = FlightReasons.VesselNotOwned };
            var data = OrbitPrediction.Build(reading);
            data["epoch"] = worldEpoch();
            return new FlightStateResult { Data = data };
        }
    }
}
