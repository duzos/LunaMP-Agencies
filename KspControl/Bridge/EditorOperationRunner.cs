using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KspControl.Contracts;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>The timings of the runner. Production uses the defaults; tests shorten nothing but inject a clock.</summary>
    internal sealed class RunnerOptions
    {
        public long SettleTimeoutMilliseconds = 60000;
        public int QuietFrames = 5;
        public long DeltaVWaitMilliseconds = 5000;
        public long GraceStableMilliseconds = 2000;
        public long GraceMaxMilliseconds = 20000;
        public long ThumbnailTimeoutMilliseconds = 10000;
        public long ThumbnailPollMilliseconds = 500;
        public int SnapshotRetention = SnapshotStore.DefaultRetention;
    }

    /// <summary>
    /// The operation state machine (plan R4-section 6.4). One job at a time, one step per call to <see cref="Update"/> on the main
    /// thread: lock, verified snapshot, stage, dispatch (the synchronous load, under the tracker's dispatch window), settle,
    /// verify, a locked grace that ends when the fingerprint has been stable for two seconds (at most twenty), thumbnail
    /// settle inside the lock, then unlock and a terminal status. A failed apply reloads the verified snapshot; Stop or authority
    /// loss after dispatch cancels without restoring; a scene change or human input inside the lock is indeterminate.
    /// Every Unity call goes through <see cref="IEditorPort"/>, so the machine is tested end to end with a fake.
    /// </summary>
    internal sealed class EditorOperationRunner
    {
        private static readonly string[] AuthorityCodes =
        {
            "authority_unavailable", "authority_revoked", "stale_context", "stale_revision", "effects_changed", "effect_denied_or_unknown",
            "invalid_effect_set", "rebase_outside_operation", "revision_regressed", "editor_unavailable", "clock_regressed", "operation_in_progress", "grant_context_mismatch"
        };

        private readonly IEditorPort port;
        private readonly EditorRevisionTracker tracker;
        private readonly ExecutionAuthority authority;
        private readonly IEditorContextSource context;
        private readonly IOperationFiles files;
        private readonly Func<Pure.CraftPaths> pathsFactory;
        private readonly OperationJobs jobs;
        private readonly Func<long> clock;
        private readonly Func<string> worldEpoch;
        private readonly Func<DateTime> utcNow;
        private readonly Func<string> newSnapshotId;
        private readonly RunnerOptions options;

        public OperationJob Current { get; private set; }
        public bool Busy { get { return Current != null && !Current.Terminal; } }

        public EditorOperationRunner(IEditorPort port, EditorRevisionTracker tracker, ExecutionAuthority authority, IEditorContextSource context, IOperationFiles files,
            Func<Pure.CraftPaths> pathsFactory, OperationJobs jobs, Func<long> clock, Func<string> worldEpoch, Func<DateTime> utcNow = null, Func<string> newSnapshotId = null, RunnerOptions options = null)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.pathsFactory = pathsFactory ?? throw new ArgumentNullException(nameof(pathsFactory));
            this.jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            this.clock = clock ?? (() => MonotonicClock.Milliseconds);
            this.worldEpoch = worldEpoch ?? throw new ArgumentNullException(nameof(worldEpoch));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.newSnapshotId = newSnapshotId;
            this.options = options ?? new RunnerOptions();
        }

        /// <summary>Takes ownership of an admitted job. The caller has already checked that the runner is idle.</summary>
        public void Start(OperationJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (Busy) throw new InvalidOperationException("operation_in_progress");
            Current = job;
            job.CreatedUtc = utcNow(); job.UpdatedUtc = job.CreatedUtc;
            SetPhase(job, OperationPhase.Locking, clock());
        }

        /// <summary>Called every frame after the trust pump. Never throws.</summary>
        public void Update()
        {
            var job = Current;
            if (job == null || job.Terminal) return;
            var now = clock();
            job.UpdatedUtc = utcNow();
            try { Step(job, now); }
            catch (InvalidOperationException error)
            {
                if (Array.IndexOf(AuthorityCodes, error.Message) >= 0) OnAuthorityError(job, error.Message, now);
                else OnUnexpected(job, error, now);
            }
            catch (Exception error) { OnUnexpected(job, error, now); }
        }

        /// <summary>Game shutdown or bridge teardown: unlock at once and report what is known.</summary>
        public void Abort()
        {
            var job = Current;
            if (job == null || job.Terminal) return;
            if (job.Dispatched) Indeterminate(job, OperationReasons.BridgeStopped, null);
            else Fail(job, OperationReasons.BridgeStopped, null);
            Finalize(job, clock());
        }

        // ---------------------------------------------------------------- steps

        private void Step(OperationJob job, long now)
        {
            switch (job.Phase)
            {
                case OperationPhase.Locking: DoLocking(job, now); break;
                case OperationPhase.Snapshot: DoSnapshot(job, now); break;
                case OperationPhase.Staging: DoStaging(job, now); break;
                case OperationPhase.Dispatch: DoDispatch(job, now, job.StagingPath, OperationPhase.Settle); break;
                case OperationPhase.Settle: DoSettle(job, now, false); break;
                case OperationPhase.Verify: DoVerify(job, now); break;
                case OperationPhase.SurfaceMeasure: DoSurfaceMeasure(job, now); break;
                case OperationPhase.RestoreStaging: DoRestoreStaging(job, now); break;
                case OperationPhase.RestoreDispatch: DoDispatch(job, now, job.StagingPath2, OperationPhase.RestoreSettle); break;
                case OperationPhase.RestoreSettle: DoSettle(job, now, true); break;
                case OperationPhase.RestoreVerify: DoRestoreVerify(job, now); break;
                case OperationPhase.GraceStart: DoGraceStart(job, now); break;
                case OperationPhase.Grace: DoGrace(job, now); break;
                case OperationPhase.Thumbnails: DoThumbnails(job, now); break;
                case OperationPhase.Finalize: Finalize(job, now); break;
            }
        }

        private void SetPhase(OperationJob job, OperationPhase phase, long now) { job.Phase = phase; job.PhaseStartedAt = now; }

        private void Rebase(OperationJob job) { authority.RebaseUnderOperation(job.Ticket, job.RequestId, tracker.EditRevision); }

        private void Validate(OperationJob job) { authority.ValidateForDispatch(job.Ticket, context.CurrentContext(), context.CurrentBinding(), job.Effects); }

        /// <summary>
        /// Checked at the top of every phase from the snapshot on: a scene change or human input inside the lock ends the job as
        /// indeterminate (plan R3-section 7). Our own lock is re-asserted if the editor dropped it.
        /// </summary>
        private bool Guard(OperationJob job)
        {
            if (!port.EditorScene) { Indeterminate(job, OperationReasons.SceneChanged, null); return false; }
            if (tracker.Report.HumanInputDuringOperation) { Indeterminate(job, OperationReasons.HumanInputDuringOperation, null); return false; }
            if (!port.OperationLockHeld) { port.SetOperationLock(); job.LocksReasserted++; }
            return true;
        }

        private void DoLocking(OperationJob job, long now)
        {
            var idle = EditorIdle.Evaluate(port.FsmState, port.HasSelectedPart, port.ActiveLockIds);
            if (!idle.Idle) { Fail(job, OperationReasons.EditorBusy, string.Join(",", idle.Busy)); return; }
            Validate(job);
            authority.BeginOperation(job.Ticket, job.RequestId); job.AuthorityOperation = true;
            tracker.BeginOperation(); job.TrackerOperation = true;
            port.SetOperationLock(); job.LockSet = true;
            job.OldShip = port.ShipIdentity;
            if (job.Kind == OperationKind.Restore) { job.ExpectedParts = job.RestoreSource.PartCount; SetPhase(job, OperationPhase.RestoreStaging, now); }
            else SetPhase(job, port.PartCount > 0 ? OperationPhase.Snapshot : OperationPhase.Staging, now);
        }

        private void DoSnapshot(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            var capture = tracker.CaptureGuarded();
            var paths = pathsFactory();
            var store = new SnapshotStore(files, paths, utcNow, newSnapshotId);
            var outcome = store.Take(capture, port, port.Facility);
            if (outcome.Record == null) { Fail(job, OperationReasons.SnapshotUnverified, outcome.Problem); return; }
            job.Snapshot = outcome.Record;
            job.EffectsApplied.Add("snapshot_taken");
            job.Declared.Add(new DeclaredOutput { Path = outcome.Record.CraftPath, Class = "recovery", Action = "created", Detail = "snapshot " + outcome.Record.SnapshotId });
            foreach (var id in store.Prune(options.SnapshotRetention))
                job.Declared.Add(new DeclaredOutput { Path = id, Class = "recovery", Action = "deleted", Detail = "pruned beyond retention" });
            SetPhase(job, OperationPhase.Staging, now);
        }

        private void DoStaging(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            Validate(job);
            var paths = pathsFactory();
            var staging = paths.StagingPath(job.RequestId);
            if (!staging.Ok) { Fail(job, OperationReasons.PathOutsideSave, staging.ReasonCode); return; }
            var bytes = new UTF8Encoding(false).GetBytes(job.Plan.CraftText);
            files.WriteAtomic(staging.FullPath, bytes);
            job.StagingPath = staging.FullPath;
            job.Declared.Add(new DeclaredOutput { Path = staging.FullPath, Class = "staging", Action = "created", Detail = "structural craft" });
            if (!string.Equals(OperationHash.Sha256Hex(files.ReadAllBytes(staging.FullPath)), OperationHash.Sha256Hex(bytes), StringComparison.Ordinal)) { Fail(job, OperationReasons.StagingFailed, "hash_mismatch"); return; }
            string missing;
            if (!port.AllPartsFound(staging.FullPath, out missing)) { Fail(job, OperationReasons.CraftPartsMissing, missing); return; }
            job.EffectsApplied.Add("staging_written");
            job.Thumbs = new ThumbnailHousekeeper(files, paths, port.Facility);
            job.Thumbs.Watch(new[] { "kc-" + job.RequestId }, new[] { port.SanitizeFileName(job.Plan.Graph.Name) }, job.RequestId);
            job.ExpectedParts = job.Plan.PartCount;
            // P2.8: a plan with surface parts is loaded twice inside this job (plan R1-section 6.5). Pass 1 uses the provisional radius.
            if (job.Kind == OperationKind.Apply && Pure.SurfaceCalibration.NeedsCalibration(job.Plan.Plan.Layout)) { job.SurfacePass = 1; job.SurfaceSites = Pure.SurfaceCalibration.Sites(job.Plan.Plan.Layout); }
            SetPhase(job, OperationPhase.Dispatch, now);
        }

        /// <summary>The synchronous load. Everything raised inside the call belongs to the operation (dispatch window); every outcome from here has notDispatched=false.</summary>
        private void DoDispatch(OperationJob job, long now, string path, OperationPhase next)
        {
            if (!Guard(job)) return;
            if (job.Dispatched) Rebase(job);
            Validate(job);
            job.Dispatched = true;
            job.EffectsApplied.Add(job.InRecovery || job.Kind == OperationKind.Restore ? "snapshot_load_dispatched" : "craft_load_dispatched");
            job.OldShip = port.ShipIdentity;
            string loadError = null;
            tracker.BeginDispatch();
            try { port.LoadCraftFile(path); }
            catch (Exception error) { loadError = error.GetType().Name; }
            finally { tracker.EndDispatch(); }
            job.SettleStartedAt = now; job.QuietFrames = 0; job.LastObservedEvents = -1; job.ReadySince = -1;
            SetPhase(job, next, now);
            if (loadError != null) LoadFailure(job, OperationReasons.LoadFailed, loadError);
        }

        private static bool FsmOk(string state) { return state == null || state == "st_idle"; }

        private void DoSettle(OperationJob job, long now, bool recovery)
        {
            if (!Guard(job)) return;
            var ready = port.SceneReady && !port.RestartingEditor;
            try { Rebase(job); }
            catch (InvalidOperationException error) when (error.Message == ControlReasons.EditorUnavailable) { ready = false; } // the editor is restarting: keep waiting
            if (now - job.SettleStartedAt > options.SettleTimeoutMilliseconds) { LoadFailure(job, OperationReasons.LoadTimeout, "settle did not complete in " + options.SettleTimeoutMilliseconds / 1000 + " s"); return; }
            var parts = port.InEditor ? port.PartCount : 0;
            job.ObservedParts = parts;
            var replaced = port.InEditor && !ReferenceEquals(port.ShipIdentity, job.OldShip);
            if (replaced && parts == 0 && job.ExpectedParts > 0) { LoadFailure(job, OperationReasons.LoadFailed, "the editor is empty after the load"); return; }
            ready = ready && replaced && parts == job.ExpectedParts && port.AllPartsStarted && FsmOk(port.FsmState) && !port.HasSelectedPart;
            var events = tracker.Report.ObservedEvents;
            if (events != job.LastObservedEvents) { job.LastObservedEvents = events; job.QuietFrames = 0; } else job.QuietFrames++;
            job.ObservedEvents = events;
            if (!ready || job.QuietFrames < options.QuietFrames) { if (!ready) job.ReadySince = -1; return; }
            if (job.ReadySince < 0) job.ReadySince = now;
            job.DeltaVReady = port.DeltaVReady;
            if (!job.DeltaVReady && now - job.ReadySince < options.DeltaVWaitMilliseconds) return;
            Rebase(job);
            // Pass 1 of a surface placement is only measured: its positions use the provisional radius, so the verify step runs on pass 2.
            if (!recovery && job.SurfacePass == 1) { SetPhase(job, OperationPhase.SurfaceMeasure, now); return; }
            SetPhase(job, recovery ? OperationPhase.RestoreVerify : OperationPhase.Verify, now);
        }

        /// <summary>
        /// Between the two loads, locks still held: read each parent's live radius at its attach height and each child's live srfAttachNode, re-plan
        /// with them, overwrite the staging file with the pass-2 craft and dispatch the second load. Any gap fails the job (and restores the snapshot).
        /// </summary>
        private void DoSurfaceMeasure(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            var plan = job.Plan;
            var measured = new Pure.SurfaceMeasurements();
            foreach (var site in Pure.SurfaceCalibration.MeasurementSites(plan.Plan.Layout))
            {
                var radius = port.MeasureSurfaceRadius(site.ParentCid, site.Height);
                if (!radius.HasValue) { LoadFailure(job, OperationReasons.GeometryMismatchAfterLoad, "surface_unmeasured " + site.PartId); return; }
                measured.SetRadius(site.ParentCid, site.Height, radius.Value);
            }
            foreach (var site in job.SurfaceSites)
            {
                if (measured.ChildNodes.ContainsKey(site.ChildPart)) continue;
                var node = port.ReadSurfaceNode(site.ChildCid);
                if (node == null) { LoadFailure(job, OperationReasons.GeometryMismatchAfterLoad, "surface_node_unreadable " + site.PartId); return; }
                measured.ChildNodes[site.ChildPart] = node;
            }
            var second = Pure.SurfaceCalibration.Recalibrate(plan.Graph, plan.Catalog, plan.Options, plan.Plan.Layout, measured);
            if (!second.Ok) { LoadFailure(job, OperationReasons.GeometryMismatchAfterLoad, second.Issues.Count == 0 ? "recalibration_failed" : second.Issues[0].ToString()); return; }
            var text = second.Craft.ToText();
            if (text.Length > Pure.ConfigText.MaxChars) { LoadFailure(job, OperationReasons.GeometryMismatchAfterLoad, "recalibrated_craft_too_large"); return; }
            Validate(job);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            files.WriteAtomic(job.StagingPath, bytes); // the pass-1 file was already consumed by the synchronous load; the same declared path is reused
            if (!string.Equals(OperationHash.Sha256Hex(files.ReadAllBytes(job.StagingPath)), OperationHash.Sha256Hex(bytes), StringComparison.Ordinal)) { LoadFailure(job, OperationReasons.StagingFailed, "hash_mismatch_pass2"); return; }
            string missing;
            if (!port.AllPartsFound(job.StagingPath, out missing)) { LoadFailure(job, OperationReasons.CraftPartsMissing, missing); return; }
            plan.Plan = second; plan.CraftText = text; // the admitted planHash stays: it names the plan the model approved, not the calibrated positions
            job.SurfaceSites = Pure.SurfaceCalibration.Sites(second.Layout);
            job.SurfacePass = 2;
            job.EffectsApplied.Add("surface_calibrated");
            SetPhase(job, OperationPhase.Dispatch, now);
        }

        private void DoVerify(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            var capture = tracker.CaptureGuarded();
            if (capture == null) { LoadFailure(job, OperationReasons.StructureMismatchAfterLoad, "capture_unavailable"); return; }
            var engineering = port.ReadEngineering(0, 1, false);
            var problems = PlanVerifier.Verify(job.Plan.Plan.Craft, capture.Craft, engineering == null ? null : engineering.MaxStackNodeGapMetres, engineering == null ? null : engineering.AllPartsConnected);
            if (problems.Count != 0)
            {
                foreach (var problem in problems.Take(20)) job.VerifyProblems.Add(problem.ToString());
                LoadFailure(job, problems[0].Code, problems[0].Detail);
                return;
            }
            if (job.SurfacePass == 2)
            {
                var clearance = Pure.SurfaceCalibration.Clearance(job.SurfaceSites, (cid, height) => port.MeasureSurfaceRadius(cid, height));
                if (clearance.Count != 0)
                {
                    foreach (var problem in clearance.Take(20)) job.VerifyProblems.Add(OperationReasons.GeometryMismatchAfterLoad + ": " + problem);
                    LoadFailure(job, OperationReasons.GeometryMismatchAfterLoad, clearance[0]);
                    return;
                }
                job.EffectsApplied.Add("surface_clearance_verified");
            }
            if (!ApplyOverwriteGuard(job)) { LoadFailure(job, OperationReasons.SaveOverwriteGuardUnavailable, "the save-name guard could not be written"); return; }
            job.EffectsApplied.Add("craft_replaced");
            SetPhase(job, OperationPhase.GraceStart, now);
        }

        /// <summary>After an apply the craft is generated, not saved: KSP must prompt before a human Save overwrites a same-named file (plan R2-section 8).</summary>
        private bool ApplyOverwriteGuard(OperationJob job)
        {
            if (!port.Capabilities.SaveOverwriteGuard) { job.OverwriteGuard = "unavailable"; return true; }
            var named = port.TryWriteSavedName(EditorObservationService.GuardSentinel, EditorObservationService.GuardSentinel);
            var marked = port.TryMarkUnsaved();
            if (!named || !marked) { job.OverwriteGuard = "write_failed"; return false; }
            job.OverwriteGuard = "sentinel_and_unsaved_marker";
            return true;
        }

        private SnapshotRecord RestoreTarget(OperationJob job) { return job.InRecovery ? job.Snapshot : job.RestoreSource; }

        private void DoRestoreStaging(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            if (job.Dispatched) Rebase(job);
            Validate(job);
            var snapshot = RestoreTarget(job);
            var paths = pathsFactory();
            var problem = new SnapshotStore(files, paths, utcNow, newSnapshotId).Verify(snapshot);
            if (problem != null) { FailRestore(job, problem); return; }
            string missing;
            if (!port.AllPartsFound(snapshot.CraftPath, out missing)) { FailRestore(job, "parts_not_found"); return; }
            job.StagingPath2 = snapshot.CraftPath;
            job.ExpectedParts = snapshot.PartCount;
            if (job.Thumbs == null) job.Thumbs = new ThumbnailHousekeeper(files, paths, port.Facility);
            job.Thumbs.Watch(new[] { "kc-snap-" + snapshot.SnapshotId }, new[] { port.SanitizeFileName(snapshot.UiName) }, "rs-" + snapshot.SnapshotId);
            if (job.Restore == null) job.Restore = new RestoreInfo { Attempted = true, Result = "in_progress" };
            SetPhase(job, OperationPhase.RestoreDispatch, now);
        }

        private void DoRestoreVerify(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            var snapshot = RestoreTarget(job);
            var capture = tracker.CaptureGuarded();
            if (capture == null) { FailRestore(job, "capture_unavailable"); return; }
            if (!string.Equals(SnapshotStore.Fingerprint(capture.Craft, snapshot.Ui), snapshot.Fingerprint, StringComparison.Ordinal)) { FailRestore(job, "fingerprint_mismatch"); return; }
            // Our own writes come before the grace baseline, so nothing they trigger counts as a human edit.
            port.WriteUi(snapshot.Ui);
            if (!port.ReadUi().Equals(snapshot.Ui)) { FailRestore(job, "ui_fields_not_restored"); return; }
            WriteBackSaveState(job, snapshot);
            job.Restore.Result = "restored";
            job.EffectsApplied.Add("snapshot_restored");
            SetPhase(job, OperationPhase.GraceStart, now);
        }

        /// <summary>Writes the save-name fields and the unsaved marker back (plan R2-section 8, R3-section 8) and reports how each went.</summary>
        private void WriteBackSaveState(OperationJob job, SnapshotRecord snapshot)
        {
            var caps = port.Capabilities;
            var namesWritten = false;
            if (snapshot.VesselNameAtLastSave != null && caps.SaveOverwriteGuard)
                namesWritten = port.TryWriteSavedName(snapshot.VesselNameAtLastSave, snapshot.VesselNameAtLastSaveSanitized ?? snapshot.VesselNameAtLastSave);
            string marker;
            if (snapshot.CountsAsUnsaved)
            {
                if (caps.UnsavedMarker && port.TryMarkUnsaved()) marker = "private_field";
                else if (port.TrySetBackup()) marker = "set_backup";
                else marker = "unavailable";
            }
            else marker = caps.UnsavedMarker && port.TryMarkSaved() ? "private_field" : "not_needed";
            job.Restore.UnsavedMarkerRestored = marker;
            job.Restore.SaveFieldsRestored = namesWritten && marker == "private_field" ? "true" : (snapshot.VesselNameAtLastSave == null && !caps.SaveOverwriteGuard ? "unavailable" : "false");
        }

        private void DoGraceStart(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            // Adopt the end state silently: the tracker captures the baseline, then the ticket is rebased onto its revision.
            tracker.BeginGrace();
            Rebase(job);
            job.GraceStartedAt = now;
            SetPhase(job, OperationPhase.Grace, now);
        }

        private void DoGrace(OperationJob job, long now)
        {
            if (!port.EditorScene) { Indeterminate(job, OperationReasons.SceneChanged, null); return; }
            if (!port.OperationLockHeld) { port.SetOperationLock(); job.LocksReasserted++; }
            var report = tracker.Report;
            if (report.TakeoverDuringGrace) { job.TakeoverDuringGrace = true; EndGrace(job, now); return; }
            Rebase(job); // doubles as the liveness check: it throws as soon as the lease or grant is gone
            job.GraceFingerprintChanges = report.GraceFingerprintChanges;
            if (now - report.LastGraceChangeAt >= options.GraceStableMilliseconds) { EndGrace(job, now); return; }
            if (now - job.GraceStartedAt >= options.GraceMaxMilliseconds)
            {
                job.SettleUnstable = true;
                job.UnstableKeys.AddRange(report.GraceChangedKeys);
                EndGrace(job, now);
            }
        }

        private void EndGrace(OperationJob job, long now)
        {
            job.ThumbStartedAt = now;
            SetPhase(job, OperationPhase.Thumbnails, now);
        }

        private void DoThumbnails(OperationJob job, long now)
        {
            if (!port.EditorScene) { Indeterminate(job, OperationReasons.SceneChanged, null); return; }
            if (!port.OperationLockHeld) { port.SetOperationLock(); job.LocksReasserted++; }
            if (tracker.Report.TakeoverDuringGrace) job.TakeoverDuringGrace = true;
            else Rebase(job);
            var waited = now - job.ThumbStartedAt;
            var polled = false;
            if (job.Thumbs != null && (job.LastThumbPollAt == long.MinValue || now - job.LastThumbPollAt >= options.ThumbnailPollMilliseconds))
            {
                job.LastThumbPollAt = now; polled = true;
                if (!job.ThumbObserved && job.Thumbs.Observed()) { job.ThumbObserved = true; job.ThumbObservedAt = now; }
            }
            var settled = job.Thumbs == null
                || (job.ThumbObserved && now - job.ThumbObservedAt >= options.ThumbnailPollMilliseconds && polled)
                || waited >= options.ThumbnailTimeoutMilliseconds
                || job.TakeoverDuringGrace;
            if (!settled) return;
            if (job.Thumbs != null) { job.Thumbs.Finish(job.Declared); job.ThumbnailResult = job.ThumbObserved ? "settled" : "cache_unobserved"; }
            SetPhase(job, OperationPhase.Finalize, now);
        }

        // ---------------------------------------------------------------- outcomes

        /// <summary>A failure before the load was dispatched: nothing changed, so nothing is restored.</summary>
        private void Fail(OperationJob job, string reason, string detail)
        {
            job.PendingStatus = JobStatuses.Failed; job.PendingReason = reason; job.PendingDetail = detail;
            SetPhase(job, OperationPhase.Finalize, clock());
        }

        /// <summary>A failure after dispatch (settle, verify, guard): reload the verified snapshot when one exists.</summary>
        private void LoadFailure(OperationJob job, string reason, string detail)
        {
            if (job.InRecovery || job.Kind == OperationKind.Restore) { FailRestore(job, reason + (detail == null ? "" : ": " + detail)); return; }
            job.PendingStatus = JobStatuses.Failed; job.PendingReason = reason; job.PendingDetail = detail;
            var now = clock();
            if (job.Snapshot != null)
            {
                job.InRecovery = true;
                job.Restore = new RestoreInfo { Attempted = true, Result = "in_progress" };
                SetPhase(job, OperationPhase.RestoreStaging, now);
            }
            else
            {
                // Nothing was there to restore. A failed load already left the editor empty; a verify failure leaves the generated craft in place.
                job.Restore = new RestoreInfo { Attempted = false, Result = port.InEditor && port.PartCount > 0 ? "previous_editor_empty_generated_craft_left_in_place" : "previous_editor_empty" };
                SetPhase(job, OperationPhase.GraceStart, now);
            }
        }

        /// <summary>The snapshot reload itself failed. Before any dispatch (a restore job's staging check) the job is simply refused.</summary>
        private void FailRestore(OperationJob job, string detail)
        {
            if (!job.Dispatched) { Fail(job, OperationReasons.SnapshotUnverified, detail); return; }
            job.PendingStatus = JobStatuses.Failed; job.PendingReason = OperationReasons.RestoreFailed;
            job.PendingDetail = (job.PendingDetail == null ? "" : job.PendingDetail + "; ") + detail;
            if (job.Restore == null) job.Restore = new RestoreInfo();
            job.Restore.Attempted = true; job.Restore.Result = detail;
            SetPhase(job, OperationPhase.GraceStart, clock());
        }

        private void Cancel(OperationJob job, string reason)
        {
            job.PendingStatus = JobStatuses.Cancelled; job.PendingReason = reason;
            if (job.Restore == null) job.Restore = new RestoreInfo { Attempted = false, Result = "not_attempted_cancelled" };
            SetPhase(job, OperationPhase.Finalize, clock());
        }

        private void Indeterminate(OperationJob job, string reason, string detail)
        {
            job.PendingStatus = JobStatuses.Indeterminate; job.PendingReason = reason; job.PendingDetail = detail;
            job.KeepStaging = true;
            if (job.Restore == null) job.Restore = new RestoreInfo { Attempted = false, Result = "not_attempted_indeterminate" };
            SetPhase(job, OperationPhase.Finalize, clock());
        }

        private void OnAuthorityError(OperationJob job, string code, long now)
        {
            if (code == ControlReasons.EditorUnavailable)
            {
                // Not a revocation: the editor may simply be restarting. Before dispatch the job is refused; afterwards it waits, bounded.
                if (!job.Dispatched) { Fail(job, ControlReasons.EditorUnavailable, null); return; }
                if (now - job.PhaseStartedAt > options.SettleTimeoutMilliseconds) LoadFailure(job, OperationReasons.LoadTimeout, "the editor stayed unavailable");
                return;
            }
            if (!job.Dispatched)
            {
                string reason;
                switch (code)
                {
                    case "stale_context": case "stale_revision": reason = OperationReasons.StaleRevision; break;
                    case "effect_denied_or_unknown": reason = OperationReasons.GrantOperationDenied; break;
                    case "clock_regressed": reason = ControlReasons.ClockRegressed; break;
                    default: reason = ControlReasons.AuthorityRevoked; break;
                }
                Fail(job, reason, code);
                return;
            }
            if (tracker.Report.HumanInputDuringOperation) { Indeterminate(job, OperationReasons.HumanInputDuringOperation, null); return; }
            if (!port.EditorScene) { Indeterminate(job, OperationReasons.SceneChanged, null); return; }
            var suspended = authority.Status().Grant.State == GrantStates.Suspended;
            Cancel(job, suspended ? ControlReasons.GrantSuspended : ControlReasons.AuthorityRevoked);
        }

        private void OnUnexpected(OperationJob job, Exception error, long now)
        {
            var detail = error.GetType().Name;
            if (!job.Dispatched) { Fail(job, OperationReasons.OperationError, detail); return; }
            if (job.Phase == OperationPhase.Finalize || job.Phase == OperationPhase.Thumbnails || job.Phase == OperationPhase.Grace || job.Phase == OperationPhase.GraceStart)
            {
                // Past the verified point: the effect stands. Skip the rest, unlock and report what is known.
                job.PendingStatus = job.PendingStatus ?? JobStatuses.Indeterminate; job.PendingReason = job.PendingReason ?? OperationReasons.OperationError; job.PendingDetail = detail;
                job.KeepStaging = true;
                SetPhase(job, OperationPhase.Finalize, now);
                return;
            }
            LoadFailure(job, OperationReasons.OperationError, detail);
        }

        // ---------------------------------------------------------------- finalize

        /// <summary>Always unlocks. Reached on every path; each undo step runs only if the matching step ran.</summary>
        private void Finalize(OperationJob job, long now)
        {
            try { FinalizeCore(job); }
            catch (Exception error)
            {
                // Finalization must always reach a terminal state, or the runner would retry it every frame.
                job.Status = JobStatuses.Indeterminate; job.ReasonCode = OperationReasons.OperationError; job.Detail = "finalize: " + error.GetType().Name;
                job.Phase = OperationPhase.Done; job.CompletedUtc = utcNow(); job.UpdatedUtc = job.CompletedUtc.Value;
                jobs.NoteFinished(job);
            }
        }

        private void FinalizeCore(OperationJob job)
        {
            try { if (job.LockSet) port.ClearOperationLock(); } catch (Exception) { /* the lock stack is global: never let a scene teardown strand the job */ }
            job.LockSet = false;
            long revision = 0;
            try { if (job.TrackerOperation) revision = tracker.EndOperation(); } catch (Exception) { }
            job.TrackerOperation = false;
            if (job.AuthorityOperation)
            {
                try { authority.RebaseUnderOperation(job.Ticket, job.RequestId, revision); } catch (InvalidOperationException) { /* the lease is gone or taken over */ }
                try { authority.EndOperation(job.RequestId); } catch (Exception) { }
                job.AuthorityOperation = false;
            }
            if (job.Thumbs != null && job.ThumbnailResult == null)
            {
                try { job.Thumbs.Finish(job.Declared); job.ThumbnailResult = "not_settled"; } catch (Exception) { job.ThumbnailResult = "housekeeping_failed"; }
            }
            CleanStaging(job);
            job.EditorRevision = null;
            try
            {
                if (port.EditorScene && port.InEditor)
                {
                    var observed = tracker.Observe(true);
                    job.EditorRevision = tracker.Token(worldEpoch(), observed);
                    // Only the craft an apply left untouched by any human is "ours": a restored craft or a human edit during grace is not.
                    jobs.LastOperationFingerprint = job.Kind == OperationKind.Apply && job.PendingStatus == null && !job.TakeoverDuringGrace ? observed.Fingerprint : null;
                }
            }
            catch (Exception) { job.EditorRevision = null; }
            job.Status = job.PendingStatus ?? JobStatuses.Completed;
            job.ReasonCode = job.PendingReason; job.Detail = job.PendingDetail;
            job.Phase = OperationPhase.Done;
            job.CompletedUtc = utcNow(); job.UpdatedUtc = job.CompletedUtc.Value;
            jobs.NoteFinished(job);
        }

        /// <summary>Staging files go at the terminal state unless the job is indeterminate (plan R3-section 9). Recovery files stay.</summary>
        private void CleanStaging(OperationJob job)
        {
            if (job.StagingPath == null) return;
            if (job.KeepStaging || job.PendingStatus == JobStatuses.Indeterminate)
            {
                job.Declared.Add(new DeclaredOutput { Path = job.StagingPath, Class = "staging", Action = "reported", Detail = "kept: job is indeterminate" });
                return;
            }
            try
            {
                files.Delete(job.StagingPath);
                job.Declared.Add(new DeclaredOutput { Path = job.StagingPath, Class = "staging", Action = "deleted", Detail = "terminal state" });
            }
            catch (System.IO.IOException) { job.Declared.Add(new DeclaredOutput { Path = job.StagingPath, Class = "staging", Action = "reported", Detail = "could_not_delete" }); }
        }
    }
}
