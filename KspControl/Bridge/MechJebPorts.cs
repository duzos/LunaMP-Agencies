using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    // Pure types and ports of the MechJeb adapter layer. No Unity or KSP types: the job logic runs against fakes, the reflection adapter
    // is tested against look-alike classes, and only MechJebUnity.cs touches the game.

    /// <summary>What was resolved about the installed MechJeb. Resolved once; never holds a game object.</summary>
    internal sealed class MechJebCapabilities
    {
        /// <summary>The MechJeb assembly with a MechJebCore type was found.</summary>
        public bool Installed { get; set; }
        public string Version { get; set; }
        /// <summary>The version is one the adapter was written against (2.15.x). Mutations are refused otherwise.</summary>
        public bool VersionSupported { get; set; }
        /// <summary>Why the adapter is not usable, as a reason code, or null.</summary>
        public string Reason { get; set; }
        /// <summary>Module name to "the type and the members the adapter needs were found".</summary>
        public Dictionary<string, bool> Modules { get; } = new Dictionary<string, bool>(StringComparer.Ordinal);
        /// <summary>True when the type for that module exists and the members the adapter needs are all present.</summary>
        public bool Has(string module) { bool value; return Modules.TryGetValue(module, out value) && value; }
        /// <summary>Operation classes of the maneuver planner that were found (informational: the planner path uses stock math).</summary>
        public List<string> PlannerOperations { get; } = new List<string>();
        public bool AtmosphereAutopilotInstalled { get; set; }
        public bool Usable { get { return Installed && VersionSupported; } }
        public string State { get { return Installed ? (VersionSupported ? "available" : "unsupported_version") : "unavailable"; } }
    }

    internal sealed class AscentSettingsView
    {
        public string AscentType { get; set; }
        public double? TargetAltitudeMeters { get; set; }
        public double? InclinationDegrees { get; set; }
        public bool? Autostage { get; set; }
        public bool? SkipCircularization { get; set; }
    }

    internal sealed class AscentReading
    {
        public bool Enabled { get; set; }
        public string Status { get; set; }
        /// <summary>The adapter's user object is in the module's user pool.</summary>
        public bool OwnUserPresent { get; set; }
        /// <summary>Users other than ours.</summary>
        public int OtherUsers { get; set; }
    }

    internal sealed class NodeReading
    {
        public bool Enabled { get; set; }
        /// <summary>WARPALIGN, LEAD, BURN or IDLE.</summary>
        public string State { get; set; }
        public bool OwnUserPresent { get; set; }
        public int OtherUsers { get; set; }
        public bool Autowarp { get; set; }
    }

    /// <summary>
    /// The MechJeb operations the autopilot job needs. Main thread only. Every call re-reads the live core: nothing is cached across calls,
    /// because the vessel and its parts can be destroyed between frames. Methods throw <see cref="MechJebException"/> with a reason code.
    /// </summary>
    internal interface IMechJebPort
    {
        MechJebCapabilities Capabilities { get; }
        /// <summary>True when the active vessel carries a MechJeb core this frame.</summary>
        bool HasCore();
        /// <summary>The full status for mechjeb_status, including engaged states and current settings. Never throws.</summary>
        JObject ReadStatus();
        /// <summary>
        /// Names of other controllers that are engaged: MechJeb modules with a foreign user, or AtmosphereAutopilot. A module that only our own user holds is not a competitor.
        /// <paramref name="autopilotsOnly"/> skips the support modules (attitude, rover), which MechJeb's own autopilots legitimately use while running.
        /// </summary>
        List<string> FindCompetitors(object ownUser, bool autopilotsOnly);
        /// <summary>Writes the ascent settings, then reads them back. Throws if a value did not take.</summary>
        AscentSettingsView ConfigureAscent(double altitudeMeters, double inclinationDegrees, bool autostage);
        void EngageAscent(object user);
        void DisengageAscent(object user);
        AscentReading ReadAscent(object user);
        void EngageNode(object user, bool all);
        void DisengageNode(object user);
        NodeReading ReadNode(object user);
        /// <summary>Asks MechJeb's thrust controller to stop. Never throws.</summary>
        void ThrustOff();
    }

    internal sealed class MechJebException : Exception
    {
        public string Code { get; }
        public MechJebException(string code, string detail = null) : base(detail ?? code) { Code = code; }
    }

    internal sealed class FlightTelemetry
    {
        public string VesselId { get; set; }
        public string BodyName { get; set; }
        public double AltitudeMeters { get; set; }
        public double ApoapsisMeters { get; set; }
        public double PeriapsisMeters { get; set; }
        /// <summary>Height of the atmosphere of the current body, 0 when airless.</summary>
        public double AtmosphereTopMeters { get; set; }
        public string Situation { get; set; }
        /// <summary>The vessel is on a closed orbit around its main body (stock situation ORBITING or equivalent).</summary>
        public bool Orbiting { get; set; }
        public int ManeuverNodes { get; set; }
        public double UniversalTime { get; set; }
    }

    internal sealed class PlanOutcome
    {
        public bool Ok { get; set; }
        public string Reason { get; set; }
        public string Detail { get; set; }
        public JObject Data { get; set; } = new JObject();
        public static PlanOutcome Fail(string reason, string detail) { return new PlanOutcome { Ok = false, Reason = reason, Detail = detail }; }
    }

    /// <summary>The game-side reads and the two stock maneuver-node plans. Main thread only; the Unity implementation lives in MechJebUnity.cs.</summary>
    internal interface IAutopilotFlightPort
    {
        bool InFlight { get; }
        /// <summary>The active vessel's telemetry, or null when there is none.</summary>
        FlightTelemetry Read();
        /// <summary>A human is on the controls this frame (flight keys or axes). Throttle written by MechJeb or this bridge does not count.</summary>
        bool HumanInputDetected();
        /// <summary>Sets the stock main throttle to zero.</summary>
        void CutThrottle();
        PlanOutcome PlanCircularize();
        PlanOutcome PlanHohmann(string targetBodyName);
    }
}
