// Compiled by BOTH KspControl.EditorModel.Tests (net10.0) and KspControl.EditorModel.Net472Tests (net472, C# 7.3), so the pinned
// hashes prove planHash and catalogHash are identical on .NET Framework and modern .NET. Keep this file C# 7.3 clean.
using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.GoldenTests
{
    internal static class GoldenFixtures
    {
        private static ConstructionPart P(string name, string category, bool srf, params ConstructionNode[] nodes)
        {
            return new ConstructionPart
            {
                Name = name, Category = category, Buildable = true, ConstructionSupport = "verified",
                AttachRules = new AttachRulesDefinition { Stack = true, AllowStack = true, Srf = srf, AllowSrf = true },
                StackNodes = nodes.ToList(),
                SurfaceNode = srf ? new SurfaceNodeDefinition { Position = new Vector(0, 0, 0), Orientation = new Vector(1, 0, 0) } : null
            };
        }
        private static ConstructionNode N(string id, double y, double oy)
        {
            return new ConstructionNode { Id = id, Position = new Vector(0, y, 0), Orientation = new Vector(0, oy, 0), Size = 1 };
        }
        /// <summary>Real stock node values (S0 twin). radialDecoupler, solidBooster.sm.v2 and pointyNoseConeB are fixture values.</summary>
        public static ConstructionCatalog Catalog()
        {
            var c = new ConstructionCatalog();
            c.Add(P("mk1pod.v2", PartCategories.Command, false, N("bottom", -0.4050379, -1), N("top", 0.6423756, 1)));
            c.Add(P("probeCoreOcto.v2", PartCategories.Command, false, N("bottom", -0.1870818, -1), N("top", 0.1870818, 1)));
            c.Add(P("fuelTankSmall", PartCategories.Tank, false, N("top", 0.55525, 1), N("bottom", -0.55525, -1)));
            c.Add(P("liquidEngine.v2", PartCategories.Engine, false, N("top", 0, 1), N("bottom", -1.63, -1)));
            c.Add(P("Decoupler.1", PartCategories.Decoupler, false, N("top", 0.05, 1), N("bottom", -0.05, -1)));
            c.Add(P("radialDecoupler", PartCategories.Decoupler, true));
            c.Add(P("solidBooster.sm.v2", PartCategories.Engine, true, N("top", 0.9, 1)));
            c.Add(P("pointyNoseConeB", PartCategories.Other, false, N("bottom01", -0.625, -1)));
            return c;
        }
        private static GraphPartDto G(string id, string part, string parent, string pn, string n)
        {
            return new GraphPartDto { Id = id, Part = part, Parent = parent, ParentNode = pn, Node = n };
        }
        public static GraphDto T1()
        {
            var g = new GraphDto { Name = "Golden T1", Facility = "VAB", Root = "core" };
            g.Parts.Add(G("core", "probeCoreOcto.v2", null, null, null)); g.Parts.Add(G("tank", "fuelTankSmall", "core", "bottom", "top")); g.Parts.Add(G("engine", "liquidEngine.v2", "tank", "bottom", "top"));
            return g;
        }
        public static GraphDto T2()
        {
            var g = new GraphDto { Name = "Golden T2", Facility = "VAB", Root = "pod" };
            g.Parts.Add(G("pod", "mk1pod.v2", null, null, null)); g.Parts.Add(G("t1", "fuelTankSmall", "pod", "bottom", "top")); g.Parts.Add(G("e1", "liquidEngine.v2", "t1", "bottom", "top"));
            g.Parts.Add(G("d", "Decoupler.1", "e1", "bottom", "top")); g.Parts.Add(G("t2", "fuelTankSmall", "d", "bottom", "top")); g.Parts.Add(G("e2", "liquidEngine.v2", "t2", "bottom", "top"));
            return g;
        }
        public static GraphDto T3()
        {
            var g = new GraphDto { Name = "Golden T3", Facility = "VAB", Root = "core" };
            g.Parts.Add(G("core", "probeCoreOcto.v2", null, null, null)); g.Parts.Add(G("tank", "fuelTankSmall", "core", "bottom", "top")); g.Parts.Add(G("engine", "liquidEngine.v2", "tank", "bottom", "top"));
            g.Parts.Add(new GraphPartDto { Id = "rd", Part = "radialDecoupler", Parent = "tank", Symmetry = 3, Surface = new SurfaceDto { HeightOffset = 0.25, AngleDegrees = 0 } });
            g.Parts.Add(new GraphPartDto { Id = "srb", Part = "solidBooster.sm.v2", Parent = "rd", Surface = new SurfaceDto { HeightOffset = 0, AngleDegrees = 270 } });
            g.Parts.Add(G("cone", "pointyNoseConeB", "srb", "top", "bottom01"));
            return g;
        }
    }

    [TestClass]
    public class PlanGoldenTests
    {
        private static string[] Hashes(GraphDto graph, PlannerOptions options = null)
        {
            var catalog = GoldenFixtures.Catalog();
            var plan = CraftPlanner.Plan(graph, catalog, options);
            Assert.IsTrue(plan.Ok, string.Join("; ", plan.Issues.Select(i => i.ToString())));
            return new[] { catalog.Hash(), PlanHasher.Hash(graph, plan, catalog.Hash()) };
        }

        [TestMethod]
        public void CatalogHashIsPinned()
        {
            Assert.AreEqual("ba78ff69a0a7efa551e6b4096ea181d08ab874d03c25eb193e0d0fd6d7d27bab", GoldenFixtures.Catalog().Hash());
        }

        [TestMethod]
        public void T1PlanHashIsPinned() { Assert.AreEqual("1a5def769e2d9071a5b574301bcde0e206f19ff9ff5c75d58d5c873dd26f414d", Hashes(GoldenFixtures.T1())[1]); }

        [TestMethod]
        public void T2PlanHashIsPinned() { Assert.AreEqual("53ffa7f0d063d01a3bfbb236f3fbb46b66cbd6f0517133bad4ad434cd2cadcf3", Hashes(GoldenFixtures.T2())[1]); }

        [TestMethod]
        public void T3PlanHashIsPinned() { Assert.AreEqual("430df9cb744d7ebdc52e56aa4c63a0e1b7dfbfeff7debfd66a410cf8e343b4a2", Hashes(GoldenFixtures.T3())[1]); }
    }
}
