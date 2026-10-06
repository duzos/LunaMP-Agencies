using CommNet;
using HarmonyLib;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace LmpClient.Harmony
{
    /// <summary>Retracts forbidden vessel edges while leaving stock range and home-station logic intact.</summary>
    public static class CommNet_AgencyFilter
    {
        public static bool Ready { get; private set; }
        public static string DiagnosticReason { get; private set; }
        private static MethodInfo disconnect;
        private static readonly Dictionary<CommNode, Guid> vessels = new Dictionary<CommNode, Guid>(new NodeIdentityComparer());
        private static int refreshRequested = 1, resetUiRequested;
        private static bool lastEnabled, lastOptIn;
        private sealed class NodeIdentityComparer : IEqualityComparer<CommNode>
        {
            public bool Equals(CommNode a, CommNode b) => ReferenceEquals(a, b);
            public int GetHashCode(CommNode node) => global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node);
        }
        private static bool Enabled => MainSystem.NetworkState >= ClientState.Handshaking && SettingsSystem.ServerSettings.AgencyCommNetPerAgency;
        public static bool TryAttach(HarmonyLib.Harmony harmony)
        {
            var attached = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                var target = AccessTools.Method(typeof(CommNetwork), "SetNodeConnection", new[] { typeof(CommNode), typeof(CommNode) });
                disconnect = AccessTools.Method(typeof(CommNetwork), "Disconnect", new[] { typeof(CommNode), typeof(CommNode), typeof(bool) });
                var update = AccessTools.Method(typeof(CommNetwork), "UpdateNetwork", Type.EmptyTypes);
                // Harmony requires the declaring-type MethodInfo for inherited generic methods.
                update = update == null ? null : AccessTools.DeclaredMethod(update.DeclaringType, update.Name, Type.EmptyTypes);
                var tick = AccessTools.Method(typeof(MainSystem), "Update", Type.EmptyTypes);
                if (target == null || target.ReturnType != typeof(bool) || disconnect == null || disconnect.ReturnType != typeof(void) || update == null || tick == null)
                    throw new MissingMethodException("Installed CommNet API signatures do not match the verified adapter.");
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(CommNet_AgencyFilter), nameof(BuildNodeMap)));
                attached.Add(new KeyValuePair<MethodBase, MethodInfo>(update, AccessTools.Method(typeof(CommNet_AgencyFilter), nameof(BuildNodeMap))));
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(CommNet_AgencyFilter), nameof(Postfix)));
                attached.Add(new KeyValuePair<MethodBase, MethodInfo>(target, AccessTools.Method(typeof(CommNet_AgencyFilter), nameof(Postfix))));
                harmony.Patch(tick, postfix: new HarmonyMethod(typeof(CommNet_AgencyFilter), nameof(Tick)));
                attached.Add(new KeyValuePair<MethodBase, MethodInfo>(tick, AccessTools.Method(typeof(CommNet_AgencyFilter), nameof(Tick))));
                Ready = true; DiagnosticReason = null;
                LunaLog.Log("[CommNet_AgencyFilter]: exact SetNodeConnection/Disconnect adapter attached.");
                return true;
            }
            catch (Exception e)
            {
                foreach (var patch in attached)
                {
                    try { harmony.Unpatch(patch.Key, patch.Value); }
                    catch (Exception rollback) { LunaLog.LogWarning("[CommNet_AgencyFilter]: rollback failed: " + rollback.Message); }
                }
                Ready = false; DiagnosticReason = e.Message;
                LunaLog.LogError("[CommNet_AgencyFilter]: adapter unavailable: " + DiagnosticReason);
                return false;
            }
        }
        public static void RequestRefresh(bool resetUi = false)
        {
            Interlocked.Exchange(ref refreshRequested, 1);
            if (resetUi) Interlocked.Exchange(ref resetUiRequested, 1);
        }
        private static void Tick()
        {
            if (Interlocked.Exchange(ref resetUiRequested, 0) != 0) Windows.Agency.AgencyWindow.ResetCommNetUi();
            var enabled = Enabled;
            var optIn = enabled && SettingsSystem.ServerSettings.AgencyCommNetOptIn;
            if (enabled != lastEnabled || optIn != lastOptIn) RequestRefresh();
            lastEnabled = enabled; lastOptIn = optIn;
            if (Volatile.Read(ref refreshRequested) == 0) return;
            // Keep a pending refresh until a scene has initialized its network.
            var network = CommNetNetwork.Instance;
            if (!network) return;
            Interlocked.Exchange(ref refreshRequested, 0);
            BuildNodeMap();
            network.QueueRebuild();
        }
        private static void BuildNodeMap()
        {
            vessels.Clear();
            if (!Enabled || FlightGlobals.Vessels == null) return;
            // Unloaded vessels also expose connection.Comm. Never infer vessel identity from transforms.
            try
            {
                foreach (var vessel in FlightGlobals.Vessels)
                    if (vessel && vessel.connection != null && vessel.connection.Comm != null)
                        vessels[vessel.connection.Comm] = vessel.id;
            }
            catch (Exception e)
            {
                vessels.Clear();
                Diagnostics.PlaytestDiagnostics.Write("client.commnet.node-map-error", () => e.GetType().Name, traffic: true);
            }
        }
        private static void Postfix(CommNetwork __instance, CommNode __0, CommNode __1, ref bool __result)
        {
            if (!Enabled || __0 == null || __1 == null || __0.isHome || __1.isHome || disconnect == null) return;
            var allowed = false;
            try
            {
                if (vessels.TryGetValue(__0, out var a) && vessels.TryGetValue(__1, out var b))
                {
                    if (SettingsSystem.ServerSettings.AgencyCommNetOptIn)
                        allowed = AgencySystem.Singleton.CanLinkCommNet(a, b);
                    else
                    {
                        var mine = AgencySystem.Singleton.MyAgencyId;
                        var ownerA = AgencySystem.Singleton.GetVesselAgency(a);
                        var ownerB = AgencySystem.Singleton.GetVesselAgency(b);
                        allowed = mine != Guid.Empty && ownerA != Guid.Empty && ownerB != Guid.Empty &&
                                  !((ownerA == mine && ownerB != mine) || (ownerB == mine && ownerA != mine));
                    }
                }
            }
            catch (Exception e)
            {
                Diagnostics.PlaytestDiagnostics.Write("client.commnet.policy-error", () => e.GetType().Name, traffic: true);
            }
            if (allowed) return;
            try
            {
                disconnect.Invoke(__instance, new object[] { __0, __1, true });
                __result = false;
                Diagnostics.PlaytestDiagnostics.Write("client.commnet.denied", () => "optIn=" + SettingsSystem.ServerSettings.AgencyCommNetOptIn, traffic: true);
            }
            catch (Exception e)
            {
                Ready = false; DiagnosticReason = "CommNet disconnect failed: " + e.GetType().Name;
                Diagnostics.PlaytestDiagnostics.Write("client.commnet.disconnect-error", () => DiagnosticReason, traffic: true);
            }
        }
    }
}
