using System;
using System.Text.RegularExpressions;

namespace KspControl.Contracts
{
    /// <summary>The effect names a grant lists in its operations and the authority classifies. One per mutation tool.</summary>
    public static class OperationEffects
    {
        public const string ReplaceCraft = "editor.replace_craft";
        public const string RestoreSnapshot = "editor.restore_snapshot";
    }

    /// <summary>Job envelope status values (plan R1-section 4.3).</summary>
    public static class JobStatuses
    {
        public const string Accepted = "accepted";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Indeterminate = "indeterminate";
        public static bool IsTerminal(string status)
        {
            return status == Completed || status == Failed || status == Cancelled || status == Indeterminate;
        }
    }

    /// <summary>Bounds shared by host validation and bridge checks for the mutation tools.</summary>
    public static class OperationLimits
    {
        public const int ExpectedRevisionMax = 128;
        public const int WaitSecondsMax = 20;
        public const int PlanHashLength = 64;
        public const int SnapshotIdMin = 8;
        public const int SnapshotIdMax = 128;
        private static readonly Regex RequestId = new Regex("^[A-Za-z0-9_-]{8,128}$", RegexOptions.CultureInvariant);
        public static bool IsRequestId(string value) { return value != null && RequestId.IsMatch(value); }
        public static bool IsSnapshotId(string value) { return IsRequestId(value); }
        public static bool IsPlanHash(string value)
        {
            if (value == null || value.Length != PlanHashLength) return false;
            foreach (var c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
    }

    /// <summary>Reason codes produced by the mutation path, beyond the control codes.</summary>
    public static class OperationReasons
    {
        public const string StaleRevision = "stale_revision";
        public const string EditorBusy = "editor_busy";
        public const string UnsavedHumanCraft = "unsaved_human_craft";
        public const string InvalidGraph = "invalid_graph";
        public const string PlanChanged = "plan_changed";
        public const string PartLocked = "part_locked";
        public const string PartNotBuildable = "part_not_buildable";
        public const string PartConstructionUnverified = "part_construction_unverified";
        public const string UnsupportedConfiguration = "unsupported_configuration";
        public const string UnsupportedStagingTopology = "unsupported_staging_topology";
        public const string CraftTooLarge = "craft_too_large";
        public const string ModVersionsUnavailable = "mod_versions_unavailable";
        public const string SnapshotUnverified = "snapshot_unverified";
        public const string SnapshotNotFound = "snapshot_not_found";
        public const string LoadFailed = "load_failed";
        public const string LoadTimeout = "load_timeout";
        public const string GeometryMismatchAfterLoad = "geometry_mismatch_after_load";
        public const string StructureMismatchAfterLoad = "structure_mismatch_after_load";
        public const string RestoreFailed = "restore_failed";
        public const string SceneChanged = "scene_changed";
        public const string BridgeStopped = "bridge_stopped";
        public const string HumanInputDuringOperation = "human_input_during_operation";
        public const string RequestIdConflict = "request_id_conflict";
        public const string JournalUnavailable = "journal_unavailable";
        public const string JournalLockedByOtherHost = "journal_locked_by_other_host";
        public const string ReconcileRequired = "reconcile_required";
        public const string GrantOperationDenied = "grant_operation_denied";
        public const string SaveOverwriteGuardUnavailable = "save_overwrite_guard_unavailable";
        public const string NameCollisionUnguarded = "name_collision_unguarded";
        public const string PathOutsideSave = "path_outside_save";
        public const string JobUnknown = "job_unknown";
        public const string OperationError = "operation_error";
        public const string StagingFailed = "staging_failed";
        public const string CraftPartsMissing = "craft_parts_missing";
    }
}
