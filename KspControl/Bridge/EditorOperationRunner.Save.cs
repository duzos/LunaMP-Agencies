using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KspControl.Contracts;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// editor_save_craft (plan R1-section 9, R3-sections 8 and 9). One synchronous step under the operation lock: capture the live craft,
    /// refuse a craft that breaks the LunaMP identifier rule, decide create or replace from the ledger, write through a temporary
    /// sibling, read the file back and compare it with the capture, record ownership, then sync the save-name fields so a later human
    /// Save of that same file does not prompt. A failed verification removes the new file (or puts the replaced bytes back).
    /// </summary>
    internal sealed partial class EditorOperationRunner
    {
        /// <summary>The largest ship file this service writes. Real modded craft are a few hundred KB.</summary>
        public const int MaxSaveBytes = 2 * 1024 * 1024;

        private void DoSave(OperationJob job, long now)
        {
            var request = job.Save;
            if (!port.EditorScene) { Fail(job, OperationReasons.SceneChanged, null); return; }
            if (tracker.Report.HumanInputDuringOperation) { Fail(job, OperationReasons.HumanInputDuringOperation, null); return; }
            if (!port.OperationLockHeld) { port.SetOperationLock(); job.LocksReasserted++; }
            Validate(job);

            var capture = tracker.CaptureGuarded();
            if (capture == null) { Fail(job, OperationReasons.CaptureUnavailable, null); return; }
            var live = SnapshotStore.PartCountOf(capture.Craft);
            if (live == 0 || live != port.PartCount) { Fail(job, live == 0 ? OperationReasons.CraftEmpty : OperationReasons.CaptureUnavailable, "part_count " + live); return; }
            string identifierProblem;
            job.CraftIdentifiersValid = CraftIdentifierRule.Check(capture.Craft, out identifierProblem);
            if (job.CraftIdentifiersValid != true) { Fail(job, OperationReasons.CraftIdentifiersInvalid, identifierProblem); return; }
            var node = SnapshotStore.WithUiHeader(capture.Craft, capture.Ui);
            var bytes = new UTF8Encoding(false).GetBytes(Pure.ConfigText.Print(node));
            if (bytes.Length > MaxSaveBytes) { Fail(job, OperationReasons.CraftTooLarge, bytes.Length + " bytes"); return; }

            // Everything about the target is decided again here, in the same step as the write: admission was an earlier frame.
            var paths = pathsFactory();
            var target = paths.ResolveNewShip(request.Facility, request.FileName);
            var ledgerPath = paths.LedgerPath();
            if (!target.Ok || !ledgerPath.Ok) { Fail(job, OperationReasons.PathOutsideSave, target.Ok ? ledgerPath.ReasonCode : target.ReasonCode); return; }
            var ledger = CraftLedger.Load(files, ledgerPath.FullPath);
            bool exists; string currentHash = null; byte[] previous = null;
            try
            {
                exists = files.Exists(target.FullPath);
                if (exists) { previous = files.ReadAllBytes(target.FullPath); currentHash = OperationHash.Sha256Hex(previous); }
            }
            catch (IOException) { exists = true; previous = null; currentHash = null; } // unreadable: the policy refuses to replace it
            var owned = ledger.Find(request.Facility, request.FileName + Pure.CraftPaths.CraftExtension);
            var decision = SavePolicy.Decide(exists, currentHash, request.ReplaceExpectedSha256, owned, ledger.State);
            if (decision.Action == SaveAction.Refuse) { Fail(job, decision.Reason, decision.Detail); return; }
            job.ReplacedSha256 = decision.ReplacedSha256;
            job.ShipsBaseline = ListShips(paths, request.Facility);

            var hash = OperationHash.Sha256Hex(bytes);
            try
            {
                if (decision.Action == SaveAction.Create) files.CreateNew(target.FullPath, bytes);
                else files.ReplaceExisting(target.FullPath, bytes);
            }
            catch (IOException error)
            {
                // Neither call can leave a half-written target: the write failed before the move, or the move itself was refused.
                if (error.Message == "exists") Fail(job, OperationReasons.FileExists, "the file appeared during the save");
                else Fail(job, OperationReasons.WriteFailed, error.GetType().Name);
                return;
            }
            catch (UnauthorizedAccessException) { Fail(job, OperationReasons.WriteFailed, "access_denied"); return; }
            job.Dispatched = true;
            job.SavedPath = target.FullPath;
            job.Declared.Add(new DeclaredOutput { Path = target.FullPath, Class = "ships", Action = decision.Action == SaveAction.Create ? "created" : "replaced", Detail = "saved craft " + request.FileName });

            var problem = VerifyWritten(job, target.FullPath, hash, node);
            if (problem != null)
            {
                Revert(job, target.FullPath, decision.Action, previous);
                job.PendingStatus = job.PendingStatus ?? JobStatuses.Failed; job.PendingReason = OperationReasons.SaveVerifyFailed; job.PendingDetail = problem;
                job.SavedPath = null;
                SetPhase(job, OperationPhase.Finalize, now);
                return;
            }
            job.SavedSha256 = hash;
            job.EffectsApplied.Add("craft_saved");
            RecordOwnership(job, paths, ledger, ledgerPath.FullPath, hash);
            SyncSaveFields(job, request.FileName);
            SetPhase(job, OperationPhase.Finalize, now);
        }

        /// <summary>Reads the file back and checks bytes, parse, identifier rule, the comparator against the capture and AllPartsFound. Null when all hold.</summary>
        private string VerifyWritten(OperationJob job, string path, string expectedHash, Pure.ConfigNode node)
        {
            byte[] back;
            try { back = files.ReadAllBytes(path); } catch (IOException) { return "readback_failed"; }
            if (!string.Equals(OperationHash.Sha256Hex(back), expectedHash, StringComparison.Ordinal)) return "hash_mismatch";
            Pure.ConfigNode reloaded;
            try { reloaded = Pure.ConfigText.Parse(new UTF8Encoding(false, true).GetString(back)); }
            catch (Pure.ConfigParseException) { return "unparsable"; }
            catch (DecoderFallbackException) { return "unparsable"; }
            string identifierProblem;
            if (!CraftIdentifierRule.Check(reloaded, out identifierProblem)) { job.CraftIdentifiersValid = false; return identifierProblem; }
            job.Comparison = Pure.CraftComparator.Compare(node, reloaded);
            if (!job.Comparison.Equal) return "comparison_differs (" + job.Comparison.TotalDifferences + ")";
            string missing;
            if (!port.AllPartsFound(path, out missing)) return "parts_not_found " + missing;
            return null;
        }

        private void Revert(OperationJob job, string path, SaveAction action, byte[] previous)
        {
            try
            {
                if (action == SaveAction.Create)
                {
                    files.Delete(path);
                    job.Declared.Add(new DeclaredOutput { Path = path, Class = "ships", Action = "deleted", Detail = "verification failed; the new file was removed" });
                }
                else
                {
                    files.ReplaceExisting(path, previous);
                    job.Declared.Add(new DeclaredOutput { Path = path, Class = "ships", Action = "restored", Detail = "verification failed; the previous bytes were put back" });
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                job.PendingStatus = JobStatuses.Indeterminate;
                job.Declared.Add(new DeclaredOutput { Path = path, Class = "ships", Action = "reported", Detail = "verification failed and the revert failed: " + error.GetType().Name });
            }
        }

        private void RecordOwnership(OperationJob job, Pure.CraftPaths paths, CraftLedger ledger, string ledgerPath, string hash)
        {
            try
            {
                ledger.Prune(e => { var check = paths.ResolveExistingShip(e.Facility, e.FileName); return check.Ok && files.Exists(check.FullPath); });
                var recorded = ledger.Record(new LedgerEntry
                {
                    Facility = job.Save.Facility, FileName = job.Save.FileName + Pure.CraftPaths.CraftExtension, Sha256 = hash, RequestId = job.RequestId,
                    SavedUtc = utcNow().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
                });
                if (!recorded) { job.LedgerResult = "not_recorded"; return; }
                ledger.Save(files, ledgerPath);
                job.LedgerResult = "recorded";
                job.Declared.Add(new DeclaredOutput { Path = ledgerPath, Class = "ledger", Action = "reported", Detail = "ownership recorded" });
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // The file landed but is not owned, so it can never be replaced: the safe direction. Reported, not failed.
                job.LedgerResult = "write_failed";
            }
        }

        /// <summary>R3-section 8: after a save the save-name fields name the file written and the craft counts as saved.</summary>
        private void SyncSaveFields(OperationJob job, string fileName)
        {
            var caps = port.Capabilities ?? EditorCapabilities.None();
            if (!caps.SaveOverwriteGuard) { job.BookkeepingResult = "unavailable"; return; }
            var named = port.TryWriteSavedName(fileName, fileName);
            var marked = port.TryMarkSaved();
            job.BookkeepingResult = named && marked ? "synced" : named || marked ? "partial" : "write_failed";
        }

        private Dictionary<string, FileEntry> ListShips(Pure.CraftPaths paths, string facility)
        {
            var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            var directory = paths.ShipsDirectory(facility);
            if (directory == null) return result;
            try { foreach (var entry in files.List(directory, null)) result[entry.Name] = entry; }
            catch (IOException) { /* an unreadable folder reports no sidecars */ }
            return result;
        }

        /// <summary>Anything else new or changed in the Ships folder (a craft.original, a loadmeta) is reported, never a failure (R3-section 9).</summary>
        private void ReportShipsSidecars(OperationJob job)
        {
            if (job.Kind != OperationKind.Save || job.ShipsBaseline == null) return;
            try
            {
                var targetName = job.SavedPath == null ? null : Path.GetFileName(job.SavedPath);
                var after = ListShips(pathsFactory(), job.Save.Facility);
                foreach (var entry in after.Values.OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    if (targetName != null && string.Equals(entry.Name, targetName, StringComparison.OrdinalIgnoreCase)) continue;
                    FileEntry before;
                    if (job.ShipsBaseline.TryGetValue(entry.Name, out before) && before.SameAs(entry)) continue;
                    job.Declared.Add(new DeclaredOutput { Path = entry.Path, Class = "ships", Action = "reported", Detail = before == null ? "created_beside_save" : "changed_beside_save" });
                }
            }
            catch (Exception) { /* the sidecar report is advisory */ }
        }

        /// <summary>An unexpected exception inside a save: before the write nothing changed; after it the file state is unknown.</summary>
        private void OnSaveUnexpected(OperationJob job, string detail)
        {
            if (!job.Dispatched) { Fail(job, OperationReasons.OperationError, detail); return; }
            job.PendingStatus = JobStatuses.Indeterminate; job.PendingReason = OperationReasons.OperationError; job.PendingDetail = detail;
            SetPhase(job, OperationPhase.Finalize, clock());
        }
    }
}
