using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CommNet;
using HarmonyLib;
using LmpClient.Systems.Agency;
using LmpClient.Systems.PlayerColorSys;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using LmpCommon.Enums;
using UnityEngine;

namespace LmpClient.Harmony
{
    /// <summary>Masks only CommNetUI geometry; network connections and signal calculations stay untouched.</summary>
    internal static class AgencyCommNetVisibility
    {
        internal static string DiagnosticReason { get; private set; }
        private static FieldInfo uiPoints, uiLine;
        private static PropertyInfo lineActive;
        private static MethodInfo getLineColor, setLineColor;
        private static bool coloursReady;
        private static DateTime nextDiagnostic;
        [ThreadStatic] private static RenderScope current;

        private sealed class NodeIdentityComparer : IEqualityComparer<CommNode>
        {
            public bool Equals(CommNode a, CommNode b) => ReferenceEquals(a, b);
            public int GetHashCode(CommNode node) => global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node);
        }
        private sealed class RenderScope
        {
            internal CommNetUI Ui;
            internal List<Vector3> Points;
            internal bool MaskHidden = true, ColourShared;
            internal Guid Viewer;
            internal readonly Dictionary<CommNode, bool> Visible = new Dictionary<CommNode, bool>(new NodeIdentityComparer());
            internal readonly Dictionary<Guid, Color32?> AgencyColours = new Dictionary<Guid, Color32?>();
            internal readonly List<Color32?> SegmentColours = new List<Color32?>();
            internal int Evaluated, Masked, Unresolved, Coloured;
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
                TryAttachColours(harmony, uiLine.FieldType);
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

        // Colouring is optional. Its API/patch failures must never roll back the masking hooks above.
        private static void TryAttachColours(HarmonyLib.Harmony harmony, Type lineType)
        {
            coloursReady = false;
            var attached = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                getLineColor = AccessTools.Method(lineType, "GetColor", new[] { typeof(int) });
                setLineColor = AccessTools.Method(lineType, "SetColor", new[] { typeof(Color32), typeof(int) });
                if (getLineColor?.ReturnType != typeof(Color32) || setLineColor?.ReturnType != typeof(void))
                    throw new MissingMethodException("CommNet line colour API is unavailable.");
                Attach(harmony, attached, ExactMethod(lineType, "Draw", Type.EmptyTypes), nameof(BeforeDraw), null);
                Attach(harmony, attached, ExactMethod(lineType, "Draw3D", Type.EmptyTypes), nameof(BeforeDraw), null);
                coloursReady = true;
            }
            catch (Exception e)
            {
                foreach (var patch in attached)
                {
                    try { harmony.Unpatch(patch.Key, patch.Value); }
                    catch (Exception rollback) { LunaLog.LogWarning("[AgencyCommNetVisibility] Colour hook rollback failed: " + rollback.Message); }
                }
                DiagnosticReason = "CommNet colours unavailable; hiding remains active: " + e.Message;
                LunaLog.LogWarning("[AgencyCommNetVisibility] " + DiagnosticReason);
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
            var mask = VisibilityClient.Enabled;
            var colour = coloursReady && MainSystem.NetworkState >= ClientState.Handshaking &&
                SettingsSystem.ServerSettings.AgencyCommNetPerAgency && SettingsSystem.ServerSettings.AgencyCommNetOptIn;
            if (!mask && !colour) return true;
            try
            {
                current = new RenderScope { Ui = __instance, Points = (List<Vector3>)uiPoints.GetValue(__instance),
                    MaskHidden = mask, ColourShared = colour, Viewer = AgencySystem.Singleton.MyAgencyId };
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
                    $"evaluated={completed.Evaluated} masked={completed.Masked} unresolved={completed.Unresolved} coloured={completed.Coloured} diagnostic={DiagnosticReason ?? "none"}");
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
            if (current.MaskHidden && (link == null || !Visible(link.a) || !Visible(link.b)))
            {
                points[pair * 2] = points[pair * 2 + 1] = Vector3.zero;
                current.Masked++;
                current.SegmentColours.Add(null);
            }
            else current.SegmentColours.Add(current.ColourShared ? SharedColour(link) : null);
        }
        private static bool CheckCount(List<Vector3> points, int links)
        {
            current.SegmentColours.Clear();
            if (points.Count == links * 2) return true;
            Fail(current, "CommNet display point/link count mismatch.");
            return false;
        }
        private static void Fail(RenderScope scope, string reason)
        {
            DiagnosticReason = reason;
            scope?.SegmentColours.Clear();
            if (scope?.Points != null)
                for (var i = 0; i < scope.Points.Count; i++) scope.Points[i] = Vector3.zero;
        }

        private static Color32? SharedColour(CommLink link)
        {
            try
            {
                if (link?.a == null || link.b == null || link.a.isHome || link.b.isHome ||
                    !CommNet_AgencyFilter.TryGetVessel(link.a, out var a) || !a ||
                    !CommNet_AgencyFilter.TryGetVessel(link.b, out var b) || !b) return null;
                var system = AgencySystem.Singleton;
                var selected = SelectColourAgency(current.Viewer, system.GetVesselAgency(a.id), system.GetVesselAgency(b.id));
                if (selected == Guid.Empty || !system.CanLinkCommNet(a.id, b.id)) return null;
                if (current.AgencyColours.TryGetValue(selected, out var cached)) return cached;
                Color32? colour = null;
                if (AgencyIdentityClient.TryColour(selected, out var explicitColour)) colour = (Color32)explicitColour;
                else if (system.KnownAgencies.TryGetValue(selected, out var agency))
                    colour = RepresentativeColour(agency, PlayerColorSystem.Singleton.PlayerColors,
                        SettingsSystem.CurrentSettings.PlayerName, SettingsSystem.CurrentSettings.PlayerColor);
                current.AgencyColours[selected] = colour;
                return colour;
            }
            catch (Exception e)
            {
                DiagnosticReason = "CommNet colour lookup failed; stock colours retained: " + e.GetType().Name;
                return null;
            }
        }
        private static Guid SelectColourAgency(Guid viewer, Guid a, Guid b)
        {
            if (a == Guid.Empty || b == Guid.Empty || a == b) return Guid.Empty;
            if (a == viewer) return b;
            if (b == viewer) return a;
            return a.CompareTo(b) < 0 ? a : b;
        }
        private static Color32? RepresentativeColour(AgencyInfo agency, IDictionary<string, Color> colours, string localName, Color localColour)
        {
            var candidates = new[] { agency.OwnerDisplayName }.Concat((agency.MemberDisplayNames ?? Array.Empty<string>()).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var name in candidates)
            {
                if (string.IsNullOrEmpty(name)) continue;
                Color colour;
                if (string.Equals(name, localName, StringComparison.Ordinal)) colour = localColour;
                else if (!colours.TryGetValue(name, out colour)) continue;
                if (float.IsNaN(colour.r) || float.IsInfinity(colour.r) || float.IsNaN(colour.g) || float.IsInfinity(colour.g) ||
                    float.IsNaN(colour.b) || float.IsInfinity(colour.b)) continue;
                return (Color32)colour;
            }
            return null;
        }
        private static void BeforeDraw(object __instance)
        {
            if (!coloursReady || current == null || !current.ColourShared || ReferenceEquals(current.Ui, null)) return;
            var originals = new List<KeyValuePair<int, Color32>>();
            try
            {
                if (!ReferenceEquals(__instance, uiLine.GetValue(current.Ui))) return;
                if (current.Points.Count != current.SegmentColours.Count * 2) return;
                for (var i = 0; i < current.SegmentColours.Count; i++)
                {
                    if (!current.SegmentColours[i].HasValue) continue;
                    var original = (Color32)getLineColor.Invoke(__instance, new object[] { i });
                    originals.Add(new KeyValuePair<int, Color32>(i, original));
                    var colour = current.SegmentColours[i].Value;
                    colour.a = original.a;
                    setLineColor.Invoke(__instance, new object[] { colour, i });
                }
                current.Coloured += originals.Count;
            }
            catch (Exception e)
            {
                // Keep an optional colour failure independent of geometry hiding, including partial application.
                foreach (var original in originals)
                    try { setLineColor.Invoke(__instance, new object[] { original.Value, original.Key }); } catch { }
                DiagnosticReason = "CommNet tint failed; stock colours retained: " + e.GetType().Name;
            }
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
