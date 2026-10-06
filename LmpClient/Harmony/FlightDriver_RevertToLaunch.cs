using HarmonyLib;
using LmpClient.Events;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to trigger an event when reverting to launch
    /// </summary>
    [HarmonyPatch(typeof(FlightDriver))]
    [HarmonyPatch("RevertToLaunch")]
    public class FlightDriver_RevertToLaunch
    {
        [HarmonyPrefix]
        private static bool PrefixRevertToLaunch()
        {
            if (!LmpClient.Systems.Agency.ToolingClient.BeginRevert(EditorFacility.None, true)) return false;
            RevertEvent.onRevertingToLaunch.Fire();
            return true;
        }

        [HarmonyPostfix]
        private static void PostfixRevertToLaunch(bool __runOriginal)
        {
            if (!__runOriginal) return;
            RevertEvent.onRevertedToLaunch.Fire();
        }
    }
}
