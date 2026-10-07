using HarmonyLib;
using LmpClient.Diagnostics;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

// ReSharper disable All

namespace LmpClient.Harmony
{
    /// <summary>
    /// Contains a stock UI failure that other mods can trigger: KSP's bundled <c>SoftMasking.SoftMask</c> (Assembly-CSharp)
    /// can end up with a null <c>_materials</c> (its constructor builds it from <c>MaterialReplacer.globalReplacers</c>, an
    /// all-assemblies type scan that a broken mod assembly can make throw). Once that happens every frame throws from
    /// <c>Canvas.willRenderCanvases</c> (<c>OnWillRenderCanvases</c> -> <c>UpdateMaskParameters</c>) and from graphic rebuilds
    /// (<c>ISoftMask.GetReplacement</c>). <c>Canvas.SendWillRenderCanvases</c> is a plain <c>willRenderCanvases?.Invoke()</c>,
    /// so one throwing subscriber skips every subscriber after it in the multicast list, every frame - players see a UI that
    /// stops responding (staging, part action windows, even the Alt+F12 console).
    ///
    /// <para>
    /// Finalizers wrap the whole patched method (original body plus every other mod's prefixes/postfixes) in a try/catch
    /// and return null, so the exception never leaves the SoftMask method and the multicast invoke carries on. Each failing
    /// instance is then repaired: rebuild the missing <c>_materials</c> in place and mark it dirty; if it keeps failing,
    /// disable/enable it (bounded); if that fails too, leave just that SoftMask disabled so its content renders unmasked.
    /// </para>
    /// Every lookup is reflective so a KSP build without these members skips the guard with one log line.
    /// </summary>
    internal static class SoftMaskGuard
    {
        private const string Tag = "[LMP]: [UiGuard] ";
        private const double SummaryIntervalSeconds = 60;
        private const double ProbeIntervalSeconds = 5;

        private static readonly UiFaultLogThrottle Throttle = new UiFaultLogThrottle(SummaryIntervalSeconds);
        private static readonly UiFaultRepairPolicy Policy = new UiFaultRepairPolicy(graceSeconds: 1, healthyAfterSeconds: 10, maxRestarts: 2);
        private static readonly Dictionary<int, KeyValuePair<Behaviour, UiFaultAction>> PendingActions = new Dictionary<int, KeyValuePair<Behaviour, UiFaultAction>>();

        private static Type _softMaskType;
        private static FieldInfo _materialsField, _parametersField, _dirtyField, _maskTransformField, _canvasField, _globalReplacersField;
        private static ConstructorInfo _replacerImplCtor, _chainCtor, _replacementsCtor;
        private static MethodInfo _applyMethod, _globalReplacersGetter, _invalidateChildren;
        private static readonly List<Behaviour> PendingInvalidations = new List<Behaviour>();
        private static Type _replacerInterface, _replacerAttribute;
        private static MethodBase _navBallLateUpdate;
        private static bool _repairAvailable;

        private static bool _wasInFlight;
        private static double _nextProbeAt;
        private static string _lastProbe;

        #region Install

        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                _softMaskType = AccessTools.TypeByName("SoftMasking.SoftMask");
                if (_softMaskType == null)
                {
                    LunaLog.Log(Tag + "SoftMasking.SoftMask not found on this KSP build; SoftMask guards skipped.");
                }
                else
                {
                    ResolveRepairMembers();
                    var finalizer = Hook(nameof(VoidFinalizer));
                    TryPatch(harmony, "SoftMask.OnWillRenderCanvases", AccessTools.DeclaredMethod(_softMaskType, "OnWillRenderCanvases", Type.EmptyTypes), finalizer);
                    TryPatch(harmony, "SoftMask.UpdateMaskParameters", AccessTools.DeclaredMethod(_softMaskType, "UpdateMaskParameters", Type.EmptyTypes), finalizer);
                    TryPatch(harmony, "SoftMask.DestroyMaterials", AccessTools.DeclaredMethod(_softMaskType, "DestroyMaterials", Type.EmptyTypes), finalizer);
                    TryPatch(harmony, "SoftMask.LateUpdate", AccessTools.DeclaredMethod(_softMaskType, "LateUpdate", Type.EmptyTypes), finalizer);
                    TryPatch(harmony, "SoftMask.ISoftMask.GetReplacement", AccessTools.DeclaredMethod(_softMaskType, "SoftMasking.ISoftMask.GetReplacement", new[] { typeof(Material) }), Hook(nameof(GetReplacementFinalizer)));
                    TryPatch(harmony, "SoftMask.ISoftMask.ReleaseReplacement", AccessTools.DeclaredMethod(_softMaskType, "SoftMasking.ISoftMask.ReleaseReplacement", new[] { typeof(Material) }), finalizer);
                    TryPatch(harmony, "SoftMask.IsRaycastLocationValid", AccessTools.DeclaredMethod(_softMaskType, "IsRaycastLocationValid", new[] { typeof(Vector2), typeof(Camera) }), Hook(nameof(RaycastFinalizer)));
                }

                var navBall = AccessTools.TypeByName("KSP.UI.Screens.Flight.NavBallBurnVector");
                _navBallLateUpdate = navBall == null ? null : AccessTools.DeclaredMethod(navBall, "LateUpdate", Type.EmptyTypes);
                TryPatch(harmony, "NavBallBurnVector.LateUpdate", _navBallLateUpdate, Hook(nameof(VoidFinalizer)));
            }
            catch (Exception e)
            {
                LunaLog.LogWarning(Tag + "Install failed, remaining UI guards skipped: " + e.Message);
            }
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(SoftMaskGuard).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

        private static void TryPatch(HarmonyLib.Harmony harmony, string label, MethodBase target, HarmonyMethod finalizer)
        {
            if (target == null)
            {
                LunaLog.Log(Tag + label + " not found on this KSP build; guard skipped.");
                return;
            }
            try
            {
                harmony.Patch(target, finalizer: finalizer);
                PlaytestDiagnostics.Write("client.patch.optional-attached", () => "patch=UiGuard." + label);
            }
            catch (Exception e)
            {
                LunaLog.LogWarning(Tag + "Could not guard " + label + ": " + e.Message);
            }
        }

        private static void ResolveRepairMembers()
        {
            try
            {
                const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _materialsField = _softMaskType.GetField("_materials", instance);
                _parametersField = _softMaskType.GetField("_parameters", instance);
                _dirtyField = _softMaskType.GetField("_dirty", instance);
                _maskTransformField = _softMaskType.GetField("_maskTransform", instance);
                _canvasField = _softMaskType.GetField("_canvas", instance);

                var implType = _softMaskType.GetNestedType("MaterialReplacerImpl", BindingFlags.NonPublic);
                _replacerInterface = AccessTools.TypeByName("SoftMasking.IMaterialReplacer");
                _replacerAttribute = AccessTools.TypeByName("SoftMasking.GlobalMaterialReplacerAttribute");
                var chainType = AccessTools.TypeByName("SoftMasking.MaterialReplacerChain");
                var replacementsType = AccessTools.TypeByName("SoftMasking.MaterialReplacements");
                var replacerType = AccessTools.TypeByName("SoftMasking.MaterialReplacer");
                if (implType == null || _replacerInterface == null || chainType == null || replacementsType == null || replacerType == null || _parametersField == null)
                    throw new MissingMemberException("SoftMasking repair types");

                var replacerList = typeof(IEnumerable<>).MakeGenericType(_replacerInterface);
                _replacerImplCtor = AccessTools.Constructor(implType, new[] { _softMaskType });
                _chainCtor = AccessTools.Constructor(chainType, new[] { replacerList, _replacerInterface });
                _replacementsCtor = AccessTools.Constructor(replacementsType, new[] { _replacerInterface, typeof(Action<Material>) });
                _applyMethod = AccessTools.DeclaredMethod(_parametersField.FieldType, "Apply", new[] { typeof(Material) });
                _globalReplacersGetter = AccessTools.PropertyGetter(replacerType, "globalReplacers");
                _globalReplacersField = AccessTools.Field(replacerType, "_globalReplacers");
                _invalidateChildren = AccessTools.DeclaredMethod(_softMaskType, "InvalidateChildren", Type.EmptyTypes);

                _repairAvailable = _materialsField != null && _dirtyField != null && _replacerImplCtor != null && _chainCtor != null &&
                                   _replacementsCtor != null && _applyMethod != null && _globalReplacersGetter != null;
                if (!_repairAvailable) throw new MissingMemberException("SoftMasking repair members");
            }
            catch (Exception e)
            {
                _repairAvailable = false;
                LunaLog.Log(Tag + "SoftMask self-repair unavailable on this KSP build (" + e.Message + "); failing SoftMasks will be disabled instead.");
            }
        }

        #endregion

        #region Finalizers - must never throw

        private static Exception VoidFinalizer(Exception __exception, object __instance, MethodBase __originalMethod)
        {
            if (__exception != null) Handle(__originalMethod, __exception, __instance);
            return null;
        }

        private static Exception GetReplacementFinalizer(Exception __exception, object __instance, MethodBase __originalMethod, ref Material __result)
        {
            if (__exception == null) return null;
            // SoftMaskable.GetModifiedMaterial falls back to the unmasked base material on a null replacement.
            __result = null;
            Handle(__originalMethod, __exception, __instance);
            return null;
        }

        private static Exception RaycastFinalizer(Exception __exception, object __instance, MethodBase __originalMethod, ref bool __result)
        {
            if (__exception == null) return null;
            // A broken mask must never swallow clicks: let the raycast through.
            __result = true;
            Handle(__originalMethod, __exception, __instance);
            return null;
        }

        private static void Handle(MethodBase method, Exception exception, object instance)
        {
            try
            {
                var key = method == null ? "unknown" : method.DeclaringType?.Name + "." + method.Name;
                var now = Time.realtimeSinceStartup;
                if (Throttle.Record(key, now))
                {
                    LunaLog.LogWarning(Tag + "Suppressed " + exception.GetType().Name + " in " + key + " (stock KSP UI code or another mod's patch, not LMP). " +
                                       "LMP is containing it so the rest of the UI keeps working; repeats are counted and summarised at most every " +
                                       SummaryIntervalSeconds + " s.\n" + exception);
                }

                if (_softMaskType == null || instance == null || !_softMaskType.IsInstanceOfType(instance)) return;
                var mask = instance as Behaviour;
                if (mask == null) return; // destroyed Unity object

                var id = mask.GetInstanceID();
                var action = Policy.OnFailure(id, now);
                if (action == UiFaultAction.SoftRepair) SoftRepair(mask);
                else if (action != UiFaultAction.None) PendingActions[id] = new KeyValuePair<Behaviour, UiFaultAction>(mask, action);
            }
            catch
            {
                // Containment code must never become a new source of exceptions.
            }
        }

        #endregion

        #region Repair

        /// <summary>Rebuilds broken internal state in place. Pure managed work, safe inside a render/rebuild callback.</summary>
        private static void SoftRepair(Behaviour mask)
        {
            var rebuilt = false;
            if (_repairAvailable && _materialsField.GetValue(mask) == null)
            {
                var materials = BuildMaterials(mask);
                if (materials != null)
                {
                    _materialsField.SetValue(mask, materials);
                    rebuilt = true;
                }
            }

            // Cached Unity references that were destroyed underneath the mask: clear so its lazy getters re-resolve them.
            ClearIfDestroyed(_maskTransformField, mask);
            ClearIfDestroyed(_canvasField, mask);
            _dirtyField?.SetValue(mask, true);

            // Children that already fell back to the unmasked material only ask again once their material is dirtied;
            // that must not happen inside a graphic rebuild, so it is done from the next Pump.
            if (rebuilt && _invalidateChildren != null && !PendingInvalidations.Contains(mask)) PendingInvalidations.Add(mask);
            LunaLog.Log(Tag + "Soft-repaired SoftMask '" + PathOf(mask) + "'" + (rebuilt ? " (rebuilt missing material replacements)" : " (marked dirty)") + ".");
        }

        private static void ClearIfDestroyed(FieldInfo field, Behaviour mask)
        {
            if (field == null) return;
            var value = field.GetValue(mask) as UnityEngine.Object;
            if (!ReferenceEquals(value, null) && value == null) field.SetValue(mask, null);
        }

        /// <summary>Same construction as <c>SoftMask..ctor</c>: MaterialReplacements(new MaterialReplacerChain(globalReplacers, new MaterialReplacerImpl(this)), m => _parameters.Apply(m)).</summary>
        private static object BuildMaterials(Behaviour mask)
        {
            var replacers = GlobalReplacers();
            var impl = _replacerImplCtor.Invoke(new object[] { mask });
            var chain = _chainCtor.Invoke(new[] { replacers, impl });
            Action<Material> apply = material => _applyMethod.Invoke(_parametersField.GetValue(mask), new object[] { material });
            return _replacementsCtor.Invoke(new[] { chain, apply });
        }

        private static object GlobalReplacers()
        {
            try
            {
                return _globalReplacersGetter.Invoke(null, null);
            }
            catch (Exception e)
            {
                var inner = (e as TargetInvocationException)?.InnerException ?? e;
                LunaLog.LogWarning(Tag + "SoftMasking.MaterialReplacer.globalReplacers throws - this is what leaves new SoftMasks without materials. " +
                                   "Replacing it with a fault-tolerant scan. Cause:\n" + inner);
                var safe = SafeGlobalReplacers();
                // Cache it where the stock getter looks so SoftMasks constructed from now on initialise normally.
                _globalReplacersField?.SetValue(null, safe);
                return safe;
            }
        }

        /// <summary>Stock <c>CollectGlobalReplacers</c> with every assembly and type isolated in its own try/catch.</summary>
        private static IList SafeGlobalReplacers()
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_replacerInterface));
            if (_replacerAttribute == null) return list;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var type in types)
                {
                    try
                    {
                        if (type == null || type is TypeBuilder || type.IsAbstract || !type.IsDefined(_replacerAttribute, false) || !_replacerInterface.IsAssignableFrom(type)) continue;
                        list.Add(Activator.CreateInstance(type));
                    }
                    catch
                    {
                        // A type that cannot be inspected or created is exactly what the stock scan chokes on - skip it.
                    }
                }
            }
            return list;
        }

        private static void ApplyPendingActions()
        {
            if (PendingInvalidations.Count > 0)
            {
                var invalidations = PendingInvalidations.ToArray();
                PendingInvalidations.Clear();
                foreach (var mask in invalidations)
                {
                    if (mask == null || !mask.isActiveAndEnabled) continue;
                    try { _invalidateChildren.Invoke(mask, null); }
                    catch (Exception e) { LunaLog.LogWarning(Tag + "Could not refresh children of '" + PathOf(mask) + "': " + e.Message); }
                }
            }

            if (PendingActions.Count == 0) return;
            var actions = PendingActions.ToList();
            PendingActions.Clear();
            foreach (var entry in actions)
            {
                var mask = entry.Value.Key;
                if (mask == null)
                {
                    Policy.Forget(entry.Key);
                    continue;
                }
                try
                {
                    if (entry.Value.Value == UiFaultAction.Restart)
                    {
                        if (!mask.enabled) continue;
                        LunaLog.LogWarning(Tag + "SoftMask '" + PathOf(mask) + "' still failing after repair; restarting it (disable/enable).");
                        if (_repairAvailable && _materialsField.GetValue(mask) == null) _materialsField.SetValue(mask, BuildMaterials(mask));
                        mask.enabled = false;
                        mask.enabled = true;
                    }
                    else if (entry.Value.Value == UiFaultAction.Disable)
                    {
                        mask.enabled = false;
                        LunaLog.LogWarning(Tag + "Disabled SoftMask '" + PathOf(mask) + "' after repeated failures; its UI now renders unmasked instead of freezing the whole UI.");
                    }
                }
                catch (Exception e)
                {
                    LunaLog.LogWarning(Tag + "Repair action on '" + PathOf(mask) + "' failed: " + e.Message);
                }
            }
        }

        private static string PathOf(Component component)
        {
            try
            {
                var names = new List<string>();
                for (var t = component.transform; t != null && names.Count < 8; t = t.parent) names.Add(t.name);
                names.Reverse();
                return string.Join("/", names.ToArray());
            }
            catch
            {
                return "?";
            }
        }

        #endregion

        #region Pump and diagnostics

        /// <summary>Called once per frame from <c>MainSystem.Update</c>. Never throws.</summary>
        public static void Pump()
        {
            try
            {
                var now = Time.realtimeSinceStartup;
                ApplyPendingActions();

                var summary = Throttle.TakeSummary(now);
                if (summary != null)
                    LunaLog.LogWarning(Tag + "Still suppressing (last " + SummaryIntervalSeconds + " s): " + summary + "; SoftMasks disabled so far: " + Policy.DisabledCount + ".");

                var inFlight = HighLogic.LoadedSceneIsFlight;
                if (inFlight && !_wasInFlight) LogPatchOwners();
                _wasInFlight = inFlight;

                if (PlaytestDiagnostics.Enabled && now >= _nextProbeAt)
                {
                    _nextProbeAt = now + ProbeIntervalSeconds;
                    ProbeUiState();
                }
            }
            catch
            {
                // Diagnostics must not interrupt the Unity update loop.
            }
        }

        private static void LogPatchOwners()
        {
            var line = new StringBuilder("[LMP-DIAG] NavBallBurnVector.LateUpdate patched by: ");
            line.Append(_navBallLateUpdate == null ? "n/a (method missing)" : Owners(_navBallLateUpdate));
            line.Append(" | SoftMask methods patched by: ");
            if (_softMaskType == null)
            {
                line.Append("n/a (type missing)");
            }
            else
            {
                var patched = HarmonyLib.Harmony.GetAllPatchedMethods().Where(m => m.DeclaringType == _softMaskType).OrderBy(m => m.Name).ToList();
                line.Append(patched.Count == 0 ? "none" : string.Join("; ", patched.Select(m => m.Name + "=" + Owners(m)).ToArray()));
            }
            LunaLog.Log(line.ToString());
        }

        private static string Owners(MethodBase method)
        {
            var owners = HarmonyLib.Harmony.GetPatchInfo(method)?.Owners;
            return owners == null || owners.Count == 0 ? "none" : string.Join(", ", owners.ToArray());
        }

        private static void ProbeUiState()
        {
            var locks = InputLockManager.lockStack == null ? "" : string.Join(",", InputLockManager.lockStack.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            var eventSystem = EventSystem.current;
            string selected, pointerOverUi;
            if (eventSystem == null)
            {
                selected = "no-eventsystem";
                pointerOverUi = "n/a";
            }
            else
            {
                var go = eventSystem.currentSelectedGameObject;
                selected = go == null ? "none" : go.name;
                pointerOverUi = eventSystem.IsPointerOverGameObject().ToString();
            }

            var state = $"locks=[{locks}] selected={selected} pointerOverUi={pointerOverUi}";
            if (state == _lastProbe) return;
            _lastProbe = state;
            var suppressed = Throttle.TotalSuppressed;
            var disabled = Policy.DisabledCount;
            PlaytestDiagnostics.Write("client.ui.state", () => $"{state} uiGuardSuppressed={suppressed} softMasksDisabled={disabled}");
        }

        #endregion
    }
}
