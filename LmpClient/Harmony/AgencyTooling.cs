using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LmpClient.Systems.Agency;
using LmpClient.Systems.ShareFunds;
using LmpClient.Systems.ShareScience;
using LmpCommon.Agency;

namespace LmpClient.Harmony
{
    public static class AgencyTooling
    {
        public static bool Ready { get; private set; }
        public static string DiagnosticReason { get; private set; }
        [ThreadStatic] private static CurrencyChange currency;
        [ThreadStatic] private static RecoveryContext recovery;
        private sealed class CurrencyChange
        {
            internal CurrencyChange Parent;
            internal double Before, Children;
            internal bool Science, Suppress;
        }
        private sealed class RecoveryContext
        {
            internal RecoveryContext Previous;
            internal ProtoVessel Vessel;
            internal PaidVesselRecord Paid;
            internal HashSet<ProtoPartSnapshot> Physical;
            internal List<RecoveryPart> Parts = new List<RecoveryPart>();
            internal List<ToolingCargo> Cargo = new List<ToolingCargo>();
            internal double Factor, CurrentMultiplier = 1;
            internal Queue<Tuple<uint, string>> CargoHosts = new Queue<Tuple<uint, string>>();
        }
        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                Patch(harmony, typeof(FlightDriver), "StartWithNewLaunch", new[] { typeof(string), typeof(string), typeof(string), typeof(VesselCrewManifest) }, nameof(Launch));
                var affordability = AccessTools.Constructor(typeof(PreFlightTests.CanAffordLaunchTest), new[] { typeof(ShipConstruct), typeof(Funding) });
                if (affordability == null) throw new MissingMethodException("CanAffordLaunchTest(ShipConstruct, Funding)");
                harmony.Patch(affordability, postfix: new HarmonyMethod(typeof(AgencyTooling), nameof(Affordability)));
                var templateAffordability = AccessTools.Constructor(typeof(PreFlightTests.CanAffordLaunchTest), new[] { typeof(ShipTemplate), typeof(Funding) });
                if (templateAffordability == null) throw new MissingMethodException("CanAffordLaunchTest(ShipTemplate, Funding)");
                harmony.Patch(templateAffordability, postfix: new HarmonyMethod(typeof(AgencyTooling), nameof(TemplateAffordability)));
                Patch(harmony, typeof(Funding), "onVesselRollout", new[] { typeof(ShipConstruct) }, nameof(Rollout));
                foreach (var name in new[] { "AddFunds", "SetFunds" }) Patch(harmony, typeof(Funding), name, new[] { typeof(double), typeof(TransactionReasons) }, nameof(Funds), null, nameof(CurrencyFinalizer));
                foreach (var name in new[] { "AddScience", "SetScience" }) Patch(harmony, typeof(ResearchAndDevelopment), name, new[] { typeof(float), typeof(TransactionReasons) }, nameof(Science), null, nameof(CurrencyFinalizer));
                Patch(harmony, typeof(Funding), "onVesselRecoveryProcessing", new[] { typeof(ProtoVessel), typeof(KSP.UI.Screens.MissionRecoveryDialog), typeof(float) }, nameof(Recovery), null, nameof(RecoveryFinalizer));
                Patch(harmony, typeof(ShipConstruction), "GetPartCosts", new[] { typeof(ProtoPartSnapshot), typeof(bool), typeof(AvailablePart), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }, null, nameof(PartCost));
                Patch(harmony, typeof(KSP.UI.Screens.SpaceCenter.MissionSummaryDialog.ResourceWidget), "Create", new[] { typeof(PartResourceDefinition), typeof(float), typeof(float), typeof(KSP.UI.Screens.MissionRecoveryDialog) }, nameof(ResourceCost));
                Patch(harmony, typeof(MainSystem), "Update", Type.EmptyTypes, null, nameof(Tick));
                Patch(harmony, typeof(KerbalEVA), "proceedAndBoard", new[] { typeof(Part) }, nameof(Board), null, nameof(BoardFinalizer));
                GameEvents.onLevelWasLoadedGUIReady.Add(ToolingClient.SceneChanged);
                Ready = true;
            }
            catch (Exception e) { Ready = false; DiagnosticReason = e.Message; LunaLog.LogError("[Tooling] Required hooks unavailable: " + e); }
        }
        private static void Patch(HarmonyLib.Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix = null, string finalizer = null)
        {
            var target = AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(AgencyTooling), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(AgencyTooling), postfix), null,
                finalizer == null ? null : new HarmonyMethod(typeof(AgencyTooling), finalizer));
        }
        private static bool Launch(string __0, string __1, string __2, VesselCrewManifest __3)
            => !ToolingClient.Enabled || Ready && ToolingClient.BeginLaunch(__0, __1, __2, __3);
        private static void Affordability(PreFlightTests.CanAffordLaunchTest __instance, ShipConstruct __0)
        {
            if (!ToolingClient.Enabled) return;
            var cost = Ready ? ToolingClient.AffordableCost(__0) : float.MaxValue;
            AccessTools.Field(typeof(PreFlightTests.CanAffordLaunchTest), "launchCost").SetValue(__instance, cost);
        }
        private static void TemplateAffordability(PreFlightTests.CanAffordLaunchTest __instance, ShipTemplate __0)
        {
            if (!ToolingClient.Enabled) return;
            AccessTools.Field(typeof(PreFlightTests.CanAffordLaunchTest), "launchCost").SetValue(__instance, Ready ? ToolingClient.AffordableTemplateCost(__0) : float.MaxValue);
        }
        private static bool Board(KerbalEVA __instance, Part __0, out bool __state) => ToolingClient.BeginBoarding(__instance, __0, out __state);
        private static Exception BoardFinalizer(Exception __exception, bool __state) { ToolingClient.EndBoarding(__state, __exception); return __exception; }
        private static bool Rollout() => ToolingClient.BeforeRollout();
        private static bool Funds(out CurrencyChange __state)
        {
            __state = null;
            if (!ToolingClient.Enabled) return true;
            if (recovery != null) return false; // Recovery is paid once by the durable server settlement.
            __state = BeginCurrency(false, Funding.Instance.Funds, ShareFundsSystem.Singleton.IgnoreEvents);
            return true;
        }
        private static void Science(out CurrencyChange __state)
        {
            __state = ToolingClient.Enabled ? BeginCurrency(true, ResearchAndDevelopment.Instance.Science, ShareScienceSystem.Singleton.IgnoreEvents) : null;
        }
        private static CurrencyChange BeginCurrency(bool science, double before, bool ignore)
        {
            var value = new CurrencyChange { Parent = currency, Science = science, Before = before, Suppress = ignore || ToolingClient.ApplyingBalance };
            currency = value; return value;
        }
        private static Exception CurrencyFinalizer(Exception __exception, CurrencyChange __state)
        {
            if (__state == null) return __exception;
            var after = __state.Science ? ResearchAndDevelopment.Instance.Science : Funding.Instance.Funds;
            var total = after - __state.Before;
            currency = __state.Parent;
            for (var ancestor = currency; ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.Science == __state.Science) { ancestor.Children += total; break; }
            if (!__state.Suppress && __exception == null)
                ToolingClient.SendDelta(__state.Science ? 0 : total - __state.Children, __state.Science ? total - __state.Children : 0);
            return __exception;
        }
        private static void Recovery(ProtoVessel __0, float __2, out RecoveryContext __state)
        {
            __state = null;
            if (!ToolingClient.Enabled || __0 == null) return;
            __state = new RecoveryContext { Previous = recovery, Vessel = __0, Paid = ToolingClient.PaidVessel(__0.vesselID),
                Physical = new HashSet<ProtoPartSnapshot>(__0.protoPartSnapshots), Factor = __2 };
            foreach (var part in __0.protoPartSnapshots)
                if (part.partInfo != null && part.partInfo.name != "kerbalEVA")
                    foreach (var module in part.modules)
                        if (module.moduleName == "ModuleInventoryPart") AddCargoHosts(module.moduleValues, part.flightID, __state.CargoHosts);
            foreach (var kerbal in __0.GetVesselCrew())
            {
                var host = __0.protoPartSnapshots.FirstOrDefault(p => p.protoModuleCrew.Contains(kerbal));
                AddCargoHosts(kerbal.InventoryNode, host?.flightID ?? 0, __state.CargoHosts, kerbal.name);
            }
            recovery = __state;
            ToolingClient.MarkRecovering(__0.vesselID);
        }
        private static void AddCargoHosts(ConfigNode inventory, uint host, Queue<Tuple<uint, string>> hosts, string crewName = null)
        {
            var stored = inventory?.GetNode("STOREDPARTS");
            if (stored == null) return;
            foreach (ConfigNode row in stored.nodes)
            {
                if (row.GetNode("PART") == null) continue;
                var count = 1; row.TryGetValue("quantity", ref count);
                for (var i = 0; i < count; i++) hosts.Enqueue(Tuple.Create(host, crewName));
            }
        }
        private static void PartCost(ProtoPartSnapshot __0, ref float __3, ref float __4, ref float __result)
        {
            var context = recovery;
            if (context == null || __0 == null) return;
            var stock = Math.Max(0, (double)__3 + __4);
            var multiplier = 1d;
            if (context.Physical.Contains(__0))
            {
                var paid = context.Paid?.Parts?.FirstOrDefault(p => p.FlightId == __0.flightID);
                multiplier = paid == null ? 0 : paid.Multiplier;
                if (paid != null && stock > 0 && context.Factor > 0) multiplier = Math.Min(multiplier, paid.MaximumRefund / (stock * context.Factor));
                context.Parts.Add(new RecoveryPart { FlightId = __0.flightID, StockValue = stock });
            }
            else
            {
                var host = context.CargoHosts.Count > 0 ? context.CargoHosts.Dequeue() : Tuple.Create(0u, (string)null);
                context.Cargo.Add(new ToolingCargo { Name = __0.partName, Count = 1, UnitCost = stock, ContainerFlightId = host.Item1, CrewName = host.Item2 });
            }
            context.CurrentMultiplier = multiplier;
            __3 *= (float)multiplier; __4 *= (float)multiplier; __result = __3 + __4;
        }
        private static void ResourceCost(ref float __2) { if (recovery != null) __2 *= (float)recovery.CurrentMultiplier; }
        private static Exception RecoveryFinalizer(Exception __exception, RecoveryContext __state)
        {
            if (__state == null) return __exception;
            recovery = __state.Previous;
            if (__exception != null) ToolingClient.RecoveryDisconnect("Recovery interrupted; reload authoritative state.");
            else
            {
                try
                {
                    var buffer = new byte[VesselOwnershipPolicy.MaxMergedVesselBytes];
                    LmpClient.VesselUtilities.VesselSerializer.SerializeVesselToArray(__state.Vessel, buffer, out var count);
                    if (count <= 0 || count > buffer.Length) throw new InvalidOperationException("Cannot serialize final recovery craft.");
                    var bytes = new byte[count]; Array.Copy(buffer, bytes, count);
                    ToolingClient.Recover(new EconomyCommand { Operation = EconomyOperation.Recover, VesselId = __state.Vessel.vesselID,
                        VesselData = bytes, RecoveredParts = __state.Parts.ToArray(), RecoveredCargo = __state.Cargo.ToArray(), RecoveryFactor = __state.Factor });
                }
                catch (Exception e) { ToolingClient.RecoveryDisconnect(e.Message); }
            }
            return __exception;
        }
        private static void Tick() => ToolingClient.Tick();
    }
}
