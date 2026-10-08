using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Contracts;
using Newtonsoft.Json.Linq;

namespace KspControl.Bridge
{
    /// <summary>
    /// Reads loaded parts for parts.construction_catalog. Main thread only: it runs on the existing queued observation path
    /// (Observations.Execute), never on the loopback worker threads.
    /// </summary>
    internal sealed class UnityConstructionCatalogReader : ICatalogPartReader
    {
        public bool ResearchAvailable { get { return ResearchAndDevelopment.Instance != null; } }
        // Sandbox means the game mode, not "no R&D instance": a career or science save without R&D is unreadable, not allowed.
        public bool SandboxMode { get { return HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX; } }

        public CatalogPartSource Read(string partName)
        {
            var info = (PartLoader.LoadedPartsList ?? new List<AvailablePart>()).FirstOrDefault(p => p != null && p.name == partName);
            if (info == null || info.partPrefab == null) return null;
            var prefab = info.partPrefab;
            var variant = info.variant;
            var source = new CatalogPartSource
            {
                Name = info.name,
                KspCategory = info.category.ToString(),
                Buildable = PartFilters.IsBuildable(info.category == PartCategories.none, info.TechRequired, info.TechHidden),
                VariantName = variant == null ? null : variant.Name
            };
            if (ResearchAvailable)
            {
                source.TechAvailable = ResearchAndDevelopment.PartTechAvailable(info);
                source.ModelPurchased = ResearchAndDevelopment.PartModelPurchased(info);
            }
            foreach (PartModule module in prefab.Modules) if (module != null && source.ModuleNames.Count < 128) source.ModuleNames.Add(module.moduleName);
            foreach (PartModule module in prefab.Modules) if (module != null && module.IsStageable()) { source.Stageable = true; break; }
            foreach (PartModule module in prefab.Modules) if ((module is ModuleDecouple || module is ModuleAnchoredDecoupler) && module.IsStageable()) { source.DecouplerStageable = true; break; }
            foreach (PartResource resource in prefab.Resources) if (resource != null && source.ResourceNames.Count < 32) source.ResourceNames.Add(resource.resourceName);
            // Prefab nodes are the base layer. The default variant's list is an override (possibly empty), overlaid by id in the mapper.
            AddStackNodes(source.StackNodes, prefab.attachNodes);
            if (variant != null && variant.AttachNodes != null)
            {
                source.VariantStackNodes = new List<CatalogNodeSource>();
                AddStackNodes(source.VariantStackNodes, variant.AttachNodes);
            }
            var rules = prefab.attachRules;
            if (rules != null) source.AttachRules = Rules(rules);
            // The prefab srfAttachNode keeps a default nodeType; real capability lives in attachRules.srfAttach.
            if (prefab.srfAttachNode != null && rules != null && rules.srfAttach)
                source.SurfaceNode = Convert("srfAttach", prefab.srfAttachNode.position, prefab.srfAttachNode.orientation, prefab.srfAttachNode.size);
            return source;
        }

        private static void AddStackNodes(IList<CatalogNodeSource> into, List<AttachNode> nodes)
        {
            foreach (var node in nodes ?? new List<AttachNode>())
                if (node != null && node.nodeType == AttachNode.NodeType.Stack) into.Add(Convert(node.id, node.position, node.orientation, node.size));
        }

        private static CatalogAttachRulesSource Rules(AttachRules rules)
        {
            return new CatalogAttachRulesSource { Stack = rules.stack, Srf = rules.srfAttach, AllowStack = rules.allowStack,
                AllowSrf = rules.allowSrfAttach, AllowCollision = rules.allowCollision, AllowDock = rules.allowDock };
        }

        private static CatalogNodeSource Convert(string id, UnityEngine.Vector3 position, UnityEngine.Vector3 orientation, int size)
        {
            return new CatalogNodeSource { Id = id, Position = new double[] { position.x, position.y, position.z },
                Orientation = new double[] { orientation.x, orientation.y, orientation.z }, Size = size };
        }
    }

    internal sealed partial class Observations
    {
        private static readonly ConstructionSupportPolicy ConstructionPolicy = ConstructionSupportPolicy.Default;
        private static JObject ConstructionCatalog(JObject args)
        {
            List<string> names;
            try { names = ConstructionCatalogMapper.ParseNames(Arg(args, "partNames")); }
            catch (ArgumentException) { throw new InvalidArgumentException(); }
            return ConstructionCatalogMapper.Build(names, new UnityConstructionCatalogReader(), ConstructionPolicy);
        }
    }
}
