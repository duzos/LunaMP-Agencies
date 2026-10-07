using System;
using KSP.UI.Screens;

namespace KspControl.Bridge
{
    /// <summary>
    /// Subscribes the tracker to the stock editor events (plan R3-§7). Handlers only forward a kind; they never read or save the
    /// craft, so an event fired from inside our own native save cannot recurse. Not linked into the unit-test assembly.
    /// </summary>
    internal sealed class EditorEvents : IDisposable
    {
        private readonly EditorRevisionTracker tracker;
        private readonly Action unsubscribe;

        public EditorEvents(EditorRevisionTracker tracker)
        {
            this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            var forwarders = new System.Collections.Generic.List<Action>();
            // One local forwarder per delegate shape, kept so every subscription can be removed again.
            Action<EditorEventKind> send = this.tracker.OnEvent;
            Add(forwarders, GameEvents.onEditorShipModified, (ShipConstruct s) => send(EditorEventKind.ShipModified));
            Add(forwarders, GameEvents.onEditorPartEvent, (ConstructionEventType t, Part p) => send(EditorEventKind.PartEvent));
            Add(forwarders, GameEvents.onEditorVariantApplied, (Part p, PartVariant v) => send(EditorEventKind.VariantApplied));
            Add(forwarders, GameEvents.onEditorShipCrewModified, (VesselCrewManifest m) => send(EditorEventKind.ShipCrewModified));
            Add(forwarders, GameEvents.onEditorSetBackup, (ShipConstruct s) => send(EditorEventKind.SetBackup));
            Add(forwarders, GameEvents.onEditorRestart, () => send(EditorEventKind.Restart));
            Add(forwarders, GameEvents.onEditorStarted, () => send(EditorEventKind.Started));
            Add(forwarders, GameEvents.onEditorRestoreState, () => send(EditorEventKind.RestoreState));
            Add(forwarders, GameEvents.onEditorPartPicked, (Part p) => send(EditorEventKind.PartPicked));
            Add(forwarders, GameEvents.onEditorPartPlaced, (Part p) => send(EditorEventKind.PartPlaced));
            Add(forwarders, GameEvents.onEditorPartDeleted, (Part p) => send(EditorEventKind.PartDeleted));
            Add(forwarders, GameEvents.onEditorPodPicked, (Part p) => send(EditorEventKind.PodPicked));
            Add(forwarders, GameEvents.onEditorPodDeleted, () => send(EditorEventKind.PodDeleted));
            Add(forwarders, GameEvents.onEditorUndo, (ShipConstruct s) => send(EditorEventKind.Undo));
            Add(forwarders, GameEvents.onEditorRedo, (ShipConstruct s) => send(EditorEventKind.Redo));
            Add(forwarders, GameEvents.onEditorLoad, (ShipConstruct s, CraftBrowserDialog.LoadType t) => send(EditorEventKind.Load));
            Add(forwarders, GameEvents.onPartActionUIShown, (UIPartActionWindow w, Part p) => send(EditorEventKind.PawShown));
            Add(forwarders, GameEvents.onPartActionUICreate, (Part p) => send(EditorEventKind.PawShown));
            unsubscribe = () => { foreach (var remove in forwarders) { try { remove(); } catch (Exception) { /* teardown */ } } forwarders.Clear(); };
        }

        private static void Add(System.Collections.Generic.List<Action> list, EventVoid evt, EventVoid.OnEvent handler)
        { evt.Add(handler); list.Add(() => evt.Remove(handler)); }
        private static void Add<T>(System.Collections.Generic.List<Action> list, EventData<T> evt, EventData<T>.OnEvent handler)
        { evt.Add(handler); list.Add(() => evt.Remove(handler)); }
        private static void Add<T, U>(System.Collections.Generic.List<Action> list, EventData<T, U> evt, EventData<T, U>.OnEvent handler)
        { evt.Add(handler); list.Add(() => evt.Remove(handler)); }

        public void Dispose() { unsubscribe(); }
    }
}
