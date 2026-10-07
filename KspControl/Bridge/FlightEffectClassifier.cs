using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Contracts;

namespace KspControl.Bridge
{
    /// <summary>The complete effect set of one flight request, classified before anything is dispatched.</summary>
    internal sealed class FlightEffectPlan
    {
        public ClassifiedEffect[] Effects { get; set; }
        /// <summary>Human-readable names of effects that cannot be undone or that thrust, separate, deploy or release: "decouple:123/ModuleDecouple".</summary>
        public List<string> Consequential { get; } = new List<string>();
        /// <summary>Modules the classifier does not know. A non-empty list refuses the whole request: unknown effects are blocked, never assumed harmless.</summary>
        public List<string> Unclassified { get; } = new List<string>();
        public bool Allowed { get { return Unclassified.Count == 0; } }
    }

    /// <summary>
    /// Names what a stage or an action group would do, by the part modules it reaches. Pure and table driven: a mod's module that is not in a
    /// table is unclassified, and an unclassified effect is refused until someone classifies it (plan: expanded part controls).
    /// </summary>
    internal static class FlightEffectClassifier
    {
        /// <summary>State changes with no thrust, separation or release: lights, gear, brakes, panels, animations, control surfaces.</summary>
        private static readonly HashSet<string> Benign = new HashSet<string>(StringComparer.Ordinal)
        {
            "ModuleLight", "ModuleColorChanger", "ModuleAnimateGeneric", "ModuleLandingGear", "ModuleLandingGearFixed", "ModuleWheelBrakes", "ModuleWheelDeployment",
            "ModuleWheelSteering", "ModuleWheelMotor", "ModuleDeployableSolarPanel", "ModuleDeployableAntenna", "ModuleDeployableRadiator", "ModuleCargoBay",
            "ModuleControlSurface", "ModuleAeroSurface", "ModuleResourceIntake", "ModuleSAS", "ModuleReactionWheel", "ModuleDataTransmitter", "ModuleStatusLight",
            "ModuleAnimationGroup", "ModuleLiftingSurface", "ModuleCommand", "ModuleGenerator", "ModuleResourceConverter", "ModuleActiveRadiator"
        };

        /// <summary>Known and consequential, with the kind that is reported.</summary>
        private static readonly Dictionary<string, string> Consequential = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ModuleEngines", "engine" }, { "ModuleEnginesFX", "engine" }, { "ModuleRCS", "thruster" }, { "ModuleRCSFX", "thruster" },
            { "ModuleDecouple", "decouple" }, { "ModuleAnchoredDecoupler", "decouple" }, { "ModuleProceduralFairing", "decouple" },
            { "ModuleDockingNode", "dock" }, { "ModuleGrappleNode", "dock" }, { "ModuleParachute", "chute" }, { "LaunchClamp", "release" },
            { "ModuleScienceExperiment", "science" }, { "ModuleRoboticServoRotor", "robotics" }, { "ModuleRoboticServoHinge", "robotics" },
            { "ModuleRoboticServoPiston", "robotics" }, { "ModuleRoboticRotationServo", "robotics" }, { "ModuleRoboticServoHinge2", "robotics" }
        };

        /// <summary>
        /// Classifies <paramref name="actions"/> for the vessel. With <paramref name="staging"/> set only modules that act on stage activation count;
        /// an action group binds individual actions, so every bound action counts.
        /// </summary>
        public static FlightEffectPlan Plan(string vesselId, IEnumerable<FlightPartAction> actions, bool staging)
        {
            var plan = new FlightEffectPlan();
            var effects = new List<ClassifiedEffect> { new ClassifiedEffect(FlightEffects.Family, EntityOf(vesselId), 0) };
            foreach (var action in (actions ?? Enumerable.Empty<FlightPartAction>()))
            {
                if (action == null) continue;
                // On stage activation a module counts when it runs its own code there, or when the tables name it as consequential.
                if (staging && !action.ActsOnStaging && !Consequential.ContainsKey(action.Module ?? "")) continue;
                var module = action.Module ?? "";
                var label = module + (string.IsNullOrEmpty(action.Action) ? "" : "." + action.Action);
                var part = action.PartId ?? "0";
                string kind;
                if (Benign.Contains(module))
                    effects.Add(new ClassifiedEffect(FlightEffects.Family, Clip(EntityOf(vesselId) + "/part:" + part + "/" + module), 0));
                else if (Consequential.TryGetValue(module, out kind))
                {
                    effects.Add(new ClassifiedEffect(FlightEffects.Family, Clip(EntityOf(vesselId) + "/" + kind + ":" + part + "/" + module), 0));
                    plan.Consequential.Add(kind + ":" + part + "/" + label);
                }
                else
                {
                    // Not in any table: the effect name is deliberately not a known effect, so the authority refuses it as well.
                    effects.Add(new ClassifiedEffect(FlightEffects.Unclassified, Clip(EntityOf(vesselId) + "/part:" + part + "/" + (module.Length == 0 ? "unknown" : module)), 0));
                    plan.Unclassified.Add(part + "/" + (label.Length == 0 ? "unknown" : label));
                }
                if (effects.Count > 1000) { plan.Unclassified.Add("too_many_effects"); break; }
            }
            plan.Effects = effects.ToArray();
            return plan;
        }

        /// <summary>Joins two plans for one request. The second plan's leading vessel effect is dropped; order stays deterministic.</summary>
        public static FlightEffectPlan Merge(FlightEffectPlan first, FlightEffectPlan second)
        {
            var merged = new FlightEffectPlan { Effects = first.Effects.Concat(second.Effects.Skip(1)).ToArray() };
            merged.Consequential.AddRange(first.Consequential); merged.Consequential.AddRange(second.Consequential);
            merged.Unclassified.AddRange(first.Unclassified); merged.Unclassified.AddRange(second.Unclassified);
            return merged;
        }

        public static string EntityOf(string vesselId) { return FlightEffects.EntityPrefix + vesselId; }

        private static string Clip(string text) { return text.Length <= 256 ? text : text.Substring(0, 256); }
    }
}
