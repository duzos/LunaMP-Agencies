using HarmonyLib;
using LmpClient.Events;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// This harmony patch is intended to trigger an event when docking a vessel
    /// </summary>
    [HarmonyPatch(typeof(ModuleDockingNode))]
    [HarmonyPatch("DockToVessel")]
    public class ModuleDockingNode_DockToVessel
    {
        [HarmonyPrefix]
        private static bool PrefixDockToVessel(ModuleDockingNode __instance, ModuleDockingNode node, out bool __state)
        {
            if (!LmpClient.Systems.Agency.DockingCoordinator.Enter(__instance.part, node?.part, LmpCommon.Enums.CoupleTrigger.DockingNode, out __state)) return false;
            VesselDockEvent.onDocking.Fire(__instance.vessel, node.vessel);
            return true;
        }

        [HarmonyPostfix]
        private static void PostfixDockToVessel(ModuleDockingNode __instance, ModuleDockingNode node, bool __runOriginal)
        {
            if (!__runOriginal) return;
            VesselDockEvent.onDockingComplete.Fire(__instance.vessel, node.vessel);
        }
        [HarmonyFinalizer]
        private static System.Exception Finalizer(System.Exception __exception, bool __state)
        {
            LmpClient.Systems.Agency.DockingCoordinator.Exit(__state, __exception);
            return __exception;
        }
    }
}
