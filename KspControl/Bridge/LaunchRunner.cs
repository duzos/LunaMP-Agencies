using System;
using KspControl.Contracts;

namespace KspControl.Bridge
{
    /// <summary>The timings of the launch runner. Tests inject a clock instead of shortening them.</summary>
    internal sealed class LaunchOptions
    {
        /// <summary>How long the editor may show no reservation at all (a pre-flight prompt nobody answered, for example) before the job fails.</summary>
        public long NotStartedMilliseconds = 30000;
        /// <summary>The whole wait for reservation, scene change and the registered vessel.</summary>
        public long LaunchTimeoutMilliseconds = 180000;
        /// <summary>How long a cancelled reservation may take to show its refund before the job is indeterminate.</summary>
        public long RefundWaitMilliseconds = 30000;
        public double RefundTolerance = 0.5;
    }

    /// <summary>
    /// The launch state machine, one step per frame on the main thread: validate and invoke the editor's own launch routine, then wait for the
    /// reservation, the scene change to FLIGHT, a new pad vessel owned by this agency and the registration that clears LaunchPending.
    /// A launch that is aborted before the flight scene loads is cancelled through the existing tooling cancel flow and ends only after the
    /// refund shows in the confirmed balance; once the flight scene is loading it cannot be cancelled, so it ends indeterminate. A disconnect
    /// is indeterminate. Nothing here touches a balance, saves a craft or assembles a vessel.
    /// </summary>
    internal sealed class LaunchRunner
    {
        private readonly ILaunchPort port;
        private readonly IEditorPort editor;
        private readonly EditorRevisionTracker tracker;
        private readonly ExecutionAuthority authority;
        private readonly IEditorContextSource context;
        private readonly Func<long> clock;
        private readonly Func<string> worldEpoch;
        private readonly Func<DateTime> utcNow;
        private readonly LaunchOptions options;

        public LaunchRunner(ILaunchPort port, IEditorPort editor, EditorRevisionTracker tracker, ExecutionAuthority authority, IEditorContextSource context,
            Func<long> clock, Func<string> worldEpoch, Func<DateTime> utcNow = null, LaunchOptions options = null)
        {
            this.port = port; this.editor = editor; this.tracker = tracker; this.authority = authority; this.context = context;
            this.clock = clock; this.worldEpoch = worldEpoch; this.utcNow = utcNow ?? (() => DateTime.UtcNow); this.options = options ?? new LaunchOptions();
        }

        public LaunchJob Current { get; private set; }
        public bool Busy { get { return Current != null && !Current.Terminal; } }

        public void Start(LaunchJob job)
        {
            if (Busy) throw new InvalidOperationException("launch_running");
            Current = job; job.BeganAt = clock();
        }

        public void Update()
        {
            var job = Current;
            if (job == null || job.Terminal) return;
            try
            {
                switch (job.Phase)
                {
                    case LaunchPhase.Begin: DoBegin(job); break;
                    case LaunchPhase.Await: DoAwait(job); break;
                    case LaunchPhase.Cancelling: DoCancelling(job); break;
                }
            }
            catch (Exception error) { OnUnexpected(job, error); }
        }

        // ---------------------------------------------------------------- begin

        private void DoBegin(LaunchJob job)
        {
            if (!editor.InEditor) { Fail(job, ControlReasons.EditorUnavailable, "the editor closed before the launch began"); return; }
            try { authority.ValidateForDispatch(job.Ticket, context.CurrentContext(), context.CurrentBinding(), job.Effects); }
            catch (InvalidOperationException error) { Fail(job, MapAuthority(error.Message, job), error.Message); return; }
            if (port.LaunchPending) { Fail(job, OperationReasons.LaunchPending, "a launch reservation is already pending"); return; }
            authority.BeginOperation(job.Ticket, job.RequestId); job.AuthorityOperation = true;
            authority.BeginTransition(job.Ticket);
            tracker.BeginOperation(); job.TrackerOperation = true;
            job.ChargeSerialBefore = port.ChargeSerial;
            job.ConnectedAtStart = port.Connected;
            string reason = null; bool ok; bool threw = false;
            tracker.BeginDispatch();
            try { ok = port.BeginLaunch(job.Site, out reason); }
            catch (Exception error) { ok = false; threw = true; reason = error.GetType().Name; }
            finally { tracker.EndDispatch(); }
            if (!ok)
            {
                // A refusal reported by the facade acted on nothing; an exception inside the launch routine may have.
                if (threw) { job.Dispatched = true; Indeterminate(job, OperationReasons.OperationError, "the launch routine threw " + reason); return; }
                Fail(job, MapRefusal(reason), reason);
                return;
            }
            job.Dispatched = true; job.BeganAt = clock(); job.SawPending = port.LaunchPending;
            job.Phase = LaunchPhase.Await; Touch(job);
        }

        private static string MapRefusal(string reason)
        {
            switch (reason)
            {
                case "launch_pending": return OperationReasons.LaunchPending;
                case "launch_site_invalid": return OperationReasons.LaunchSiteInvalid;
                case "editor_unavailable": return ControlReasons.EditorUnavailable;
                default: return OperationReasons.LaunchRefused;
            }
        }

        // ---------------------------------------------------------------- await

        private void DoAwait(LaunchJob job)
        {
            var now = clock();
            if (!authority.TicketCurrent(job.Ticket)) { Abort(job, AuthorityReason(job)); return; }
            if (job.ConnectedAtStart && !port.Connected) { Indeterminate(job, OperationReasons.Disconnected, "the connection to the agency server ended while the launch was in progress"); return; }
            var scene = port.Scene; var pending = port.LaunchPending;
            if (pending) job.SawPending = true;
            if (scene == "FLIGHT")
            {
                var vessel = port.ActiveVessel;
                if (vessel != null && vessel.Prelaunch && !pending && !string.IsNullOrEmpty(vessel.Id) && port.VesselOwned(vessel.Id)) { Complete(job, vessel); return; }
            }
            else if (scene == "EDITOR")
            {
                if (job.SawPending && !pending) { WaitForRefund(job, JobStatuses.Failed, OperationReasons.LaunchRejected, port.LaunchStatus ?? "the launch reservation ended without a flight scene"); return; }
                if (!job.SawPending && now - job.BeganAt > options.NotStartedMilliseconds)
                {
                    Fail(job, OperationReasons.LaunchNotStarted, "no launch reservation began; a pre-flight prompt may be waiting for the human");
                    return;
                }
            }
            else if (scene != "LOADING" && scene != "LOADINGBUFFER")
            {
                Indeterminate(job, OperationReasons.SceneChanged, "the game left the editor for " + scene + " without reaching a launched vessel");
                return;
            }
            if (now - job.BeganAt > options.LaunchTimeoutMilliseconds) Abort(job, OperationReasons.LaunchTimeout);
        }

        // ---------------------------------------------------------------- abort and cancel

        /// <summary>Stops a launch that has not produced a vessel. Before the flight scene loads the reservation is cancelled through the normal flow.</summary>
        private void Abort(LaunchJob job, string reason)
        {
            if (port.Scene != "EDITOR") { Indeterminate(job, reason, "the flight scene had already started loading, so the reservation cannot be cancelled"); return; }
            if (port.LaunchPending)
            {
                if (!port.CancelPendingLaunch()) { Indeterminate(job, reason, "the reservation could not be cancelled"); return; }
                job.SawPending = true;
                WaitForRefund(job, JobStatuses.Cancelled, reason, null);
                return;
            }
            if (!job.SawPending) { Finish(job, JobStatuses.Cancelled, reason, null); return; }
            WaitForRefund(job, JobStatuses.Cancelled, reason, null);
        }

        private void WaitForRefund(LaunchJob job, string status, string reason, string detail)
        {
            job.PendingStatus = status; job.PendingReason = reason; job.PendingDetail = detail;
            job.Phase = LaunchPhase.Cancelling; job.CancelStartedAt = clock(); Touch(job);
        }

        private void DoCancelling(LaunchJob job)
        {
            if (job.ConnectedAtStart && !port.Connected) { Indeterminate(job, OperationReasons.Disconnected, "the connection to the agency server ended before the refund was confirmed"); return; }
            if (port.Scene != "EDITOR")
            {
                Indeterminate(job, OperationReasons.SceneChanged, "the launch went ahead while it was being cancelled");
                return;
            }
            if (!port.LaunchPending)
            {
                var funds = port.Funds;
                if (job.FundsBefore == null || (funds.HasValue && funds.Value >= job.FundsBefore.Value - options.RefundTolerance))
                {
                    job.RefundConfirmed = true;
                    Finish(job, job.PendingStatus, job.PendingReason, job.PendingDetail);
                    return;
                }
            }
            if (clock() - job.CancelStartedAt > options.RefundWaitMilliseconds)
                Indeterminate(job, OperationReasons.RefundUnconfirmed, "the refund of the launch reservation did not show in the confirmed balance");
        }

        // ---------------------------------------------------------------- outcomes

        private void Complete(LaunchJob job, LaunchVessel vessel)
        {
            job.VesselId = vessel.Id; job.VesselName = vessel.Name;
            job.FundsAfter = port.Funds;
            var charge = port.LastCharge;
            if (port.ChargeSerial > job.ChargeSerialBefore && charge.HasValue) { job.Charge = charge.Value; job.ChargeSource = "tooling_result"; }
            else { job.Charge = job.Quote == null ? 0 : job.Quote.LaunchCost; job.ChargeSource = "quote_fallback"; }
            var entity = "vessel:" + vessel.Id;
            var continues = false;
            try { continues = authority.FinishTransition(job.Ticket, entity); } catch (InvalidOperationException) { continues = false; }
            job.LeaseContinues = continues; job.LeaseReleased = !continues; job.LeaseEntity = continues ? entity : null;
            job.EndEpoch = worldEpoch();
            Finish(job, JobStatuses.Completed, null, null);
        }

        private void Fail(LaunchJob job, string reason, string detail) { Finish(job, JobStatuses.Failed, reason, detail); }

        private void Indeterminate(LaunchJob job, string reason, string detail) { Finish(job, JobStatuses.Indeterminate, reason, detail); }

        private void Finish(LaunchJob job, string status, string reason, string detail)
        {
            if (job.Terminal) return;
            if (status != JobStatuses.Completed)
            {
                // The scene did not change (the lease stays) or it did (the lease is released).
                try { authority.FinishTransition(job.Ticket, null); } catch (InvalidOperationException) { }
            }
            try { if (job.AuthorityOperation) authority.EndOperation(job.RequestId); } catch (Exception) { }
            job.AuthorityOperation = false;
            try { if (job.TrackerOperation) tracker.EndOperation(); } catch (Exception) { }
            job.TrackerOperation = false;
            job.Status = status; job.ReasonCode = reason; job.Detail = detail ?? job.Detail;
            if (status != JobStatuses.Completed && job.EndEpoch == null) job.EndEpoch = worldEpoch();
            job.Phase = LaunchPhase.Done; job.CompletedUtc = utcNow(); job.UpdatedUtc = job.CompletedUtc.Value;
        }

        private void OnUnexpected(LaunchJob job, Exception error)
        {
            var detail = error.GetType().Name;
            if (!job.Dispatched) Fail(job, OperationReasons.OperationError, detail);
            else Indeterminate(job, OperationReasons.OperationError, detail);
        }

        private string AuthorityReason(LaunchJob job)
        {
            if (authority.Status().Grant.State == GrantStates.Suspended) return ControlReasons.GrantSuspended;
            var reason = authority.LeaseFailureReason(job.LeaseId);
            return reason == ControlReasons.LeaseInvalid ? ControlReasons.AuthorityRevoked : reason;
        }

        private string MapAuthority(string code, LaunchJob job)
        {
            switch (code)
            {
                case "stale_context": case "stale_revision": return OperationReasons.StaleRevision;
                case "effect_denied_or_unknown": return OperationReasons.GrantOperationDenied;
                case "clock_regressed": return ControlReasons.ClockRegressed;
                case "authority_unavailable": return AuthorityReason(job);
                default: return ControlReasons.AuthorityRevoked;
            }
        }

        private void Touch(LaunchJob job) { job.UpdatedUtc = utcNow(); }
    }
}
