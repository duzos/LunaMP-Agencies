using System.Collections.Generic;
using KspControl.Bridge;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>
    /// Categories for the live modpack. CryoTanks/B9PartSwitch strip the RESOURCE nodes from stock tanks (the resources come from a B9 subtype at
    /// runtime), so the prefab reports no LiquidFuel/Oxidizer and a resource-only rule called a stock FL-T200 "other". Module lists below are the
    /// live ModuleManager.ConfigCache values.
    /// </summary>
    [TestClass]
    public class LiveCatalogCategoryTests
    {
        private static readonly string[] B9TankModules =
            { "ModuleCargoPart", "FundsKeeper", "StealBackMyFunds", "TweakScale", "ModuleTechnicolor", "ShipEffectsCollisions", "ModuleB9PartSwitch", "ModuleCryoTank", "ModuleFreeIva", "ModuleB9PartSwitch", "ModuleB9PartInfo", "AttachedOnEditor" };
        private static readonly string[] HeatShieldModules =
            { "ModuleJettison", "ModuleAblator", "ModuleColorChanger", "ModuleDecouple", "ModuleTestSubject", "ModuleLiftingSurface", "ModuleCargoPart", "TweakScale", "AttachedOnEditor" };

        [DataTestMethod]
        [DataRow("fuelTankSmallFlat")] [DataRow("fuelTankSmall")] [DataRow("fuelTank")] [DataRow("fuelTank.long")]
        public void AB9SwitchedStockTankWithNoPrefabResourcesIsATankByItsKspCategory(string name)
        {
            Assert.AreEqual("tank", ConstructionCatalogMapper.MapCategory(B9TankModules, new string[0], "FuelTank"), name);
        }

        [TestMethod] public void KspCategoryNeverOverridesAModuleRole()
        {
            Assert.AreEqual("engine", ConstructionCatalogMapper.MapCategory(new[] { "ModuleEnginesFX" }, new string[0], "FuelTank"));
            Assert.AreEqual("command", ConstructionCatalogMapper.MapCategory(new[] { "ModuleCommand" }, new string[0], "FuelTank"));
            Assert.AreEqual("other", ConstructionCatalogMapper.MapCategory(new[] { "ModuleRCSFX" }, new string[0], "FuelTank"));
            Assert.AreEqual("other", ConstructionCatalogMapper.MapCategory(B9TankModules, new string[0], "Structural"));
            Assert.AreEqual("tank", ConstructionCatalogMapper.MapCategory(new string[0], new[] { "LiquidFuel", "Oxidizer" }, "Propulsion"));
        }

        [TestMethod] public void AnAblativeDecouplerIsAHeatShieldNotAStagedDecoupler()
        {
            Assert.AreEqual("heatshield", ConstructionCatalogMapper.MapCategory(HeatShieldModules, new[] { "Ablator" }, "Thermal"));
            Assert.AreEqual(Pure.PartCategories.HeatShield, ConstructionCatalogMapper.HeatShield);
            Assert.AreEqual("decoupler", ConstructionCatalogMapper.MapCategory(new[] { "ModuleDecouple" }, new string[0], "Coupling"));
        }

        private sealed class Reader : ICatalogPartReader
        {
            public bool ResearchAvailable => false;
            public bool SandboxMode => true;
            public CatalogPartSource Read(string name) => new CatalogPartSource
            {
                Name = name, KspCategory = name == "HeatShield1" ? "Thermal" : "FuelTank", Buildable = true,
                ModuleNames = name == "HeatShield1" ? HeatShieldModules : B9TankModules, ResourceNames = name == "HeatShield1" ? new[] { "Ablator" } : new string[0],
                StackNodes = new[] { new CatalogNodeSource { Id = "top", Position = new[] { 0.0, 0.5, 0.0 }, Orientation = new[] { 0.0, 1.0, 0.0 }, Size = 1 } },
                AttachRules = new CatalogAttachRulesSource { Stack = true, AllowStack = true }
            };
        }

        [TestMethod] public void TheCatalogReplyAndBothParsersCarryTheLiveCategories()
        {
            var names = new List<string> { "fuelTankSmall", "HeatShield1" };
            var data = ConstructionCatalogMapper.Build(names, new Reader(), ConstructionSupportPolicy.Default);
            Assert.AreEqual("tank", (string)data["parts"][0]["category"]);
            Assert.AreEqual("heatshield", (string)data["parts"][1]["category"]);
            var into = new ReplanCatalog(); string error;
            Assert.IsTrue(ReplanCatalog.TryParse(data, names, into, out error), error);
            Assert.AreEqual(Pure.PartCategories.Tank, into.Found["fuelTankSmall"].Part.Category);
            Assert.AreEqual(Pure.PartCategories.HeatShield, into.Found["HeatShield1"].Part.Category);
        }
    }
}
