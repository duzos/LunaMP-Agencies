using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LmpClient.Systems.Agency;
using LmpCommon.Enums;

namespace LmpClient.Harmony
{
    public static class AgencyVesselDocking
    {
        public static bool Ready { get; private set; }
        private static readonly ConditionalWeakTable<KerbalFSM, PartModule> modules = new ConditionalWeakTable<KerbalFSM, PartModule>();
        private static FieldInfo grappleFsm, grappleEvent, grappleOther;
        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                grappleFsm = AccessTools.Field(typeof(ModuleGrappleNode), "fsm") ?? throw new MissingFieldException("ModuleGrappleNode.fsm");
                grappleEvent = AccessTools.Field(typeof(ModuleGrappleNode), "on_contact") ?? throw new MissingFieldException("ModuleGrappleNode.on_contact");
                grappleOther = AccessTools.Field(typeof(ModuleGrappleNode), "otherPart") ?? throw new MissingFieldException("ModuleGrappleNode.otherPart");
                Patch(harmony, typeof(KerbalFSM), "RunEvent", new[] { typeof(KFSMEvent) }, nameof(EventPrefix), null, nameof(EventFinalizer));
                Patch(harmony, typeof(ModuleDockingNode), "SetupFSM", Type.EmptyTypes, null, nameof(Setup));
                Patch(harmony, typeof(ModuleGrappleNode), "SetupFSM", Type.EmptyTypes, null, nameof(Setup));
                Patch(harmony, typeof(ModuleGrappleNode), "Grapple", new[] { typeof(Part), typeof(Part) }, nameof(Grapple), null, nameof(EventFinalizer));
                Patch(harmony, typeof(KerbalEVA), "BoardSeat", new[] { typeof(KerbalSeat) }, nameof(BoardSeat), null, nameof(EventFinalizer));
                Patch(harmony, typeof(MainSystem), "Update", Type.EmptyTypes, null, nameof(Tick));
                Ready = true;
                LunaLog.Log("[AgencyOwnership] Docking/claw FSM and replay-safe guards attached.");
            }
            catch (Exception e) { Ready = false; LunaLog.LogError("[AgencyOwnership] Docking hooks unavailable; local docking blocked: " + e.Message); }
        }
        private static void Patch(HarmonyLib.Harmony harmony, Type type, string name, Type[] args, string prefix, string postfix = null, string finalizer = null)
        {
            var method = AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
            harmony.Patch(method, prefix == null ? null : new HarmonyMethod(typeof(AgencyVesselDocking), prefix), postfix == null ? null : new HarmonyMethod(typeof(AgencyVesselDocking), postfix), null,
                finalizer == null ? null : new HarmonyMethod(typeof(AgencyVesselDocking), finalizer));
        }
        private static void Setup(PartModule __instance)
        {
            var fsm = __instance is ModuleDockingNode node ? node.fsm : grappleFsm.GetValue(__instance) as KerbalFSM;
            if (fsm == null) return;
            modules.Remove(fsm); modules.Add(fsm, __instance);
        }
        private static bool EventPrefix(KerbalFSM __instance, KFSMEvent __0, out bool __state)
        {
            __state = false;
            if (!DockingCoordinator.Enabled || DockingCoordinator.Replaying || __0 == null) return true;
            if (!modules.TryGetValue(__instance, out var module)) module = __0.OnEvent?.Target as PartModule;
            if (module is ModuleDockingNode node && ReferenceEquals(node.fsm, __instance))
            {
                var relevant = ReferenceEquals(__0, node.on_capture) || ReferenceEquals(__0, node.on_capture_dockee) ||
                    ReferenceEquals(__0, node.on_capture_docker) || ReferenceEquals(__0, node.on_capture_docker_sameVessel) ||
                    ReferenceEquals(__0, node.on_swapPrimary) || ReferenceEquals(__0, node.on_swapSecondary);
                if (relevant && node.otherNode)
                    return DockingCoordinator.Enter(node.part, node.otherNode.part, CoupleTrigger.DockingNode, out __state, false);
            }
            else if (module is ModuleGrappleNode grapple && ReferenceEquals(grappleFsm.GetValue(grapple), __instance) && ReferenceEquals(grappleEvent.GetValue(grapple), __0))
                return DockingCoordinator.Enter(grapple.part, grappleOther.GetValue(grapple) as Part, CoupleTrigger.GrappleNode, out __state, false);
            return true;
        }
        private static bool BoardSeat(KerbalEVA __instance, KerbalSeat __0, out bool __state) => DockingCoordinator.Enter(__instance.part, __0?.part, CoupleTrigger.Kerbal, out __state, false);
        private static bool Grapple(ModuleGrappleNode __instance, Part __0, out bool __state) => DockingCoordinator.Enter(__instance.part, __0, CoupleTrigger.GrappleNode, out __state);
        private static Exception EventFinalizer(Exception __exception, bool __state) { DockingCoordinator.Exit(__state, __exception); return __exception; }
        private static void Tick() => VesselOwnershipClient.Tick();
    }
}

