using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    internal sealed class EditorResult
    {
        public string Reason { get; private set; }
        public string Detail { get; private set; }
        public JObject Data { get; private set; }
        public static EditorResult Ok(JObject data) { return new EditorResult { Data = data }; }
        public static EditorResult Fail(string reason, string detail = null) { return new EditorResult { Reason = reason, Detail = detail }; }
    }

    /// <summary>The craft identifier rule of ToolingClient.CraftIndices: every PART.part value ends in a unique unsigned integer.</summary>
    internal static class CraftIdentifierRule
    {
        public static bool Check(Pure.ConfigNode craft, out string problem)
        {
            problem = null;
            if (craft == null) { problem = "no_craft"; return false; }
            var seen = new HashSet<uint>();
            var index = 0;
            foreach (var part in craft.Children("PART"))
            {
                var key = part.First("part");
                uint id;
                if (string.IsNullOrEmpty(key) || !uint.TryParse(key.Substring(key.LastIndexOf('_') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                { problem = "invalid_identifier_at_part_" + index.ToString(CultureInfo.InvariantCulture); return false; }
                if (!seen.Add(id)) { problem = "duplicate_identifier_at_part_" + index.ToString(CultureInfo.InvariantCulture); return false; }
                index++;
            }
            return true;
        }
    }

    internal sealed class EngineeringArguments
    {
        public int Offset { get; private set; }
        public int Limit { get; private set; }
        public bool IncludeDeltaV { get; private set; }

        /// <summary>Strict parse: wrong types and out-of-range values are errors, never clamped.</summary>
        public static string TryParse(JObject args, out EngineeringArguments parsed)
        {
            parsed = null;
            var offset = 0; var limit = ObservationLimits.DefaultPage; var includeDeltaV = true;
            var token = args == null ? null : args["offset"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.Integer) return "offset must be an integer";
                var value = (long)token;
                if (value < 0 || value > ObservationLimits.MaxOffset) return "offset must be 0.." + ObservationLimits.MaxOffset.ToString(CultureInfo.InvariantCulture);
                offset = (int)value;
            }
            token = args == null ? null : args["limit"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.Integer) return "limit must be an integer";
                var value = (long)token;
                if (value < 1 || value > ObservationLimits.MaxPage) return "limit must be 1.." + ObservationLimits.MaxPage.ToString(CultureInfo.InvariantCulture);
                limit = (int)value;
            }
            token = args == null ? null : args["includeDeltaV"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.Boolean) return "includeDeltaV must be a boolean";
                includeDeltaV = (bool)token;
            }
            parsed = new EngineeringArguments { Offset = offset, Limit = limit, IncludeDeltaV = includeDeltaV };
            return null;
        }
    }

    /// <summary>
    /// Builds the editor.state and editor.engineering payloads from the port and the tracker. Pure: the same code runs
    /// against a fake port in tests and against the Unity adapter in the game. Read-only; never grants authority.
    /// </summary>
    internal sealed class EditorObservationService
    {
        /// <summary>Reported instead of the save-name sentinel's raw control characters.</summary>
        internal const string SentinelLastSavedName = "kspcontrol_unsaved_sentinel";
        /// <summary>The value the overwrite guard writes to vesselNameAtLastSave (R2-§8). Contains a control character on purpose.</summary>
        internal const string GuardSentinel = "\u0001kspcontrol-unsaved";
        private const int MaxText = 256;
        private const int MaxStageGroups = 100;
        private const int MaxStageParts = 50;
        private const int MaxDeltaVStages = 64;

        private readonly IEditorPort port;
        private readonly EditorRevisionTracker tracker;
        private readonly Func<string> worldEpoch;

        public EditorObservationService(IEditorPort port, EditorRevisionTracker tracker, Func<string> worldEpoch)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            this.worldEpoch = worldEpoch ?? throw new ArgumentNullException(nameof(worldEpoch));
        }

        public EditorResult State()
        {
            if (!port.InEditor) return EditorResult.Fail(ControlReasons.EditorUnavailable);
            var observed = tracker.Observe(true);
            if (!observed.InEditor) return EditorResult.Fail(ControlReasons.EditorUnavailable);
            var idle = EditorIdle.Evaluate(port.FsmState, port.HasSelectedPart, port.ActiveLockIds);
            var caps = port.Capabilities ?? EditorCapabilities.None();
            var unsaved = port.Unsaved;
            var lastSaved = port.LastSavedName;
            var data = new JObject
            {
                ["editorRevision"] = Str(tracker.Token(worldEpoch(), observed)),
                ["editRevision"] = observed.EditRevision,
                ["generation"] = observed.Generation,
                ["fingerprintAvailable"] = observed.FingerprintOk,
                ["fingerprintMode"] = tracker.Mode == FingerprintMode.Full ? "full" : "dirty_tracked",
                ["lastPollCostMs"] = Finite(tracker.LastPollCostMilliseconds),
                ["partCount"] = port.PartCount,
                ["facility"] = Str(Text(port.Facility)),
                ["shipName"] = observed.Ui == null ? JValue.CreateNull() : Str(Text(observed.Ui.Name)),
                ["unsaved"] = unsaved.HasValue ? (JToken)new JValue(unsaved.Value) : new JValue("unknown"),
                ["idle"] = idle.Idle,
                ["fsmState"] = idle.FsmState == null ? "unavailable" : idle.FsmState,
                ["busy"] = new JArray(idle.Busy),
                ["capabilities"] = new JObject
                {
                    ["fsm"] = EditorCapabilities.Text(caps.Fsm), ["unsavedMarker"] = EditorCapabilities.Text(caps.UnsavedMarker),
                    ["saveOverwriteGuard"] = EditorCapabilities.Text(caps.SaveOverwriteGuard),
                    ["guardMembers"] = new JObject
                    {
                        ["vesselNameAtLastSave"] = caps.VesselNameAtLastSave, ["vesselNameAtLastSave_Sanitized"] = caps.VesselNameAtLastSaveSanitized,
                        ["undoLevel"] = caps.UndoLevel, ["undoIndexAtLastSave"] = caps.UndoIndexAtLastSave, ["setLastSanitizedSaveName"] = caps.SetLastSanitizedSaveName
                    }
                },
                ["lastSavedName"] = lastSaved == null ? "unavailable" : (lastSaved == GuardSentinel ? SentinelLastSavedName : Text(lastSaved)),
                ["recentSnapshots"] = new JArray(),
                ["lastOperation"] = null,
                ["operationWindow"] = tracker.Window.ToString().ToLowerInvariant()
            };
            return EditorResult.Ok(data);
        }

        public EditorResult Engineering(JObject args)
        {
            EngineeringArguments parsed;
            var problem = EngineeringArguments.TryParse(args, out parsed);
            if (problem != null) return EditorResult.Fail(ControlReasons.InvalidArgument, problem);
            if (!port.InEditor) return EditorResult.Fail(ControlReasons.EditorUnavailable);
            var engineering = port.ReadEngineering(parsed.Offset, parsed.Limit, parsed.IncludeDeltaV);
            if (engineering == null) return EditorResult.Fail(ControlReasons.EditorUnavailable);
            var capture = SafeCapture();
            string identifierProblem = null;
            var identifiersValid = capture != null && CraftIdentifierRule.Check(capture.Craft, out identifierProblem);
            if (capture == null) identifierProblem = "capture_unavailable";
            var next = parsed.Offset + parsed.Limit;

            var parts = new JArray();
            foreach (var part in engineering.Parts)
                parts.Add(new JObject
                {
                    ["craftId"] = part.CraftId, ["ref"] = Text(part.Name) + "_" + part.CraftId.ToString(CultureInfo.InvariantCulture), ["name"] = Str(Text(part.Name)),
                    ["parentCraftId"] = part.ParentCraftId.HasValue ? (JToken)new JValue(part.ParentCraftId.Value) : JValue.CreateNull(),
                    ["attach"] = new JObject { ["kind"] = Str(part.AttachKind), ["node"] = Str(Text(part.Node)), ["parentNode"] = Str(Text(part.ParentNode)) },
                    ["symmetryGroup"] = part.SymmetryGroup.HasValue ? (JToken)new JValue(part.SymmetryGroup.Value) : JValue.CreateNull(),
                    ["stage"] = part.Stage, ["massTonnes"] = Finite(part.MassTonnes), ["resourceMassTonnes"] = Finite(part.ResourceMassTonnes)
                });

            var stages = new JArray();
            foreach (var stage in engineering.Stages.Take(MaxStageGroups))
                stages.Add(new JObject
                {
                    ["stage"] = stage.Stage,
                    ["parts"] = new JArray(stage.Parts.Take(MaxStageParts).Select(p => new JObject { ["craftId"] = p.CraftId, ["name"] = Str(Text(p.Name)) })),
                    ["partCount"] = stage.Parts.Count
                });

            var deltaV = new JObject { ["requested"] = engineering.DeltaVRequested, ["ready"] = engineering.DeltaVReady, ["stages"] = new JArray() };
            if (engineering.DeltaVRequested && engineering.DeltaVReady)
                deltaV["stages"] = new JArray(engineering.DeltaV.Take(MaxDeltaVStages).Select(s => new JObject
                {
                    ["stage"] = s.Stage, ["deltaVVacuum"] = Finite(s.DeltaVVac), ["deltaVAtmosphere"] = Finite(s.DeltaVAtmosphere),
                    ["twrVacuum"] = Finite(s.TwrVac), ["twrAtmosphere"] = Finite(s.TwrAtmosphere),
                    ["startMassTonnes"] = Finite(s.StartMassTonnes), ["endMassTonnes"] = Finite(s.EndMassTonnes), ["burnTimeSeconds"] = Finite(s.BurnTimeSeconds)
                }));

            var data = new JObject
            {
                ["partTotal"] = engineering.PartTotal, ["offset"] = parsed.Offset,
                ["nextOffset"] = next < engineering.PartTotal ? (JToken)new JValue(next) : JValue.CreateNull(),
                ["totals"] = new JObject
                {
                    ["dryMassTonnes"] = Finite(engineering.DryMassTonnes), ["fuelMassTonnes"] = Finite(engineering.FuelMassTonnes),
                    ["dryCostFunds"] = Finite(engineering.DryCostFunds), ["fuelCostFunds"] = Finite(engineering.FuelCostFunds),
                    ["allPartsConnected"] = Nullable(engineering.AllPartsConnected), ["shipPartsUnlocked"] = Nullable(engineering.ShipPartsUnlocked)
                },
                ["stages"] = stages, ["deltaV"] = deltaV,
                ["parts"] = parts,
                ["geometry"] = new JObject { ["maxLinkedStackNodeGapMetres"] = Finite(engineering.MaxStackNodeGapMetres) },
                // Stock research only: a missing research instance (sandbox) makes the question not apply rather than true.
                ["partsStockAllowed"] = Nullable(engineering.PartsStockAllowed),
                ["partsStockAllowedBasis"] = engineering.PartsStockAllowed.HasValue ? "PartTechAvailable_and_PartModelPurchased" : "no_research_instance",
                ["deniedParts"] = new JArray(engineering.DeniedParts.Take(16).Select(Text)),
                ["craftIdentifiersValid"] = identifiersValid,
                ["craftIdentifiersProblem"] = Str(identifierProblem),
                ["toolingQuote"] = "deferred_to_P3", ["launchResearchAllowance"] = "deferred_to_P3",
                ["provenance"] = new JObject
                {
                    ["totals"] = "ShipConstruct.GetShipMass_and_GetShipCosts_stock",
                    ["stages"] = "stageable_parts_by_Part.inverseStage_ordered_by_inStageIndex",
                    ["deltaV"] = "stock_VesselDeltaV_editor_simulation_no_mod_adapters",
                    ["geometry"] = "world_positions_of_linked_stack_nodes",
                    ["parts"] = "configured_editor_instances_current_observation",
                    ["craftIdentifiers"] = "ToolingClient.CraftIndices_rule_over_ShipConstruct.SaveShip"
                }
            };
            return EditorResult.Ok(data);
        }

        private EditorCraft SafeCapture()
        {
            return tracker.CaptureGuarded();
        }

        private static JToken Str(string value) { return value == null ? JValue.CreateNull() : new JValue(value); }
        private static JToken Nullable(bool? value) { return value.HasValue ? (JToken)new JValue(value.Value) : JValue.CreateNull(); }
        private static JToken Finite(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return JValue.CreateNull();
            return new JValue(value.Value);
        }
        private static string Text(string value)
        {
            if (value == null) return null;
            var chars = value.Where(c => !char.IsControl(c)).Take(MaxText).ToArray();
            return new string(chars);
        }
    }
}
