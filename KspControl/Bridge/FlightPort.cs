using System;
using System.Collections.Generic;

namespace KspControl.Bridge
{
    // Pure data and ports only: no Unity or KSP types, so this file links into the unit-test assembly.
    // Every member is called on the main thread unless its comment says otherwise.

    /// <summary>One resource total across the vessel.</summary>
    internal sealed class FlightResource
    {
        public string Name { get; set; }
        public double Amount { get; set; }
        public double Capacity { get; set; }
    }

    /// <summary>The stock delta-V calculation for one stage. Ready is false while the simulation has not finished.</summary>
    internal sealed class FlightStageDeltaV
    {
        public int Stage { get; set; }
        public double DeltaVActual { get; set; }
        public double DeltaVVacuum { get; set; }
        public double DeltaVSeaLevel { get; set; }
        public double BurnTimeSeconds { get; set; }
        public double ThrustToWeightActual { get; set; }
        public double MassKilograms { get; set; }
    }

    /// <summary>The orbit the vessel is on right now, with the body whose sphere of influence it is in.</summary>
    internal sealed class FlightOrbit
    {
        public string ReferenceBody { get; set; }
        public double ApoapsisAltitude { get; set; }
        public double PeriapsisAltitude { get; set; }
        public double InclinationDegrees { get; set; }
        public double Eccentricity { get; set; }
        public double SemiMajorAxis { get; set; }
        public double PeriodSeconds { get; set; }
        public double TimeToApoapsis { get; set; }
        public double TimeToPeriapsis { get; set; }
        /// <summary>The predicted next patch, never an achieved encounter. Null when the orbit has no next patch.</summary>
        public string PredictedNextBody { get; set; }
        public string PatchEndTransition { get; set; }
        /// <summary>Seconds until the current patch ends in an ENCOUNTER or ESCAPE; NaN when it does not.</summary>
        public double TimeToSoiChange { get; set; } = double.NaN;
    }

    /// <summary>The binary controls the vessel reports. Null when unknown.</summary>
    internal sealed class FlightControlStates
    {
        public double Throttle { get; set; }
        public bool Sas { get; set; }
        public bool Rcs { get; set; }
        public bool Gear { get; set; }
        public bool Lights { get; set; }
        public bool Brakes { get; set; }
        public int CurrentStage { get; set; }
        public int StageCount { get; set; }
    }

    internal sealed class FlightWarpInfo
    {
        /// <summary>"rails" or "physics".</summary>
        public string Mode { get; set; } = "rails";
        public int CurrentIndex { get; set; }
        public double CurrentRate { get; set; }
        /// <summary>The rate table of the current mode, indexed by rate index.</summary>
        public float[] Rates { get; set; } = new float[0];
        /// <summary>The highest rails index the altitude allows, or -1 when unknown. Physics warp has no altitude limit here.</summary>
        public int AltitudeLimitIndex { get; set; } = -1;
    }

    /// <summary>Everything flight.state reports, read in one frame by the port.</summary>
    internal sealed class FlightSnapshot
    {
        public string VesselId { get; set; }
        public string VesselName { get; set; }
        /// <summary>The disclosure decision: only an active vessel this agency owns may be reported or controlled.</summary>
        public bool Owned { get; set; }
        public bool Controllable { get; set; }
        public string Situation { get; set; }
        public string Body { get; set; }
        public double UniversalTime { get; set; }
        public double Altitude { get; set; }
        public double VerticalSpeed { get; set; }
        public double SurfaceSpeed { get; set; }
        public double OrbitalSpeed { get; set; }
        public FlightOrbit Orbit { get; set; }
        public FlightControlStates Controls { get; set; } = new FlightControlStates();
        public List<FlightStageDeltaV> Stages { get; set; } = new List<FlightStageDeltaV>();
        public bool DeltaVReady { get; set; }
        public double TotalDeltaVActual { get; set; }
        public List<FlightResource> Resources { get; set; } = new List<FlightResource>();
        public FlightWarpInfo Warp { get; set; } = new FlightWarpInfo();
        public int CrewCount { get; set; }
        public int PartCount { get; set; }
        /// <summary>True when the MechJeb flight computer is present on this vessel. Presence only: nothing is read from it.</summary>
        public bool MechJebPresent { get; set; }
        /// <summary>The earliest maneuver node, or null when there is none.</summary>
        public ManeuverNodeInfo NextNode { get; set; }
        public int NodeCount { get; set; }
    }

    /// <summary>A part module that a stage or an action group would trigger.</summary>
    internal sealed class FlightPartAction
    {
        public string PartId { get; set; }
        public string PartName { get; set; }
        public string Module { get; set; }
        public string Action { get; set; }
        /// <summary>True when the module overrides the stage activation hook, so staging runs its own code.</summary>
        public bool ActsOnStaging { get; set; }
    }

    /// <summary>The Unity-facing reads and callbacks of the flight layer. The unit tests fake it; UnityFlightPort is the only implementation.</summary>
    internal interface IFlightPort
    {
        /// <summary>True in the flight scene once the active vessel exists.</summary>
        bool InFlight { get; }
        /// <summary>The active vessel snapshot, or null when there is none. Owned is the agency disclosure decision.</summary>
        FlightSnapshot Read();
        bool GetGroup(string group);
        /// <summary>Sets a built-in or custom group to a state through the stock setter and returns the state observed afterwards.</summary>
        bool SetGroup(string group, bool desired);
        /// <summary>Toggles a group once and returns the state observed afterwards.</summary>
        bool ToggleGroup(string group);
        bool StagingLocked { get; }
        /// <summary>The parts that activating this stage would trigger, with the modules that act.</summary>
        IList<FlightPartAction> PartsInStage(int stage);
        /// <summary>The part actions currently bound to a group (Gear, Light, Brakes, SAS, RCS, CustomNN or Abort).</summary>
        IList<FlightPartAction> GroupBindings(string group);
        void ActivateNextStage();
        void FireAbort();
        /// <summary>Calls the stock rate setter (LunaMP's patch may veto it) and returns nothing: read the warp state afterwards.</summary>
        void SetWarpIndex(int index);
    }

    /// <summary>The flight inputs the bridge may own while a lease is held.</summary>
    internal interface IFlightInputPort
    {
        /// <summary>
        /// Installs a fly-by-wire callback on the active vessel. Every vessel tick the callback asks <paramref name="tick"/> for the throttle to
        /// apply; a null answer makes it remove itself. Returns false when no vessel can be controlled.
        /// </summary>
        bool Attach(Func<float?> tick);
        /// <summary>Removes the callback. Idempotent.</summary>
        void Detach();
        /// <summary>Writes the throttle to the attached vessel's control state and, while it is still the active vessel, the stock input state.</summary>
        void WriteThrottle(float value);
        /// <summary>Drops time warp to real time.</summary>
        void CancelWarp();
    }

    /// <summary>The raw human control input, as a pure answer so takeover detection is testable.</summary>
    internal interface IFlightHumanInput
    {
        /// <summary>True while the player holds a flight control (stick, throttle or stage key).</summary>
        bool PlayerIsInputting();
    }
}
