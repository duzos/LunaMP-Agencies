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
        /// <summary>The bridge engaged through the ascent window module, so MechJeb's own Disengage button ends the ascent.</summary>
        public bool ViaWindow { get; set; }
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

    /// <summary>Directions the recovery job holds with MechJeb's attitude controller.</summary>
    internal static class AttitudeDirections
    {
        /// <summary>attitudeTo(Vector3d.back, AttitudeReference.ORBIT): retrograde to the orbital velocity, for the deorbit burn.</summary>
        public const string OrbitRetrograde = "orbit_retrograde";
        /// <summary>attitudeTo(Vector3d.back, AttitudeReference.SURFACE_VELOCITY): heat shield into the airflow, for reentry.</summary>
        public const string SurfaceRetrograde = "surface_retrograde";
    }

    internal sealed class AttitudeReading
    {
        public bool Enabled { get; set; }
        public bool OwnUserPresent { get; set; }
        public int OtherUsers { get; set; }
        /// <summary>MechJeb's attitudeAngleFromTarget(): degrees between the vessel's forward and the target. 0 while the controller is disabled.</summary>
        public double AngleFromTargetDegrees { get; set; }
    }

    /// <summary>
    /// The MechJeb operations the autopilot job needs. Main thread only. Every call names the vessel (a vessel id, or null for the active vessel) and
    /// re-reads that vessel's live core: nothing is cached across calls, and a job never touches the core of whatever vessel is active now.
    /// Methods throw <see cref="MechJebException"/> with a reason code.
    /// </summary>
    internal interface IMechJebPort
    {
        MechJebCapabilities Capabilities { get; }
        /// <summary>True when that vessel carries a MechJeb core this frame.</summary>
        bool HasCore(string vesselId);
        /// <summary>The full status for mechjeb_status (active vessel), including engaged states and current settings. Never throws.</summary>
        JObject ReadStatus();
        /// <summary>
        /// Names of other controllers that are engaged: any MechJeb autopilot or support module (attitude, thrust, rover) with a user that is not ours, or
        /// AtmosphereAutopilot. A user is ours if it is the bridge's user, the ascent window module the bridge engages through, or a MechJeb module whose own
        /// user set contains ours (the ascent hands over to the node executor and the attitude controller with itself as the user).
        /// The ascent window module is ours only when <paramref name="includeWindow"/> (ascent jobs): for any other job, a person engaging the ascent through
        /// the window is a competitor.
        /// </summary>
        List<string> FindCompetitors(string vesselId, object ownUser, bool includeWindow);
        /// <summary>Writes the ascent settings, then reads them back. Throws if a value did not take.</summary>
        AscentSettingsView ConfigureAscent(string vesselId, double altitudeMeters, double inclinationDegrees, bool autostage);
        void EngageAscent(string vesselId, object user);
        void DisengageAscent(string vesselId, object user);
        AscentReading ReadAscent(string vesselId, object user);
        /// <summary>Starts the node executor with Autowarp off and returns the Autowarp value it had, to be restored on release.</summary>
        bool EngageNode(string vesselId, object user, bool all);
        /// <summary>Removes our user. Aborts the executor only when nobody else holds it, and restores Autowarp when a saved value is given.</summary>
        void DisengageNode(string vesselId, object user, bool? restoreAutowarp);
        NodeReading ReadNode(string vesselId, object user);
        /// <summary>Asks that vessel's MechJeb thrust controller to stop. Never throws.</summary>
        void ThrustOff(string vesselId);
        /// <summary>Points the vessel with MechJeb's attitude controller (core.Attitude.attitudeTo) with our user. See <see cref="AttitudeDirections"/>.</summary>
        void HoldAttitude(string vesselId, object user, string direction);
        AttitudeReading ReadAttitude(string vesselId, object user);
        /// <summary>Removes our user from the attitude controller (MechJeb disables it once nobody holds it). Others' holds are left alone.</summary>
        void ReleaseAttitude(string vesselId, object user);
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
        /// <summary>The body's minimum safe orbit altitude (minOrbitalDistance minus radius); 0 when unknown.</summary>
        public double SafeAltitudeMeters { get; set; }
        /// <summary>Periapsis must be above this for the orbit to count: the atmosphere top or the safe altitude, whichever is higher.</summary>
        public double OrbitFloorMeters { get { return Math.Max(AtmosphereTopMeters, SafeAltitudeMeters); } }
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
        /// <summary>Sets that vessel's stock main throttle to zero (and the live input state only while it is still the active vessel).</summary>
        void CutThrottle(string vesselId);
        PlanOutcome PlanCircularize();
        PlanOutcome PlanHohmann(string targetBodyName);
    }
}
