using System.Text;
using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

/// <summary>
/// Staging rules against KSP-editor-staged stock craft (Orbiter One, GDLV3), read from the install and never committed.
/// The planned sub-structure omits parachutes, fins, fairings and struts, so stage numbers shift by a fixed offset per field;
/// the offsets are stated in each test and follow from which groups/slots the omitted parts occupy in the oracle.
/// </summary>
[TestClass]
public class StagingTests
{
    record Oracle(string Name, int Istg, int Dstg, int Sidx, int Sqor, int SepI, int Attm);

    static List<Oracle> Read(string craft)
    {
        var path = Path.Combine(@"D:\SteamLibrary\steamapps\common\Kerbal Space Program\Ships\VAB", craft + ".craft");
        if (!File.Exists(path)) Assert.Inconclusive(craft + " not installed");
        var root = ConfigText.Parse(File.ReadAllText(path, Encoding.Latin1));
        return root.Children("PART").Select(p =>
        {
            CraftModel.SplitPart(p.First("part"), out var name, out _, out _, out _);
            int I(string k) => int.Parse(p.First(k)!);
            return new Oracle(name, I("istg"), I("dstg"), I("sidx"), I("sqor"), I("sepI"), I("attm"));
        }).ToList();
    }

    static ConstructionPart Part(string name, string cat, (string id, double y, double oy)[] nodes, bool srf = false) => new()
    {
        Name = name, Category = cat, Buildable = true, ConstructionSupport = "verified",
        AttachRules = new() { Stack = true, AllowStack = true, Srf = srf, AllowSrf = true },
        StackNodes = nodes.Select(n => new ConstructionNode { Id = n.id, Position = new(0, n.y, 0), Orientation = new(0, n.oy, 0), Size = 1 }).ToList(),
        SurfaceNode = srf ? new SurfaceNodeDefinition { Position = new(0, 0, 0), Orientation = new(1, 0, 0) } : null
    };
    static ConstructionCatalog Catalog()
    {
        var c = new ConstructionCatalog();
        var tb = new[] { ("top", 0.5, 1.0), ("bottom", -0.5, -1.0) };
        c.Add(Part("mk1pod.v2", PartCategories.Command, new[] { ("bottom", -0.4, -1.0), ("top", 0.6, 1.0) }));
        c.Add(Part("probeCoreOcto.v2", PartCategories.Command, new[] { ("bottom", -0.19, -1.0), ("top", 0.19, 1.0) }));
        c.Add(Part("Decoupler.1", PartCategories.Decoupler, new[] { ("top", 0.05, 1.0), ("bottom", -0.05, -1.0) }));
        c.Add(Part("fuelTankSmall", PartCategories.Tank, new[] { ("top", 0.55525, 1.0), ("bottom", -0.55525, -1.0) }));
        c.Add(Part("Rockomax64.BW", PartCategories.Tank, new[] { ("top", 3.73, 1.0), ("bottom", -3.73, -1.0) }));
        c.Add(Part("liquidEngine2", PartCategories.Engine, new[] { ("top", 0.9, 1.0), ("bottom", -0.7, -1.0) }));
        c.Add(Part("engineLargeSkipper", PartCategories.Engine, new[] { ("top", 0.0, 1.0), ("bottom", -1.6, -1.0) }));
        c.Add(Part("radialDecoupler", PartCategories.Decoupler, new (string, double, double)[0], srf: true));
        c.Add(Part("solidBooster1-1", PartCategories.Engine, new[] { ("top", 3.92, 1.0) }, srf: true));
        c.Add(Part("pointyNoseConeB", PartCategories.Other, new[] { ("bottom01", -0.625, -1.0) }));
        _ = tb;
        return c;
    }
    static GraphPartDto G(string id, string part, string? parent = null, string? pn = null, string? n = null) => new() { Id = id, Part = part, Parent = parent, ParentNode = pn, Node = n };

    // ---- T2: Orbiter One stack chain ----
    static GraphDto OrbiterChain()
    {
        var g = new GraphDto { Name = "orbiter", Facility = "VAB", Root = "pod" };
        g.Parts.Add(G("pod", "mk1pod.v2"));
        g.Parts.Add(G("d1", "Decoupler.1", "pod", "bottom", "top"));
        g.Parts.Add(G("t1", "fuelTankSmall", "d1", "bottom", "top"));
        g.Parts.Add(G("t2", "fuelTankSmall", "t1", "bottom", "top"));
        g.Parts.Add(G("e1", "liquidEngine2", "t2", "bottom", "top"));
        g.Parts.Add(G("d2", "Decoupler.1", "e1", "bottom", "top"));
        string prev = "d2";
        for (int i = 0; i < 6; i++) { g.Parts.Add(G("s" + i, "fuelTankSmall", prev, "bottom", "top")); prev = "s" + i; }
        g.Parts.Add(G("e2", "liquidEngine2", prev, "bottom", "top"));
        return g;
    }

    [TestMethod]
    public void T2StackDecouplerChainMatchesOrbiterOne()
    {
        var oracle = Read("Orbiter One");
        var plan = CraftPlanner.Plan(OrbiterChain(), Catalog());
        Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues));
        Assert.AreEqual("T2", plan.Topology);
        var skip = new HashSet<string> { "parachuteSingle", "basicFin", "radialDecoupler", "solidBooster.v2", "noseCone" };
        var expect = oracle.Where(o => !skip.Contains(o.Name)).ToList();
        var fields = plan.Craft!.Parts.ToList();
        Assert.AreEqual(expect.Count, fields.Count);
        // Omitted parachute (stage 0) and the radial pair of groups (above the bottom engine) shift stage numbers:
        // groups below the bottom engine are offset by 1 (the parachute group). Bottom engine istg/sqor are offset by 3 (parachute, radial decoupler, shared booster slot is the same group).
        for (int i = 0; i < expect.Count; i++)
        {
            var o = expect[i]; var p = fields[i].Staging; var label = i + ":" + o.Name;
            bool bottomEngine = i == expect.Count - 1;
            Assert.AreEqual(o.Dstg, p.Dstg, label + " dstg");
            Assert.AreEqual(o.Sidx, p.Sidx, label + " sidx");
            Assert.AreEqual(o.Attm, p.Attm, label + " attm");
            Assert.AreEqual(o.SepI < 0 ? -1 : o.SepI - 1, p.SepI, label + " sepI");
            if (!bottomEngine)
            {
                Assert.AreEqual(o.Istg < 0 ? -1 : o.Istg - 1, p.Istg, label + " istg");
                Assert.AreEqual(o.Sqor < 0 ? -1 : o.Sqor - 1, p.Sqor, label + " sqor");
            }
            else
            {
                // Oracle: ignition shares stage 5 with the radial booster, whose decoupler sits at stage 4. Without them the group is 3.
                Assert.AreEqual(3, p.Istg); Assert.AreEqual(3, p.Sqor);
                Assert.IsTrue(p.Istg > fields[5].Staging.Istg);
            }
        }
    }

    [TestMethod]
    public void T2FirstSectionEngineFiresLast()
    {
        // pod - tank - engine - decoupler - tank - engine: lower engine, lower decoupler, then the upper engine.
        var g = new GraphDto { Name = "x", Facility = "VAB", Root = "pod" };
        g.Parts.Add(G("pod", "mk1pod.v2")); g.Parts.Add(G("t", "fuelTankSmall", "pod", "bottom", "top")); g.Parts.Add(G("e0", "liquidEngine2", "t", "bottom", "top"));
        g.Parts.Add(G("d", "Decoupler.1", "e0", "bottom", "top")); g.Parts.Add(G("t2", "fuelTankSmall", "d", "bottom", "top")); g.Parts.Add(G("e1", "liquidEngine2", "t2", "bottom", "top"));
        var plan = CraftPlanner.Plan(g, Catalog()); Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues));
        var s = plan.Craft!.Parts.ToDictionary(p => p.SourceId!, p => p.Staging);
        Assert.AreEqual(2, s["e1"].Istg); Assert.AreEqual(1, s["d"].Istg); Assert.AreEqual(0, s["e0"].Istg);
        Assert.AreEqual(1, s["d"].SepI); Assert.AreEqual(1, s["t2"].SepI); Assert.AreEqual(-1, s["e0"].SepI);
        Assert.AreEqual(1, s["t2"].Istg, "non-stageable takes parent's istg");
    }

    [TestMethod]
    public void T2ShapeIsExact()
    {
        var g = OrbiterChain(); g.Parts.RemoveAt(g.Parts.Count - 1); // last section without an engine
        var r = CraftPlanner.Plan(g, Catalog()); Assert.IsNull(r.Craft);
        Assert.IsTrue(r.Issues.Any(i => i.Code == "unsupported_staging_topology"));
        var g2 = OrbiterChain(); g2.Parts[1].Stage = 7; // override differs from derived stage
        Assert.IsTrue(CraftPlanner.Plan(g2, Catalog()).Issues.Any(i => i.Code == "unsupported_staging_topology"));
        var g3 = OrbiterChain(); g3.Parts[1].Stage = 0;
        Assert.IsTrue(CraftPlanner.Plan(g3, Catalog()).Ok);
    }

    // ---- T3: GDLV3 radial decoupler + booster + nose cone ----
    static GraphDto RadialCraft(int n, bool nose = true)
    {
        var g = new GraphDto { Name = "gdl", Facility = "VAB", Root = "core" };
        g.Parts.Add(G("core", "probeCoreOcto.v2"));
        g.Parts.Add(G("tank", "Rockomax64.BW", "core", "bottom", "top"));
        g.Parts.Add(G("engine", "engineLargeSkipper", "tank", "bottom", "top"));
        g.Parts.Add(new() { Id = "rd", Part = "radialDecoupler", Parent = "tank", Symmetry = n, Surface = new() { HeightOffset = 0, AngleDegrees = 0 } });
        g.Parts.Add(new() { Id = "srb", Part = "solidBooster1-1", Parent = "rd", Surface = new() { HeightOffset = 0, AngleDegrees = 270 } });
        if (nose) g.Parts.Add(G("cone", "pointyNoseConeB", "srb", "top", "bottom01"));
        return g;
    }

    [TestMethod]
    public void T3RadialQuadMatchesGdlv3()
    {
        var oracle = Read("GDLV3");
        var plan = CraftPlanner.Plan(RadialCraft(4), Catalog());
        Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues));
        Assert.AreEqual("T3", plan.Topology);
        var planned = plan.Craft!.Parts;
        string[] names = { "Rockomax64.BW", "engineLargeSkipper", "radialDecoupler", "solidBooster1-1", "pointyNoseConeB" };
        // Oracle offsets: GDLV3 has a fairing + decoupler group at stage 0 (two slots 0 and 1) and stages above Rockomax come from
        // the fairing/asas chain (dstg base 2 instead of 0): dstg -2, istg/sepI -1, sqor -2, sidx and attm exact.
        foreach (var name in names)
        {
            var o = oracle.Where(x => x.Name == name).Select(x => (x.Istg < 0 ? -1 : x.Istg - 1, x.Dstg - 2, x.Sidx, x.Sqor < 0 ? -1 : x.Sqor - 2, x.SepI < 0 ? -1 : x.SepI - 1, x.Attm)).Distinct().ToList();
            var p = planned.Where(x => x.Name == name).Select(x => (x.Staging.Istg, x.Staging.Dstg, x.Staging.Sidx, x.Staging.Sqor, x.Staging.SepI, x.Staging.Attm)).Distinct().ToList();
            Assert.AreEqual(1, o.Count, name + " oracle is uniform"); Assert.AreEqual(1, p.Count, name + " counterparts share values");
            Assert.AreEqual(o[0], p[0], name);
            Assert.AreEqual(name is "radialDecoupler" or "solidBooster1-1" or "pointyNoseConeB" ? 4 : 1, planned.Count(x => x.Name == name), name);
        }
    }

    [TestMethod]
    public void T3PairAndQuadShareValuesAndRejectUnsupportedShapes()
    {
        foreach (var n in new[] { 2, 3, 4 })
        {
            var plan = CraftPlanner.Plan(RadialCraft(n), Catalog()); Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues));
            Assert.AreEqual(n, plan.Craft!.Parts.Count(p => p.Name == "radialDecoupler"));
            Assert.AreEqual(1, plan.Craft.Parts.Where(p => p.Name == "radialDecoupler").Select(p => p.Staging.Istg).Distinct().Count());
            Assert.IsTrue(plan.Craft.Parts.All(p => p.Name != "radialDecoupler" || p.Sym.Count == n - 1));
        }
        Assert.IsTrue(CraftPlanner.Plan(RadialCraft(2, nose: false), Catalog()).Ok, "nose cone is optional");
        // radial group on a T2 core is not supported
        var g = RadialCraft(2); g.Parts[1].Part = "fuelTankSmall";
        var t2 = OrbiterChain(); t2.Parts.Add(new() { Id = "rd", Part = "radialDecoupler", Parent = "s5", Symmetry = 2, Surface = new() { AngleDegrees = 0 } });
        Assert.IsTrue(CraftPlanner.Plan(t2, Catalog()).Issues.Any(i => i.Code == "unsupported_staging_topology"));
        // a booster-less decoupler is not T3
        var g2 = RadialCraft(2); g2.Parts.RemoveAll(p => p.Id is "srb" or "cone");
        Assert.IsTrue(CraftPlanner.Plan(g2, Catalog()).Issues.Any(i => i.Code == "unsupported_staging_topology"));
        // a second decoupler under the booster is not T3
        var g3 = RadialCraft(2); g3.Parts[5].Part = "radialDecoupler"; g3.Parts[5].ParentNode = null; g3.Parts[5].Node = null; g3.Parts[5].Surface = new() { AngleDegrees = 0 };
        Assert.IsTrue(CraftPlanner.Plan(g3, Catalog()).Issues.Any(i => i.Code == "unsupported_staging_topology"));
        // parachutes, fins etc. (other parts on the core) stay refused
        var g4 = RadialCraft(2); g4.Parts.Add(G("extra", "pointyNoseConeB", "core", "top", "bottom01"));
        Assert.IsTrue(CraftPlanner.Plan(g4, Catalog()).Issues.Any(i => i.Code == "unsupported_staging_topology"));
    }

    [TestMethod]
    public void T3BoosterSitsOutsideItsOwnDecouplerAtTheSameAngle()
    {
        foreach (var n in new[] { 2, 3, 4 })
        {
            var issues = new List<PlanIssue>();
            var layout = CraftPlanner.Layout(RadialCraft(n), Catalog(), new PlannerOptions(), issues)!;
            Assert.AreEqual(0, issues.Count, string.Join(";", issues));
            var tank = layout.Parts.First(p => p.Source.Id == "tank");
            var rds = layout.Parts.Where(p => p.Source.Id == "rd").ToList();
            var srbs = layout.Parts.Where(p => p.Source.Id == "srb").ToList();
            Assert.AreEqual(n, rds.Count); Assert.AreEqual(n, srbs.Count);
            var angles = new HashSet<long>();
            for (int k = 0; k < n; k++)
            {
                var rd = rds[k]; var srb = srbs[k];
                Assert.AreEqual(rd.Index, srb.ParentIndex);
                double Radius(LayoutPart p) => Math.Sqrt(Math.Pow(p.Position.X - tank.Position.X, 2) + Math.Pow(p.Position.Z - tank.Position.Z, 2));
                Assert.AreEqual(0.625, Radius(rd), 1e-9);
                Assert.AreEqual(1.25, Radius(srb), 1e-9, "booster is one radius beyond its decoupler, not inside the tank");
                var a = 2 * Math.PI * k / n;
                Assert.AreEqual(1.25 * Math.Sin(a), srb.Position.X - tank.Position.X, 1e-9);
                Assert.AreEqual(1.25 * Math.Cos(a), srb.Position.Z - tank.Position.Z, 1e-9);
                Assert.AreEqual(1.0, RotationMath.Rotate(new Vector(0, 1, 0), rd.Rotation).Y, 1e-9, "decoupler stays upright");
                Assert.AreEqual(1.0, RotationMath.Rotate(new Vector(0, 1, 0), srb.Rotation).Y, 1e-9, "booster stays upright");
                angles.Add((long)Math.Round(Math.Atan2(srb.Position.X, srb.Position.Z) * 1000));
            }
            Assert.AreEqual(n, angles.Count, "boosters are all at distinct angles");
        }
    }
}
