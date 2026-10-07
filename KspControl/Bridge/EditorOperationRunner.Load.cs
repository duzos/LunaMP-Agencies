using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// The load-specific steps of the runner (plan R3-section 6.4, R4-section 6.4): staging a copy of the human's file, running the upgrade
    /// pipeline on the copy, pre-validating, comparing the loaded craft with what was staged, and proving the human's file untouched.
    /// The human file is never loaded in place and never gets a sidecar.
    /// </summary>
    internal sealed partial class EditorOperationRunner
    {
        private static string HashOf(IOperationFiles files, string path)
        {
            try { return files.Exists(path) ? OperationHash.Sha256Hex(files.ReadAllBytes(path)) : "absent"; }
            catch (System.IO.IOException) { return "unreadable"; }
        }

        /// <summary>The phase "staging" of a load. Every refusal here is pre-dispatch (notDispatched=true).</summary>
        private void DoLoadStaging(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            Validate(job);
            var load = job.Load;
            var paths = pathsFactory();
            var source = paths.ResolveExistingShip(load.Facility, load.FileName);
            if (!source.Ok) { Fail(job, OperationReasons.PathOutsideSave, source.ReasonCode); return; }
            var copy = paths.StagingPath(job.RequestId);
            if (!copy.Ok) { Fail(job, OperationReasons.PathOutsideSave, copy.ReasonCode); return; }
            if (!files.Exists(source.FullPath)) { Fail(job, LoadReasons.CraftNotFound, load.FileName); return; }

            // The human's file and its sidecars, as they are before anything runs. They are hashed again after grace.
            load.SidecarBaseline[source.FullPath] = HashOf(files, source.FullPath);
            load.SidecarBaseline[source.FullPath + ".original"] = HashOf(files, source.FullPath + ".original");
            load.SidecarBaseline[System.IO.Path.ChangeExtension(source.FullPath, ".loadmeta")] = HashOf(files, System.IO.Path.ChangeExtension(source.FullPath, ".loadmeta"));

            // 1. Copy the source and hash-check the copy.
            byte[] bytes;
            try
            {
                files.Copy(source.FullPath, copy.FullPath, true);
                load.CopyPath = copy.FullPath;
                job.Declared.Add(new DeclaredOutput { Path = copy.FullPath, Class = "staging", Action = "created", Detail = "copy of " + load.FileName });
                bytes = files.ReadAllBytes(copy.FullPath);
            }
            catch (System.IO.IOException error) { Fail(job, OperationReasons.StagingFailed, error.Message); return; }
            if (bytes.Length > Pure.ConfigText.MaxChars) { Fail(job, LoadReasons.CraftTooLarge, bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes"); return; }
            load.SourceSha256 = OperationHash.Sha256Hex(bytes);
            if (!string.Equals(load.SourceSha256, load.ExpectedSha256, StringComparison.Ordinal)) { Fail(job, LoadReasons.FileChanged, "the file hashes to " + load.SourceSha256); return; }
            job.EffectsApplied.Add("staging_copy_verified");

            // 2. The structure is checked on the text before KSP's own code sees the craft.
            Pure.ConfigNode text;
            try { text = Pure.ConfigText.Parse(new System.Text.UTF8Encoding(false).GetString(bytes)); }
            catch (Pure.ConfigParseException error) { Fail(job, LoadReasons.CraftUnreadable, error.Code + " at line " + error.Line.ToString(CultureInfo.InvariantCulture)); return; }
            var links = LoadValidator.Links(text);
            if (links != null) { Fail(job, links.Reason, links.Detail); return; }

            // 3. The upgrade pipeline, synchronously, on the staged copy. Nothing is written beside the human's file.
            load.PipelineRan = true;
            var outcome = port.RunUpgradePipeline(copy.FullPath);
            load.PopupDismissed = outcome.PopupDismissed; load.LockRemoved = outcome.LockRemoved; load.PipelineError = outcome.Error;
            load.ScriptsApplied = outcome.ScriptsApplied;
            if (!outcome.Succeeded || outcome.Output == null)
            {
                load.PipelineRan = true;
                Fail(job, LoadReasons.CraftUpgradeFailed, (outcome.Error ?? "the pipeline did not report success")
                    + (outcome.PopupDismissed ? "; the failure popup was dismissed" : ""));
                return;
            }
            job.EffectsApplied.Add("upgrade_pipeline_run");

            // 4. What did the pipeline change?
            var reference = outcome.Reference ?? text;
            var output = outcome.Output;
            load.PipelineComparison = Pure.CraftComparator.Compare(reference, output);
            load.HeaderVersionReference = reference.First("version"); load.HeaderVersionOutput = output.First("version");
            load.UpgradedOnLoad = !load.PipelineComparison.Equal || !string.Equals(load.HeaderVersionReference, load.HeaderVersionOutput, StringComparison.Ordinal);
            if (load.UpgradedOnLoad && !load.AllowUpgrade)
            {
                Fail(job, LoadReasons.CraftRequiresUpgrade, "the upgrade pipeline changes this craft (" + (load.PipelineComparison.TotalDifferences).ToString(CultureInfo.InvariantCulture)
                    + " differences); pass allowUpgrade=true to load the upgraded craft. The file on disk is never rewritten.");
                return;
            }
            load.Output = load.UpgradedOnLoad ? output : text;
            load.ShipName = load.Output.First("ship") ?? "";

            // 5. Stage what the editor will hold: the pipeline output when it changed the craft, otherwise the verified copy itself.
            var loadPath = copy.FullPath;
            if (load.UpgradedOnLoad)
            {
                var upgraded = paths.UpgradedStagingPath(job.RequestId);
                if (!upgraded.Ok) { Fail(job, OperationReasons.PathOutsideSave, upgraded.ReasonCode); return; }
                var upgradedBytes = new System.Text.UTF8Encoding(false).GetBytes(Pure.ConfigText.Print(output));
                try { files.WriteAtomic(upgraded.FullPath, upgradedBytes); load.UpgradedPath = upgraded.FullPath; }
                catch (System.IO.IOException error) { Fail(job, OperationReasons.StagingFailed, error.Message); return; }
                job.Declared.Add(new DeclaredOutput { Path = upgraded.FullPath, Class = "staging", Action = "created", Detail = "pipeline output" });
                if (!string.Equals(OperationHash.Sha256Hex(files.ReadAllBytes(upgraded.FullPath)), OperationHash.Sha256Hex(upgradedBytes), StringComparison.Ordinal)) { Fail(job, OperationReasons.StagingFailed, "hash_mismatch"); return; }
                loadPath = upgraded.FullPath;
            }

            // 6. Pre-validation of the craft that will be loaded, before any dispatch.
            var finalLinks = LoadValidator.Links(load.Output);
            if (finalLinks != null) { Fail(job, finalLinks.Reason, finalLinks.Detail); return; }
            var modules = LoadValidator.Modules(load.Output, port.PrefabModuleNames);
            if (modules != null) { Fail(job, modules.Reason, modules.Detail); return; }
            string missing;
            if (!port.AllPartsFound(loadPath, out missing)) { Fail(job, OperationReasons.CraftPartsMissing, missing); return; }

            job.StagingPath = loadPath;
            job.EffectsApplied.Add("staging_written");
            job.Thumbs = new ThumbnailHousekeeper(files, paths, port.Facility);
            job.Thumbs.Watch(new[] { "kc-" + job.RequestId, "kc-" + job.RequestId + ".upgraded" }, new[] { port.SanitizeFileName(load.ShipName) }, job.RequestId);
            job.ExpectedParts = load.Output.Children("PART").Count();
            SetPhase(job, OperationPhase.Dispatch, now);
        }

        /// <summary>
        /// Verify after a load: the craft the editor holds (a native save) against what was staged. The comparison is reported either way.
        /// A part-count difference cannot reach here (settle waits for the expected count), so a difference in values is data for the
        /// caller, not a failure that would throw away a craft KSP loaded legitimately.
        /// </summary>
        private void DoLoadVerify(OperationJob job, long now)
        {
            if (!Guard(job)) return;
            var capture = tracker.CaptureGuarded();
            if (capture == null) { LoadFailure(job, OperationReasons.StructureMismatchAfterLoad, "capture_unavailable"); return; }
            job.Load.Comparison = Pure.CraftComparator.Compare(job.Load.Output, capture.Craft);
            // After every load, KSP must prompt before a human Save overwrites the source with an upgraded or differing craft:
            // this path deliberately writes no .original backup.
            if (!ApplyOverwriteGuard(job)) { LoadFailure(job, OperationReasons.SaveOverwriteGuardUnavailable, "the save-name guard could not be written"); return; }
            job.EffectsApplied.Add("craft_loaded");
            SetPhase(job, OperationPhase.GraceStart, now);
        }

        /// <summary>After grace and the thumbnail settle: the human's file and its sidecars must hash as they did. A changed loadmeta is reported, never a failure.</summary>
        private void CheckLoadSource(OperationJob job)
        {
            var load = job.Load;
            if (load == null || load.SidecarBaseline.Count == 0) return;
            var changed = new List<string>();
            foreach (var pair in load.SidecarBaseline)
            {
                var now = HashOf(files, pair.Key);
                if (string.Equals(now, pair.Value, StringComparison.Ordinal)) continue;
                var name = System.IO.Path.GetFileName(pair.Key);
                if (pair.Key.EndsWith(".loadmeta", StringComparison.OrdinalIgnoreCase))
                {
                    load.SidecarChanges.Add(name + " (reported)");
                    job.Declared.Add(new DeclaredOutput { Path = pair.Key, Class = "ships-sidecar", Action = "reported", Detail = "changed during the load" });
                }
                else changed.Add(name);
            }
            load.SourceUnchanged = changed.Count == 0;
            if (changed.Count == 0) return;
            load.SidecarChanges.AddRange(changed);
            if (job.PendingStatus != null) return; // already failed another way: the report still says the file changed
            job.PendingStatus = JobStatuses.Failed; job.PendingReason = LoadReasons.SourceChangedDuringLoad; job.PendingDetail = string.Join(",", changed);
            // The editor keeps the loaded copy and the snapshot stays available: nothing is restored automatically.
            job.Restore = job.Restore ?? new RestoreInfo { Attempted = false, Result = "not_attempted_source_changed" };
        }

        /// <summary>The staged copy and the upgraded file go with the job, unless it is indeterminate. The one that was loaded is handled by the shared cleanup.</summary>
        private void CleanLoadFiles(OperationJob job)
        {
            var load = job.Load;
            if (load == null) return;
            foreach (var path in new[] { load.CopyPath, load.UpgradedPath })
            {
                if (path == null || path == job.StagingPath) continue;
                Remove(job, path);
            }
            foreach (var path in new[] { load.CopyPath, load.UpgradedPath, job.StagingPath })
                if (path != null && files.Exists(path + ".original")) Remove(job, path + ".original");
        }

        private void Remove(OperationJob job, string path)
        {
            if (job.KeepStaging || job.PendingStatus == JobStatuses.Indeterminate)
            {
                job.Declared.Add(new DeclaredOutput { Path = path, Class = "staging", Action = "reported", Detail = "kept: job is indeterminate" });
                return;
            }
            try
            {
                if (!files.Exists(path)) return;
                files.Delete(path);
                job.Declared.Add(new DeclaredOutput { Path = path, Class = "staging", Action = "deleted", Detail = "terminal state" });
            }
            catch (System.IO.IOException) { job.Declared.Add(new DeclaredOutput { Path = path, Class = "staging", Action = "reported", Detail = "could_not_delete" }); }
        }
    }
}
