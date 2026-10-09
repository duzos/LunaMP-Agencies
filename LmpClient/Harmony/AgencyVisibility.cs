using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using LmpClient.Systems.Agency;
using LmpClient.VesselUtilities;
using UnityEngine;

namespace LmpClient.Harmony
{
    public static class AgencyVisibility
    {
        public static bool Ready {get;private set;}
        private static string diagnosticReason;
        public static string DiagnosticReason => diagnosticReason ?? AgencyCommNetVisibility.DiagnosticReason;
        private static FieldInfo markerVessel,markerCanvas,orbitTarget;
        private static MethodInfo rebuildList,updateCounts;
        private static bool restoringCounts;
        private static FieldInfo orbitLineField;
        private static PropertyInfo orbitLineActive;
        private sealed class OrbitMask { internal object Line; internal bool Active; }
        private static readonly Dictionary<OrbitRendererBase,OrbitMask> orbitMasks = new Dictionary<OrbitRendererBase,OrbitMask>();
        private static readonly HashSet<OrbitRendererBase> observedOrbits = new HashSet<OrbitRendererBase>();
        private sealed class Mask {internal float Alpha;internal bool Interactable,Blocks;}
        private static readonly Dictionary<CanvasGroup,Mask> masks=new Dictionary<CanvasGroup,Mask>();
        private static readonly Dictionary<TrackingStationObjectButton,bool> counts=new Dictionary<TrackingStationObjectButton,bool>();
        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                orbitLineField=AccessTools.Field(typeof(OrbitRendererBase),"orbitLine")??throw new MissingFieldException("OrbitRendererBase.orbitLine");
                orbitLineActive=orbitLineField.FieldType.GetProperty("active")??throw new MissingMemberException("VectorLine.active");
                markerVessel=AccessTools.Field(typeof(KSCVesselMarker),"v")??throw new MissingFieldException("KSCVesselMarker.v");
                markerCanvas=AccessTools.Field(typeof(KSCVesselMarker),"canvasGroup")??throw new MissingFieldException("KSCVesselMarker.canvasGroup");
                orbitTarget=AccessTools.Field(typeof(OrbitTargeter),"target")??throw new MissingFieldException("OrbitTargeter.target");
                rebuildList=AccessTools.Method(typeof(SpaceTracking),"ConstructUIList",Type.EmptyTypes)??throw new MissingMethodException("SpaceTracking.ConstructUIList");
                updateCounts=AccessTools.Method(typeof(MapViewFiltering),"UpdateVesselCounts",Type.EmptyTypes)??throw new MissingMethodException("MapViewFiltering.UpdateVesselCounts");
                Patch(harmony,typeof(MapViewFiltering),"CheckAgainstFilter",new[]{typeof(Vessel)},null,nameof(Filter));
                Patch(harmony,typeof(OrbitRenderer),"CanDrawAnyIcons",Type.EmptyTypes,null,nameof(Icons));
                Patch(harmony,typeof(OrbitRendererBase),"CanDrawAnyIcons",Type.EmptyTypes,null,nameof(Icons));
                Patch(harmony,typeof(OrbitRendererBase),"DrawSpline",Type.EmptyTypes,nameof(Spline));
                var hit = typeof(OrbitRendererBase.OrbitCastHit).MakeByRefType();
                Patch(harmony,typeof(OrbitRendererBase),"OrbitCast",new[]{typeof(Vector3),hit,typeof(float)},nameof(OrbitHit));
                Patch(harmony,typeof(OrbitRendererBase),"OrbitCast",new[]{typeof(Vector3),typeof(Camera),hit,typeof(float)},nameof(OrbitHit));
                Patch(harmony,typeof(OrbitRenderer),"OrbitCast",new[]{typeof(Vector3),hit,typeof(float)},nameof(OrbitHit));
                Patch(harmony,typeof(MainSystem),"OnGUI",Type.EmptyTypes,null,nameof(DrawContacts));
                Patch(harmony,typeof(VesselLabels),"ProcessLabel",new[]{typeof(BaseLabel),typeof(Vessel),typeof(ITargetable),typeof(Vector3)},nameof(Label));
                foreach(var method in new[]{"GoToAndFocusVessel","RequestVessel","FlyVessel","onVesselIconClick"})Patch(harmony,typeof(SpaceTracking),method,new[]{typeof(Vessel)},nameof(Select));
                Patch(harmony,typeof(SpaceTracking),"SetVessel",new[]{typeof(Vessel),typeof(bool)},nameof(Select));
                Patch(harmony,typeof(PlanetariumCamera),"SetTarget",new[]{typeof(MapObject)},nameof(MapTarget));
                Patch(harmony,typeof(FlightGlobals),"SetVesselTarget",new[]{typeof(ITargetable),typeof(bool)},nameof(Target));
                Patch(harmony,typeof(OrbitTargeter),"SetTarget",new[]{typeof(OrbitDriver)},nameof(OrbitSelect));
                Patch(harmony,typeof(OrbitTargeter),"CanDrawAnyLines",Type.EmptyTypes,null,nameof(OrbitVisible));
                Patch(harmony,typeof(OrbitTargeter),"CanDrawAnyNode",Type.EmptyTypes,null,nameof(OrbitVisible));
                Patch(harmony,typeof(KSCVesselMarker),"OnLateUpdate",Type.EmptyTypes,nameof(BeforeMarker),nameof(AfterMarker));
                foreach(var method in new[]{"Expand","OnFlyButtonInput","OnRecoverButtonInput"})Patch(harmony,typeof(KSCVesselMarker),method,Type.EmptyTypes,nameof(MarkerSelect));
                foreach(var method in new[]{"FlyVessel","RecoverVessel"})Patch(harmony,typeof(KSCVesselMarkers),method,new[]{typeof(Vessel)},nameof(Select));
                Patch(harmony,typeof(TrackingStationObjectButton),"ShowCount",new[]{typeof(bool)},nameof(Count));
                Patch(harmony,typeof(MainSystem),"Update",Type.EmptyTypes,null,nameof(Tick));
                if (!AgencyCommNetVisibility.TryAttach(harmony)) throw new InvalidOperationException(AgencyCommNetVisibility.DiagnosticReason);
                Ready=true;
            }
            catch(Exception e){Ready=false;diagnosticReason=e.Message;LunaLog.LogError("[AgencyVisibility] Required presentation hooks unavailable: "+e);}
        }
        private static void Patch(HarmonyLib.Harmony harmony,Type type,string name,Type[] args,string prefix,string postfix=null)
        {
            var method=AccessTools.Method(type,name,args)??throw new MissingMethodException(type.FullName,name);
            harmony.Patch(method,prefix==null?null:new HarmonyMethod(typeof(AgencyVisibility),prefix),postfix==null?null:new HarmonyMethod(typeof(AgencyVisibility),postfix));
        }
        private static void Filter(Vessel __0,ref bool __result){if(__result && !VisibilityClient.CanSee(__0))__result=false;}
        private static void Icons(OrbitRendererBase __instance,ref bool __result){if(__result && !VisibilityClient.CanSee(__instance.vessel))__result=false;}
        private static bool Label(BaseLabel __0,Vessel __1){if(VisibilityClient.CanSee(__1))return true;__0.Disable();return false;}
        private static bool Select(Vessel __0)=>VisibilityClient.CanSee(__0);
        private static bool MapTarget(MapObject __0)=>!__0 || VisibilityClient.CanSee(__0.vessel);
        private static bool Target(ITargetable __0)=>__0==null || VisibilityClient.CanSee(__0.GetVessel());
        private static bool OrbitSelect(OrbitDriver __0)=>!__0 || VisibilityClient.CanSee(__0.vessel);
        private static void OrbitVisible(OrbitTargeter __instance,ref bool __result)
        {var target=orbitTarget.GetValue(__instance) as OrbitDriver;if(__result && target && !VisibilityClient.CanSee(target.vessel))__result=false;}
        private static void DrawContacts()
        {
            try { Windows.Agency.VisibilityContactOverlay.Draw(); }
            catch(Exception e) { Diagnostics.PlaytestDiagnostics.Write("client.contacts.overlay-error",()=>e.Message); }
        }
        private static bool OrbitHit(OrbitRendererBase __instance, ref bool __result)
        { if(VisibilityClient.CanSee(__instance.vessel))return true; __result=false; return false; }
        private static bool Spline(OrbitRendererBase __instance)
        {
            if(VisibilityClient.Enabled)observedOrbits.Add(__instance);
            return ApplyOrbitMask(__instance);
        }
        private static bool ApplyOrbitMask(OrbitRendererBase renderer)
        {
            var allowed = !VisibilityClient.Enabled || VisibilityClient.CanSee(renderer.vessel);
            var line = orbitLineField.GetValue(renderer);
            if(orbitMasks.TryGetValue(renderer,out var saved) && (!ReferenceEquals(saved.Line,line) || allowed))
            {
                if(allowed && ReferenceEquals(saved.Line,line))try { orbitLineActive.SetValue(saved.Line,saved.Active,null); } catch { }
                orbitMasks.Remove(renderer);
            }
            if(!allowed && line!=null)
            {
                if(!orbitMasks.ContainsKey(renderer))orbitMasks[renderer]=new OrbitMask{Line=line,Active=(bool)orbitLineActive.GetValue(line,null)};
                orbitLineActive.SetValue(line,false,null);
            }
            return allowed;
        }
        internal static void RefreshOrbitMasks()
        {
            foreach(var renderer in new List<OrbitRendererBase>(observedOrbits))
            {
                if(!renderer){observedOrbits.Remove(renderer);orbitMasks.Remove(renderer);continue;}
                ApplyOrbitMask(renderer);
            }
        }
        private static void RestoreOrbitMasks()
        {
            foreach(var pair in orbitMasks)try{if(ReferenceEquals(orbitLineField.GetValue(pair.Key),pair.Value.Line))orbitLineActive.SetValue(pair.Value.Line,pair.Value.Active,null);}catch{}
            orbitMasks.Clear();observedOrbits.Clear();
        }
        private static void Restore(CanvasGroup group)
        {
            if(!group || !masks.TryGetValue(group,out var state))return;
            group.alpha=state.Alpha;group.interactable=state.Interactable;group.blocksRaycasts=state.Blocks;masks.Remove(group);
        }
        private static void BeforeMarker(KSCVesselMarker __instance)=>Restore(markerCanvas.GetValue(__instance) as CanvasGroup);
        private static void AfterMarker(KSCVesselMarker __instance)
        {
            if(VisibilityClient.CanSee(markerVessel.GetValue(__instance) as Vessel))return;
            var group=markerCanvas.GetValue(__instance) as CanvasGroup;if(!group)return;
            masks[group]=new Mask{Alpha=group.alpha,Interactable=group.interactable,Blocks=group.blocksRaycasts};
            group.alpha=0;group.interactable=false;group.blocksRaycasts=false;
        }
        private static bool MarkerSelect(KSCVesselMarker __instance)=>VisibilityClient.CanSee(markerVessel.GetValue(__instance) as Vessel);
        private static void Count(TrackingStationObjectButton __instance,ref bool __0)
        {if(VisibilityClient.Enabled && !restoringCounts){counts[__instance]=__0;__0=false;}}
        internal static void RestorePresentation()
        {
            RestoreOrbitMasks();
            foreach(var group in new List<CanvasGroup>(masks.Keys))Restore(group);
            masks.Clear();
            restoringCounts=true;
            try {foreach(var count in new List<KeyValuePair<TrackingStationObjectButton,bool>>(counts))if(count.Key)count.Key.ShowCount(count.Value);}
            finally {restoringCounts=false;}
            counts.Clear();RefreshPresentation();
        }
        internal static void RefreshPresentation()
        {
            foreach(var group in new List<CanvasGroup>(masks.Keys))if(!group)masks.Remove(group);
            foreach(var button in new List<TrackingStationObjectButton>(counts.Keys))if(!button)counts.Remove(button);
            if(SpaceTracking.Instance)rebuildList?.Invoke(SpaceTracking.Instance,null);
            if(MapViewFiltering.Instance)updateCounts?.Invoke(MapViewFiltering.Instance,null);
            // Plan 41: identification may have changed, so re-run the agency orbit / icon tint (orbit-only, no Version bump).
            Systems.Agency.AgencyIdentityClient.RequestOrbitRefresh();
        }
        internal static void ClearHiddenSelection()
        {
            if(!VisibilityClient.Enabled)return;
            var target=FlightGlobals.fetch?.VesselTarget;
            if(target!=null && !VisibilityClient.CanSee(target.GetVessel()))FlightGlobals.fetch.SetVesselTarget(null,false);
            if(SpaceTracking.Instance && SpaceTracking.Instance.SelectedVessel && !VisibilityClient.CanSee(SpaceTracking.Instance.SelectedVessel))SpaceTracking.Instance.SetVessel(null,true);
            if(PlanetariumCamera.fetch && PlanetariumCamera.fetch.target && PlanetariumCamera.fetch.target.vessel && !VisibilityClient.CanSee(PlanetariumCamera.fetch.target.vessel))
                PlanetariumCamera.fetch.SetTarget(PlanetariumCamera.fetch.target.vessel.mainBody);
            if(HighLogic.LoadedSceneIsFlight && VesselCommon.IsSpectating && FlightGlobals.ActiveVessel && !VisibilityClient.CanSee(FlightGlobals.ActiveVessel))
            {
                Diagnostics.PlaytestDiagnostics.Write("client.visibility.spectate-ended",()=> $"vessel={FlightGlobals.ActiveVessel.id}");
                HighLogic.LoadScene(GameScenes.SPACECENTER);
            }
        }
        private static void Tick()
        {
            try{VisibilityClient.Tick();}
            catch(Exception e){diagnosticReason=e.Message;Diagnostics.PlaytestDiagnostics.Write("client.visibility.adapter-error",()=>e.Message);}
        }
    }
}
