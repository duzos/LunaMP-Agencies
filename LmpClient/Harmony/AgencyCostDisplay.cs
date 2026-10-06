using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LmpClient.Systems.Agency;
using UnityEngine;

namespace LmpClient.Harmony
{
    internal static class AgencyCostDisplay
    {
        private sealed class Source
        {
            internal ShipConstruct Ship;
            internal ShipTemplate Template;
            internal VesselCrewManifest Crew;
            internal Vector3 Scale;
        }
        private static readonly Dictionary<CostWidget, Source> sources = new Dictionary<CostWidget, Source>();
        internal static void Install(HarmonyLib.Harmony harmony)
        {
            var patches = new List<Tuple<MethodInfo, MethodInfo>>();
            try
            {
                var names = new[] { "onShipModified", "OnCrewModified", "onCrewDialogChange", "onCraftFileSelected", "onShipReset", "OnDestroy" };
                var handlers = new[] { nameof(Ship), nameof(Crew), nameof(Crew), nameof(Template), nameof(Reset), nameof(Reset) };
                for (var i = 0; i < names.Length; i++)
                    patches.Add(Tuple.Create(AccessTools.Method(typeof(CostWidget), names[i]) ?? throw new MissingMethodException(names[i]),
                        AccessTools.Method(typeof(AgencyCostDisplay), handlers[i])));
                foreach (var patch in patches) harmony.Patch(patch.Item1, prefix: new HarmonyMethod(patch.Item2));
                Diagnostics.PlaytestDiagnostics.Write("client.patch.cost-display", () => "attached=True");
            }
            catch (Exception e)
            {
                foreach (var patch in patches) harmony.Unpatch(patch.Item1, patch.Item2);
                LunaLog.LogError("[Tooling] Stock cost display patch unavailable: " + e);
            }
        }
        private static bool Ship(CostWidget __instance, ShipConstruct __0, Vector3 ___textSizeDefault)
            => Set(__instance, new Source { Ship = __0, Crew = ShipConstruction.ShipManifest, Scale = ___textSizeDefault });
        private static bool Crew(CostWidget __instance, VesselCrewManifest __0, Vector3 ___textSizeDefault)
            => !HighLogic.LoadedSceneIsEditor || Set(__instance, new Source { Ship = EditorLogic.fetch?.ship, Crew = __0, Scale = ___textSizeDefault });
        private static bool Template(CostWidget __instance, ShipTemplate __0, Vector3 ___textSizeDefault)
            => Set(__instance, new Source { Template = __0, Crew = ShipConstruction.ShipManifest, Scale = ___textSizeDefault });
        private static bool Set(CostWidget widget, Source source)
        {
            if (!ToolingClient.Enabled || !HighLogic.LoadedSceneIsEditor) { sources.Remove(widget); return true; }
            sources[widget] = source;
            Render(widget, source);
            return false;
        }
        private static void Reset(CostWidget __instance) => sources.Remove(__instance);
        internal static void Clear() => sources.Clear();
        internal static void Refresh()
        {
            foreach (var pair in sources.ToArray())
            {
                if (!pair.Key) { sources.Remove(pair.Key); continue; }
                Render(pair.Key, pair.Value);
            }
        }
        private static void Render(CostWidget widget, Source source)
        {
            if (!widget || !widget.text || !ToolingClient.Enabled) return;
            try
            {
                var quote = ToolingClient.DisplayQuote(source.Ship, source.Template, source.Crew);
                widget.text.transform.localScale = source.Scale;
                if (quote == null || !quote.Success)
                {
                    widget.text.text = "…";
                    widget.text.color = widget.irrelevantColor;
                    return;
                }
                // The agency quote is the final charge; stock currency modifiers must not
                // be applied a second time by CostWidget.onCostChange.
                widget.text.text = KSPUtil.LocalizeNumber(quote.LaunchCost, "N0");
                widget.text.color = !Funding.Instance ? widget.irrelevantColor :
                    quote.LaunchCost > Funding.Instance.Funds ? widget.unaffordableColor : widget.affordableColor;
            }
            catch (Exception e)
            {
                widget.text.text = "…";
                widget.text.color = widget.irrelevantColor;
                Diagnostics.PlaytestDiagnostics.Write("client.tooling.cost-display-unavailable", () => $"error={e.GetType().Name}:{e.Message}");
            }
        }
    }
}
