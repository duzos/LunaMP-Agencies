using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>The phases of a launch job as the envelope reports them. The host's own reservation precedes <see cref="Begin"/>.</summary>
    internal enum LaunchPhase { Begin, Await, Cancelling, Done }

    /// <summary>
    /// One launch. Created at admission (the quote step), advanced once per frame by <see cref="LaunchRunner"/> and read by
    /// editor.operation_status. A bridge restart loses it, which the host journal turns into "indeterminate".
    /// </summary>
    internal sealed class LaunchJob
    {
        public string RequestId { get; set; }
        public string Fingerprint { get; set; }
        public string LeaseId { get; set; }
        public string Site { get; set; }
        public long MaxSpendFunds { get; set; }
        public string PreviousEditorRevision { get; set; }
        public ExecutionTicket Ticket { get; set; }
        public ClassifiedEffect[] Effects { get; set; }
        public LaunchQuote Quote { get; set; }
        public string Status { get; set; } = JobStatuses.Running;
        public LaunchPhase Phase { get; set; } = LaunchPhase.Begin;
        public string ReasonCode { get; set; }
        public string Detail { get; set; }
        /// <summary>True once the editor's launch routine was invoked. A terminal job with this false changed nothing.</summary>
        public bool Dispatched { get; set; }
        public double? FundsBefore { get; set; }
        public double? FundsAfter { get; set; }
        public double? Charge { get; set; }
        /// <summary>tooling_result (the server's confirmed reservation) or quote_fallback.</summary>
        public string ChargeSource { get; set; }
        public bool? RefundConfirmed { get; set; }
        public string VesselId { get; set; }
        public string VesselName { get; set; }
        public bool LeaseContinues { get; set; }
        public bool LeaseReleased { get; set; }
        public string LeaseEntity { get; set; }
        /// <summary>The world epoch after the scene change, which the host adopts for a continuing lease.</summary>
        public string EndEpoch { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public bool Terminal { get { return JobStatuses.IsTerminal(Status); } }

        // ---- runner-only state ----
        internal long BeganAt, CancelStartedAt;
        internal long ChargeSerialBefore;
        internal bool SawPending, ConnectedAtStart, AuthorityOperation, TrackerOperation;
        internal string PendingStatus, PendingReason, PendingDetail;

        public static string PhaseName(LaunchPhase phase)
        {
            switch (phase)
            {
                case LaunchPhase.Begin: return "begin_launch";
                case LaunchPhase.Await: return "await_flight";
                case LaunchPhase.Cancelling: return "cancelling";
                default: return "done";
            }
        }

        private static JToken Number(double? value) { return value.HasValue ? (JToken)new JValue(value.Value) : JValue.CreateNull(); }
        private static JToken Text(string value) { return value == null ? JValue.CreateNull() : (JToken)value; }
        private static string Format(DateTime value) { return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }

        public JObject ToEnvelope()
        {
            var envelope = new JObject
            {
                ["operation"] = "launch",
                ["requestId"] = RequestId,
                ["phase"] = PhaseName(Phase),
                ["notDispatched"] = Terminal && !Dispatched,
                ["dispatched"] = Dispatched,
                ["launchSite"] = Site,
                ["maxSpendFunds"] = MaxSpendFunds,
                ["previousEditorRevision"] = Text(PreviousEditorRevision),
                ["quote"] = Quote == null ? JValue.CreateNull() : (JToken)new JObject
                {
                    ["launchCost"] = Quote.LaunchCost, ["toolingCost"] = Quote.ToolingCost, ["alreadyTooled"] = Quote.AlreadyTooled, ["fingerprint"] = Text(Quote.Fingerprint)
                },
                ["charge"] = Terminal ? Number(Charge) : JValue.CreateNull(),
                ["chargeSource"] = Terminal ? Text(ChargeSource) : JValue.CreateNull(),
                ["fundsBefore"] = Number(FundsBefore),
                ["fundsAfter"] = Terminal ? Number(FundsAfter) : JValue.CreateNull(),
                ["refundConfirmed"] = RefundConfirmed.HasValue ? (JToken)new JValue(RefundConfirmed.Value) : JValue.CreateNull(),
                ["vesselId"] = Text(VesselId),
                ["vesselName"] = Text(VesselName),
                ["leaseContinues"] = LeaseContinues,
                ["leaseReleased"] = LeaseReleased,
                ["leaseEntity"] = Text(LeaseEntity),
                ["epoch"] = Terminal ? Text(EndEpoch) : JValue.CreateNull(),
                ["startedUtc"] = Format(CreatedUtc), ["updatedUtc"] = Format(UpdatedUtc),
                ["completedUtc"] = CompletedUtc.HasValue ? (JToken)Format(CompletedUtc.Value) : JValue.CreateNull()
            };
            if (Detail != null) envelope["detail"] = Detail;
            return envelope;
        }
    }

    /// <summary>In-memory launch registry: dedupe by request id with a bounded history.</summary>
    internal sealed class LaunchJobs
    {
        private const int MaxJobs = 16;
        private readonly List<LaunchJob> jobs = new List<LaunchJob>();
        private readonly object gate = new object();

        public LaunchJob Get(string requestId) { lock (gate) return jobs.FirstOrDefault(j => j.RequestId == requestId); }

        public void Add(LaunchJob job)
        {
            lock (gate)
            {
                jobs.Add(job);
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
