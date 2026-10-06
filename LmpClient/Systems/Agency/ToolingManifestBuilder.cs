using System;
using System.Collections.Generic;
using LmpCommon.Agency;

namespace LmpClient.Systems.Agency
{
    internal static class ToolingManifestBuilder
    {
        public static ToolingManifest Build(ShipConstruct ship, VesselCrewManifest crew)
        {
            if (ship == null || ship.parts == null) throw new InvalidOperationException("No craft is loaded.");
            var parts = new List<ToolingPart>();
            var cargo = new List<ToolingCargo>();
            for (var index = 0; index < ship.parts.Count; index++)
            {
                var part = ship.parts[index];
                if (!part || part.partInfo == null) throw new InvalidOperationException("A craft part is unavailable.");
                var cost = (double)part.partInfo.cost + part.GetModuleCosts(part.partInfo.cost);
                foreach (var resource in part.Resources)
                    cost += resource.info.unitCost * (resource.amount - resource.maxAmount);
                foreach (var inventory in part.FindModulesImplementing<ModuleInventoryPart>())
                {
                    cost -= inventory.GetModuleCost(0, ModifierStagingSituation.CURRENT);
                    AddCargo(inventory, cargo, index);
                }
                parts.Add(new ToolingPart { Name = part.partInfo.name, UnitCost = Math.Max(0, cost),
                    IsScience = part.partInfo.category == PartCategories.Science || part.FindModuleImplementing<ModuleScienceExperiment>() != null });
            }
            if (crew != null)
                foreach (var kerbal in crew.GetAllCrew(false))
                    if (kerbal != null)
                    {
                        var seat = crew.GetPartForCrew(kerbal);
                        var index = seat == null ? -1 : ship.parts.FindIndex(p => p.craftID == seat.PartID);
                        AddCargo(kerbal.KerbalInventoryModule, cargo, index, kerbal.name);
                    }
            return new ToolingManifest { Parts = parts.ToArray(), Cargo = cargo.ToArray() };
        }
        public static ToolingManifest FromFile(string path, VesselCrewManifest crew) => FromConfig(ConfigNode.Load(path), crew);
        public static ToolingManifest FromConfig(ConfigNode craft, VesselCrewManifest crew)
        {
            if (craft == null) throw new InvalidOperationException("Craft file is unavailable.");
            var parts = new List<ToolingPart>();
            var cargo = new List<ToolingCargo>();
            var nodes = craft.GetNodes("PART");
            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];
                var name = PartName(node);
                var info = PartLoader.getPartInfoByName(name) ?? throw new InvalidOperationException("Missing part: " + name);
                ShipConstruction.GetPartCostsAndMass(node, info, out var dry, out var fuel, out _, out _);
                var cost = (double)dry + fuel;
                foreach (var module in node.GetNodes("MODULE"))
                {
                    if (module.GetValue("name") != "ModuleInventoryPart") continue;
                    var inventory = module.GetNode("STOREDPARTS");
                    if (inventory == null) continue;
                    foreach (ConfigNode stored in inventory.nodes)
                    {
                        var storedNode = stored.GetNode("PART");
                        if (storedNode == null) continue;
                        var storedName = PartName(storedNode);
                        var storedInfo = PartLoader.getPartInfoByName(storedName) ?? throw new InvalidOperationException("Missing cargo: " + storedName);
                        ShipConstruction.GetPartCostsAndMass(storedNode, storedInfo, out var cargoDry, out var cargoFuel, out _, out _);
                        int quantity = 1; stored.TryGetValue("quantity", ref quantity); quantity = Math.Max(1, quantity);
                        var cargoCost = Math.Max(0, (double)cargoDry + cargoFuel);
                        cost -= cargoCost * quantity;
                        cargo.Add(new ToolingCargo { Name = storedName, Count = quantity, UnitCost = cargoCost, ContainerPartIndex = index });
                    }
                }
                parts.Add(new ToolingPart { Name = name, UnitCost = Math.Max(0, cost), IsScience = info.category == PartCategories.Science || info.partPrefab.FindModuleImplementing<ModuleScienceExperiment>() != null });
            }
            if (crew != null)
                foreach (var kerbal in crew.GetAllCrew(false))
                {
                    var seat = crew.GetPartForCrew(kerbal);
                    var container = -1;
                    if (seat != null)
                        for (var index = 0; index < nodes.Length; index++)
                            if (nodes[index].GetValue("part")?.EndsWith("_" + seat.PartID, StringComparison.Ordinal) == true) { container = index; break; }
                    AddCargo(kerbal.KerbalInventoryModule, cargo, container, kerbal.name);
                }
            return new ToolingManifest { Parts = parts.ToArray(), Cargo = cargo.ToArray() };
        }
        internal static string PartName(ConfigNode node)
        {
            var canonical = node.GetValue("name");
            if (!string.IsNullOrEmpty(canonical)) return canonical;
            var name = node.GetValue("part");
            if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Part has no name.");
            var split = name.LastIndexOf('_');
            if (split > 0 && uint.TryParse(name.Substring(split + 1), out _)) name = name.Substring(0, split);
            return name;
        }
        internal static ToolingCargo[] CaptureCargo(ProtoVessel vessel)
        {
            var cargo = new List<ToolingCargo>();
            foreach (var part in vessel.protoPartSnapshots)
            {
                if (vessel.vesselType != VesselType.EVA)
                    foreach (var module in part.modules)
                        if (module.moduleName == "ModuleInventoryPart") CaptureInventory(module.moduleValues, part.flightID, null, cargo);
                foreach (var crew in part.protoModuleCrew)
                    CaptureInventory(crew.InventoryNode, part.flightID, crew.name, cargo);
            }
            return cargo.ToArray();
        }
        private static void CaptureInventory(ConfigNode inventory, uint host, string crew, List<ToolingCargo> cargo)
        {
            var stored = inventory?.GetNode("STOREDPARTS");
            if (stored == null) return;
            foreach (ConfigNode row in stored.nodes)
            {
                var part = row.GetNode("PART");
                if (part == null) continue;
                var count = 1; row.TryGetValue("quantity", ref count);
                cargo.Add(new ToolingCargo { Name = PartName(part), Count = Math.Max(1, count), ContainerFlightId = host, CrewName = crew });
            }
        }
        private static void AddCargo(ModuleInventoryPart inventory, List<ToolingCargo> cargo, int container, string crewName = null)
        {
            if (!inventory) return;
            foreach (var stored in inventory.storedParts.Values)
            {
                if (stored?.snapshot == null) continue;
                ShipConstruction.GetPartCosts(stored.snapshot, stored.snapshot.partInfo, out var dry, out var fuel);
                cargo.Add(new ToolingCargo { Name = stored.partName, CrewName = crewName, ContainerPartIndex = container, Count = Math.Max(1, stored.quantity), UnitCost = Math.Max(0, dry + fuel) });
            }
        }
    }
}
