using System;
using Newtonsoft.Json;

namespace KspControl.Contracts
{
    /// <summary>Inline control operations. These never reach the main-thread queue.</summary>
    public static class ControlOperations
    {
        public const string Prefix = "control.";
        public const string Acquire = "control.acquire";
        public const string Renew = "control.renew";
        public const string Release = "control.release";
        public const string Heartbeat = "control.heartbeat";
        public const string Status = "control.status";
        public static readonly string[] All = { Acquire, Renew, Release, Heartbeat, Status };
        public static bool IsControl(string operation) => operation != null && operation.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>Single source for control argument bounds shared by host validation and bridge checks.</summary>
    public static class ControlLimits
    {
        public const int PurposeMin = 1;
        public const int PurposeMax = 128;
        public const int DurationMinSeconds = 30;
        public const int DurationMaxSeconds = 300;
        public const int LeaseIdLength = 32;
        public const int CooldownSeconds = 30;
        public const int WatchdogMilliseconds = 2000;
        public const int HeartbeatIntervalMilliseconds = 1000;
        public const int HeartbeatTimeoutMilliseconds = 800;
        public const int HeartbeatFailureThreshold = 3;
        public const int ContextStaleMilliseconds = 1000;
        public const int MaxQueueWaiters = 3;
        public const int MaxOpenSockets = 32;

        public static bool IsLeaseId(string value)
        {
            if (value == null || value.Length != LeaseIdLength) return false;
            foreach (var c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }
    }

    /// <summary>Reason codes produced by the control path.</summary>
    public static class ControlReasons
    {
        public const string InvalidArgument = "invalid_argument";
        public const string GrantMissing = "grant_missing";
        public const string GrantMalformed = "grant_malformed";
        public const string GrantInvalidMac = "grant_invalid_mac";
        public const string GrantExpired = "grant_expired";
        public const string GrantRevoked = "grant_revoked";
        public const string GrantSuspended = "grant_suspended";
        public const string GrantBindingMismatch = "grant_binding_mismatch";
        public const string GrantNotYetApplicable = "grant_not_yet_applicable";
        public const string LeaseRequired = "lease_required";
        public const string LeaseInvalid = "lease_invalid";
        public const string LeaseExpired = "lease_expired";
        public const string ControlBusy = "control_busy";
        public const string HumanActivityCooldown = "human_activity_cooldown";
        public const string AuthorityRevoked = "authority_revoked";
        public const string EditorUnavailable = "editor_unavailable";
        public const string FacilityMismatch = "facility_mismatch";
        public const string BridgeBusy = "bridge_busy";
        public const string ClockRegressed = "clock_regressed";
        public const string OperationUnavailable = "operation_unavailable";

        /// <summary>Maps a grant state (other than valid) to its reason code.</summary>
        public static string ForGrantState(string state)
        {
            switch (state)
            {
                case GrantStates.Malformed: return GrantMalformed;
                case GrantStates.InvalidMac: return GrantInvalidMac;
                case GrantStates.Expired: return GrantExpired;
                case GrantStates.Revoked: return GrantRevoked;
                case GrantStates.Suspended: return GrantSuspended;
                case GrantStates.BindingMismatch: return GrantBindingMismatch;
                case GrantStates.NotYetApplicable: return GrantNotYetApplicable;
                default: return GrantMissing;
            }
        }
    }

    /// <summary>What a tool or panel may learn about the grant. Never contains the payload, MAC or key.</summary>
    public sealed class GrantStatusInfo
    {
        [JsonProperty("present")] public bool Present { get; set; }
        [JsonProperty("state")] public string State { get; set; } = GrantStates.Missing;
        [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)] public string Detail { get; set; }
        [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)] public string Id { get; set; }
        [JsonProperty("generation", NullValueHandling = NullValueHandling.Ignore)] public long? Generation { get; set; }
        [JsonProperty("operations", NullValueHandling = NullValueHandling.Ignore)] public string[] Operations { get; set; }
        [JsonProperty("facilities", NullValueHandling = NullValueHandling.Ignore)] public string[] Facilities { get; set; }
        [JsonProperty("unsavedCraftPolicy", NullValueHandling = NullValueHandling.Ignore)] public string UnsavedCraftPolicy { get; set; }
        [JsonProperty("expiresUtc", NullValueHandling = NullValueHandling.Ignore)] public string ExpiresUtc { get; set; }
        /// <summary>The gross funds this grant may spend across all jobs and retries, fixed when the human issued it.</summary>
        [JsonProperty("spendLimitFunds", NullValueHandling = NullValueHandling.Ignore)] public long? SpendLimitFunds { get; set; }

        /// <summary>The live spend cap actually in force: min(spendLimitFunds, 100000, 25% of the confirmed balance at the grant's first acquire), fixed per grant generation. Absent until a lease was acquired with the balance known.</summary>
        [JsonProperty("effectiveSpendCap", NullValueHandling = NullValueHandling.Ignore)] public long? EffectiveSpendCap { get; set; }

        public static GrantStatusInfo Missing(string detail = null) => new GrantStatusInfo { State = GrantStates.Missing, Detail = detail };
    }

    public sealed class LeaseStatusInfo
    {
        [JsonProperty("held")] public bool Held { get; set; }
        [JsonProperty("purpose", NullValueHandling = NullValueHandling.Ignore)] public string Purpose { get; set; }
        [JsonProperty("expiresInSeconds", NullValueHandling = NullValueHandling.Ignore)] public long? ExpiresInSeconds { get; set; }
    }

    public sealed class ControlStatusInfo
    {
        [JsonProperty("grant")] public GrantStatusInfo Grant { get; set; } = GrantStatusInfo.Missing();
        [JsonProperty("lease")] public LeaseStatusInfo Lease { get; set; } = new LeaseStatusInfo();
        [JsonProperty("cooldownSeconds")] public long CooldownSeconds { get; set; }
        /// <summary>True when a Stop or suspension could not be written, so it may not survive a game restart.</summary>
        [JsonProperty("stopPersistFailed")] public bool StopPersistFailed { get; set; }
    }
}
