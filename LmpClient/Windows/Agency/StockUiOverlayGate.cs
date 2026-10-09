using KSP.UI;

namespace LmpClient.Windows.Agency
{
    /// <summary>
    /// Plan 41: shared "is stock UI in the way" test for the screen-space agency overlays (nameplates, site
    /// flags), so they never draw over a hidden UI (F2), the pause menu or a KSC facility screen. Allocation free.
    /// </summary>
    internal static class StockUiOverlayGate
    {
        private const ulong KscUi = (ulong)ControlTypes.KSC_UI;
        private const ulong WindowHoverLock = (ulong)ControlTypes.ALLBUTCAMERAS;

        /// <summary>True when the overlays must not draw this frame.</summary>
        internal static bool Covered()
        {
            var ui = UIMasterController.Instance;
            if (ui != null && !ui.IsUIShowing) return true; // F2
            if (PauseMenu.exists && PauseMenu.isOpen) return true;
            return HighLogic.LoadedScene == GameScenes.SPACECENTER && KscFacilityOpen();
        }

        /// <summary>
        /// Facility screens (R&amp;D, admin, mission control, astronaut complex, flag pole, launch site picker) and the
        /// recovery / demolish dialogs lock <see cref="ControlTypes.KSC_UI"/>. KSC_FACILITIES is not used: the
        /// camera drag and building hover lock it too. LMP (and most mod) windows lock ALLBUTCAMERAS while hovered,
        /// which also contains KSC_UI, so those locks are ignored.
        /// </summary>
        private static bool KscFacilityOpen()
        {
            if ((InputLockManager.lockMask & KscUi) == 0) return false;
            foreach (var pair in InputLockManager.lockStack)
                if ((pair.Value & KscUi) != 0 && pair.Value != WindowHoverLock) return true;
            return false;
        }
    }
}
