using System;

namespace KspControl.Bridge
{
    // Pure launch contracts: no Unity or KSP types, so the launch state machine links into the unit-test assembly.

    /// <summary>The agency quote for the craft in the editor, as the LunaMP facade reports it.</summary>
    internal sealed class LaunchQuote
    {
        public bool Success { get; set; }
        public string Reason { get; set; }
        public double LaunchCost { get; set; }
        public double ToolingCost { get; set; }
        public bool AlreadyTooled { get; set; }
        public string Fingerprint { get; set; }
    }

    /// <summary>The active vessel of the flight scene.</summary>
    internal sealed class LaunchVessel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>True while the vessel is still on the pad (Situations.PRELAUNCH): a freshly launched one.</summary>
        public bool Prelaunch { get; set; }
    }

    /// <summary>
    /// The game-facing half of a launch. Main thread only. The Unity implementation is a thin reflection adapter over the LunaMP
    /// facade and KSP; every decision lives in <see cref="LaunchRunner"/> and <see cref="LaunchService"/>, which are tested against a fake.
    /// </summary>
    internal interface ILaunchPort
    {
        /// <summary>The LunaMP facade is present and reports ApiVersion 2 or later.</summary>
        bool FacadeAvailable { get; }
        /// <summary>HighLogic.LoadedScene as upper-case text: EDITOR, FLIGHT, LOADING, SPACECENTER, ...</summary>
        string Scene { get; }
        /// <summary>The client is connected to an agency server. False for a single-player save.</summary>
        bool Connected { get; }
        /// <summary>The agency quote for the editor craft. Never throws.</summary>
        LaunchQuote Quote();
        /// <summary>The trade and research launch allowance of the editor craft (AgencyTradeResearch.ValidateLive).</summary>
        bool Allowed(out string reason);
        /// <summary>A launch reservation was requested and is not yet registered or cancelled.</summary>
        bool LaunchPending { get; }
        /// <summary>The confirmed server-authoritative balance, or null while unknown.</summary>
        double? Funds { get; }
        /// <summary>The latest tooling status line.</summary>
        string LaunchStatus { get; }
        /// <summary>Counts confirmed launch reservations; <see cref="LastCharge"/> is the latest one's charge.</summary>
        long ChargeSerial { get; }
        double? LastCharge { get; }
        /// <summary>Runs the editor's own launch routine for the site. False with a reason when it could not be invoked.</summary>
        bool BeginLaunch(string site, out string reason);
        /// <summary>Cancels a reservation that has not started loading the flight scene, through the normal cancel flow. False when there is nothing to cancel.</summary>
        bool CancelPendingLaunch();
        /// <summary>The active vessel when the flight scene is ready, otherwise null.</summary>
        LaunchVessel ActiveVessel { get; }
        /// <summary>The vessel has an ownership record owned by this agency (always true for a single-player save).</summary>
        bool VesselOwned(string vesselId);
    }
}
