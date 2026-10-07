using System;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// Admission and status for editor.launch. Runs on the main thread inside the queued observation drain. It either refuses with a reason
    /// and a no-effect envelope or registers a job for <see cref="LaunchRunner"/> and answers running. The admission checks, in order: arguments,
    /// lease, request-id dedupe, the common editor mutation checks (authority, fresh editor revision, idle editor), the facade, the launch
    /// allowance, no pending reservation, the agency quote against the per-call spend ceiling, and the confirmed balance.
    /// </summary>
    internal sealed class LaunchService
    {
        private readonly ILaunchPort port;
        private readonly IEditorPort editor;
        private readonly LaunchRunner runner;
        private readonly LaunchJobs jobs;
        private readonly EditorOperationService common;
        private readonly Func<string> worldEpoch;
        private readonly Func<DateTime> utcNow;

        public LaunchService(ILaunchPort port, IEditorPort editor, LaunchRunner runner, LaunchJobs jobs, EditorOperationService common, Func<string> worldEpoch, Func<DateTime> utcNow = null)
        {
            this.port = port; this.editor = editor; this.runner = runner; this.jobs = jobs; this.common = common; this.worldEpoch = worldEpoch;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>True when this service holds a job for the request carried by an editor.operation_status request.</summary>
        public bool Owns(BridgeRequest request)
        {
            var args = request.Arguments;
            var token = args == null ? null : args["requestId"];
            return token != null && token.Type == JTokenType.String && jobs.Get((string)token) != null;
        }

        public BridgeResponse Status(BridgeRequest request)
        {
            var job = jobs.Get((string)request.Arguments["requestId"]);
            return job == null ? Fail(request, OperationReasons.JobUnknown, new JObject { ["detail"] = "this bridge session has no such job" }) : Envelope(request, job);
        }

        public BridgeResponse Handle(BridgeRequest request)
        {
            if (request.Operation != EditorOperations.Launch) return Refuse(request, ControlReasons.OperationUnavailable, null);
            var args = request.Arguments ?? new JObject();
            var problem = Arguments(args, out var requestId, out var site, out var maxSpend, out var revision);
            if (problem != null) return Refuse(request, ControlReasons.InvalidArgument, problem);
            if (string.IsNullOrEmpty(request.LeaseId)) return Refuse(request, ControlReasons.LeaseRequired, null);
            if (!ControlLimits.IsLeaseId(request.LeaseId)) return Refuse(request, ControlReasons.LeaseInvalid, null);
            var leaseId = request.LeaseId.ToLowerInvariant();
            var fingerprint = OperationHash.Sha256Hex("launch|" + leaseId + "|" + site + "|" + maxSpend + "|" + revision);
            var existing = jobs.Get(requestId);
            if (existing != null)
                return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal) ? Envelope(request, existing) : Refuse(request, OperationReasons.RequestIdConflict, "this requestId was used for a different request");

            if (runner.Busy) return Refuse(request, OperationReasons.EditorBusy, "another launch is running", new JObject { ["busy"] = new JArray("launch_running") });
            ExecutionTicket ticket; ClassifiedEffect[] effects;
            var refusal = common.AdmitCommon(request, OperationEffects.Launch, leaseId, revision, out ticket, out effects);
            if (refusal != null) return refusal;
            if (!port.FacadeAvailable) return Refuse(request, OperationReasons.FacadeUnavailable, "the LunaMP control facade (ApiVersion 2) is not loaded");
            string why;
            if (!port.Allowed(out why)) return Refuse(request, OperationReasons.LaunchNotAllowed, why);
            if (port.LaunchPending) return Refuse(request, OperationReasons.LaunchPending, "a launch reservation is already pending");
            var quote = port.Quote();
            if (quote == null || !quote.Success) return Refuse(request, OperationReasons.QuoteUnavailable, quote == null ? "no quote" : quote.Reason);
            if (double.IsNaN(quote.LaunchCost) || double.IsInfinity(quote.LaunchCost) || quote.LaunchCost < 0) return Refuse(request, OperationReasons.QuoteUnavailable, "the quote is not a finite cost");
            var quoted = new JObject { ["launchCost"] = quote.LaunchCost, ["toolingCost"] = quote.ToolingCost, ["alreadyTooled"] = quote.AlreadyTooled, ["fingerprint"] = quote.Fingerprint };
            // The host reserved maxSpendFunds before asking; a quote above it is refused here, so the grant's cap is never exceeded.
            if (quote.LaunchCost > maxSpend) return Refuse(request, OperationReasons.SpendExceedsMax, "the launch costs " + quote.LaunchCost.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " funds, above maxSpendFunds " + maxSpend, new JObject { ["quote"] = quoted });
            var funds = port.Funds;
            if (quote.LaunchCost > 0 && !funds.HasValue) return Refuse(request, OperationReasons.EconomyNotReady, "the confirmed agency balance is not available yet", new JObject { ["quote"] = quoted });
            if (funds.HasValue && funds.Value < quote.LaunchCost) return Refuse(request, OperationReasons.InsufficientFunds, "the agency balance is below the launch cost", new JObject { ["quote"] = quoted });

            var job = new LaunchJob
            {
                RequestId = requestId, Fingerprint = fingerprint, LeaseId = leaseId, Site = site, MaxSpendFunds = maxSpend, PreviousEditorRevision = revision,
                Ticket = ticket, Effects = effects, Quote = quote, FundsBefore = funds, CreatedUtc = utcNow(), UpdatedUtc = utcNow()
            };
            jobs.Add(job);
            runner.Start(job);
            return Envelope(request, job);
        }

        private static string Arguments(JObject args, out string requestId, out string site, out long maxSpend, out string revision)
        {
            requestId = null; site = null; maxSpend = 0; revision = null;
            string text;
            var problem = Text(args, "requestId", out text);
            if (problem != null) return problem;
            requestId = text;
            if (!OperationLimits.IsRequestId(requestId)) return "requestId must match [A-Za-z0-9_-]{8,128}";
            problem = Text(args, "launchSite", out text);
            if (problem != null) return problem;
            site = text;
            if (!OperationLimits.IsLaunchSite(site)) return "launchSite must be 1.." + OperationLimits.LaunchSiteMax + " printable characters";
            problem = Text(args, "expectedRevision", out text);
            if (problem != null) return problem;
            revision = text;
            if (revision.Length > OperationLimits.ExpectedRevisionMax) return "expectedRevision is too long";
            var spend = args["maxSpendFunds"];
            if (spend == null || spend.Type != JTokenType.Integer) return "maxSpendFunds is required and must be an integer";
            long value;
            try { value = (long)spend; } catch (OverflowException) { return "maxSpendFunds is out of range"; }
            if (value < 0 || value > OperationLimits.MaxSpendFunds) return "maxSpendFunds must be 0.." + OperationLimits.MaxSpendFunds;
            maxSpend = value;
            return null;
        }

        private static string Text(JObject args, string name, out string value)
        {
            value = null;
            var token = args[name];
            if (token == null || token.Type != JTokenType.String) return name + " is required and must be a string";
            value = (string)token;
            return string.IsNullOrEmpty(value) ? name + " must not be empty" : null;
        }

        private BridgeResponse Envelope(BridgeRequest request, LaunchJob job)
        {
            return new BridgeResponse
            {
                RequestId = request.RequestId, Status = job.Status, ReasonCode = job.Terminal && job.Status != JobStatuses.Completed ? job.ReasonCode : null,
                WorldEpoch = worldEpoch(), Data = job.ToEnvelope()
            };
        }

        private BridgeResponse Refuse(BridgeRequest request, string reason, string detail, JObject extra = null)
        {
            var data = new JObject { ["operation"] = "launch", ["phase"] = "quote", ["notDispatched"] = true, ["dispatched"] = false };
            var args = request.Arguments;
            if (args != null && args["requestId"] != null && args["requestId"].Type == JTokenType.String) data["requestId"] = (string)args["requestId"];
            if (detail != null) data["detail"] = detail;
            if (extra != null) data.Merge(extra);
            return Fail(request, reason, data);
        }

        private BridgeResponse Fail(BridgeRequest request, string reason, JObject data)
        {
            return new BridgeResponse { RequestId = request.RequestId, Status = JobStatuses.Failed, ReasonCode = reason, WorldEpoch = worldEpoch(), Data = data ?? new JObject() };
        }
    }
}
