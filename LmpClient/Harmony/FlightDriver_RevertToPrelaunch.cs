using HarmonyLib;
using LmpClient.Events;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to trigger an event when reverting to prelaunch (reverting to editor)
    /// </summary>
    [HarmonyPatch(typeof(FlightDriver))]
    [HarmonyPatch("RevertToPrelaunch")]
    public class FlightDriver_RevertToPrelaunch
    {
        [HarmonyPrefix]
        private static bool PrefixRevertToPrelaunch(EditorFacility facility)
        {
            if (!LmpClient.Systems.Agency.ToolingClient.BeginRevert(facility, false)) return false;
            RevertEvent.onReturningToEditor.Fire(facility);
            return true;
        }

        [HarmonyPostfix]
        private static void PostfixRevertToPrelaunch(EditorFacility facility, bool __runOriginal)
        {
            if (!__runOriginal) return;
            RevertEvent.onReturnedToEditor.Fire(facility);
        }
    }
}
