using System;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    internal sealed partial class EditorOperationService
    {
        // ---------------------------------------------------------------- editor.save_craft

        /// <summary>
        /// Admission for editor_save_craft (plan R3-section 4): the same lease, grant, token, idle and dedupe checks as an apply, with the
        /// effect <c>craft.write</c> on <c>ships:&lt;facility&gt;</c>, then the target decision (create, or replace of a ledger-owned file).
        /// The runner decides the same thing again in the step that writes.
        /// </summary>
        private BridgeResponse Save(BridgeRequest request)
        {
            var args = request.Arguments ?? new JObject();
            string requestId, fileName, revision;
            var problem = First(Text(args, "requestId", out requestId), Text(args, "fileName", out fileName), Text(args, "expectedRevision", out revision));
            if (problem == null && !OperationLimits.IsRequestId(requestId)) problem = "requestId must match [A-Za-z0-9_-]{8,128}";
            if (problem == null && (!Pure.CraftPaths.IsName(fileName) || fileName.EndsWith(Pure.CraftPaths.CraftExtension, StringComparison.OrdinalIgnoreCase)))
                problem = "fileName must be 1..64 characters of A-Z a-z 0-9 space . _ -, without a leading dot or a .craft suffix";
            if (problem == null && revision.Length > OperationLimits.ExpectedRevisionMax) problem = "expectedRevision is too long";
            string replace = null;
            var replaceToken = args["replaceExpectedSha256"];
            if (problem == null && replaceToken != null && replaceToken.Type != JTokenType.Null)
            {
                if (replaceToken.Type != JTokenType.String || !OperationLimits.IsSha256((string)replaceToken)) problem = "replaceExpectedSha256 must be 64 lower-case hex characters";
                else replace = (string)replaceToken;
            }
            if (problem != null) return Refuse(request, ControlReasons.InvalidArgument, problem);
            var lease = LeaseOf(request);
            if (lease.Reason != null) return Refuse(request, lease.Reason, null);
            var fingerprint = OperationHash.Sha256Hex("save|" + lease.Id + "|" + fileName + "|" + (replace ?? "") + "|" + revision);
            var existing = Existing(request, requestId, fingerprint);
            if (existing != null) return existing;

            ExecutionTicket ticket; ClassifiedEffect[] effects;
            var refusal = AdmitCommon(request, OperationEffects.WriteCraft, lease.Id, revision, out ticket, out effects, "ships:");
            if (refusal != null) return refusal;
            if (port.PartCount == 0) return Refuse(request, OperationReasons.CraftEmpty, "the editor holds no craft to save");

            Pure.CraftPaths paths;
            try { paths = pathsFactory(); } catch (ArgumentException) { return Refuse(request, OperationReasons.PathOutsideSave, "the save folder name is not usable"); }
            var target = paths.ResolveNewShip(port.Facility, fileName);
            if (!target.Ok)
            {
                if (target.ReasonCode == "invalid_file_name") return Refuse(request, ControlReasons.InvalidArgument, "fileName is not a usable ship name");
                if (target.ReasonCode == "facility_mismatch") return Refuse(request, ControlReasons.FacilityMismatch, "the editor facility cannot hold ship files");
                return Refuse(request, OperationReasons.PathOutsideSave, target.ReasonCode);
            }
            var ledgerPath = paths.LedgerPath();
            if (!ledgerPath.Ok) return Refuse(request, OperationReasons.PathOutsideSave, ledgerPath.ReasonCode);
            var ledger = CraftLedger.Load(files, ledgerPath.FullPath);
            bool exists = false; string currentHash = null;
            try
            {
                exists = files.Exists(target.FullPath);
                if (exists)
                {
                    var length = files.FileLength(target.FullPath);
                    if (length > EditorOperationRunner.MaxSaveBytes)
                    {
                        // Hashing runs on the main thread: an oversize file is never read. Without a replace token it is simply "there".
                        if (replace != null) return Refuse(request, OperationReasons.CraftTooLarge, "the existing file is " + length + " bytes");
                    }
                    else currentHash = OperationHash.Sha256Hex(files.ReadAllBytes(target.FullPath));
                }
            }
            catch (System.IO.IOException) { exists = true; }
            var decision = SavePolicy.Decide(exists, currentHash, replace, ledger.Find(port.Facility, fileName + Pure.CraftPaths.CraftExtension), ledger.State);
            if (decision.Action == SaveAction.Refuse) return Refuse(request, decision.Reason, decision.Detail);

            var job = NewJob(request, OperationKind.Save, requestId, fingerprint, lease.Id, revision, ticket, effects);
            job.Save = new SaveRequest { FileName = fileName, Facility = port.Facility, ReplaceExpectedSha256 = replace };
            job.ReplacedSha256 = decision.ReplacedSha256;
            return StartJob(request, job);
        }
    }
}
