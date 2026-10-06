using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LmpCommon.Agency;
using UnityEngine;

namespace LmpClient.Systems.Agency
{
    public enum KkIntegrationState { Absent, Ready, Unsupported }

    public static class KkLaunchSiteIntegration
    {
        public static KkIntegrationState State { get; private set; }
        public static string DiagnosticReason { get; private set; }
        private static Type manager, siteType, selector;
        private static FieldInfo siteName, selectorInstance, allSites;
        private static MethodInfo register, validity, selectorClose;
        private static bool baseManagerMatched, baseBossMatched, wasActive;
        private static int lastClosedGeneration = -1;
        private static readonly Dictionary<object, object> originalFacilities = new Dictionary<object, object>();
        private static bool Active => State == KkIntegrationState.Ready && LaunchSiteAccess.Enabled;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            var attached = new List<MethodBase>();
            manager = AccessTools.TypeByName("KerbalKonstructs.Core.LaunchSiteManager");
            if (manager == null)
            {
                State = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "KerbalKonstructs") ? KkIntegrationState.Unsupported : KkIntegrationState.Absent;
                DiagnosticReason = State == KkIntegrationState.Absent ? "Kerbal Konstructs is not installed." : "LaunchSiteManager type is unavailable.";
                return;
            }
            State = KkIntegrationState.Unsupported;
            try
            {
                siteType = RequiredType("KerbalKonstructs.Core.KKLaunchSite");
                selector = RequiredType("KerbalKonstructs.UI.LaunchSiteSelectorGUI");
                siteName = AccessTools.Field(siteType, "LaunchSiteName") ?? throw new MissingFieldException("LaunchSiteName");
                // The public getter calls ToList on this array before KK initializes it.
                allSites = AccessTools.Field(manager, "allLaunchSites") ?? throw new MissingFieldException("allLaunchSites");
                if (!allSites.IsStatic || allSites.FieldType != siteType.MakeArrayType())
                    throw new InvalidOperationException("KK launch catalog type differs");
                register = Required(manager, "RegisterMHLaunchSites", typeof(EditorFacility));
                validity = Required(manager, "CheckLaunchSiteIsValid", siteType);
                selectorClose = Required(selector, "Close");
                selectorInstance = AccessTools.Field(selector, "_instance") ?? throw new MissingFieldException("LaunchSiteSelectorGUI._instance");
                var getter = AccessTools.PropertyGetter(siteType, "isOpen") ?? throw new MissingMemberException("isOpen");
                if (getter.ReturnType != typeof(bool) || siteName.FieldType != typeof(string) || validity.ReturnType != typeof(bool))
                    throw new InvalidOperationException("KK launch API types differ");
                var open = Required(manager, "OpenLaunchSite", siteType);
                var close = Required(manager, "CloseLaunchSite", siteType);
                var select = Required(manager, "setLaunchSite", siteType);
                var selectorOpen = Required(selector, "Open");
                var save = Required(RequiredType("KerbalKonstructs.Modules.CareerState"), "SaveLaunchsites", typeof(ConfigNode));
                var baseManager = Required(RequiredType("KerbalKonstructs.UI.BaseManager"), "drawBaseManagerWindow", typeof(int));
                var baseBoss = Required(RequiredType("KerbalKonstructs.UI.BaseBossFlight"), "DrawBaseManagerWindow", typeof(int));
                // Each transpiler validates its entire method before changing it. No overlay becomes active until both succeed.
                Patch(harmony, attached, baseManager, transpiler: nameof(Purchases));
                Patch(harmony, attached, baseBoss, transpiler: nameof(Purchases));
                if (!baseManagerMatched || !baseBossMatched) throw new InvalidOperationException("KK purchase controls could not be verified");
                Patch(harmony, attached, getter, postfix: nameof(IsOpen));
                Patch(harmony, attached, validity, postfix: nameof(IsValid));
                Patch(harmony, attached, select, prefix: nameof(Select));
                Patch(harmony, attached, open, prefix: nameof(ChangeOpenState));
                Patch(harmony, attached, close, prefix: nameof(ChangeOpenState));
                Patch(harmony, attached, selectorOpen, prefix: nameof(SelectorOpen));
                Patch(harmony, attached, register, prefix: nameof(BeforeRegistration));
                Patch(harmony, attached, save, prefix: nameof(SavePrefix), finalizer: nameof(SaveFinalizer));
                State = KkIntegrationState.Ready;
                DiagnosticReason = null;
                LunaLog.Log("[AgencyLaunchSites] KK integration ready; three purchase controls and career save scope verified.");
            }
            catch (Exception e)
            {
                State = KkIntegrationState.Unsupported;
                DiagnosticReason = e.Message;
                foreach (var method in attached)
                {
                    try { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
                    catch (Exception unpatch) { LunaLog.LogWarning("[AgencyLaunchSites] KK rollback: " + unpatch.Message); }
                }
                LunaLog.LogError("[AgencyLaunchSites] KK unsupported; assigned KK launches will be blocked: " + DiagnosticReason);
            }
        }
        private static Type RequiredType(string name) => AccessTools.TypeByName(name) ?? throw new TypeLoadException(name);
        private static MethodInfo Required(Type type, string name, params Type[] args) => AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
        private static void Patch(HarmonyLib.Harmony harmony, ICollection<MethodBase> attached, MethodBase method, string prefix = null, string postfix = null, string transpiler = null, string finalizer = null)
        {
            attached.Add(method);
            harmony.Patch(method, Hook(prefix), Hook(postfix), Hook(transpiler), Hook(finalizer));
        }
        private static HarmonyMethod Hook(string name) => name == null ? null : new HarmonyMethod(typeof(KkLaunchSiteIntegration), name);
        internal static IEnumerable<object> Sites()
        {
            var value = allSites?.GetValue(null) as IEnumerable;
            return value == null ? Enumerable.Empty<object>() : value.Cast<object>();
        }
        internal static bool CatalogPending => allSites != null && allSites.GetValue(null) == null;
        internal static string SiteId(object site) => site == null ? null : siteName?.GetValue(site) as string;
        internal static bool IsKkSite(string id)
        {
            if (id == "LaunchPad" || id == "Runway" || State == KkIntegrationState.Absent) return false;
            if (State == KkIntegrationState.Ready) return Sites().Any(site => SiteId(site) == id);
            // An unsupported catalog cannot reliably classify custom facilities. Only positively identified stock sites pass.
            var stock = PSystemSetup.Instance == null ? null : LaunchSiteCatalog.ReadMember(PSystemSetup.Instance, "LaunchSites") as IEnumerable;
            return stock == null || !stock.Cast<object>().Any(site => (LaunchSiteCatalog.ReadMember(site, "name") as string) == id);
        }
        private static void IsOpen(object __instance, ref bool __result)
        {
            if (Active && !KkCareerSaveScope.Active) __result = AgencySystem.Singleton.IsLaunchSiteAllowed(SiteId(__instance));
        }
        private static void IsValid(object __0, ref bool __result)
        {
            if (Active) __result = __result && AgencySystem.Singleton.IsLaunchSiteAllowed(SiteId(__0));
        }
        private static bool Select(object __0) => !Active || !LaunchSiteAccess.Deny(SiteId(__0));
        private static bool ChangeOpenState() => !Active;
        private static bool SelectorOpen()
        {
            if (!Active) return true;
            if (Sites().Any(site => AgencySystem.Singleton.IsLaunchSiteAllowed(SiteId(site)) && (bool)validity.Invoke(null, new[] { site }))) return true;
            ScreenMessages.PostScreenMessage("Your agency has no available launch sites for this editor.", 5f, ScreenMessageStyle.UPPER_CENTER);
            return false;
        }
        private static void SavePrefix(out IDisposable __state) => __state = KkCareerSaveScope.Enter();
        private static Exception SaveFinalizer(Exception __exception, IDisposable __state) { __state?.Dispose(); return __exception; }

        private static IEnumerable<CodeInstruction> Purchases(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            var button = AccessTools.Method(typeof(GUILayout), "Button", new[] { typeof(string), typeof(GUILayoutOption[]) });
            var projected = code.Select(i =>
            {
                var kind = KkPurchasePattern.Kind.Other;
                string text = null;
                if (i.opcode == OpCodes.Ldstr) { kind = KkPurchasePattern.Kind.Label; text = i.operand as string; }
                else if (i.opcode == OpCodes.Brfalse || i.opcode == OpCodes.Brfalse_S) kind = KkPurchasePattern.Kind.FalseBranch;
                else if (i.operand is MethodInfo method && method.DeclaringType == typeof(GUILayout) && method.Name == "Button")
                    kind = method == button ? KkPurchasePattern.Kind.Button : KkPurchasePattern.Kind.OtherButton;
                else if (i.opcode.FlowControl == FlowControl.Branch || i.opcode.FlowControl == FlowControl.Cond_Branch || i.opcode.FlowControl == FlowControl.Return || i.opcode.FlowControl == FlowControl.Throw)
                    kind = KkPurchasePattern.Kind.ControlFlow;
                return new KkPurchasePattern.Instruction(kind, text);
            }).ToArray();
            var isManager = __originalMethod.DeclaringType.FullName == "KerbalKonstructs.UI.BaseManager";
            var positions = KkPurchasePattern.Match(projected, isManager ? new[] { "Open Base for \n", "Close Base for \n" } : new[] { "Open Base for " });
            foreach (var position in positions) code[position].operand = AccessTools.Method(typeof(KkLaunchSiteIntegration), nameof(PurchaseButton));
            if (isManager) baseManagerMatched = true; else baseBossMatched = true;
            return code;
        }
        private static bool PurchaseButton(string label, GUILayoutOption[] options)
        {
            if (!Active) return GUILayout.Button(label, options);
            var enabled = GUI.enabled;
            try { GUI.enabled = false; GUILayout.Button("Agency assignments control access", options); return false; }
            finally { GUI.enabled = enabled; }
        }
        private static void BeforeRegistration(EditorFacility __0)
        {
            if (!Active) return;
            foreach (var site in Sites())
            {
                var id = SiteId(site);
                if (id == "LaunchPad" || id == "Runway") continue;
                var type = LaunchSiteCatalog.ReadMember(site, "LaunchSiteType")?.ToString();
                if ((__0 == EditorFacility.SPH && type == "VAB") || (__0 == EditorFacility.VAB && type == "SPH")) continue;
                var facility = LaunchSiteCatalog.ReadMember(site, "spaceCenterFacility");
                if (facility == null || originalFacilities.ContainsKey(facility)) continue;
                var field = AccessTools.Field(facility.GetType(), "editorFacility");
                if (field != null) originalFacilities.Add(facility, field.GetValue(facility));
            }
        }
        private static void RestoreFacilities()
        {
            foreach (var pair in originalFacilities)
            {
                // Facilities are managed registry records; ignore records replaced by a newly loaded scene.
                var current = PSystemSetup.Instance == null ? null : LaunchSiteCatalog.ReadMember(PSystemSetup.Instance, "SpaceCenterFacilities") as IEnumerable;
                if (current == null || !current.Cast<object>().Any(value => ReferenceEquals(value, pair.Key))) continue;
                AccessTools.Field(pair.Key.GetType(), "editorFacility")?.SetValue(pair.Key, pair.Value);
            }
            originalFacilities.Clear();
        }
        internal static bool Refresh(int generation)
        {
            if (State != KkIntegrationState.Ready || (!Active && !wasActive)) return true;
            if (CatalogPending) return false;
            // Retain restoration debt across loading/flight/disconnect until a real editor rebuild succeeds.
            if (Active) wasActive = true;
            if (generation != lastClosedGeneration)
            {
                // Reading the existing field avoids constructing KK UI before Camera.main exists.
                var instance = selectorInstance.GetValue(null);
                if (instance != null) selectorClose.Invoke(instance, null);
                lastClosedGeneration = generation;
            }
            var stockEligible = HighLogic.CurrentGame != null && HighLogic.CurrentGame.Parameters.Difficulty.AllowOtherLaunchSites;
            if (!Active || !stockEligible) RestoreFacilities();
            if (HighLogic.CurrentGame != null && !stockEligible)
            {
                wasActive = Active;
                return true; // A disabled stock option is not a loading condition to poll forever.
            }
            if (HighLogic.LoadedScene == GameScenes.EDITOR && stockEligible)
            {
                register.Invoke(null, new object[] { EditorDriver.editorFacility });
                wasActive = Active;
                return true;
            }
            return false;
        }
    }
}
