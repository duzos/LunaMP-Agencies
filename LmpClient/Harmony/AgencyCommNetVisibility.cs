using System;
using System.Collections.Generic;
using System.Reflection;
using CommNet;
using HarmonyLib;
using LmpClient.Systems.Agency;
using UnityEngine;

namespace LmpClient.Harmony
{
    /// <summary>Masks only CommNetUI geometry; network connections and signal calculations stay untouched.</summary>
    internal static class AgencyCommNetVisibility
    {
        internal static string DiagnosticReason { get; private set; }
        private static FieldInfo uiPoints, uiLine;
        private static PropertyInfo lineActive;
        private static DateTime nextDiagnostic;
        [ThreadStatic] private static RenderScope current;

        private sealed class NodeIdentityComparer : IEqualityComparer<CommNode>
        {
            public bool Equals(CommNode a, CommNode b) => ReferenceEquals(a, b);
            public int GetHashCode(CommNode node) => global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node);
        }
        private sealed class RenderScope
        {
            internal List<Vector3> Points;
            internal readonly Dictionary<CommNode, bool> Visible = new Dictionary<CommNode, bool>(new NodeIdentityComparer());
            internal int Evaluated, Masked, Unresolved;
        }

        internal static bool TryAttach(HarmonyLib.Harmony harmony)
        {
            var attached = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                uiPoints = AccessTools.Field(typeof(CommNetUI), "points");
                uiLine = AccessTools.Field(typeof(CommNetUI), "line");
                lineActive = uiLine == null ? null : AccessTools.Property(uiLine.FieldType, "active");
                if (uiPoints?.FieldType != typeof(List<Vector3>) || lineActive?.PropertyType != typeof(bool) || !lineActive.CanWrite)
                    throw new MissingMemberException("CommNetUI presentation fields do not match the installed adapter.");

                Attach(harmony, attached, ExactMethod(typeof(CommNetUI), "UpdateDisplay", Type.EmptyTypes), nameof(BeginDisplay), null, nameof(EndDisplay));
                var points = new[] { typeof(List<Vector3>) };
                Attach(harmony, attached, ExactMethod(typeof(CommLink), "GetPoints", points), null, nameof(LinkPoints));
                Attach(harmony, attached, ExactMethod(typeof(CommPath), "GetPoints", new[] { typeof(List<Vector3>), typeof(bool) }), null, nameof(PathPoints));
                Attach(harmony, attached, ExactMethod(typeof(CommNode), "GetLinkPoints", points), null, nameof(NodePoints));
                Attach(harmony, attached, ExactMethod(typeof(CommNetwork), "GetLinkPoints", points), null, nameof(NetworkPoints));
                DiagnosticReason = null;
                return true;
            }
            catch (Exception e)
            {
                foreach (var patch in attached)
                {
                    try { harmony.Unpatch(patch.Key, patch.Value); }
                    catch (Exception rollback) { LunaLog.LogWarning("[AgencyCommNetVisibility] Hook rollback failed: " + rollback.Message); }
                }
                DiagnosticReason = "CommNet display hooks unavailable: " + e.Message;
                LunaLog.LogError("[AgencyCommNetVisibility] " + DiagnosticReason);
                return false;
            }
        }

        private static MethodInfo ExactMethod(Type type, string name, Type[] arguments)
        {
            var method = AccessTools.Method(type, name, arguments);
            if (method == null || method.ReturnType != typeof(void)) throw new MissingMethodException(type.FullName, name);
            // Harmony needs the closed generic declaring method, not an inherited reflected method.
            return AccessTools.DeclaredMethod(method.DeclaringType, name, arguments) ?? throw new MissingMethodException(type.FullName, name);
        }
        private static void Attach(HarmonyLib.Harmony harmony, List<KeyValuePair<MethodBase, MethodInfo>> attached,
            MethodInfo target, string prefix, string postfix, string finalizer = null)
        {
            foreach (var name in new[] { prefix, postfix, finalizer })
                if (name != null) attached.Add(new KeyValuePair<MethodBase, MethodInfo>(target, AccessTools.Method(typeof(AgencyCommNetVisibility), name)));
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(AgencyCommNetVisibility), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(AgencyCommNetVisibility), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(AgencyCommNetVisibility), finalizer));
        }

        private static bool BeginDisplay(CommNetUI __instance, out RenderScope __state)
        {
            __state = current;
            current = null;
            if (!VisibilityClient.Enabled) return true;
            try
            {
                current = new RenderScope { Points = (List<Vector3>)uiPoints.GetValue(__instance) };
                // This refresh does not depend on the simulation adapter having attached successfully.
                CommNet_AgencyFilter.BuildNodeMap();
                if (!CommNetNetwork.Instance) HideLine(__instance);
                return true;
            }
            catch (Exception e)
            {
                Fail(current, "CommNet display setup failed: " + e.GetType().Name);
                try { HideLine(__instance); }
                catch { /* Preserve the setup diagnostic even if the native line is already gone. */ }
                // Do not let the stock generators overwrite the failed pass's suppressed geometry.
                return false;
            }
        }
        private static Exception EndDisplay(Exception __exception, RenderScope __state)
        {
            var completed = current;
            current = __state;
            if (completed != null && DateTime.UtcNow >= nextDiagnostic)
            {
                nextDiagnostic = DateTime.UtcNow.AddSeconds(5);
                Diagnostics.PlaytestDiagnostics.Write("client.visibility.commnet", () =>
                    $"evaluated={completed.Evaluated} masked={completed.Masked} unresolved={completed.Unresolved} diagnostic={DiagnosticReason ?? "none"}");
            }
            return __exception;
        }
        private static void HideLine(CommNetUI ui)
        {
            var line = uiLine.GetValue(ui);
            if (line != null) lineActive.SetValue(line, false, null);
        }
        private static bool Scoped(List<Vector3> points) => current != null && points != null && ReferenceEquals(points, current.Points);
        private static bool Visible(CommNode node)
        {
            if (node == null) { current.Unresolved++; return false; }
            if (node.isHome) return true;
            if (current.Visible.TryGetValue(node, out var visible)) return visible;
            // CanSee(null) deliberately means visible elsewhere. Unknown/destroyed endpoints must not use it.
            if (!CommNet_AgencyFilter.TryGetVessel(node, out var vessel) || !vessel)
            {
                current.Unresolved++;
                visible = false;
            }
            else visible = VisibilityClient.CanSee(vessel);
            current.Visible[node] = visible;
            return visible;
        }
        private static void Mask(CommLink link, List<Vector3> points, int pair)
        {
            current.Evaluated++;
            if (link != null && Visible(link.a) && Visible(link.b)) return;
            points[pair * 2] = points[pair * 2 + 1] = Vector3.zero;
            current.Masked++;
        }
        private static bool CheckCount(List<Vector3> points, int links)
        {
            if (points.Count == links * 2) return true;
            Fail(current, "CommNet display point/link count mismatch.");
            return false;
        }
        private static void Fail(RenderScope scope, string reason)
        {
            DiagnosticReason = reason;
            if (scope?.Points != null)
                for (var i = 0; i < scope.Points.Count; i++) scope.Points[i] = Vector3.zero;
        }
        private static void LinkPoints(CommLink __instance, List<Vector3> __0)
        {
            if (!Scoped(__0)) return;
            try { if (CheckCount(__0, 1)) Mask(__instance, __0, 0); }
            catch (Exception e) { Fail(current, "CommNet link display failed: " + e.GetType().Name); }
        }
        private static void PathPoints(CommPath __instance, List<Vector3> __0, bool __1)
        {
            if (!Scoped(__0)) return;
            try
            {
                if (!__1) { Fail(current, "CommNet display requires independent path segments."); return; }
                if (!CheckCount(__0, __instance.Count)) return;
                for (var i = 0; i < __instance.Count; i++) Mask(__instance[i], __0, i);
            }
            catch (Exception e) { Fail(current, "CommNet path display failed: " + e.GetType().Name); }
        }
        private static void NodePoints(CommNode __instance, List<Vector3> __0)
        {
            if (!Scoped(__0)) return;
            try
            {
                if (!CheckCount(__0, __instance.Count)) return;
                var pair = 0;
                foreach (var link in __instance.Values) Mask(link, __0, pair++);
            }
            catch (Exception e) { Fail(current, "CommNet node display failed: " + e.GetType().Name); }
        }
        private static void NetworkPoints(CommNetwork __instance, List<Vector3> __0)
        {
            if (!Scoped(__0)) return;
            try
            {
                var links = __instance.Links;
                if (!CheckCount(__0, links.Count)) return;
                for (var i = 0; i < links.Count; i++) Mask(links[i], __0, i);
            }
            catch (Exception e) { Fail(current, "CommNet network display failed: " + e.GetType().Name); }
        }
    }
}
