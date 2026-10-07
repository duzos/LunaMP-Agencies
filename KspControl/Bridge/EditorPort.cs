using System;
using System.Collections.Generic;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    // Pure editor-state contracts: no Unity or KSP types, so decision logic links into the unit-test assembly.
    // Always spell the craft tree type as Pure.ConfigNode: in the bridge assembly an unqualified ConfigNode is KSP's.

    /// <summary>The three editor UI fields a craft header does not reliably reflect until KSP syncs it.</summary>
    internal sealed class EditorUi : IEquatable<EditorUi>
    {
        public string Name { get; }
        public string Description { get; }
        public string FlagUrl { get; }
        public EditorUi(string name, string description, string flagUrl) { Name = name ?? ""; Description = description ?? ""; FlagUrl = flagUrl ?? ""; }
        public bool Equals(EditorUi other) => other != null
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(Description, other.Description, StringComparison.Ordinal)
            && string.Equals(FlagUrl, other.FlagUrl, StringComparison.Ordinal);
        public override bool Equals(object obj) => Equals(obj as EditorUi);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name) ^ StringComparer.Ordinal.GetHashCode(Description) ^ StringComparer.Ordinal.GetHashCode(FlagUrl);
    }

    /// <summary>One native-save capture of the editor craft plus the UI fields read at the same moment.</summary>
    internal sealed class EditorCraft
    {
        public Pure.ConfigNode Craft { get; }
        public EditorUi Ui { get; }
        public EditorCraft(Pure.ConfigNode craft, EditorUi ui)
        { Craft = craft ?? throw new ArgumentNullException(nameof(craft)); Ui = ui ?? throw new ArgumentNullException(nameof(ui)); }
    }

    /// <summary>
    /// What the guarded reflection probe found. Resolved once at startup; every flag is conservative: a missing
    /// member makes the capability unavailable rather than guessed.
    /// </summary>
    internal sealed class EditorCapabilities
    {
        public bool Fsm { get; set; }
        public bool UnsavedMarker { get; set; }
        public bool SaveOverwriteGuard { get; set; }
        public bool VesselNameAtLastSave { get; set; }
        public bool VesselNameAtLastSaveSanitized { get; set; }
        public bool UndoLevel { get; set; }
        public bool UndoIndexAtLastSave { get; set; }
        public bool SetLastSanitizedSaveName { get; set; }
        public static EditorCapabilities None() { return new EditorCapabilities(); }
        public static string Text(bool available) { return available ? "available" : "unavailable"; }
    }

    /// <summary>One row of the paged part table of the engineering report.</summary>
    internal sealed class EngineeringPart
    {
        public uint CraftId { get; set; }
        public string Name { get; set; }
        public uint? ParentCraftId { get; set; }
        /// <summary>"root", "stack", "surface" or "other".</summary>
        public string AttachKind { get; set; }
        public string Node { get; set; }
        public string ParentNode { get; set; }
        /// <summary>The smallest craft id in the symmetry set, or null when the part has no counterpart.</summary>
        public uint? SymmetryGroup { get; set; }
        public int Stage { get; set; }
        public double? MassTonnes { get; set; }
        public double? ResourceMassTonnes { get; set; }
    }

    internal sealed class EngineeringStagePart
    {
        public uint CraftId { get; set; }
        public string Name { get; set; }
    }

    internal sealed class EngineeringStage
    {
        public int Stage { get; set; }
        public List<EngineeringStagePart> Parts { get; } = new List<EngineeringStagePart>();
    }

    internal sealed class EngineeringDeltaV
    {
        public int Stage { get; set; }
        public double? DeltaVVac { get; set; }
        public double? DeltaVAtmosphere { get; set; }
        public double? TwrVac { get; set; }
        public double? TwrAtmosphere { get; set; }
        public double? StartMassTonnes { get; set; }
        public double? EndMassTonnes { get; set; }
        public double? BurnTimeSeconds { get; set; }
    }

    /// <summary>Stock-only engineering numbers read from the live editor craft. Null members are reported as unavailable.</summary>
    internal sealed class EngineeringData
    {
        public int PartTotal { get; set; }
        public double? DryMassTonnes { get; set; }
        public double? FuelMassTonnes { get; set; }
        public double? DryCostFunds { get; set; }
        public double? FuelCostFunds { get; set; }
        public bool? AllPartsConnected { get; set; }
        public bool? ShipPartsUnlocked { get; set; }
        /// <summary>True/false, or null when the research instance is absent (sandbox) and the question does not apply.</summary>
        public bool? PartsStockAllowed { get; set; }
        public List<string> DeniedParts { get; } = new List<string>();
        public double? MaxStackNodeGapMetres { get; set; }
        public bool DeltaVRequested { get; set; }
        public bool DeltaVReady { get; set; }
        public List<EngineeringDeltaV> DeltaV { get; } = new List<EngineeringDeltaV>();
        public List<EngineeringStage> Stages { get; } = new List<EngineeringStage>();
        /// <summary>Only the requested page of part rows.</summary>
        public List<EngineeringPart> Parts { get; } = new List<EngineeringPart>();
    }

    /// <summary>
    /// The Unity-facing inputs of the editor-state layer. Main thread only. The Unity implementation is a thin adapter;
    /// every decision (revision, takeover, idle) lives in pure classes that are tested against a fake of this interface.
    /// </summary>
    internal interface IEditorPort
    {
        /// <summary>True while the editor scene is loaded, even if the craft object is momentarily null.</summary>
        bool EditorScene { get; }
        /// <summary>True when the editor scene is loaded and has a craft object to observe.</summary>
        bool InEditor { get; }
        /// <summary>The identity of the editor's ShipConstruct. A new object means load, new craft or undo restore.</summary>
        object ShipIdentity { get; }
        string Facility { get; }
        int PartCount { get; }
        bool HasSelectedPart { get; }
        /// <summary>The state machine's current state name, "not_started" before it runs, "unreadable" when the capability exists but this read failed, or null when the capability is unavailable.</summary>
        string FsmState { get; }
        /// <summary>The ids currently in InputLockManager's lock stack.</summary>
        IReadOnlyCollection<string> ActiveLockIds { get; }
        EditorUi ReadUi();
        /// <summary>The native-save capture (ShipConstruct.SaveShip converted to the pure tree). Null when unavailable.</summary>
        EditorCraft Capture();
        /// <summary>A cheap structural hash for dirty tracking: stage layout, action groups and UI fields, never a full save.</summary>
        string CheapHash();
        EditorCapabilities Capabilities { get; }
        /// <summary>True, false, or null when the unsaved marker cannot be read.</summary>
        bool? Unsaved { get; }
        /// <summary>vesselNameAtLastSave, or null when the guard fields are unavailable.</summary>
        string LastSavedName { get; }
        EngineeringData ReadEngineering(int offset, int limit, bool includeDeltaV);

        // ---- operation members (plan R4-section 6.4). Main thread only; each is a thin pass-through to one KSP call. ----

        /// <summary>The editor scene is loaded, the editor object exists and the editor is not restarting (the lease context's SceneReady).</summary>
        bool SceneReady { get; }
        /// <summary>EditorDriver.fetch.restartingEditor.</summary>
        bool RestartingEditor { get; }
        /// <summary>Every part of the editor craft has started (Part.started, or editorStarted as the alternative).</summary>
        bool AllPartsStarted { get; }
        /// <summary>The stock delta-V calculation for the editor craft has completed.</summary>
        bool DeltaVReady { get; }
        /// <summary>True while our own operation lock id is in InputLockManager's lock stack.</summary>
        bool OperationLockHeld { get; }
        /// <summary>Sets the InputLockManager control locks and the editor Lock, both under <see cref="EditorIdle.OperationLockId"/>. Idempotent.</summary>
        void SetOperationLock();
        /// <summary>Removes both locks. Idempotent and safe in any scene.</summary>
        void ClearOperationLock();
        /// <summary>The header facts of a live native save (version and _modVersions), or null when it cannot be read. Never throws.</summary>
        EditorHeader ReadHeader();
        /// <summary>FlightGlobals.GetUniquepersistentId().</summary>
        uint NextPersistentId();
        /// <summary>KSPUtil.SanitizeFilename.</summary>
        string SanitizeFileName(string name);
        /// <summary>ShipConstruction.AllPartsFound on the craft file at <paramref name="path"/>. <paramref name="missing"/> names the first problem.</summary>
        bool AllPartsFound(string path, out string missing);
        /// <summary>EditorLogic.LoadShipFromFile(path). Synchronous: the whole load happens inside this call. May throw.</summary>
        void LoadCraftFile(string path);
        /// <summary>Writes the ship name, description and flag UI fields.</summary>
        void WriteUi(EditorUi ui);
        /// <summary>vesselNameAtLastSave, its sanitised twin and the two undo counters, or null when any of them cannot be read.</summary>
        SaveFields ReadSaveFields();
        /// <summary>Sets vesselNameAtLastSave and its sanitised twin. False when the fields are unavailable or the write failed.</summary>
        bool TryWriteSavedName(string name, string sanitized);
        /// <summary>undoIndexAtLastSave = -1, so KSP treats the craft as unsaved. False when unavailable.</summary>
        bool TryMarkUnsaved();
        /// <summary>undoIndexAtLastSave = undoLevel, so KSP treats the craft as saved. False when unavailable.</summary>
        bool TryMarkSaved();
        /// <summary>The public EditorLogic.SetBackup(): the fallback that marks the craft modified. False when it could not be called.</summary>
        bool TrySetBackup();
        /// <summary>Crew seats as "craftId|seat|name", or null when the manifest cannot be read. Best effort.</summary>
        IReadOnlyList<string> ReadCrew();
    }

    /// <summary>The facts of a live native-save header that the renderer copies.</summary>
    internal sealed class EditorHeader
    {
        public string Version { get; }
        /// <summary>The header _modVersions value, or null when the header has none.</summary>
        public string ModVersions { get; }
        public EditorHeader(string version, string modVersions) { Version = version; ModVersions = modVersions; }
    }

    /// <summary>The private save-bookkeeping fields behind KSP's overwrite prompt and unsaved-changes test.</summary>
    internal sealed class SaveFields
    {
        public string Name { get; }
        public string Sanitized { get; }
        public int UndoLevel { get; }
        public int UndoIndexAtLastSave { get; }
        public SaveFields(string name, string sanitized, int undoLevel, int undoIndexAtLastSave)
        { Name = name; Sanitized = sanitized; UndoLevel = undoLevel; UndoIndexAtLastSave = undoIndexAtLastSave; }
    }
}
