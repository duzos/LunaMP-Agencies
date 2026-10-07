using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    internal enum OperationKind { Apply, Restore, Save }

    /// <summary>
    /// The runner's phases (plan R4-section 6.4). Restore* phases are the snapshot reload: the automatic recovery after a failed apply
    /// and the whole of an explicit editor_restore_snapshot job.
    /// </summary>
    internal enum OperationPhase
    {
        Locking, Snapshot, Staging, Dispatch, Settle, Verify,
        RestoreStaging, RestoreDispatch, RestoreSettle, RestoreVerify,
        GraceStart, Grace, Thumbnails, Finalize, Done,
        /// <summary>The single synchronous step of editor_save_craft: capture, write, verify, record, sync the save-name fields.</summary>
        SaveWrite
    }

    internal sealed class DeclaredOutput
    {
        public string Path { get; set; }
        /// <summary>staging, recovery, thumbnail or thumbs-backup.</summary>
        public string Class { get; set; }
        /// <summary>created, deleted, restored or reported.</summary>
        public string Action { get; set; }
        public string Detail { get; set; }
        public JObject ToJson() { return new JObject { ["path"] = Path, ["class"] = Class, ["action"] = Action, ["detail"] = Detail }; }
    }

    internal sealed class RestoreInfo
    {
        public bool Attempted { get; set; }
        public string Result { get; set; }
        /// <summary>private_field, set_backup or not_needed; null until the write-backs ran.</summary>
        public string UnsavedMarkerRestored { get; set; }
        /// <summary>true, false or unavailable.</summary>
        public string SaveFieldsRestored { get; set; }
    }

    /// <summary>
    /// One mutation job. Created at admission, advanced once per frame by the runner, and read by editor.operation_status.
    /// Everything the envelope reports is a plain property; the runner-only state is internal.
    /// </summary>
    internal sealed partial class OperationJob
    {
        public string RequestId { get; set; }
        public OperationKind Kind { get; set; }
        public string Fingerprint { get; set; }
        public string LeaseId { get; set; }
        public ExecutionTicket Ticket { get; set; }
        public ClassifiedEffect[] Effects { get; set; }
        public string Status { get; set; } = JobStatuses.Running;
        public OperationPhase Phase { get; set; } = OperationPhase.Locking;
        public string ReasonCode { get; set; }
        public string Detail { get; set; }
        public bool Dispatched { get; set; }
        public string PreviousEditorRevision { get; set; }
        public string EditorRevision { get; set; }
        public ApplyPlan Plan { get; set; }
        /// <summary>The snapshot a restore job loads.</summary>
        public SnapshotRecord RestoreSource { get; set; }
        /// <summary>The snapshot this job took of the craft it replaced.</summary>
        public SnapshotRecord Snapshot { get; set; }
        public List<string> EffectsApplied { get; } = new List<string>();
        public RestoreInfo Restore { get; set; }
        public List<DeclaredOutput> Declared { get; } = new List<DeclaredOutput>();
        public int ExpectedParts { get; set; }
        public int ObservedParts { get; set; }
        public int ObservedEvents { get; set; }
        public int GraceFingerprintChanges { get; set; }
        public bool DeltaVReady { get; set; }
        public int LocksReasserted { get; set; }
        public bool SettleUnstable { get; set; }
        public List<string> UnstableKeys { get; } = new List<string>();
        public bool TakeoverDuringGrace { get; set; }
        /// <summary>sentinel_and_unsaved_marker, not_needed, unavailable or write_failed.</summary>
        public string OverwriteGuard { get; set; }
        public string ModVersions { get; set; }
        public string ThumbnailResult { get; set; }
        public List<string> VerifyProblems { get; } = new List<string>();
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public bool Terminal { get { return JobStatuses.IsTerminal(Status); } }

        // ---- runner-only state ----
        internal object OldShip;
        internal string StagingPath;
        internal string StagingPath2;
        internal long PhaseStartedAt, SettleStartedAt, ReadySince = -1, GraceStartedAt, ThumbStartedAt, LastThumbPollAt = long.MinValue;
        internal int QuietFrames, LastObservedEvents = -1;
        internal ThumbnailHousekeeper Thumbs;
        /// <summary>The outcome decided before grace and finalization: set when the job leaves the main path.</summary>
        internal string PendingStatus, PendingReason, PendingDetail;
        internal bool InRecovery;
        internal bool ThumbObserved;
        internal long ThumbObservedAt;
        internal bool KeepStaging;
        /// <summary>Which undo steps Finalize owes: the authority operation mark, the tracker window and the editor lock.</summary>
        internal bool AuthorityOperation, TrackerOperation, LockSet;

        public static string PhaseName(OperationPhase phase)
        {
            switch (phase)
            {
                case OperationPhase.Locking: return "locking";
                case OperationPhase.Snapshot: return "snapshot";
                case OperationPhase.Staging: return "staging";
                case OperationPhase.Dispatch: return "dispatch";
                case OperationPhase.Settle: return "settle";
                case OperationPhase.Verify: return "verify";
                case OperationPhase.RestoreStaging: return "restore_staging";
                case OperationPhase.RestoreDispatch: return "restore_dispatch";
                case OperationPhase.RestoreSettle: return "restore_settle";
                case OperationPhase.RestoreVerify: return "restore_verify";
                case OperationPhase.GraceStart: case OperationPhase.Grace: return "post_unlock_grace";
                case OperationPhase.Thumbnails: return "thumbnail_settle";
                case OperationPhase.SaveWrite: return "save";
                case OperationPhase.Finalize: return "finalizing";
                default: return "done";
            }
        }

        /// <summary>The envelope (plan R2-section 4, R3-section 4). <c>notDispatched</c> is true only for a finished job that never reached the load call.</summary>
        public JObject ToEnvelope()
        {
            var cleaned = new JArray(Declared.Where(d => d.Action == "deleted" || d.Action == "restored").Select(d => (JToken)d.ToJson()));
            var envelope = new JObject
            {
                ["operation"] = OperationName(Kind),
                ["requestId"] = RequestId,
                ["phase"] = PhaseName(Phase),
                ["notDispatched"] = Terminal && !Dispatched,
                ["dispatched"] = Dispatched,
                ["editorRevision"] = Terminal && EditorRevision != null ? (JToken)EditorRevision : JValue.CreateNull(),
                ["previousEditorRevision"] = PreviousEditorRevision == null ? JValue.CreateNull() : (JToken)PreviousEditorRevision,
                ["snapshotId"] = Snapshot != null ? (JToken)Snapshot.SnapshotId : RestoreSource != null ? (JToken)RestoreSource.SnapshotId : JValue.CreateNull(),
                ["snapshotPath"] = Terminal && Snapshot != null && Snapshot.CraftPath != null ? (JToken)Snapshot.CraftPath : JValue.CreateNull(),
                ["effects"] = new JArray(EffectsApplied),
                ["declaredOutputs"] = new JArray(Declared.Select(d => (JToken)d.ToJson())),
                ["cleanedArtifacts"] = cleaned,
                ["restore"] = Restore == null ? JValue.CreateNull() : (JToken)new JObject
                {
                    ["attempted"] = Restore.Attempted, ["result"] = Restore.Result,
                    ["unsavedMarkerRestored"] = Restore.UnsavedMarkerRestored == null ? JValue.CreateNull() : (JToken)Restore.UnsavedMarkerRestored,
                    ["saveFieldsRestored"] = Restore.SaveFieldsRestored == null ? JValue.CreateNull() : (JToken)Restore.SaveFieldsRestored
                },
                ["observed"] = new JObject
                {
                    ["expectedParts"] = ExpectedParts, ["observedParts"] = ObservedParts, ["observedEvents"] = ObservedEvents,
                    ["graceFingerprintChanges"] = GraceFingerprintChanges, ["deltaVReady"] = DeltaVReady, ["locksReasserted"] = LocksReasserted,
                    ["overwriteGuard"] = OverwriteGuard == null ? JValue.CreateNull() : (JToken)OverwriteGuard,
                    ["modVersions"] = ModVersions == null ? JValue.CreateNull() : (JToken)ModVersions,
                    ["thumbnail"] = ThumbnailResult == null ? JValue.CreateNull() : (JToken)ThumbnailResult
                },
                ["takeoverDuringGrace"] = TakeoverDuringGrace,
                ["settleUnstable"] = SettleUnstable,
                ["unstableKeys"] = new JArray(UnstableKeys),
                ["verifyProblems"] = new JArray(VerifyProblems),
                ["startedUtc"] = Format(CreatedUtc), ["updatedUtc"] = Format(UpdatedUtc),
                ["completedUtc"] = CompletedUtc.HasValue ? (JToken)Format(CompletedUtc.Value) : JValue.CreateNull()
            };
            if (Plan != null && Plan.PlanHash != null) envelope["planHash"] = Plan.PlanHash;
            AddSaveEnvelope(envelope);
            if (Detail != null) envelope["detail"] = Detail;
            return envelope;
        }

        private static string Format(DateTime value) { return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture); }
    }

    /// <summary>In-memory job registry: dedupe by request id, with a bounded history. A bridge restart loses it, which the host journal turns into "indeterminate".</summary>
    internal sealed class OperationJobs
    {
        private const int MaxJobs = 32;
        private readonly List<OperationJob> jobs = new List<OperationJob>();
        private readonly object gate = new object();
        /// <summary>The fingerprint of the editor as the last finished operation left it, or null. Used to tell our own craft from a human one.</summary>
        public string LastOperationFingerprint { get; set; }
        public OperationJob LastFinished { get; private set; }

        public OperationJob Get(string requestId)
        { lock (gate) return jobs.FirstOrDefault(j => j.RequestId == requestId); }

        public void Add(OperationJob job)
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

        public void NoteFinished(OperationJob job) { lock (gate) LastFinished = job; }
    }
}
