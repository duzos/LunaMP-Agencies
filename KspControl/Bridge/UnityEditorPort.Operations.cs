using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    /// <summary>
    /// The operation members of the Unity editor port (plan R4-section 6.4): one KSP call each, no decisions. Main thread only.
    /// Not linked into the unit-test assembly; the runner is tested against a fake of the same interface.
    /// </summary>
    internal sealed partial class UnityEditorPort
    {
        /// <summary>The control types an operation blocks: everything that could change, save, load or launch the craft, and stage or name editing.</summary>
        private const ControlTypes OperationControls = ControlTypes.EDITOR_LOCK | ControlTypes.EDITOR_SAVE | ControlTypes.EDITOR_LOAD | ControlTypes.EDITOR_NEW
            | ControlTypes.EDITOR_LAUNCH | ControlTypes.EDITOR_UNDO_REDO | ControlTypes.EDITOR_EDIT_STAGES | ControlTypes.EDITOR_EDIT_NAME_FIELDS;

        /// <summary>
        /// EditorLogic.Lock gets its own id: it may set a control lock under the id it is given, and a second SetControlLock with the same id would
        /// replace the first one's mask. The InputLockManager lock keeps the documented id, which is the one the idle check looks for.
        /// </summary>
        private const string EditorLockId = EditorIdle.OperationLockId + ".editor";

        public bool SceneReady => HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && EditorDriver.fetch != null && !EditorDriver.fetch.restartingEditor;
        public bool RestartingEditor => EditorDriver.fetch != null && EditorDriver.fetch.restartingEditor;

        public bool AllPartsStarted
        {
            get
            {
                var ship = Ship;
                if (ship == null || ship.Parts == null) return false;
                foreach (var part in ship.Parts) if (part != null && !(part.started || part.editorStarted)) return false;
                return true;
            }
        }

        public bool DeltaVReady { get { var ship = Ship; return ship != null && ship.vesselDeltaV != null && ship.vesselDeltaV.IsReady; } }

        public bool OperationLockHeld { get { var stack = InputLockManager.lockStack; return stack != null && stack.ContainsKey(EditorIdle.OperationLockId); } }

        public void SetOperationLock()
        {
            if (!OperationLockHeld) InputLockManager.SetControlLock(OperationControls, EditorIdle.OperationLockId);
            var editor = EditorLogic.fetch;
            if (editor != null) editor.Lock(true, true, true, EditorLockId);
        }

        public void ClearOperationLock()
        {
            InputLockManager.RemoveControlLock(EditorIdle.OperationLockId);
            var editor = EditorLogic.fetch;
            if (editor != null) editor.Unlock(EditorLockId);
        }

        public EditorHeader ReadHeader()
        {
            try
            {
                var ship = Ship;
                if (ship == null) return null;
                var node = ship.SaveShip();
                if (node == null) return null;
                var mods = node.GetValue("_modVersions");
                return new EditorHeader(node.GetValue("version"), string.IsNullOrEmpty(mods) ? null : mods);
            }
            catch (Exception) { return null; }
        }

        public uint NextPersistentId() { return FlightGlobals.GetUniquepersistentId(); }

        public string SanitizeFileName(string name) { return KSPUtil.SanitizeFilename(name ?? ""); }

        public bool AllPartsFound(string path, out string missing)
        {
            missing = null;
            try
            {
                var node = ConfigNode.Load(path);
                if (node == null) { missing = "craft_unreadable"; return false; }
                var reason = "";
                var found = ShipConstruction.AllPartsFound(node, ref reason);
                if (!found) missing = string.IsNullOrEmpty(reason) ? "parts_not_found" : reason;
                return found;
            }
            catch (Exception error) { missing = "check_failed_" + error.GetType().Name; return false; }
        }

        public void LoadCraftFile(string path) { EditorLogic.LoadShipFromFile(path); }

        public void WriteUi(EditorUi ui)
        {
            var editor = EditorLogic.fetch;
            if (editor == null) return;
            if (editor.shipNameField != null) editor.shipNameField.text = ui.Name;
            if (editor.shipDescriptionField != null) editor.shipDescriptionField.text = ui.Description;
            EditorLogic.FlagURL = ui.FlagUrl;
        }

        public SaveFields ReadSaveFields() { return reflection.TryReadSaveFields(EditorLogic.fetch); }
        public bool TryWriteSavedName(string name, string sanitized) { return reflection.TryWriteSavedName(EditorLogic.fetch, name, sanitized); }
        public bool TryMarkUnsaved() { return reflection.TryWriteUndoIndex(EditorLogic.fetch, -1); }
        public bool TryMarkSaved() { return reflection.TryMarkSaved(EditorLogic.fetch); }

        public bool TrySetBackup()
        {
            try { var editor = EditorLogic.fetch; if (editor == null) return false; editor.SetBackup(); return true; }
            catch (Exception) { return false; }
        }

        public IReadOnlyList<string> ReadCrew()
        {
            try
            {
                var manifest = ShipConstruction.ShipManifest;
                if (manifest == null || manifest.PartManifests == null) return null;
                var seats = new List<string>();
                foreach (var partManifest in manifest.PartManifests)
                {
                    var crew = partManifest.GetPartCrew();
                    for (var seat = 0; crew != null && seat < crew.Length; seat++)
                        if (crew[seat] != null) seats.Add(partManifest.PartID.ToString(CultureInfo.InvariantCulture) + "|" + seat.ToString(CultureInfo.InvariantCulture) + "|" + crew[seat].name);
                }
                return seats;
            }
            catch (Exception) { return null; }
        }

        // ---- surface placement (plan R1-section 6.5, P2.8): reads of the live craft only ----

        private static Part FindPart(uint craftId)
        {
            var ship = Ship;
            if (ship == null || ship.Parts == null) return null;
            foreach (var part in ship.Parts) if (part != null && part.craftID == craftId) return part;
            return null;
        }

        /// <summary>
        /// The part's own renderer boxes in its local frame: each MeshRenderer or SkinnedMeshRenderer under the part but not under a child part, its
        /// mesh-space bounds taken through the full transform chain, so a rotated or scaled mesh still gives an exact oriented box.
        /// </summary>
        private static List<Pure.Vector[]> RendererBoxes(Part part)
        {
            var boxes = new List<Pure.Vector[]>();
            foreach (var renderer in part.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer == null || !renderer.enabled || renderer.GetComponentInParent<Part>() != part) continue;
                Bounds local;
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null) local = skinned.localBounds;
                else if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    local = filter.sharedMesh.bounds;
                }
                else continue;
                var corners = new Pure.Vector[8];
                var i = 0;
                for (var x = -1; x <= 1; x += 2)
                    for (var y = -1; y <= 1; y += 2)
                        for (var z = -1; z <= 1; z += 2)
                        {
                            var world = renderer.transform.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3(x, y, z)));
                            var inPart = part.transform.InverseTransformPoint(world);
                            corners[i++] = new Pure.Vector(inPart.x, inPart.y, inPart.z);
                        }
                boxes.Add(corners);
            }
            return boxes;
        }

        public double? MeasureSurfaceRadius(uint parentCraftId, double localHeight)
        {
            try
            {
                var part = FindPart(parentCraftId);
                return part == null ? null : Pure.SurfaceCalibration.RadiusAtHeight(RendererBoxes(part), localHeight);
            }
            catch (Exception) { return null; }
        }

        public Pure.SurfaceNodeDefinition ReadSurfaceNode(uint craftId)
        {
            try
            {
                var part = FindPart(craftId);
                var node = part == null ? null : part.srfAttachNode;
                if (node == null) return null;
                return new Pure.SurfaceNodeDefinition
                {
                    Position = new Pure.Vector(node.position.x, node.position.y, node.position.z),
                    Orientation = new Pure.Vector(node.orientation.x, node.orientation.y, node.orientation.z)
                };
            }
            catch (Exception) { return null; }
        }
    }
}
