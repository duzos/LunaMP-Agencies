using KspControl.Contracts;

namespace KspControl.Bridge
{
    /// <summary>The only place the trust layer reads Unity or KSP state. Main thread only.</summary>
    internal sealed class KspContextSource : IEditorContextSource
    {
        private readonly Observations observations;
        private readonly EditorRevisionTracker tracker;
        private string installId;
        public KspContextSource(Observations observations, EditorRevisionTracker tracker = null) { this.observations = observations; this.tracker = tracker; }

        public LeaseContext CurrentContext()
        {
            var editor = HighLogic.LoadedSceneIsEditor;
            var entity = editor ? "editor:" + (EditorDriver.editorFacility == EditorFacility.SPH ? "SPH" : "VAB") : "scene:" + HighLogic.LoadedScene;
            // Scene readiness is deliberately independent of the editor state machine; idle checks belong to admission.
            var ready = editor && EditorLogic.fetch != null && EditorDriver.fetch != null && !EditorDriver.fetch.restartingEditor;
            // The tracker is updated earlier in the same frame, so the published revision includes this frame's edits. It never decreases.
            return new LeaseContext(observations.WorldEpoch, entity, tracker == null ? 0 : tracker.EditRevision, ready);
        }

        public GrantBinding CurrentBinding()
        {
            var save = HighLogic.CurrentGame == null ? null : HighLogic.SaveFolder;
            if (string.IsNullOrWhiteSpace(save)) return null;
            if (installId == null) installId = GrantBindingKey.InstallId(KSPUtil.ApplicationRootPath);
            return new GrantBinding(installId, save, GrantBindingKey.AgencyKey(observations.AgencyId, save));
        }
    }
}
