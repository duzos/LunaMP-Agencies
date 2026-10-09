using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LmpCommon.Agency;
using LmpClient.Systems.Agency;

namespace LmpClient.Harmony
{
    public static class AgencyTradeResearch
    {
        [ThreadStatic] private static HashSet<string> allowedParts;
        [ThreadStatic] private static Func<ToolingManifest> launchManifest;
        private sealed class TestContext {internal Func<ToolingManifest> Manifest;}
        private static readonly ConditionalWeakTable<PreFlightTests.ExperimentalPartsAvailable,TestContext> tests = new ConditionalWeakTable<PreFlightTests.ExperimentalPartsAvailable,TestContext>();
        public static bool Ready {get;private set;}
        /// <summary>The hooks run while trade or tooling is on: held design stock unlocks research even when trade is off (plan 40 R1.4).</summary>
        private static bool Active => TradeClient.Enabled || ToolingClient.Enabled;
        public static string DiagnosticReason {get;private set;}
        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                Patch(harmony,typeof(ShipConstruct),"LoadShip",new[]{typeof(ConfigNode),typeof(uint),typeof(bool),typeof(string).MakeByRefType()},nameof(Load),null,nameof(EndLoad));
                Patch(harmony,typeof(ShipTemplate),"LoadShip",new[]{typeof(ConfigNode)},nameof(Load),null,nameof(EndLoad));
                Patch(harmony,typeof(KSP.UI.Screens.CraftProfileInfo),"LoadDetailsFromCraftFile",new[]{typeof(ConfigNode),typeof(string),typeof(bool),typeof(bool)},nameof(Load),null,nameof(EndLoad));
                Patch(harmony,typeof(ResearchAndDevelopment),"PartTechAvailable",new[]{typeof(AvailablePart)},null,nameof(PartAvailable));
                foreach(var type in new[]{typeof(ShipConstruct),typeof(VesselCrewManifest)})
                {
                    var ctor=AccessTools.Constructor(typeof(PreFlightTests.ExperimentalPartsAvailable),new[]{type})??throw new MissingMethodException("ExperimentalPartsAvailable constructor");
                    harmony.Patch(ctor,postfix:new HarmonyMethod(typeof(AgencyTradeResearch),type==typeof(ShipConstruct)?nameof(ShipTest):nameof(CrewTest)));
                }
                Patch(harmony,typeof(PreFlightTests.ExperimentalPartsAvailable),"Test",Type.EmptyTypes,null,nameof(Test));
                Patch(harmony,typeof(LaunchSiteFacility),"launchChecks",Type.EmptyTypes,nameof(SiteChecks),null,nameof(EndSiteChecks));
                var launch=AccessTools.Method(typeof(FlightDriver),"StartWithNewLaunch",new[]{typeof(string),typeof(string),typeof(string),typeof(VesselCrewManifest)});
                if(launch==null) throw new MissingMethodException("StartWithNewLaunch");
                harmony.Patch(launch,prefix:new HarmonyMethod(typeof(AgencyTradeResearch),nameof(FinalLaunch)){priority=Priority.First});
                Ready=true;
            }
            catch(Exception e){Ready=false;DiagnosticReason=e.Message;LunaLog.LogError("[AgencyTrade] Research hooks unavailable: "+e);}
        }
        private static void Patch(HarmonyLib.Harmony harmony,Type type,string name,Type[] args,string prefix,string postfix,string finalizer=null)
        {
            var method=AccessTools.Method(type,name,args)??throw new MissingMethodException(type.FullName,name);
            harmony.Patch(method,prefix==null?null:new HarmonyMethod(typeof(AgencyTradeResearch),prefix),postfix==null?null:new HarmonyMethod(typeof(AgencyTradeResearch),postfix),null,finalizer==null?null:new HarmonyMethod(typeof(AgencyTradeResearch),finalizer));
        }
        private static void Load(ConfigNode __0,out HashSet<string> __state)
        {
            __state=allowedParts; allowedParts=null;
            if(!Active || !Ready) return;
            try
            {
                var manifest=ToolingManifestBuilder.FromConfig(__0,null);
                if(TradeClient.HasEntitlement(manifest) && CargoAllowed(manifest)) allowedParts=new HashSet<string>(manifest.Parts.Select(p=>p.Name),StringComparer.Ordinal);
            }
            catch { /* Malformed/missing parts retain normal stock load failures. */ }
        }
        private static Exception EndLoad(Exception __exception,HashSet<string> __state){allowedParts=__state;return __exception;}
        private static void PartAvailable(AvailablePart __0,ref bool __result)
        {if(!__result && Active && Ready && __0!=null && allowedParts?.Contains(__0.name)==true) __result=true;}
        private static bool StockAllowed(string name)
        {
            var part=PartLoader.getPartInfoByName(name);
            if(part==null) return false;
            if(!ResearchAndDevelopment.Instance) return true;
            return ResearchAndDevelopment.PartModelPurchased(part);
        }
        private static bool CargoAllowed(ToolingManifest manifest)=>manifest.Cargo.All(c=>StockAllowed(c.Name));
        internal static bool Validate(ToolingManifest manifest,out string reason)
        {
            reason=null;
            if(!TradeClient.Enabled && !ToolingClient.HasStockResearch(manifest)) return true;
            if(!Ready || TradeClient.Enabled && !TradeClient.Ready){reason=DiagnosticReason??"Waiting for purchased design permissions.";return false;}
            if(!CargoAllowed(manifest)){reason="Inventory contains an unresearched or unpurchased part.";return false;}
            if(manifest.Parts.All(p=>StockAllowed(p.Name)) || TradeClient.HasEntitlement(manifest)) return true;
            reason="Locked parts require the complete purchased design. Restore its part list or research the missing parts.";return false;
        }
        public static bool ValidateLive(ShipConstruct ship,out string reason)
        {return Validate(ToolingManifestBuilder.Build(ship,ShipConstruction.ShipManifest),out reason);}
        private static void ShipTest(PreFlightTests.ExperimentalPartsAvailable __instance,ShipConstruct __0)
        {tests.Remove(__instance);tests.Add(__instance,new TestContext{Manifest=()=>ToolingManifestBuilder.Build(__0,ShipConstruction.ShipManifest)});}
        private static void CrewTest(PreFlightTests.ExperimentalPartsAvailable __instance)
        {if(launchManifest!=null){tests.Remove(__instance);tests.Add(__instance,new TestContext{Manifest=launchManifest});}}
        private static void Test(PreFlightTests.ExperimentalPartsAvailable __instance,ref bool __result)
        {
            if(!Active || !tests.TryGetValue(__instance,out var context)) return;
            try {var manifest=context.Manifest();if(!Validate(manifest,out _)) __result=false;else if(TradeClient.HasEntitlement(manifest)) __result=true;}
            catch {__result=false;}
        }
        private static void SiteChecks(LaunchSiteFacility __instance,out Func<ToolingManifest> __state)
        {
            __state=launchManifest;
            launchManifest=()=>ToolingManifestBuilder.FromFile((string)AccessTools.Field(typeof(LaunchSiteFacility),"path").GetValue(__instance),
                (VesselCrewManifest)AccessTools.Field(typeof(LaunchSiteFacility),"manifest").GetValue(__instance));
        }
        private static Exception EndSiteChecks(Exception __exception,Func<ToolingManifest> __state){launchManifest=__state;return __exception;}
        private static bool FinalLaunch(string __0,VesselCrewManifest __3)
        {
            if(!Active) return true;
            try
            {
                var manifest=ToolingManifestBuilder.FromFile(__0,__3);
                if(!Validate(manifest,out var reason)) throw new InvalidOperationException(reason);
                var license=TradeClient.PrepareLaunch(manifest);
                Diagnostics.PlaytestDiagnostics.Write("client.trade.launch",()=> $"allowed=true fingerprint={ToolingPolicy.Fingerprint(manifest)} entitlement={license}");
                return true;
            }
            catch(Exception e){Diagnostics.PlaytestDiagnostics.Write("client.trade.launch",()=> $"allowed=false reason={e.Message}");ScreenMessages.PostScreenMessage(e.Message,6f,ScreenMessageStyle.UPPER_CENTER);return false;}
        }
    }
}
