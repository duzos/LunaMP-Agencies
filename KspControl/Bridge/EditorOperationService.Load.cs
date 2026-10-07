using System;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// editor.load_craft admission (plan R3-section 4, R4-section 6.4): argument and path checks, the common authority, token and idle
    /// checks, and the unsaved-craft policy. The staging, the upgrade pipeline and the load itself run later in the runner.
    /// </summary>
    internal sealed partial class EditorOperationService
    {
        private BridgeResponse LoadCraft(BridgeRequest request)
        {
            var args = request.Arguments ?? new JObject();
            string requestId, facility, fileName, sha, revision;
            var problem = First(Text(args, "requestId", out requestId), Text(args, "facility", out facility), Text(args, "fileName", out fileName),
                Text(args, "expectedSha256", out sha), Text(args, "expectedRevision", out revision));
            var allowUpgrade = false;
            if (problem == null && args["allowUpgrade"] != null && args["allowUpgrade"].Type != JTokenType.Null)
            {
                if (args["allowUpgrade"].Type != JTokenType.Boolean) problem = "allowUpgrade must be a boolean";
                else allowUpgrade = (bool)args["allowUpgrade"];
            }
            if (problem == null && !OperationLimits.IsRequestId(requestId)) problem = "requestId must match [A-Za-z0-9_-]{8,128}";
            if (problem == null && !LoadLimits.IsFacility(facility)) problem = "facility must be VAB or SPH";
            if (problem == null && fileName.Length > LoadLimits.FileNameMax) problem = "fileName is too long";
            if (problem == null && !LoadLimits.IsSha256(sha)) problem = "expectedSha256 must be 64 lower-case hex characters";
            if (problem == null && revision.Length > OperationLimits.ExpectedRevisionMax) problem = "expectedRevision is too long";
            if (problem != null) return Refuse(request, ControlReasons.InvalidArgument, problem);
            var lease = LeaseOf(request);
            if (lease.Reason != null) return Refuse(request, lease.Reason, null);
            var fingerprint = OperationHash.Sha256Hex("load|" + lease.Id + "|" + facility + "|" + fileName + "|" + sha + "|" + (allowUpgrade ? "1" : "0") + "|" + revision);
            var existing = Existing(request, requestId, fingerprint);
            if (existing != null) return existing;

            ExecutionTicket ticket; ClassifiedEffect[] effects;
            var refusal = AdmitCommon(request, OperationEffects.ReplaceCraft, lease.Id, revision, out ticket, out effects);
            if (refusal != null) return refusal;
            if (!string.Equals(facility, port.Facility, StringComparison.Ordinal)) return Refuse(request, ControlReasons.FacilityMismatch, "the request names " + facility + " but the editor is " + port.Facility);

            Pure.PathCheck source;
            try { source = pathsFactory().ResolveExistingShip(facility, fileName); }
            catch (ArgumentException) { return Refuse(request, OperationReasons.PathOutsideSave, "the save folder name is not usable"); }
            if (!source.Ok)
            {
                var reason = source.ReasonCode == "invalid_file_name" || source.ReasonCode == "invalid_extension" ? ControlReasons.InvalidArgument
                    : source.ReasonCode == "facility_mismatch" ? ControlReasons.FacilityMismatch : OperationReasons.PathOutsideSave;
                return Refuse(request, reason, source.ReasonCode);
            }
            if (!files.Exists(source.FullPath)) return Refuse(request, LoadReasons.CraftNotFound, fileName);

            // Loading replaces the craft: the same unsaved-work policy as an apply (plan R2-section 8). A craft this service made and nobody touched is not human work.
            var capabilities = port.Capabilities ?? EditorCapabilities.None();
            if (port.PartCount > 0 && port.Unsaved != false && !IsOwnUntouchedCraft())
            {
                var policy = authority.Status().Grant.UnsavedCraftPolicy ?? "refuse";
                if (policy != "snapshot_then_replace") return Refuse(request, OperationReasons.UnsavedHumanCraft, "the editor holds unsaved work and the grant policy is " + policy);
                if (!capabilities.SaveOverwriteGuard) return Refuse(request, OperationReasons.SaveOverwriteGuardUnavailable, "snapshot_then_replace needs the save-name guard");
            }

            var job = NewJob(request, OperationKind.Load, requestId, fingerprint, lease.Id, revision, ticket, effects);
            job.Load = new LoadState { Facility = facility, FileName = fileName, SourcePath = source.FullPath, ExpectedSha256 = sha, AllowUpgrade = allowUpgrade };
            return StartJob(request, job);
        }
    }
}
