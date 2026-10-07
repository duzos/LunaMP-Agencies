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

        public CatalogPartSource Read(string partName)
        {
            var info = (PartLoader.LoadedPartsList ?? new List<AvailablePart>()).FirstOrDefault(p => p != null && p.name == partName);
            if (info == null || info.partPrefab == null) return null;
            var prefab = info.partPrefab;
            var variant = info.variant;
            var rawNodes = variant != null && variant.AttachNodes != null ? variant.AttachNodes : prefab.attachNodes;
            var source = new CatalogPartSource
            {
                Name = info.name,
                KspCategory = info.category.ToString(),
                Buildable = PartFilters.IsBuildable(info.category == PartCategories.none, info.TechRequired, info.TechHidden),
                StackNodesFromVariant = variant != null && variant.AttachNodes != null,
                VariantName = variant == null ? null : variant.Name,
                RawStackNodeCount = rawNodes == null ? 0 : rawNodes.Count
            };
            if (ResearchAvailable)
            {
                source.TechAvailable = ResearchAndDevelopment.PartTechAvailable(info);
                source.ModelPurchased = ResearchAndDevelopment.PartModelPurchased(info);
            }
            foreach (PartModule module in prefab.Modules) if (module != null && source.ModuleNames.Count < 128) source.ModuleNames.Add(module.moduleName);
            foreach (PartResource resource in prefab.Resources) if (resource != null && source.ResourceNames.Count < 32) source.ResourceNames.Add(resource.resourceName);
            foreach (var node in rawNodes ?? new List<AttachNode>())
            {
                if (node == null || node.nodeType != AttachNode.NodeType.Stack) continue;
                source.StackNodes.Add(Convert(node.id, node.position, node.orientation, node.size));
            }
            var rules = prefab.attachRules;
            if (rules != null)
                source.AttachRules = new CatalogAttachRulesSource { Stack = rules.stack, Srf = rules.srfAttach, AllowStack = rules.allowStack,
                    AllowSrf = rules.allowSrfAttach, AllowCollision = rules.allowCollision, AllowDock = rules.allowDock };
            // The prefab srfAttachNode keeps a default nodeType; real capability lives in attachRules.srfAttach.
            if (prefab.srfAttachNode != null && rules != null && rules.srfAttach)
                source.SurfaceNode = Convert("srfAttach", prefab.srfAttachNode.position, prefab.srfAttachNode.orientation, prefab.srfAttachNode.size);
            return source;
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
