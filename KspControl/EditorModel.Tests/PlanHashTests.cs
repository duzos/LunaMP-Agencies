using KspControl.EditorModel;
using KspControl.EditorModel.GoldenTests;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

[TestClass]
public class PlanHashTests
{
    static (PlanResult plan, string hash) Run(GraphDto g, PlannerOptions? o = null, ConstructionCatalog? c = null)
    {
        c ??= GoldenFixtures.Catalog();
        var plan = CraftPlanner.Plan(g, c, o);
        return (plan, PlanHasher.Hash(g, plan, c.Hash())!);
    }

    [TestMethod]
    public void HashIsStableAcrossRuns()
    {
        foreach (var make in new Func<GraphDto>[] { GoldenFixtures.T1, GoldenFixtures.T2, GoldenFixtures.T3 })
        {
            var a = Run(make()); var b = Run(make());
            Assert.IsTrue(a.plan.Ok, string.Join(";", a.plan.Issues));
            Assert.AreEqual(a.hash, b.hash); Assert.AreEqual(64, a.hash.Length);
        }
        Assert.AreEqual(3, new[] { GoldenFixtures.T1(), GoldenFixtures.T2(), GoldenFixtures.T3() }.Select(g => Run(g).hash).Distinct().Count());
    }

    [TestMethod]
    public void ApplyTimeInjectionsDoNotChangeTheHash()
    {
        var plain = Run(GoldenFixtures.T3()).hash;
        uint n = 9000;
        var injected = Run(GoldenFixtures.T3(), new PlannerOptions { PersistentIdGenerator = () => ++n, ModVersions = "ModuleManager 4.2.3", Description = "x", MissionFlag = "Squad/Flags/other", Version = "1.12.4" }).hash;
        Assert.AreEqual(plain, injected, "persistentIds and header copies are apply-time data, not plan content");
    }

    [TestMethod]
    public void HashChangesWithPlanContent()
    {
        var baseline = Run(GoldenFixtures.T3()).hash;
        var g = GoldenFixtures.T3(); g.Parts[3].Symmetry = 4; Assert.AreNotEqual(baseline, Run(g).hash);
        g = GoldenFixtures.T3(); g.Parts[3].Surface!.HeightOffset = 0.5; Assert.AreNotEqual(baseline, Run(g).hash);
        g = GoldenFixtures.T3(); g.Name = "Other"; Assert.AreNotEqual(baseline, Run(g).hash);
        g = GoldenFixtures.T3(); g.Parts[4].Surface!.AngleDegrees = 90; Assert.AreNotEqual(baseline, Run(g).hash);
        var c = GoldenFixtures.Catalog(); c.Parts["fuelTankSmall"].StackNodes[0].Position = new Vector(0, 0.56, 0); Assert.AreNotEqual(baseline, Run(GoldenFixtures.T3(), null, c).hash);
        var opts = new PlannerOptions { SurfaceRadius = 0.7 }; Assert.AreNotEqual(baseline, Run(GoldenFixtures.T3(), opts).hash);
    }

    [TestMethod]
    public void NoHashWithoutAValidPlan()
    {
        var c = GoldenFixtures.Catalog();
        var g = GoldenFixtures.T1(); g.Parts.RemoveAt(2);
        var bad = CraftPlanner.Plan(g, c);
        Assert.IsFalse(bad.Ok); Assert.IsNull(PlanHasher.Hash(g, bad, c.Hash())); Assert.IsNull(PlanHasher.Canonical(g, null!, c.Hash()));
        c.Parts["fuelTankSmall"].ConstructionSupport = "unverified";
        var unverified = CraftPlanner.Plan(GoldenFixtures.T1(), c);
        Assert.IsNull(PlanHasher.Hash(GoldenFixtures.T1(), unverified, c.Hash()));
    }

    [TestMethod]
    public void CatalogHashIgnoresInsertionOrder()
    {
        var a = GoldenFixtures.Catalog(); var b = new ConstructionCatalog();
        foreach (var p in a.Parts.Values.Reverse()) b.Add(p);
        Assert.AreEqual(a.Hash(), b.Hash());
    }

    [TestMethod]
    public void CatalogHashCoversEveryPlannerVisibleField()
    {
        var baseline = GoldenFixtures.Catalog().Hash();
        void Changes(Action<ConstructionCatalog> edit, string what) { var c = GoldenFixtures.Catalog(); edit(c); Assert.AreNotEqual(baseline, c.Hash(), what); }
        Changes(c => c.Parts["mk1pod.v2"].Category = PartCategories.Other, "category");
        Changes(c => c.Parts["mk1pod.v2"].Buildable = false, "buildable");
        Changes(c => c.Parts["mk1pod.v2"].ConstructionSupport = "unverified", "support");
        Changes(c => c.Parts["mk1pod.v2"].AttachRules.AllowStack = false, "attach rules");
        Changes(c => c.Parts["mk1pod.v2"].StackNodes[0].Orientation = new Vector(0, -0.9, 0), "node orientation");
        Changes(c => c.Parts["mk1pod.v2"].StackNodes[0].Size = 2, "node size");
        Changes(c => c.Parts["radialDecoupler"].SurfaceNode!.Position = new Vector(0.1, 0, 0), "surface node");
    }
}
