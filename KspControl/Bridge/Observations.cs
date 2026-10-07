using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    internal sealed partial class Observations
    {
        private string epoch = Guid.NewGuid().ToString("N");
        private Game game;
        private GameScenes scene;
        private Guid agency;
        private long revision;
        private readonly Type facade = Type.GetType("LmpClient.Systems.Agency.ControlObservation, LmpClient", false);
        private bool FacadeCompatible
        {
            get { try { return (int?)facade?.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() == 1; } catch { return false; } }
        }
        internal const string BridgeVersion = "0.2.0";
        internal string WorldEpoch => epoch;
        internal Guid AgencyId => agency;
        /// <summary>Editor-state reads. Null when the bridge runs without the editor layer, which makes editor.state unavailable.</summary>
        private static readonly string ProcessId = Guid.NewGuid().ToString("N");
        internal EditorObservationService Editor { get; set; }
        internal EditorRevisionTracker EditorTracker { get; set; }
        /// <summary>Admission and status for the mutation operations. Null leaves them unavailable.</summary>
        internal EditorOperationService Operations { get; set; }
        /// <summary>MechJeb autopilot status and jobs. Null leaves them unavailable.</summary>
        internal MechJebService Autopilot { get; set; }
        public void RefreshContext()
        {
            var currentAgency = Agency();
            if (ReferenceEquals(game, HighLogic.CurrentGame) && scene == HighLogic.LoadedScene && agency == currentAgency) return;
            game = HighLogic.CurrentGame; scene = HighLogic.LoadedScene; agency = currentAgency;
            epoch = Guid.NewGuid().ToString("N"); revision = 0;
        }
        private Guid Agency()
        {
            try { return FacadeCompatible ? (Guid)(facade.GetProperty("AgencyId", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? Guid.Empty) : Guid.Empty; }
            catch { return Guid.Empty; }
        }
        private bool MayInspect(Vessel vessel)
        {
            try { return FacadeCompatible && vessel != null && agency != Guid.Empty && (bool)(facade.GetMethod("MayInspectActiveVessel")?.Invoke(null, new object[] { vessel.id }) ?? false); }
            catch { return false; }
        }
        public BridgeResponse Execute(BridgeRequest request)
        {
            RefreshContext();
            if (!string.IsNullOrEmpty(request.ExpectedWorldEpoch) && request.ExpectedWorldEpoch != epoch)
                return Fail(request, "stale_world");
            JObject data;
            try {
            switch (request.Operation)
            {
                case "bridge.capabilities": data = Capabilities(); break;
                case "game.context": data = Context(); break;
                case "parts.list": data = Catalog(request.Arguments); break;
                case ConstructionOperations.Catalog: data = ConstructionCatalog(request.Arguments); break;
                case "parts.definition":
                    var partName = ArgString(request.Arguments, "partName");
                    if (string.IsNullOrWhiteSpace(partName) || partName.Length > ObservationLimits.MaxPartName) return Fail(request, "invalid_part_name");
                    var definition = PartLoader.LoadedPartsList?.FirstOrDefault(p => p != null && p.name == partName);
                    if (definition?.partPrefab == null) return Fail(request, "definition_unavailable");
                    data = PartDefinition(definition, request.Arguments); break;
                case EditorOperations.State: case EditorOperations.Engineering:
                    if (Editor == null) return Fail(request, "operation_unavailable");
                    var editorResult = request.Operation == EditorOperations.State ? Editor.State() : Editor.Engineering(request.Arguments);
                    if (editorResult.Reason != null)
                    {
                        var failed = Fail(request, editorResult.Reason);
                        if (editorResult.Detail != null) failed.Data = new JObject { ["detail"] = editorResult.Detail };
                        return failed;
                    }
                    data = editorResult.Data; break;
                case EditorOperations.ApplyCraft: case EditorOperations.RestoreSnapshot: case EditorOperations.OperationStatus:
                    // Mutations answer with their own envelope: not an observation, so no readOnly marker and no size cap.
                    if (Operations == null) return Fail(request, "operation_unavailable");
                    var operation = Operations.Handle(request);
                    operation.WorldEpoch = epoch; operation.Revision = ++revision;
                    return operation;
                case AutopilotOperations.MechJebStatus: case AutopilotOperations.Status:
                case AutopilotOperations.Ascent: case AutopilotOperations.ExecuteNode: case AutopilotOperations.PlanCircularize: case AutopilotOperations.PlanHohmann:
                    // Autopilot operations answer with their own envelope, like the editor mutations.
                    if (Autopilot == null) return Fail(request, "operation_unavailable");
                    var autopilot = Autopilot.Handle(request);
                    autopilot.WorldEpoch = epoch; autopilot.Revision = ++revision;
                    return autopilot;
                case "editor.snapshot":
                    if (!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch?.ship == null) return Fail(request, "editor_unavailable");
                    data = EditorSnapshot(EditorLogic.fetch.ship, request.Arguments); break;
                case "editor.inspect":
                    if (!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return Fail(request, "editor_unavailable");
                    var ship = EditorLogic.fetch.ship;
                    data = new JObject { ["name"] = Text(ship.shipName), ["partsTotal"] = ship.Parts.Count,
                        ["offset"] = Offset(request.Arguments), ["nextOffset"] = NextOffset(ship.Parts.Count, request.Arguments),
                        ["parts"] = DescribeParts(ship.Parts, request.Arguments), ["massTonnes"] = Finite(ship.GetTotalMass()),
                        ["allPartsConnected"] = ship.AreAllPartsConnected(), ["deltaV"] = "unavailable_until_provider_validated" }; break;
                case "vessel.inspect":
                    var vessel = FlightGlobals.ActiveVessel;
                    if (!HighLogic.LoadedSceneIsFlight || !MayInspect(vessel)) return Fail(request, "owned_active_vessel_unavailable");
                    if (!string.IsNullOrEmpty(request.EntityId) && request.EntityId != vessel.id.ToString()) return Fail(request, "entity_unavailable");
                    data = new JObject { ["id"] = vessel.id.ToString(), ["name"] = Text(vessel.vesselName),
                        ["body"] = Text(vessel.mainBody?.bodyName), ["altitudeMetres"] = Finite(vessel.altitude),
                        ["surfaceSpeedMetresPerSecond"] = Finite(vessel.srfSpeed), ["situation"] = vessel.situation.ToString(),
                        ["partsTotal"] = vessel.parts.Count, ["parts"] = DescribeParts(vessel.parts, request.Arguments),
                        ["offset"] = Offset(request.Arguments), ["nextOffset"] = NextOffset(vessel.parts.Count, request.Arguments),
                        ["actionGroups"] = ActionGroups(vessel) }; break;
                case "part.controls": case "science.inspect":
                    var parts = AccessibleParts();
                    if (parts == null) return Fail(request, "craft_unavailable");
                    var requested = ArgString(request.Arguments, "partId");
                    var selected = parts.FirstOrDefault(p => p != null && p.persistentId.ToString() == requested);
                    if (selected == null) return Fail(request, "part_unavailable");
                    data = request.Operation == "part.controls" ? Controls(selected, request.Arguments) : Science(selected); break;
                default: return Fail(request, "operation_unavailable");
            }
            }
            catch (InvalidArgumentException) { return Fail(request, "invalid_argument"); }
            data["observedAtUtc"] = DateTime.UtcNow.ToString("O");
            data["readOnly"] = true;
            if (Encoding.UTF8.GetByteCount(data.ToString(Newtonsoft.Json.Formatting.None)) > 524288)
                return Fail(request, "snapshot_size_limit_use_smaller_page");
            return new BridgeResponse { RequestId = request.RequestId, Status = "completed", WorldEpoch = epoch, Revision = ++revision, Data = data };
        }
        private BridgeResponse Fail(BridgeRequest request, string reason)
        { var result = ObservationQueue.Failure(request, reason); result.WorldEpoch = epoch; result.Revision = revision; return result; }
        private JObject Context() => new JObject
        {
            ["scene"] = HighLogic.LoadedScene.ToString(), ["agencyId"] = agency == Guid.Empty ? null : agency.ToString(),
            ["agencyAdapterAvailable"] = FacadeCompatible, ["processId"] = Process.GetCurrentProcess().Id,
            ["protocolVersion"] = 1, ["bridgeVersion"] = BridgeVersion, ["universalTimeSeconds"] = game == null ? null : Finite(Planetarium.GetUniversalTime()),
            ["mutationAuthority"] = Operations == null ? "unavailable" : "lease_and_grant_required", ["revisionSemantics"] = "observation_sequence_not_mutation_precondition"
        };
        private JObject Capabilities() => new JObject
        {
            ["bridgeVersion"] = BridgeVersion,
            ["supported"] = new JArray("bridge.capabilities", "game.context", "parts.list", ConstructionOperations.Catalog, "parts.definition", "editor.snapshot", EditorOperations.State, EditorOperations.Engineering, "editor.inspect", "vessel.inspect", "part.controls", "science.inspect",
                EditorOperations.OperationStatus,
                AutopilotOperations.MechJebStatus, AutopilotOperations.Status),
            ["mutations"] = new JArray((Operations == null ? new string[0] : EditorOperations.Mutations).Concat(Autopilot == null ? new string[0] : AutopilotOperations.Mutations)),
            ["inline"] = new JArray(ControlOperations.All),
            ["unavailable"] = new JObject { ["mutations"] = Operations == null ? "operation_layer_not_wired" : "editor_load_craft_editor_save_craft_not_implemented", ["screenshots"] = "disclosure_validation_not_implemented",
                ["foreignContacts"] = "contact_adapter_not_implemented", ["mechjeb"] = Autopilot == null ? "adapter_not_wired" : "see mechjeb_status for the installed version and modules" },
            ["maximumPageSize"] = ObservationLimits.MaxPage
        };
        private static JObject Catalog(JObject args)
        {
            var query = Text(ArgString(args, "query") ?? "");
            var offset = Offset(args); var limit = Limit(args); var all = ArgBool(args, "includeNonBuildable");
            var source = (PartLoader.LoadedPartsList ?? new List<AvailablePart>()).Where(p => p != null && (all || Buildable(p)) &&
                (query.Length == 0 || (p.name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (p.title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            var page = source.Skip(offset).Take(limit + 1).ToArray();
            var items = new JArray();
            foreach (var part in page.Take(limit)) items.Add(new JObject { ["name"] = Text(part.name), ["title"] = Text(part.title),
                ["category"] = part.category.ToString(), ["buildable"] = Buildable(part), ["costFunds"] = Finite(part.cost), ["techRequired"] = Text(part.TechRequired),
                ["unlocked"] = ResearchAndDevelopment.Instance == null ? (JToken)JValue.CreateNull() : new JValue(ResearchAndDevelopment.PartTechAvailable(part)),
                ["massTonnes"] = part.partPrefab == null ? null : Finite(part.partPrefab.mass) });
            return new JObject { ["parts"] = items, ["offset"] = offset, ["nextOffset"] = page.Length > limit ? (JToken)new JValue(offset + limit) : JValue.CreateNull() };
        }
        private static bool Buildable(AvailablePart part) => PartFilters.IsBuildable(part.category == PartCategories.none, part.TechRequired, part.TechHidden);
        private List<Part> AccessibleParts()
        {
            if (HighLogic.LoadedSceneIsEditor) return EditorLogic.fetch?.ship?.Parts;
            var vessel = FlightGlobals.ActiveVessel;
            return HighLogic.LoadedSceneIsFlight && MayInspect(vessel) ? vessel.parts : null;
        }
        private static JArray DescribeParts(IEnumerable<Part> parts, JObject args)
        {
            var result = new JArray();
            foreach (var part in parts.Skip(Offset(args)).Take(Limit(args)))
            {
                if (part == null) continue;
                result.Add(new JObject { ["partId"] = part.persistentId.ToString(), ["name"] = Text(part.partInfo?.name),
                    ["title"] = Text(part.partInfo?.title), ["stage"] = part.inverseStage,
                    ["parentPartId"] = part.parent == null ? null : part.parent.persistentId.ToString(),
                    ["modules"] = new JArray(part.Modules.Cast<PartModule>().Take(64).Select(m => Text(m.moduleName))) });
            }
            return result;
        }
        private static JObject ActionGroups(Vessel vessel)
        {
            var result = new JObject();
            foreach (var group in new[] { KSPActionGroup.Gear, KSPActionGroup.Light, KSPActionGroup.Brakes, KSPActionGroup.SAS, KSPActionGroup.RCS,
                KSPActionGroup.Custom01, KSPActionGroup.Custom02, KSPActionGroup.Custom03, KSPActionGroup.Custom04, KSPActionGroup.Custom05,
                KSPActionGroup.Custom06, KSPActionGroup.Custom07, KSPActionGroup.Custom08, KSPActionGroup.Custom09, KSPActionGroup.Custom10 })
                result[group.ToString()] = vessel.ActionGroups[group];
            return result;
        }
        private static JObject Controls(Part part, JObject args)
        {
            var modules = new JArray();
            var offset = Offset(args); var end = Math.Min(part.Modules.Count, offset + ObservationLimits.PartControlsPage);
            for (var i = offset; i < end; i++)
            {
                var module = part.Modules[i]; var events = new JArray(); var fields = new JArray(); var actions = new JArray();
                foreach (BaseEvent ev in module.Events) if (events.Count < 32)
                    events.Add(new JObject { ["name"] = Text(ev.name), ["label"] = Text(ev.guiName), ["enabled"] = ev.active && !ev.EventIsDisabledByVariant,
                        ["visible"] = HighLogic.LoadedSceneIsEditor ? ev.guiActiveEditor : ev.guiActive, ["invocationSupported"] = false });
                foreach (BaseField field in module.Fields)
                {
                    if (fields.Count >= 32 || !(HighLogic.LoadedSceneIsEditor ? field.guiActiveEditor : field.guiActive)) continue;
                    var control = HighLogic.LoadedSceneIsEditor ? field.uiControlEditor : field.uiControlFlight;
                    // Values are deliberately withheld until per-control disclosure and serialization adapters exist.
                    var descriptor = new JObject { ["name"] = Text(field.name), ["label"] = Text(field.guiName), ["units"] = Text(field.guiUnits),
                        ["controlType"] = control?.GetType().Name, ["enabled"] = field.guiInteractable && control != null && control.controlEnabled, ["writeSupported"] = false };
                    var range = control as UI_FloatRange;
                    if (range != null) { descriptor["minimum"] = Finite(range.minValue); descriptor["maximum"] = Finite(range.maxValue); descriptor["step"] = Finite(range.stepIncrement); }
                    var choice = control as UI_ChooseOption;
                    if (choice != null) descriptor["options"] = new JArray((choice.options ?? new string[0]).Take(8).Select(Text));
                    fields.Add(descriptor);
                }
                foreach (BaseAction action in module.Actions) if (actions.Count < 32)
                    actions.Add(new JObject { ["name"] = Text(action.name), ["label"] = Text(action.guiName), ["group"] = action.actionGroup.ToString(),
                        ["enabled"] = HighLogic.LoadedSceneIsEditor ? action.activeEditor : action.active, ["invocationSupported"] = false });
                modules.Add(new JObject { ["moduleIndex"] = i, ["name"] = Text(module.moduleName), ["events"] = events, ["fields"] = fields, ["actions"] = actions });
            }
            return new JObject { ["partId"] = part.persistentId.ToString(), ["modules"] = modules,
                ["nextOffset"] = end < part.Modules.Count ? (JToken)new JValue(end) : JValue.CreateNull(),
                ["descriptorLimitPerKind"] = 32, ["optionLimit"] = 8, ["availability"] = "descriptors_only_not_execution_authority" };
        }
        private static JObject Science(Part part)
        {
            var experiments = new JArray();
            for (var i = 0; i < Math.Min(64, part.Modules.Count); i++)
            {
                var module = part.Modules[i] as ModuleScienceExperiment;
                if (module == null) continue;
                experiments.Add(new JObject { ["moduleIndex"] = i, ["experimentId"] = Text(module.experimentID), ["deployed"] = module.Deployed,
                    ["inoperable"] = module.Inoperable, ["rerunnable"] = module.rerunnable, ["hasData"] = module.HasExperimentData,
                    ["executionSupported"] = false });
            }
            return new JObject { ["partId"] = part.persistentId.ToString(), ["experiments"] = experiments, ["coverage"] = "stock_ModuleScienceExperiment_and_subclasses" };
        }
        private sealed class InvalidArgumentException : Exception { }
        private static JToken Arg(JObject args, string name) => args == null ? null : args[name];
        private static int ArgInt(JObject args, string name, int fallback)
        {
            var token = Arg(args, name);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type != JTokenType.Integer) throw new InvalidArgumentException();
            var value = (long)token;
            if (value < int.MinValue || value > int.MaxValue) throw new InvalidArgumentException();
            return (int)value;
        }
        private static bool ArgBool(JObject args, string name)
        {
            var token = Arg(args, name);
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type != JTokenType.Boolean) throw new InvalidArgumentException();
            return (bool)token;
        }
        private static string ArgString(JObject args, string name)
        {
            var token = Arg(args, name);
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) throw new InvalidArgumentException();
            return (string)token;
        }
        private static int Offset(JObject args) => Math.Max(0, Math.Min(ObservationLimits.MaxOffset, ArgInt(args, "offset", 0)));
        private static int Limit(JObject args) => Math.Max(1, Math.Min(ObservationLimits.MaxPage, ArgInt(args, "limit", ObservationLimits.DefaultPage)));
        private static JToken NextOffset(int total, JObject args)
        { var next = Offset(args) + Limit(args); return next < total ? (JToken)new JValue(next) : JValue.CreateNull(); }
        private static string Text(string value) => value == null ? null : value.Substring(0, Math.Min(256, value.Length));
        private static JToken Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? JValue.CreateNull() : new JValue(value);
    }
}
