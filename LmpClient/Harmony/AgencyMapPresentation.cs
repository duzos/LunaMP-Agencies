using System;
using HarmonyLib;
using LmpClient.Diagnostics;
using LmpClient.Systems.PlayerColorSys;
using LmpCommon.Enums;

namespace LmpClient.Harmony
{
    /// <summary>
    /// Plan 41 S1: map / tracking-station presentation patches. Stock <c>OrbitRenderer.Start</c> sets the
    /// renderer grey after the vessel exists, which would undo the agency tint applied on vessel creation,
    /// so a postfix re-applies it.
    /// </summary>
    internal static class AgencyMapPresentation
    {
        internal static void Install(HarmonyLib.Harmony harmony)
        {
            var start = AccessTools.Method(typeof(OrbitRenderer), "Start", Type.EmptyTypes) ?? throw new MissingMethodException(typeof(OrbitRenderer).FullName, "Start");
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(AgencyMapPresentation), nameof(OrbitRendererStartPostfix)));
        }

        private static void OrbitRendererStartPostfix(OrbitRenderer __instance)
        {
            try
            {
                if (MainSystem.NetworkState < ClientState.Connected || !__instance || !__instance.vessel) return;
                var system = PlayerColorSystem.Singleton;
                if (system == null || !system.Enabled) return;
                system.SetVesselOrbitColor(__instance.vessel, __instance);
            }
            catch (Exception e)
            {
                PlaytestDiagnostics.Write("client.agency-presentation.orbit-tint-error", () => e.Message);
            }
        }
    }
}
