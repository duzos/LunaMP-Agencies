using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>What editor.state reports about snapshots and the last operation. Read-only.</summary>
    internal interface IOperationSummary
    {
        JArray RecentSnapshots();
        JToken LastOperation();
        /// <summary>True from admission until the job reaches a terminal state.</summary>
        bool OperationRunning { get; }
    }

    /// <summary>
    /// Admission and status for the mutation operations (plan R3-section 6.4, R1-section 4). Runs on the main thread inside the queued
    /// observation drain, in one frame: it either refuses with a reason and a no-effect envelope, or registers a job, hands it to the
    /// runner and answers <c>running</c>. The long work happens in <see cref="EditorOperationRunner"/>, one step per frame.
    /// </summary>
    internal sealed partial class EditorOperationService : IOperationSummary
    {
        private readonly IEditorPort port;
        private readonly EditorRevisionTracker tracker;
        private readonly ExecutionAuthority authority;
        private readonly EditorOperationRunner runner;
        private readonly OperationJobs jobs;
        private readonly Func<ICatalogPartReader> readerFactory;
        private readonly Func<Pure.CraftPaths> pathsFactory;
        private readonly IOperationFiles files;
        private readonly Func<string> worldEpoch;
        private readonly Func<DateTime> utcNow;
        private readonly Func<string> newSnapshotId;
        private readonly Func<ConstructionSupportPolicy> policyFactory;

        public EditorOperationService(IEditorPort port, EditorRevisionTracker tracker, ExecutionAuthority authority, EditorOperationRunner runner, OperationJobs jobs,
            Func<ICatalogPartReader> readerFactory, Func<Pure.CraftPaths> pathsFactory, IOperationFiles files, Func<string> worldEpoch, Func<DateTime> utcNow = null, Func<string> newSnapshotId = null,
            Func<ConstructionSupportPolicy> policyFactory = null)
        {
            this.policyFactory = policyFactory;
            this.port = port; this.tracker = tracker; this.authority = authority; this.runner = runner; this.jobs = jobs;
            this.readerFactory = readerFactory; this.pathsFactory = pathsFactory; this.files = files; this.worldEpoch = worldEpoch;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow); this.newSnapshotId = newSnapshotId;
        }

        public BridgeResponse Handle(BridgeRequest request)
        {
            switch (request.Operation)
            {
                case EditorOperations.ApplyCraft: return Apply(request);
                case EditorOperations.RestoreSnapshot: return Restore(request);
                case EditorOperations.SaveCraft: return Save(request);
                case EditorOperations.LoadCraft: return LoadCraft(request);
                case EditorOperations.OperationStatus: return Status(request);
                default: return Refuse(request, ControlReasons.OperationUnavailable, null);
            }
        }

        // ---------------------------------------------------------------- editor.apply_craft

        private BridgeResponse Apply(BridgeRequest request)
        {
            var args = request.Arguments ?? new JObject();
            string requestId, graph, planHash, revision;
            var problem = First(Text(args, "requestId", out requestId), Text(args, "graph", out graph), Text(args, "expectedPlanHash", out planHash), Text(args, "expectedRevision", out revision));
            if (problem == null && !OperationLimits.IsRequestId(requestId)) problem = "requestId must match [A-Za-z0-9_-]{8,128}";
            if (problem == null && System.Text.Encoding.UTF8.GetByteCount(graph) > ConstructionLimits.MaxGraphBytes) problem = "graph must be at most " + ConstructionLimits.MaxGraphBytes + " bytes";
            if (problem == null && !OperationLimits.IsPlanHash(planHash)) problem = "expectedPlanHash must be 64 lower-case hex characters";
            if (problem == null && revision.Length > OperationLimits.ExpectedRevisionMax) problem = "expectedRevision is too long";
            if (problem != null) return Refuse(request, ControlReasons.InvalidArgument, problem);
            var lease = LeaseOf(request);
            if (lease.Reason != null) return Refuse(request, lease.Reason, null);
            var fingerprint = OperationHash.Sha256Hex("apply|" + lease.Id + "|" + planHash + "|" + revision + "|" + graph);
            var existing = Existing(request, requestId, fingerprint);
            if (existing != null) return existing;

            ExecutionTicket ticket; ClassifiedEffect[] effects;
            var refusal = AdmitCommon(request, OperationEffects.ReplaceCraft, lease.Id, revision, out ticket, out effects);
            if (refusal != null) return refusal;

            string parseError;
            var parsed = GraphJson.Parse(graph, out parseError);
            if (parsed == null) return Refuse(request, ControlReasons.InvalidArgument, parseError);
            if (!string.Equals(parsed.Facility, port.Facility, StringComparison.Ordinal)) return Refuse(request, ControlReasons.FacilityMismatch, "graph facility " + parsed.Facility + " but the editor is " + port.Facility);

            // Unsaved-craft policy: the human's unsaved work is never replaced under "refuse". A craft this service generated and nobody touched since is not human work.
            var capabilities = port.Capabilities ?? EditorCapabilities.None();
            var policyRefusal = UnsavedCraftGate(request, capabilities);
            if (policyRefusal != null) return policyRefusal;
            if (!capabilities.SaveOverwriteGuard && CollidesWithShipFile(parsed.Name))
                return Refuse(request, OperationReasons.NameCollisionUnguarded, "a ship file with this name exists and the save-name guard is unavailable");

            var header = tracker.Guarded(() => port.ReadHeader());
            var plan = ApplyPlanner.Prepare(graph, readerFactory(), header, port.ReadUi(), () => port.NextPersistentId(), port.Facility, policyFactory == null ? null : policyFactory());
            if (!plan.Ok)
            {
                var data = new JObject { ["issues"] = ApplyPlanner.IssuesJson(plan.Issues) };
                return Refuse(request, plan.Reason, plan.Detail, data);
            }
            if (!string.Equals(plan.PlanHash, planHash, StringComparison.Ordinal))
                return Refuse(request, OperationReasons.PlanChanged, "the plan now hashes differently: the catalog, the research state or the graph changed since craft_plan", new JObject { ["planHash"] = plan.PlanHash, ["catalogHash"] = plan.CatalogHash });

            var job = NewJob(request, OperationKind.Apply, requestId, fingerprint, lease.Id, revision, ticket, effects);
            job.Plan = plan;
            job.ModVersions = header != null && !string.IsNullOrEmpty(header.ModVersions) ? "copied_from_live_header" : "unavailable";
            return StartJob(request, job);
        }

        /// <summary>The unsaved-craft policy shared by every replacing mutation: unsaved human work is never replaced under "refuse".</summary>
        private BridgeResponse UnsavedCraftGate(BridgeRequest request, EditorCapabilities capabilities)
        {
            var nonEmpty = port.PartCount > 0;
            var unsaved = port.Unsaved;
            if (!nonEmpty || unsaved == false || IsOwnUntouchedCraft()) return null;
            var policy = authority.Status().Grant.UnsavedCraftPolicy ?? "refuse";
            if (policy != "snapshot_then_replace") return Refuse(request, OperationReasons.UnsavedHumanCraft, "the editor holds unsaved work and the grant policy is " + policy);
            if (!capabilities.SaveOverwriteGuard) return Refuse(request, OperationReasons.SaveOverwriteGuardUnavailable, "snapshot_then_replace needs the save-name guard");
            return null;
        }

        // ---------------------------------------------------------------- editor.restore_snapshot

        private BridgeResponse Restore(BridgeRequest request)
        {
            var args = request.Arguments ?? new JObject();
            string requestId, snapshotId, revision;
            var problem = First(Text(args, "requestId", out requestId), Text(args, "snapshotId", out snapshotId), Text(args, "expectedRevision", out revision));
            if (problem == null && !OperationLimits.IsRequestId(requestId)) problem = "requestId must match [A-Za-z0-9_-]{8,128}";
            if (problem == null && !OperationLimits.IsSnapshotId(snapshotId)) problem = "snapshotId must match [A-Za-z0-9_-]{8,128}";
            if (problem == null && revision.Length > OperationLimits.ExpectedRevisionMax) problem = "expectedRevision is too long";
            if (problem != null) return Refuse(request, ControlReasons.InvalidArgument, problem);
            var lease = LeaseOf(request);
            if (lease.Reason != null) return Refuse(request, lease.Reason, null);
            var fingerprint = OperationHash.Sha256Hex("restore|" + lease.Id + "|" + snapshotId + "|" + revision);
            var existing = Existing(request, requestId, fingerprint);
            if (existing != null) return existing;

            ExecutionTicket ticket; ClassifiedEffect[] effects;
            var refusal = AdmitCommon(request, OperationEffects.RestoreSnapshot, lease.Id, revision, out ticket, out effects);
            if (refusal != null) return refusal;

            Pure.CraftPaths paths;
            try { paths = pathsFactory(); } catch (ArgumentException) { return Refuse(request, OperationReasons.PathOutsideSave, "the save folder name is not usable"); }
            var store = new SnapshotStore(files, paths, utcNow, newSnapshotId);
            var record = store.TryGet(snapshotId);
            if (record == null) return Refuse(request, OperationReasons.SnapshotNotFound, null);
            var broken = store.Verify(record);
            if (broken != null) return Refuse(request, OperationReasons.SnapshotUnverified, broken);
            if (!string.Equals(record.Facility, port.Facility, StringComparison.Ordinal)) return Refuse(request, ControlReasons.FacilityMismatch, "the snapshot is a " + record.Facility + " craft");
            string missing;
            if (!port.AllPartsFound(record.CraftPath, out missing)) return Refuse(request, OperationReasons.CraftPartsMissing, missing);
            var policyRefusal = UnsavedCraftGate(request, port.Capabilities ?? EditorCapabilities.None());
            if (policyRefusal != null) return policyRefusal;

            var job = NewJob(request, OperationKind.Restore, requestId, fingerprint, lease.Id, revision, ticket, effects);
            job.RestoreSource = record;
            job.ExpectedParts = record.PartCount;
            job.Restore = new RestoreInfo { Attempted = true, Result = "in_progress" };
            return StartJob(request, job);
        }

        // ---------------------------------------------------------------- editor.operation_status

        private BridgeResponse Status(BridgeRequest request)
        {
            string requestId;
            var problem = Text(request.Arguments ?? new JObject(), "requestId", out requestId);
            if (problem != null || !OperationLimits.IsRequestId(requestId)) return Refuse(request, ControlReasons.InvalidArgument, "requestId must match [A-Za-z0-9_-]{8,128}");
            var job = jobs.Get(requestId);
            if (job == null) return Fail(request, OperationReasons.JobUnknown, new JObject { ["requestId"] = requestId, ["detail"] = "this bridge session has no such job" });
            return Envelope(request, job);
        }

        // ---------------------------------------------------------------- shared admission

        private sealed class LeaseArgument { public string Id, Reason; }

        private static LeaseArgument LeaseOf(BridgeRequest request)
        {
            if (string.IsNullOrEmpty(request.LeaseId)) return new LeaseArgument { Reason = ControlReasons.LeaseRequired };
            if (!ControlLimits.IsLeaseId(request.LeaseId)) return new LeaseArgument { Reason = ControlReasons.LeaseInvalid };
            return new LeaseArgument { Id = request.LeaseId.ToLowerInvariant() };
        }

        /// <summary>The existing job for a request id: the same request is idempotent, a different one with the same id is a conflict.</summary>
        private BridgeResponse Existing(BridgeRequest request, string requestId, string fingerprint)
        {
            var job = jobs.Get(requestId);
            if (job == null) return null;
            if (!string.Equals(job.Fingerprint, fingerprint, StringComparison.Ordinal)) return Refuse(request, OperationReasons.RequestIdConflict, "this requestId was used for a different request");
            return Envelope(request, job);
        }

        /// <summary>Authority, token, busy and context checks common to both mutations. Returns a refusal, or null with the ticket.</summary>
        internal BridgeResponse AdmitCommon(BridgeRequest request, string effectName, string leaseId, string revisionText, out ExecutionTicket ticket, out ClassifiedEffect[] effects, string recipientPrefix = "editor:")
        {
            ticket = null; effects = null;
            if (runner.Busy || (ExtraBusy != null && ExtraBusy())) return Refuse(request, OperationReasons.EditorBusy, "another operation is running", new JObject { ["busy"] = new JArray("operation_running") });
            if (!port.InEditor) return Refuse(request, ControlReasons.EditorUnavailable, null);
            Pure.EditorRevisionToken token;
            if (!Pure.EditorRevisionToken.TryParse(revisionText, out token)) return Refuse(request, ControlReasons.InvalidArgument, "expectedRevision is not a token from editor_state");
            effects = new[] { new ClassifiedEffect(effectName, recipientPrefix + port.Facility, 0) };
            try { ticket = authority.Admit(leaseId, token.EditRevision, effects); }
            catch (InvalidOperationException error) { return Refuse(request, MapAdmission(error.Message, leaseId), error.Message == "authority_unavailable" ? null : error.Message); }
            var check = tracker.CheckToken(revisionText, worldEpoch());
            if (check == TokenCheck.Unavailable) return Refuse(request, ControlReasons.EditorUnavailable, "the craft fingerprint could not be computed");
            if (check != TokenCheck.Fresh) return Refuse(request, OperationReasons.StaleRevision, "the editor changed since editor_state");
            var idle = EditorIdle.Evaluate(port.FsmState, port.HasSelectedPart, port.ActiveLockIds);
            if (!idle.Idle) return Refuse(request, OperationReasons.EditorBusy, string.Join(",", idle.Busy), new JObject { ["busy"] = new JArray(idle.Busy) });
            return null;
        }

        private string MapAdmission(string code, string leaseId)
        {
            switch (code)
            {
                case "authority_unavailable": return authority.LeaseFailureReason(leaseId);
                case "stale_revision": return OperationReasons.StaleRevision;
                case "editor_unavailable": return ControlReasons.EditorUnavailable;
                case "effect_denied_or_unknown": return OperationReasons.GrantOperationDenied;
                case "clock_regressed": return ControlReasons.ClockRegressed;
                default: return ControlReasons.AuthorityRevoked;
            }
        }

        private bool IsOwnUntouchedCraft()
        {
            var own = jobs.LastOperationFingerprint;
            if (own == null) return false;
            var observed = tracker.Observe(false);
            return observed.FingerprintOk && string.Equals(observed.Fingerprint, own, StringComparison.Ordinal);
        }

        private bool CollidesWithShipFile(string shipName)
        {
            try
            {
                var paths = pathsFactory();
                var check = paths.ResolveNewShip(port.Facility, port.SanitizeFileName(shipName));
                return check.Ok ? files.Exists(check.FullPath) : check.ReasonCode != "invalid_file_name"; // a name that cannot be a file cannot collide
            }
            catch (ArgumentException) { return true; }
        }

        private OperationJob NewJob(BridgeRequest request, OperationKind kind, string requestId, string fingerprint, string leaseId, string revision, ExecutionTicket ticket, ClassifiedEffect[] effects)
        {
            return new OperationJob
            {
                RequestId = requestId, Kind = kind, Fingerprint = fingerprint, LeaseId = leaseId, PreviousEditorRevision = revision, Ticket = ticket, Effects = effects,
                CreatedUtc = utcNow(), UpdatedUtc = utcNow()
            };
        }

        private BridgeResponse StartJob(BridgeRequest request, OperationJob job)
        {
            jobs.Add(job);
            runner.Start(job);
            return Envelope(request, job);
        }

        // ---------------------------------------------------------------- responses

        private BridgeResponse Envelope(BridgeRequest request, OperationJob job)
        {
            var response = new BridgeResponse
            {
                RequestId = request.RequestId, Status = job.Status, ReasonCode = job.Terminal && job.Status != JobStatuses.Completed ? job.ReasonCode : null,
                WorldEpoch = worldEpoch(), Data = job.ToEnvelope()
            };
            return response;
        }

        private BridgeResponse Refuse(BridgeRequest request, string reason, string detail, JObject extra = null)
        {
            var data = new JObject { ["phase"] = "admission", ["notDispatched"] = true, ["dispatched"] = false };
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

        private static string First(params string[] problems) { return problems.FirstOrDefault(p => p != null); }

        private static string Text(JObject args, string name, out string value)
        {
            value = null;
            var token = args[name];
            if (token == null || token.Type != JTokenType.String) return name + " is required and must be a string";
            value = (string)token;
            return string.IsNullOrEmpty(value) ? name + " must not be empty" : null;
        }

        // ---------------------------------------------------------------- editor.state extras

        public bool OperationRunning { get { return runner.Busy || (ExtraBusy != null && ExtraBusy()); } }

        /// <summary>Another operation family (a launch) that also owns the editor. Admission refuses while it reports busy.</summary>
        internal Func<bool> ExtraBusy { get; set; }

        public JArray RecentSnapshots()
        {
            var result = new JArray();
            try
            {
                var store = new SnapshotStore(files, pathsFactory(), utcNow, newSnapshotId);
                foreach (var record in store.Recent(10))
                    result.Add(new JObject
                    {
                        ["id"] = record.SnapshotId, ["createdUtc"] = record.CreatedUtc, ["shipName"] = record.UiName,
                        ["wasUnsaved"] = record.WasUnsaved == "true" ? new JValue(true) : record.WasUnsaved == "false" ? new JValue(false) : new JValue("unknown"), ["partCount"] = record.PartCount
                    });
            }
            catch (Exception) { /* a missing workspace or an unusable save folder simply lists nothing */ }
            return result;
        }

        public JToken LastOperation()
        {
            var job = jobs.LastFinished;
            var running = runner.Current != null && !runner.Current.Terminal ? runner.Current : null;
            var shown = running ?? job;
            if (shown == null) return JValue.CreateNull();
            return new JObject
            {
                ["requestId"] = shown.RequestId, ["operation"] = OperationJob.OperationName(shown.Kind),
                ["status"] = shown.Status, ["phase"] = OperationJob.PhaseName(shown.Phase),
                ["reasonCode"] = shown.ReasonCode == null ? JValue.CreateNull() : (JToken)shown.ReasonCode,
                ["completedUtc"] = shown.CompletedUtc.HasValue ? (JToken)shown.CompletedUtc.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) : JValue.CreateNull()
            };
        }
    }
}
