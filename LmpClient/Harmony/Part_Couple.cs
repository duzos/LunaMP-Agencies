using HarmonyLib;
using LmpClient.Events;
using LmpClient.VesselUtilities;
using System;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to trigger an event when coupling a part
    /// </summary>
    [HarmonyPatch(typeof(Part))]
    [HarmonyPatch("Couple")]
    public class Part_Couple
    {
        private sealed class CoupleState { public Guid Removed; public bool Entered; }

        [HarmonyPrefix]
        private static bool PrefixCouple(Part __instance, Part tgtPart, ref CoupleState __state)
        {
            __state = new CoupleState();
            if (VesselCommon.IsSpectating && !LmpClient.Systems.Agency.DockingCoordinator.Replaying) return false;
            if (!LmpClient.Systems.Agency.DockingCoordinator.BeforePartCouple(__instance, tgtPart, out __state.Entered)) return false;

            __state.Removed = __instance.vessel.id;
            PartEvent.onPartCoupling.Fire(__instance, tgtPart);

            return true;
        }

        [HarmonyPostfix]
        private static void PostfixCouple(Part __instance, Part tgtPart, ref CoupleState __state)
        {
            if (__state == null || __state.Removed == Guid.Empty || LmpClient.Systems.Agency.DockingCoordinator.Replaying || VesselCommon.IsSpectating) return;

            PartEvent.onPartCoupled.Fire(__instance, tgtPart, __state.Removed);
        }
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, CoupleState __state)
        {
            LmpClient.Systems.Agency.DockingCoordinator.Exit(__state?.Entered ?? false, __exception);
            return __exception;
        }
    }
}
