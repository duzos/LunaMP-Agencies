using KspControl.Contracts;

namespace KspControl.Bridge
{
    /// <summary>The only place the trust layer reads Unity or KSP state. Main thread only.</summary>
    internal sealed class KspContextSource : IEditorContextSource
    {
        private readonly Observations observations;
        private string installId;
        public KspContextSource(Observations observations) { this.observations = observations; }

        public LeaseContext CurrentContext()
        {
            var editor = HighLogic.LoadedSceneIsEditor;
            var entity = editor ? "editor:" + (EditorDriver.editorFacility == EditorFacility.SPH ? "SPH" : "VAB") : "scene:" + HighLogic.LoadedScene;
            // Scene readiness is deliberately independent of the editor state machine; idle checks belong to admission.
            var ready = editor && EditorLogic.fetch != null && EditorDriver.fetch != null && !EditorDriver.fetch.restartingEditor;
            // Editor revision tracking arrives with the editor-state slice; until then the revision is constant.
            return new LeaseContext(observations.WorldEpoch, entity, 0, ready);
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
