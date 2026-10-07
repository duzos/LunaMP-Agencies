using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    internal enum AutopilotKind { Ascent, ExecuteNode, PlanCircularize, PlanHohmann }

    /// <summary>The user object this bridge puts in MechJeb's user pools. Its identity is how the adapter tells our hold from anyone else's.</summary>
    internal sealed class BridgeUser
    {
        public override string ToString() { return "KspControl"; }
    }

    /// <summary>Timing and debounce of the autopilot runner. Tests shorten them.</summary>
    internal sealed class AutopilotOptions
    {
        public long AscentTimeoutMs = 20 * 60 * 1000L;
        public long NodeTimeoutMs = 60 * 60 * 1000L;
        /// <summary>Periapsis must stay above the atmosphere for this long before the orbit counts.</summary>
        public long OrbitConfirmMs = 1000;
        /// <summary>After the orbit is confirmed, how long MechJeb may keep finishing its circularization before the job ends anyway.</summary>
        public long FinishGraceMs = 120 * 1000L;
        /// <summary>Consecutive frames of human input before takeover. One frame is a bounce; two is a person.</summary>
        public int HumanDebounceFrames = 2;
        /// <summary>The competitor scan reflects over a dozen modules, so it runs on every Nth frame.</summary>
        public int ScanEveryFrames = 5;
    }

    /// <summary>
    /// One autopilot job: an ascent, a node execution or a (synchronous) maneuver plan. Created at admission, advanced once per frame by
    /// <see cref="AutopilotRunner"/> and read by flight.autopilot_status. Plain properties are the envelope; runner-only state is internal.
    /// </summary>
    internal sealed class AutopilotJob
    {
        public string RequestId { get; set; }
        public AutopilotKind Kind { get; set; }
        public string Fingerprint { get; set; }
        public string LeaseId { get; set; }
        public ExecutionTicket Ticket { get; set; }
        public ClassifiedEffect[] Effects { get; set; }
        public string VesselId { get; set; }
        public string Status { get; set; } = JobStatuses.Running;
        public string Phase { get; set; } = "admitted";
        public string ReasonCode { get; set; }
        public string Detail { get; set; }
        /// <summary>The bridge changed something in the game for this job (settings written, a module engaged, a node created).</summary>
        public bool Dispatched { get; set; }
        public List<string> EffectsApplied { get; } = new List<string>();
        public double TargetAltitudeMeters { get; set; }
        public double InclinationDegrees { get; set; }
        public bool Autostage { get; set; }
        public bool All { get; set; }
        public string TargetBodyName { get; set; }
        public AscentSettingsView Settings { get; set; }
        public FlightTelemetry Last { get; set; }
        public string ModuleStatus { get; set; }
        public string NodeState { get; set; }
        public bool OrbitReached { get; set; }
        /// <summary>True when MechJeb ended its own ascent after the orbit was reached; false when the grace ran out first.</summary>
        public bool? AscentFinished { get; set; }
        public int StartNodes { get; set; }
        public JObject Plan { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public bool Terminal { get { return JobStatuses.IsTerminal(Status); } }

        // ---- runner-only state ----
        internal readonly BridgeUser User = new BridgeUser();
        internal long StartedAt, OrbitSince = -1, OrbitReachedAt;
        internal int HumanFrames, Frames;
        internal bool Engaged;

        public static string OperationName(AutopilotKind kind)
        {
            switch (kind)
            {
                case AutopilotKind.Ascent: return "autopilot_ascent";
                case AutopilotKind.ExecuteNode: return "autopilot_execute_node";
                case AutopilotKind.PlanCircularize: return "autopilot_plan_circularize";
                default: return "autopilot_plan_hohmann";
            }
        }

        public JObject ToEnvelope()
        {
            var envelope = new JObject
            {
                ["operation"] = OperationName(Kind), ["requestId"] = RequestId, ["phase"] = Phase,
                ["notDispatched"] = Terminal && !Dispatched, ["dispatched"] = Dispatched,
                ["vesselId"] = VesselId,
                ["effects"] = new JArray(EffectsApplied),
                ["startedUtc"] = Format(CreatedUtc), ["updatedUtc"] = Format(UpdatedUtc),
                ["completedUtc"] = CompletedUtc.HasValue ? (JToken)Format(CompletedUtc.Value) : JValue.CreateNull()
            };
            if (Last != null)
                envelope["telemetry"] = new JObject
                {
                    ["body"] = Last.BodyName, ["altitudeMeters"] = Finite(Last.AltitudeMeters), ["apoapsisMeters"] = Finite(Last.ApoapsisMeters), ["periapsisMeters"] = Finite(Last.PeriapsisMeters),
                    ["atmosphereTopMeters"] = Finite(Last.AtmosphereTopMeters), ["situation"] = Last.Situation, ["maneuverNodes"] = Last.ManeuverNodes
                };
            if (Kind == AutopilotKind.Ascent)
            {
                envelope["request"] = new JObject { ["targetAltitudeMeters"] = TargetAltitudeMeters, ["inclinationDegrees"] = InclinationDegrees, ["autostage"] = Autostage };
                if (Settings != null)
                    envelope["configured"] = new JObject { ["ascentType"] = Settings.AscentType, ["targetAltitudeMeters"] = Settings.TargetAltitudeMeters, ["inclinationDegrees"] = Settings.InclinationDegrees, ["autostage"] = Settings.Autostage };
                envelope["mechjebStatus"] = ModuleStatus;
                envelope["orbitReached"] = OrbitReached;
                envelope["ascentFinished"] = AscentFinished.HasValue ? (JToken)AscentFinished.Value : JValue.CreateNull();
            }
            else if (Kind == AutopilotKind.ExecuteNode)
            {
                envelope["request"] = new JObject { ["all"] = All };
                envelope["executorState"] = NodeState;
                envelope["nodesAtStart"] = StartNodes;
            }
            else if (Plan != null) envelope["plan"] = Plan;
            if (Detail != null) envelope["detail"] = Detail;
            return envelope;
        }

        private static JToken Finite(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? JValue.CreateNull() : new JValue(value); }
        private static string Format(DateTime value) { return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }
    }

    /// <summary>In-memory registry: dedupe by request id with a bounded history. A bridge restart loses it, which the host journal turns into "indeterminate".</summary>
    internal sealed class AutopilotJobs
    {
        private const int MaxJobs = 32;
        private readonly List<AutopilotJob> jobs = new List<AutopilotJob>();
        private readonly object gate = new object();
        public AutopilotJob Last { get; private set; }

        public AutopilotJob Get(string requestId) { lock (gate) return jobs.FirstOrDefault(j => j.RequestId == requestId); }

        public void Add(AutopilotJob job)
        {
            lock (gate)
            {
                jobs.Add(job); Last = job;
                while (jobs.Count > MaxJobs)
                {
                    var oldest = jobs.FirstOrDefault(j => j.Terminal);
                    if (oldest == null) break;
                    jobs.Remove(oldest);
                }
            }
        }
    }
}
