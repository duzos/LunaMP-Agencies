using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.BridgeTests
{
    [TestClass] public class ConstructionCatalogMapperTests
    {
        private sealed class FakeReader : ICatalogPartReader
        {
            public bool ResearchAvailable { get; set; }
            public readonly Dictionary<string, CatalogPartSource> Parts = new Dictionary<string, CatalogPartSource>(StringComparer.Ordinal);
            public readonly List<string> Reads = new List<string>();
            public CatalogPartSource Read(string name) { Reads.Add(name); CatalogPartSource p; return Parts.TryGetValue(name, out p) ? p : null; }
        }

        private static CatalogNodeSource N(string id, double y, double oy) => new CatalogNodeSource { Id = id, Position = new[] { 0.0, y, 0.0 }, Orientation = new[] { 0.0, oy, 0.0 }, Size = 1 };

        private static CatalogPartSource Part(string name, string[] modules, string[] resources, params CatalogNodeSource[] nodes) => new CatalogPartSource
        {
            Name = name, KspCategory = "Propulsion", Buildable = true, TechAvailable = true, ModelPurchased = true,
            ModuleNames = modules, ResourceNames = resources, StackNodes = nodes, RawStackNodeCount = nodes.Length,
            AttachRules = new CatalogAttachRulesSource { Stack = true, AllowStack = true, Srf = false, AllowSrf = true, AllowCollision = false, AllowDock = false }
        };

        // Real stock node values (S0 twin, 33-log): see EditorModel fixtures.
        private static FakeReader Stock()
        {
            var r = new FakeReader { ResearchAvailable = true };
            r.Parts["mk1pod.v2"] = Part("mk1pod.v2", new[] { "ModuleCommand", "ModuleReactionWheel" }, new[] { "ElectricCharge", "MonoPropellant" }, N("bottom", -0.4050379, -1), N("top", 0.6423756, 1));
            r.Parts["probeCoreOcto.v2"] = Part("probeCoreOcto.v2", new[] { "ModuleCommand" }, new[] { "ElectricCharge" }, N("top", 0.1870818, 1), N("bottom", -0.1870818, -1));
            r.Parts["fuelTankSmall"] = Part("fuelTankSmall", new string[0], new[] { "LiquidFuel", "Oxidizer" }, N("top", 0.55525, 1), N("bottom", -0.55525, -1));
            r.Parts["liquidEngine.v2"] = Part("liquidEngine.v2", new[] { "ModuleEnginesFX" }, new[] { "LiquidFuel" }, N("top", 0, 1), N("bottom", -1.63, -1));
            r.Parts["Decoupler.1"] = Part("Decoupler.1", new[] { "ModuleDecouple" }, new string[0], N("top", 0.05, 1), N("bottom", -0.05, -1));
            return r;
        }

        [DataTestMethod]
        [DataRow("mk1pod.v2", "command")] [DataRow("probeCoreOcto.v2", "command")] [DataRow("fuelTankSmall", "tank")]
        [DataRow("liquidEngine.v2", "engine")] [DataRow("Decoupler.1", "decoupler")]
        public void CategoriesUseTheEditorModelVocabulary(string name, string expected)
        {
            var part = Stock().Parts[name];
            Assert.AreEqual(expected, ConstructionCatalogMapper.MapCategory(part.ModuleNames, part.ResourceNames));
        }

        [TestMethod] public void CategoryPrecedenceAndFallbacks()
        {
            // A solid booster carries ModuleEnginesFX and SolidFuel: an engine, not a tank.
            Assert.AreEqual("engine", ConstructionCatalogMapper.MapCategory(new[] { "ModuleEnginesFX" }, new[] { "SolidFuel" }));
            Assert.AreEqual("decoupler", ConstructionCatalogMapper.MapCategory(new[] { "ModuleAnchoredDecoupler" }, new string[0]));
            Assert.AreEqual("command", ConstructionCatalogMapper.MapCategory(new[] { "ModuleCommand", "ModuleEngines" }, new[] { "LiquidFuel" }));
            Assert.AreEqual("other", ConstructionCatalogMapper.MapCategory(new[] { "ModuleRCSFX" }, new[] { "MonoPropellant" }));
            Assert.AreEqual("other", ConstructionCatalogMapper.MapCategory(new[] { "ModuleParachute" }, new string[0]));
            Assert.AreEqual("other", ConstructionCatalogMapper.MapCategory(null, null));
        }

        [TestMethod] public void BuildEmitsNodesRulesAndSupport()
        {
            var json = ConstructionCatalogMapper.Build(new[] { "mk1pod.v2", "Decoupler.1", "missingPart" }, Stock(), ConstructionSupportPolicy.Default);
            Assert.AreEqual(3, (int)json["requested"]); Assert.AreEqual("available", (string)json["researchAndDevelopment"]);
            var parts = (JArray)json["parts"];
            var pod = (JObject)parts[0];
            Assert.AreEqual("verified", (string)pod["constructionSupport"]); Assert.AreEqual("command", (string)pod["category"]);
            Assert.IsTrue((bool)pod["partsStockAllowed"]); Assert.AreEqual("prefab", (string)pod["nodeSource"]);
            var nodes = (JArray)pod["stackNodes"];
            Assert.AreEqual("bottom", (string)nodes[0]["id"]); Assert.AreEqual(-0.4050379, (double)nodes[0]["position"][1], 1e-6);
            Assert.AreEqual(1, (int)nodes[1]["size"]);
            Assert.IsTrue((bool)pod["attachRules"]["allowStack"]);
            Assert.AreEqual(JTokenType.Null, pod["surfaceNode"].Type);
            Assert.AreEqual("unverified", (string)parts[1]["constructionSupport"], "Decoupler.1 has no S0 evidence yet");
            Assert.IsFalse((bool)parts[2]["found"]); Assert.IsNull(parts[2]["stackNodes"]);
        }

        [TestMethod] public void VerifiedPolicyIsInjectable()
        {
            var json = ConstructionCatalogMapper.Build(new[] { "Decoupler.1" }, Stock(), new ConstructionSupportPolicy(new[] { "Decoupler.1" }));
            Assert.AreEqual("verified", (string)json["parts"][0]["constructionSupport"]);
        }

        [TestMethod] public void SandboxWithoutResearchTreatsPartsAsAllowedAndSaysSo()
        {
            var reader = Stock(); reader.ResearchAvailable = false;
            foreach (var p in reader.Parts.Values) { p.TechAvailable = null; p.ModelPurchased = null; }
            var json = ConstructionCatalogMapper.Build(new[] { "fuelTankSmall" }, reader, null);
            Assert.AreEqual("absent_sandbox_allowed", (string)json["researchAndDevelopment"]);
            var part = json["parts"][0];
            Assert.IsTrue((bool)part["partsStockAllowed"]); Assert.AreEqual("research_absent_sandbox", (string)part["stockAllowedBasis"]);
            Assert.AreEqual(JTokenType.Null, part["techAvailable"].Type);
        }

        [DataTestMethod]
        [DataRow(true, true, true)] [DataRow(true, false, false)] [DataRow(false, true, false)] [DataRow(false, false, false)]
        public void StockAllowedIsTechAvailableAndModelPurchased(bool tech, bool purchased, bool expected)
        {
            var reader = Stock(); reader.Parts["fuelTankSmall"].TechAvailable = tech; reader.Parts["fuelTankSmall"].ModelPurchased = purchased;
            var part = ConstructionCatalogMapper.Build(new[] { "fuelTankSmall" }, reader, null)["parts"][0];
            Assert.AreEqual(expected, (bool)part["partsStockAllowed"]);
            Assert.AreEqual(tech, (bool)part["techAvailable"]); Assert.AreEqual(purchased, (bool)part["modelPurchased"]);
        }

        [TestMethod] public void UnreadableResearchStateIsNotAllowed()
        {
            var reader = Stock(); reader.Parts["fuelTankSmall"].TechAvailable = null;
            Assert.IsFalse((bool)ConstructionCatalogMapper.Build(new[] { "fuelTankSmall" }, reader, null)["parts"][0]["partsStockAllowed"]);
        }

        [TestMethod] public void BadNodeDataMakesThePartUnverifiedAndOmitsTheNode()
        {
            var reader = Stock(); var tank = reader.Parts["fuelTankSmall"];
            tank.StackNodes = new[] { N("top", double.NaN, 1), N("bottom", -0.55525, -1), N("bottom", -0.55525, -1) };
            var part = ConstructionCatalogMapper.Build(new[] { "fuelTankSmall" }, reader, new ConstructionSupportPolicy(new[] { "fuelTankSmall" }))["parts"][0];
            Assert.AreEqual("unverified", (string)part["constructionSupport"]);
            Assert.AreEqual(1, ((JArray)part["stackNodes"]).Count);
            var problems = ((JArray)part["problems"]).Select(t => (string)t).ToArray();
            CollectionAssert.AreEquivalent(new[] { "invalid_node", "duplicate_node_id" }, problems);
        }

        [TestMethod] public void SurfaceNodeAndVariantSourceAreReported()
        {
            var reader = Stock(); var tank = reader.Parts["fuelTankSmall"];
            tank.SurfaceNode = new CatalogNodeSource { Id = "srfAttach", Position = new[] { 0.625, 0.0, 0.0 }, Orientation = new[] { 1.0, 0.0, 0.0 } };
            tank.StackNodesFromVariant = true; tank.VariantName = "Basic";
            var part = ConstructionCatalogMapper.Build(new[] { "fuelTankSmall" }, reader, null)["parts"][0];
            Assert.AreEqual("variant", (string)part["nodeSource"]); Assert.AreEqual("Basic", (string)part["variant"]);
            Assert.AreEqual(0.625, (double)part["surfaceNode"]["position"][0], 1e-9);
            Assert.IsNull(part["surfaceNode"]["id"]);
        }

        [TestMethod] public void NodeCountOverTheCapIsFlagged()
        {
            var reader = Stock(); var tank = reader.Parts["fuelTankSmall"]; tank.RawStackNodeCount = ConstructionCatalogMapper.MaxNodes + 1;
            var part = ConstructionCatalogMapper.Build(new[] { "fuelTankSmall" }, reader, new ConstructionSupportPolicy(new[] { "fuelTankSmall" }))["parts"][0];
            Assert.AreEqual("unverified", (string)part["constructionSupport"]);
        }

        [TestMethod] public void OutputIsDeterministicAndKeepsRequestOrder()
        {
            var a = ConstructionCatalogMapper.Build(new[] { "liquidEngine.v2", "mk1pod.v2" }, Stock(), null).ToString();
            var b = ConstructionCatalogMapper.Build(new[] { "liquidEngine.v2", "mk1pod.v2" }, Stock(), null).ToString();
            Assert.AreEqual(a, b);
            Assert.IsTrue(a.IndexOf("liquidEngine.v2", StringComparison.Ordinal) < a.IndexOf("mk1pod.v2", StringComparison.Ordinal));
        }

        [TestMethod] public void ParseNamesAcceptsUpTo32AndDedupes()
        {
            var names = ConstructionCatalogMapper.ParseNames(new JArray("a", "b", "a", "mk1pod.v2"));
            CollectionAssert.AreEqual(new[] { "a", "b", "mk1pod.v2" }, names);
            var max = new JArray(Enumerable.Range(0, ConstructionLimits.MaxCatalogParts).Select(i => "p" + i));
            Assert.AreEqual(32, ConstructionCatalogMapper.ParseNames(max).Count);
        }

        [TestMethod] public void ParseNamesRejectsBadInput()
        {
            var tooMany = new JArray(Enumerable.Range(0, ConstructionLimits.MaxCatalogParts + 1).Select(i => "p" + i));
            foreach (JToken bad in new JToken[] { null, new JValue("a"), new JArray(), tooMany, new JArray(1), new JArray(new JValue((string)null)), new JArray(""), new JArray("a b"), new JArray("a_b"),
                new JArray(new string('x', 65)), new JArray("../x") })
                Assert.ThrowsException<ArgumentException>(() => ConstructionCatalogMapper.ParseNames(bad), bad == null ? "null" : bad.ToString());
        }

        [TestMethod] public void EachPartIsReadOncePerRequestedName()
        {
            var reader = Stock();
            ConstructionCatalogMapper.Build(new[] { "mk1pod.v2", "fuelTankSmall" }, reader, null);
            CollectionAssert.AreEqual(new[] { "mk1pod.v2", "fuelTankSmall" }, reader.Reads);
        }
    }
}
