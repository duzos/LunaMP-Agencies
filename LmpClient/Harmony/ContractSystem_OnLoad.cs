using Contracts;
using HarmonyLib;
using LmpClient.Systems.ShareContracts;
using LmpCommon.Enums;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// Wraps ContractSystem.OnLoad() with IgnoreEvents so that contracts restored from the
    /// server scenario data are not killed by the ContractOffered lock-ownership check.
    ///
    /// ContractSystem does NOT declare its own OnLoad — the method lives on ScenarioModule.
    /// HarmonyPatch attribute lookup only searches the declared methods of the specified type,
    /// so [HarmonyPatch(typeof(ContractSystem), "OnLoad")] silently finds nothing and skips.
    /// The correct target is ScenarioModule, which is the declaring type. We guard on
    /// __instance type so only the ContractSystem load is affected.
    /// </summary>
    [HarmonyPatch(typeof(ScenarioModule))]
    [HarmonyPatch("OnLoad")]
    public class ContractSystem_OnLoad
    {
        private static bool _wasIgnoring;

        [HarmonyPrefix]
        private static void PrefixOnLoad(ScenarioModule __instance)
        {
            if (!(__instance is ContractSystem)) return;
            if (MainSystem.NetworkState < ClientState.Connected) return;

            var system = ShareContractsSystem.Singleton;
            if (system?.Enabled != true) return;

            // KSP treats OnLoad() as a state restore and does not fire onOffered per contract,
            // so this guard is currently a no-op. It is kept as a safety net in case a future
            // KSP version or mod causes onOffered to fire during scenario restoration.
            _wasIgnoring = true;
            system.StartIgnoringEvents();
        }

        [HarmonyPostfix]
        private static void PostfixOnLoad(ScenarioModule __instance)
        {
            if (!(__instance is ContractSystem)) return;
            if (!_wasIgnoring) return;
            ShareContractsSystem.Singleton?.StopIgnoringEvents();
        }
    }
}
