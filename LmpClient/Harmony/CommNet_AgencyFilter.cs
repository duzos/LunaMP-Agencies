using CommNet;
using HarmonyLib;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.VesselUtilities;
using System;
using System.Reflection;

namespace LmpClient.Harmony
{
    /// <summary>
    /// Best-effort per-agency CommNet filter. When the server has
    /// <c>AgencyCommNetPerAgency=true</c>, prevents this client's relay
    /// graph from forming connections through foreign-agency vessels.
    ///
    /// Implementation strategy: postfix on
    /// <c>CommNet.CommNetwork.SetNodeConnection</c>. If the patch attaches
    /// successfully and the connection it just established crosses the
    /// agency boundary, we immediately call <c>DisconnectNodes</c> to
    /// retract it. The graph is recomputed each tick so the disconnect
    /// sticks until the next reachability check.
    ///
    /// KSP's CommNet API has shifted across versions; if the candidate
    /// method signatures don't match this KSP version the Harmony patch
    /// silently fails to attach and per-agency CommNet effectively reverts
    /// to no-op (vanilla behaviour). Look for the
    /// <c>[CommNet_AgencyFilter]: patch attached</c> log line on first
    /// connect to confirm it's active.
    /// </summary>
    [HarmonyPatch]
    public static class CommNet_AgencyFilter
    {
        private static MethodBase _setNodeConnection;
        private static MethodInfo _disconnectNodes;
        private static FieldInfo _commNetVessel_Vessel;

        public static bool TryAttach(HarmonyLib.Harmony harmony)
        {
            try
            {
                // SetNodeConnection (instance method). Signature varies; we
                // grab the longest overload.
                var t = typeof(CommNetwork);
                MethodInfo target = null;
                foreach (var m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m.Name == "SetNodeConnection" && m.GetParameters().Length >= 2 && !m.IsStatic)
                    {
                        if (target == null || m.GetParameters().Length > target.GetParameters().Length)
                            target = m;
                    }
                }
                if (target == null)
                {
                    LunaLog.LogWarning("[CommNet_AgencyFilter]: CommNetwork.SetNodeConnection not found — per-agency CommNet inactive.");
                    return false;
                }
                _setNodeConnection = target;

                _disconnectNodes = AccessTools.Method(t, "DisconnectNodes", new[] { typeof(CommNode), typeof(CommNode) });
                if (_disconnectNodes == null)
                {
                    LunaLog.LogWarning("[CommNet_AgencyFilter]: CommNetwork.DisconnectNodes not found — per-agency CommNet inactive.");
                    return false;
                }

                // Helper used to walk from a CommNode back to its owning
                // CommNetVessel. KSP keeps a back-reference via the static
                // CommNetVessel.GetCommNetVessel(CommNode) helper if it
                // exists, otherwise the field on Vessel.connection.
                _commNetVessel_Vessel = AccessTools.Field(typeof(CommNetVessel), "vessel")
                                        ?? AccessTools.Field(typeof(CommNetVessel), "_vessel");

                var postfix = new HarmonyMethod(typeof(CommNet_AgencyFilter).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(target, postfix: postfix);
                LunaLog.Log("[CommNet_AgencyFilter]: patch attached.");
                return true;
            }
            catch (Exception e)
            {
                LunaLog.LogError($"[CommNet_AgencyFilter]: failed to attach patch: {e.Message}");
                return false;
            }
        }

        private static void Postfix(CommNetwork __instance, CommNode a, CommNode b)
        {
            // Cheap early-out: only filter when the server told us to.
            if (SettingsSystem.ServerSettings == null || !SettingsSystem.ServerSettings.AgencyCommNetPerAgency)
                return;
            if (a == null || b == null || _disconnectNodes == null) return;

            try
            {
                var myAgency = AgencySystem.Singleton.MyAgencyId;
                if (myAgency == Guid.Empty) return; // no agency assigned yet — leave the graph alone

                var aAgency = ResolveAgency(a, myAgency);
                var bAgency = ResolveAgency(b, myAgency);

                // We only filter connections that cross OUR boundary. Two
                // foreign relays talking to each other is fine — that's
                // their universe. The constraint is: nothing that our
                // probes connect to may belong to another agency.
                bool crosses = (aAgency == myAgency && bAgency != myAgency)
                            || (bAgency == myAgency && aAgency != myAgency);

                if (crosses)
                {
                    _disconnectNodes.Invoke(__instance, new object[] { a, b });
                }
            }
            catch
            {
                // Never let a Harmony postfix throw — KSP's CommNet update
                // path is hot. Swallow and let the graph behave as vanilla.
            }
        }

        /// <summary>
        /// Walks from a CommNode back to a vessel guid, then looks up the
        /// owning agency in the client-cached map. Returns the local
        /// player's agency id for ground stations / unknown nodes so they
        /// remain visible.
        /// </summary>
        private static Guid ResolveAgency(CommNode node, Guid fallbackOurs)
        {
            if (node == null) return fallbackOurs;

            // KSP exposes the CommNetVessel via CommNode.transform's parent
            // chain in most versions; we use a simpler heuristic: look at
            // the CommNet system's vessel registry. This isn't 100% but it's
            // resilient — if we can't resolve, treat as ours and let the
            // connection through.
            var commVessel = node?.transform?.GetComponentInParent<CommNetVessel>();
            if (commVessel == null) return fallbackOurs;

            var vessel = _commNetVessel_Vessel?.GetValue(commVessel) as Vessel;
            if (vessel == null) vessel = commVessel.Vessel;
            if (vessel == null) return fallbackOurs;

            var owning = AgencySystem.Singleton.GetVesselAgency(vessel.id);
            return owning == Guid.Empty ? fallbackOurs : owning;
        }
    }
}
