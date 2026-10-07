using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// The thin Unity/KSP adapter behind <see cref="IEditorPort"/>. It only reads the game and shapes values; every decision
    /// lives in the pure editor-state classes. Main thread only. Not linked into the unit-test assembly.
    /// </summary>
    internal sealed partial class UnityEditorPort : IEditorPort
    {
        private readonly EditorReflection reflection = new EditorReflection(typeof(EditorLogic));
        private const int MaxConfigEntries = 500000;

        public bool EditorScene => HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null;
        public bool InEditor => HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorLogic.fetch.ship != null;
        public object ShipIdentity { get { var editor = EditorLogic.fetch; return editor == null ? null : editor.ship; } }
        public string Facility => EditorDriver.editorFacility == EditorFacility.SPH ? "SPH" : "VAB";
        public int PartCount { get { var ship = Ship; return ship == null || ship.Parts == null ? 0 : ship.Parts.Count; } }
        public bool HasSelectedPart => EditorLogic.SelectedPart != null;
        public EditorCapabilities Capabilities => reflection.Capabilities;

        private static ShipConstruct Ship { get { var editor = EditorLogic.fetch; return editor == null ? null : editor.ship; } }

        public string FsmState
        {
            get
            {
                if (!reflection.Capabilities.Fsm) return null;
                string state; return reflection.TryReadFsmState(EditorLogic.fetch, out state) ? state : EditorIdle.FsmUnreadable;
            }
        }

        public IReadOnlyCollection<string> ActiveLockIds
        {
            get
            {
                var stack = InputLockManager.lockStack;
                lockIds.Clear();
                if (stack != null) foreach (var id in stack.Keys) lockIds.Add(id);
                return lockIds;
            }
        }

        private readonly List<string> lockIds = new List<string>();
        private EditorUi lastUi = new EditorUi("", "", "");

        /// <summary>Called every frame: the previous object is reused while the three strings are unchanged, so an idle editor allocates nothing.</summary>
        public EditorUi ReadUi()
        {
            var editor = EditorLogic.fetch;
            if (editor == null) return lastUi;
            var name = editor.shipNameField == null ? "" : editor.shipNameField.text ?? "";
            var description = editor.shipDescriptionField == null ? "" : editor.shipDescriptionField.text ?? "";
            var flag = EditorLogic.FlagURL ?? "";
            if (string.Equals(name, lastUi.Name, StringComparison.Ordinal) && string.Equals(description, lastUi.Description, StringComparison.Ordinal) && string.Equals(flag, lastUi.FlagUrl, StringComparison.Ordinal)) return lastUi;
            lastUi = new EditorUi(name, description, flag);
            return lastUi;
        }

        public EditorCraft Capture()
        {
            var ship = Ship;
            if (ship == null) return null;
            var ui = ReadUi();
            // An empty editor has nothing to save, and SaveShip on it is never needed.
            if (ship.Parts == null || ship.Parts.Count == 0) return new EditorCraft(new Pure.ConfigNode(""), ui);
            var budget = new int[] { MaxConfigEntries };
            return new EditorCraft(Convert(ship.SaveShip(), "", 0, budget), ui);
        }

        /// <summary>Values first, then child nodes: the order KSP itself prints. Bounded in depth and entry count.</summary>
        private static Pure.ConfigNode Convert(ConfigNode node, string name, int depth, int[] budget)
        {
            if (node == null) throw new InvalidOperationException("config_unavailable");
            if (depth > Pure.ConfigText.MaxDepth) throw new InvalidOperationException("config_too_deep");
            var result = new Pure.ConfigNode(name);
            foreach (ConfigNode.Value value in node.values)
            {
                if (--budget[0] < 0) throw new InvalidOperationException("config_too_large");
                result.AddValue(value.name, value.value);
            }
            foreach (ConfigNode child in node.nodes)
            {
                if (--budget[0] < 0) throw new InvalidOperationException("config_too_large");
                result.AddNode(Convert(child, child.name, depth + 1, budget));
            }
            return result;
        }

        /// <summary>
        /// A cheap structural hash (no native save): UI fields, then per part the craft id, parent, stage placement, manual stage
        /// offset and symmetry count, and per action its group mask. It exists to notice stage drags, action-group edits and renames,
        /// which fire no editor event.
        /// </summary>
        public string CheapHash()
        {
            var ship = Ship;
            if (ship == null) return null;
            unchecked
            {
                ulong h = 14695981039346656037UL;
                Action<long> mix = value => { h ^= (ulong)value; h *= 1099511628211UL; };
                Action<string> mixText = text => { if (text != null) foreach (var c in text) mix(c); mix(-1); };
                var ui = ReadUi(); mixText(ui.Name); mixText(ui.Description); mixText(ui.FlagUrl);
                foreach (var part in ship.Parts)
                {
                    if (part == null) continue;
                    mix(part.craftID); mix(part.parent == null ? 0 : part.parent.craftID); mix(part.inverseStage); mix(part.inStageIndex);
                    mix(part.manualStageOffset); mix(part.symmetryCounterparts == null ? 0 : part.symmetryCounterparts.Count);
                    foreach (PartModule module in part.Modules)
                        foreach (BaseAction action in module.Actions) mix((int)action.actionGroup);
                }
                return h.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        public bool? Unsaved
        {
            get
            {
                int level, saved;
                if (reflection.TryReadUndo(EditorLogic.fetch, out level, out saved)) return EditorUnsaved.Evaluate(level, saved, PartCount);
                return EditorUnsaved.Evaluate(null, null, PartCount);
            }
        }

        public string LastSavedName
        {
            get { string name; return reflection.TryReadLastSavedName(EditorLogic.fetch, out name) ? name : null; }
        }

        public EngineeringData ReadEngineering(int offset, int limit, bool includeDeltaV)
        {
            var ship = Ship;
            if (ship == null) return null;
            var parts = ship.Parts.Where(p => p != null).ToList();
            var result = new EngineeringData { PartTotal = parts.Count, DeltaVRequested = includeDeltaV };

            float dryMass, fuelMass, dryCost, fuelCost;
            ship.GetShipMass(out dryMass, out fuelMass); ship.GetShipCosts(out dryCost, out fuelCost);
            result.DryMassTonnes = dryMass; result.FuelMassTonnes = fuelMass; result.DryCostFunds = dryCost; result.FuelCostFunds = fuelCost;
            result.AllPartsConnected = ship.AreAllPartsConnected();
            result.ShipPartsUnlocked = ship.shipPartsUnlocked;

            // Stock research only. Without a research instance (sandbox) the question does not apply, and the result stays null.
            if (ResearchAndDevelopment.Instance != null)
            {
                var allowed = true;
                foreach (var part in parts)
                {
                    var info = part.partInfo;
                    if (info != null && ResearchAndDevelopment.PartTechAvailable(info) && ResearchAndDevelopment.PartModelPurchased(info)) continue;
                    allowed = false;
                    if (result.DeniedParts.Count < 16) result.DeniedParts.Add(info == null ? "unknown" : info.name);
                }
                result.PartsStockAllowed = allowed;
            }

            // Stageable parts by inverse stage, highest (first to fire) first, in the editor's in-stage order.
            foreach (var group in parts.Where(p => p.hasStagingIcon).GroupBy(p => p.inverseStage).OrderByDescending(g => g.Key))
            {
                var stage = new EngineeringStage { Stage = group.Key };
                foreach (var part in group.OrderBy(p => p.inStageIndex)) stage.Parts.Add(new EngineeringStagePart { CraftId = part.craftID, Name = part.partInfo == null ? null : part.partInfo.name });
                result.Stages.Add(stage);
            }

            if (includeDeltaV && ship.vesselDeltaV != null)
            {
                result.DeltaVReady = ship.vesselDeltaV.IsReady;
                if (result.DeltaVReady && ship.vesselDeltaV.OperatingStageInfo != null)
                    foreach (var stage in ship.vesselDeltaV.OperatingStageInfo)
                        result.DeltaV.Add(new EngineeringDeltaV
                        {
                            Stage = stage.stage, DeltaVVac = stage.deltaVinVac, DeltaVAtmosphere = stage.deltaVatASL, TwrVac = stage.TWRVac, TwrAtmosphere = stage.TWRASL,
                            StartMassTonnes = stage.startMass, EndMassTonnes = stage.endMass, BurnTimeSeconds = stage.stageBurnTime
                        });
            }

            // The largest world-space distance between the two nodes of every stack link.
            double gap = 0; var gapped = false;
            foreach (var part in parts)
            {
                if (part.parent == null || part.attachMode != AttachModes.STACK) continue;
                var child = part.attachNodes == null ? null : part.attachNodes.FirstOrDefault(n => n != null && n.attachedPart == part.parent);
                var upper = part.parent.attachNodes == null ? null : part.parent.attachNodes.FirstOrDefault(n => n != null && n.attachedPart == part);
                if (child == null || upper == null) continue;
                var distance = (part.transform.TransformPoint(child.position) - part.parent.transform.TransformPoint(upper.position)).magnitude;
                if (!gapped || distance > gap) gap = distance;
                gapped = true;
            }
            result.MaxStackNodeGapMetres = gapped ? (double?)gap : null;

            foreach (var part in parts.Skip(offset).Take(limit)) result.Parts.Add(Row(part));
            return result;
        }

        private static EngineeringPart Row(Part part)
        {
            var row = new EngineeringPart
            {
                CraftId = part.craftID, Name = part.partInfo == null ? null : part.partInfo.name,
                ParentCraftId = part.parent == null ? (uint?)null : part.parent.craftID,
                Stage = part.inverseStage, MassTonnes = part.mass + part.GetModuleMass(part.mass, ModifierStagingSituation.CURRENT), ResourceMassTonnes = part.GetResourceMass(), AttachKind = "root"
            };
            if (part.parent != null)
            {
                if (part.attachMode == AttachModes.STACK)
                {
                    row.AttachKind = "stack";
                    var child = part.attachNodes == null ? null : part.attachNodes.FirstOrDefault(n => n != null && n.attachedPart == part.parent);
                    var upper = part.parent.attachNodes == null ? null : part.parent.attachNodes.FirstOrDefault(n => n != null && n.attachedPart == part);
                    row.Node = child == null ? null : child.id; row.ParentNode = upper == null ? null : upper.id;
                }
                else if (part.attachMode == AttachModes.SRF_ATTACH) row.AttachKind = "surface";
                else row.AttachKind = "other";
            }
            if (part.symmetryCounterparts != null && part.symmetryCounterparts.Count > 0)
                row.SymmetryGroup = Math.Min(part.craftID, part.symmetryCounterparts.Where(p => p != null).Select(p => p.craftID).DefaultIfEmpty(part.craftID).Min());
            return row;
        }
    }
}
