using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>The "parachute" catalog category and the construction-support status of parachuteSingle.</summary>
    [TestClass]
    public class ParachuteCatalogTests
    {
        private sealed class Reader : ICatalogPartReader
        {
            public bool ResearchAvailable => false;
            public bool SandboxMode => true;
            public CatalogPartSource Read(string name)
            {
                if (name != "parachuteSingle") return null;
                return new CatalogPartSource
                {
                    Name = name, KspCategory = "Utility", Buildable = true, ModuleNames = new[] { "ModuleParachute", "ModuleDragModifier" },
                    StackNodes = new[] { new CatalogNodeSource { Id = "bottom", Position = new[] { 0.0, -0.01508113, 0.0 }, Orientation = new[] { 0.0, -1.0, 0.0 }, Size = 1 } },
                    AttachRules = new CatalogAttachRulesSource { Stack = true, AllowStack = true, AllowSrf = true }
                };
            }
        }

        [TestMethod] public void AModuleParachutePartIsTheParachuteCategory()
        {
            Assert.AreEqual("parachute", ConstructionCatalogMapper.MapCategory(new[] { "ModuleParachute", "ModuleDragModifier" }, new string[0]));
            Assert.AreEqual(Pure.PartCategories.Parachute, ConstructionCatalogMapper.Parachute);
        }

        [TestMethod] public void ParachuteLosesToCommandDecouplerAndEngineButBeatsTankAndOther()
        {
            Assert.AreEqual("command", ConstructionCatalogMapper.MapCategory(new[] { "ModuleParachute", "ModuleCommand" }, new string[0]));
            Assert.AreEqual("decoupler", ConstructionCatalogMapper.MapCategory(new[] { "ModuleParachute", "ModuleDecouple" }, new string[0]));
            Assert.AreEqual("engine", ConstructionCatalogMapper.MapCategory(new[] { "ModuleParachute", "ModuleEngines" }, new string[0]));
            Assert.AreEqual("parachute", ConstructionCatalogMapper.MapCategory(new[] { "ModuleParachute" }, new[] { "LiquidFuel" }));
        }

        [TestMethod] public void ParachuteSingleIsReportedUnverifiedUntilALiveStructuralLoadIsRecorded()
        {
            Assert.IsFalse(ConstructionSupportPolicy.Default.IsVerified("parachuteSingle"));
            Assert.IsFalse(ConstructionSupportPolicy.VerifiedTable.Any(r => r.Key == "parachuteSingle"));
            var data = ConstructionCatalogMapper.Build(new List<string> { "parachuteSingle" }, new Reader(), ConstructionSupportPolicy.Default);
            var part = (JObject)data["parts"][0];
            Assert.AreEqual("parachute", (string)part["category"]);
            Assert.AreEqual("unverified", (string)part["constructionSupport"]);
        }

        [TestMethod] public void TheBridgeCatalogParserKeepsTheParachuteCategory()
        {
            var data = ConstructionCatalogMapper.Build(new List<string> { "parachuteSingle" }, new Reader(), new ConstructionSupportPolicy(new[] { "parachuteSingle" }));
            var into = new ReplanCatalog(); string error;
            Assert.IsTrue(ReplanCatalog.TryParse(data, new[] { "parachuteSingle" }, into, out error), error);
            Assert.AreEqual(Pure.PartCategories.Parachute, into.Found["parachuteSingle"].Part.Category);
            Assert.IsTrue(into.Found["parachuteSingle"].Part.IsVerified, "verified only when the injected policy says so");
        }
    }
}
