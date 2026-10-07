using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

internal static class Fx
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    public static ConfigNode Node(string name) => ConfigText.Parse(Text(name));

    static ConstructionPart P(string name, string cat, params (string id, double y, double oy)[] nodes) => new()
    {
        Name = name, Category = cat, Buildable = true, ConstructionSupport = "verified",
        AttachRules = new() { Stack = true, AllowStack = true, Srf = true, AllowSrf = true },
        StackNodes = nodes.Select(n => new ConstructionNode { Id = n.id, Position = new(0, n.y, 0), Orientation = new(0, n.oy, 0), Size = 1 }).ToList()
    };
    /// <summary>Node vectors read off the S0a twin (module-independent structure data).</summary>
    public static ConstructionCatalog TwinCatalog()
    {
        var c = new ConstructionCatalog();
        c.Add(P("mk1pod.v2", PartCategories.Command, ("bottom", -0.40503791, -1), ("top", 0.642375588, 1)));
        c.Add(P("fuelTankSmall", PartCategories.Tank, ("top", 0.555249989, 1), ("bottom", -0.555249989, -1)));
        c.Add(P("liquidEngine.v2", PartCategories.Engine, ("top", 0, 1), ("bottom", -1.63, -1)));
        c.Add(P("Decoupler.1", PartCategories.Decoupler, ("top", 0.1, 1), ("bottom", -0.1, -1)));
        var fin = new ConstructionPart { Name = "basicFin", Category = PartCategories.Other, Buildable = true, ConstructionSupport = "verified",
            AttachRules = new() { Srf = true }, SurfaceNode = new() { Position = new(0, 0, 0), Orientation = new(1, 0, 0) } };
        c.Add(fin);
        return c;
    }
    public static GraphDto TwinGraph() => new()
    {
        Name = "KspControlS0-a", Facility = "VAB", Root = "pod",
        Parts = new()
        {
            new() { Id = "pod", Part = "mk1pod.v2" },
            new() { Id = "tank", Part = "fuelTankSmall", Parent = "pod", ParentNode = "bottom", Node = "top" },
            new() { Id = "engine", Part = "liquidEngine.v2", Parent = "tank", ParentNode = "bottom", Node = "top" },
        }
    };
    public static PlannerOptions Options(ConfigNode twin) => new()
    {
        ModVersions = twin.First("_modVersions"), VesselType = twin.First("vesselType")!, MissionFlag = twin.First("missionFlag")!, Version = twin.First("version")!,
    };
}

[TestClass]
public class PlannerTests
{
    [TestMethod]
    public void PlannerReproducesT1TwinStructure()
    {
        var twin = Fx.Node("S0a-manual.craft");
        var plan = CraftPlanner.Plan(Fx.TwinGraph(), Fx.TwinCatalog(), Fx.Options(twin));
        Assert.IsTrue(plan.Ok, string.Join("; ", plan.Issues));
        Assert.AreEqual("T1", plan.Topology);
        var cmp = CraftComparator.Compare(twin, plan.Craft!.ToConfigNode(), new ComparatorOptions { TwinMode = true, StructureOnly = true });
        Assert.IsTrue(cmp.Equal, string.Join("\n", cmp.Differences));
        Assert.AreEqual("tree", cmp.Mapping);
    }

    [TestMethod]
    public void PlannerStagingFieldsMatchTwinForEveryPart()
    {
        var twin = Fx.Node("S0a-manual.craft");
        var plan = CraftPlanner.Plan(Fx.TwinGraph(), Fx.TwinCatalog(), Fx.Options(twin));
        var planned = plan.Craft!.ToConfigNode().Children("PART").ToList();
        var real = twin.Children("PART").ToList();
        Assert.AreEqual(real.Count, planned.Count);
        for (int i = 0; i < real.Count; i++)
            foreach (var k in new[] { "istg", "dstg", "sidx", "sqor", "sepI", "attm" })
                Assert.AreEqual(real[i].First(k), planned[i].First(k), $"{real[i].First("part")} {k}");
    }

    [TestMethod]
    public void PlannerEmitsCidsHeaderAndRelativeTransforms()
    {
        var twin = Fx.Node("S0a-manual.craft");
        uint n = 5000;
        var o = Fx.Options(twin); o.PersistentIdGenerator = () => ++n;
        var plan = CraftPlanner.Plan(Fx.TwinGraph(), Fx.TwinCatalog(), o);
        var craft = plan.Craft!;
        CollectionAssert.AreEqual(new uint[] { 100000, 100001, 100002 }, craft.Parts.Select(p => p.Cid).ToArray());
        Assert.AreEqual(5001u, craft.Header.PersistentId);
        Assert.AreEqual(5002u, craft.Parts[0].PersistentId);
        var node = craft.ToConfigNode();
        Assert.AreEqual("0,0,0,0", node.First("rot"));
        Assert.AreEqual(twin.First("_modVersions"), node.First("_modVersions"));
        Assert.AreEqual("VAB", node.First("type"));
        var parts = node.Children("PART").ToList();
        Assert.AreEqual("0,15,0", parts[0].First("pos"));
        Assert.AreEqual("0,15,0", parts[0].First("attPos0"));
        Assert.AreEqual("0,0,0,1", parts[0].First("rot"));
        Assert.IsFalse(parts.Any(p => p.Children().Any()), "structural craft has no child nodes");
        Assert.AreEqual("fuelTankSmall_100001", parts[0].First("link"));
        Assert.IsTrue(parts[1].Values("attN").Any(v => v == "top,mk1pod.v2_100000_0|0.55525|0"), string.Join(";", parts[1].Values("attN")));
        // attPos0 = inv(parentRot) * (pos - parentPos)
        var py = double.Parse(parts[0].First("pos")!.Split(',')[1]);
        var cy = double.Parse(parts[1].First("pos")!.Split(',')[1]);
        Assert.AreEqual(cy - py, double.Parse(parts[1].First("attPos0")!.Split(',')[1]), 1e-5);
    }

    [TestMethod]
    public void VectorLessAttNFlagOmitsVectors()
    {
        var o = new PlannerOptions { VectorLessAttN = true };
        var node = CraftPlanner.Plan(Fx.TwinGraph(), Fx.TwinCatalog(), o).Craft!.ToConfigNode();
        Assert.IsTrue(node.Children("PART").SelectMany(p => p.Values("attN")).All(v => v.Count(c => c == '_') == 1), "name_cid only");
    }

    [TestMethod]
    public void StackChildOfRotatedSurfaceParentStaysAligned()
    {
        bool rotatedSeen = false;
        var cat = Fx.TwinCatalog();
        cat.Parts["basicFin"].StackNodes = new() { new() { Id = "bottom", Position = new(0, -0.1, 0), Orientation = new(0, -1, 0) } };
        cat.Parts["basicFin"].AttachRules.AllowStack = true;
        foreach (var n in new[] { 2, 3, 4 })
        {
            var issues = new List<PlanIssue>();
            var layout = CraftPlanner.Layout(FinGraph(n, true), cat, new PlannerOptions(), issues)!;
            Assert.AreEqual(0, issues.Count, string.Join(";", issues));
            foreach (var cap in layout.Parts.Where(p => p.Source.Id == "cap"))
            {
                var fin = layout.Parts[cap.ParentIndex];
                rotatedSeen |= RotationMath.AngleBetween(fin.Rotation, RotationMath.Identity) > 0.1;
                var pn = fin.Definition.FindNode(cap.ParentNodeId)!; var cn = cap.Definition.FindNode(cap.NodeId)!;
                var a = RotationMath.Add(fin.Position, RotationMath.Rotate(pn.Position, fin.Rotation));
                var b = RotationMath.Add(cap.Position, RotationMath.Rotate(cn.Position, cap.Rotation));
                Assert.AreEqual(0, RotationMath.Length(RotationMath.Sub(a, b)), 1e-9, "nodes coincide in world space");
                var oa = RotationMath.Rotate(pn.Orientation, fin.Rotation); var ob = RotationMath.Rotate(cn.Orientation, cap.Rotation);
                Assert.AreEqual(-1.0, RotationMath.Dot(oa, ob), 1e-9, "node normals oppose");
            }
        }
        Assert.IsTrue(rotatedSeen, "parents are actually rotated");
    }

    [TestMethod]
    public void UnsupportedTopologiesAreRefused()
    {
        var g3 = Fx.TwinGraph();
        g3.Parts.Add(new() { Id = "fin", Part = "basicFin", Parent = "tank", Symmetry = 2, Surface = new() { HeightOffset = -0.3, AngleDegrees = 0 } });
        var r = CraftPlanner.Plan(g3, Fx.TwinCatalog());
        Assert.IsNull(r.Craft);
        Assert.IsTrue(r.Issues.Any(i => i.Code == "unsupported_staging_topology"), string.Join(";", r.Issues));
    }

    [TestMethod]
    public void OtherTopologiesAndStageOverridesAreUnsupported()
    {
        var g = Fx.TwinGraph(); g.Parts.RemoveAt(1); g.Parts[1].Parent = "pod"; g.Parts[1].ParentNode = "bottom"; // pod -> engine, no tank
        Assert.IsTrue(CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Any(i => i.Code == "unsupported_staging_topology"));
        var g2 = Fx.TwinGraph(); g2.Parts[2].Stage = 3;
        Assert.IsTrue(CraftPlanner.Plan(g2, Fx.TwinCatalog()).Issues.Any(i => i.Code == "unsupported_staging_topology"));
        var g3 = Fx.TwinGraph(); g3.Parts[2].Stage = 0;
        Assert.IsTrue(CraftPlanner.Plan(g3, Fx.TwinCatalog()).Ok);
    }

    [TestMethod]
    public void GraphBoundsAndCatalogIssues()
    {
        var cat = Fx.TwinCatalog();
        var g = Fx.TwinGraph(); g.Parts[0].Configuration = new() { "variant=x" };
        Assert.IsTrue(CraftPlanner.Plan(g, cat).Issues.Any(i => i.Code == "unsupported_configuration"));
        g = Fx.TwinGraph(); g.Parts[1].Part = "nope";
        Assert.IsTrue(CraftPlanner.Plan(g, cat).Issues.Any(i => i.Code == "unknown_part"));
        cat = Fx.TwinCatalog(); cat.Parts["fuelTankSmall"].Buildable = false; cat.Parts["liquidEngine.v2"].ConstructionSupport = "unverified";
        var issues = CraftPlanner.Plan(Fx.TwinGraph(), cat).Issues.Select(i => i.Code).ToList();
        CollectionAssert.Contains(issues, "part_not_buildable");
        CollectionAssert.DoesNotContain(issues, "part_construction_unverified", "construction support is informational, never an issue");
        g = Fx.TwinGraph(); g.Parts[2].ParentNode = "top"; g.Parts[1].ParentNode = "bottom";
        g.Parts[2].Parent = "pod"; g.Parts[2].ParentNode = "bottom";
        Assert.IsTrue(CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Any(i => i.Code == "node_occupied"));
        g = Fx.TwinGraph(); g.Parts[0].Parent = "engine";
        Assert.IsTrue(CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Any(i => i.Code == "root_has_parent"));
        g = Fx.TwinGraph(); g.Facility = "SPH";
        Assert.IsTrue(CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Any(i => i.Code == "facility_mismatch"));
        g = Fx.TwinGraph(); for (int i = 0; i < 260; i++) g.Parts.Add(new() { Id = "x" + i, Part = "fuelTankSmall" });
        Assert.IsTrue(CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Any(i => i.Code == "part_count_out_of_range"));
        g = Fx.TwinGraph(); g.Parts[1].Part = "bad_name";
        Assert.IsTrue(CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Any(i => i.Code == "invalid_part_name"));
        g = Fx.TwinGraph(); g.Parts.Add(new() { Id = "s", Part = "basicFin", Parent = "tank", Symmetry = 9, Surface = new() { AngleDegrees = 360 } });
        var codes = CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Select(i => i.Code).ToList();
        CollectionAssert.IsSubsetOf(new[] { "invalid_symmetry", "invalid_surface" }, codes);
    }

    [TestMethod]
    public void CatalogHashIsStableAndSensitive()
    {
        var a = Fx.TwinCatalog().Hash(); Assert.AreEqual(a, Fx.TwinCatalog().Hash());
        var c = Fx.TwinCatalog(); c.Parts["fuelTankSmall"].StackNodes[0].Position = new(0, 0.6, 0);
        Assert.AreNotEqual(a, c.Hash());
    }

    // ---- placement: surface, symmetry, subtree duplication ----
    private static GraphDto FinGraph(int n, bool booster)
    {
        var g = Fx.TwinGraph();
        g.Parts.Add(new() { Id = "fin", Part = "basicFin", Parent = "tank", Symmetry = n, Surface = new() { HeightOffset = -0.3, AngleDegrees = 0 } });
        if (booster) g.Parts.Add(new() { Id = "cap", Part = "fuelTankSmall", Parent = "fin", ParentNode = "bottom", Node = "top" });
        return g;
    }
    [TestMethod]
    public void SurfaceCounterpartsRotateAboutParentAxis()
    {
        foreach (var n in new[] { 2, 3, 4, 6, 8 })
        {
            var issues = new List<PlanIssue>();
            var layout = CraftPlanner.Layout(FinGraph(n, false), Fx.TwinCatalog(), new PlannerOptions(), issues)!;
            Assert.AreEqual(0, issues.Count, string.Join(";", issues));
            var tank = layout.Parts.First(p => p.Source.Id == "tank");
            var fins = layout.Parts.Where(p => p.Source.Id == "fin").ToList();
            Assert.AreEqual(n, fins.Count);
            Assert.AreEqual(1, layout.SymmetryGroups.Count);
            Assert.AreEqual(n, layout.SymmetryGroups[0].Count);
            for (int k = 0; k < n; k++)
            {
                var d = new Vector(fins[k].Position.X - tank.Position.X, fins[k].Position.Y - tank.Position.Y, fins[k].Position.Z - tank.Position.Z);
                Assert.AreEqual(-0.3, d.Y, 1e-9);
                Assert.AreEqual(0.625, Math.Sqrt(d.X * d.X + d.Z * d.Z), 1e-9);
                var a = 2 * Math.PI * k / n;
                Assert.AreEqual(0.625 * Math.Sin(a), d.X, 1e-9); Assert.AreEqual(0.625 * Math.Cos(a), d.Z, 1e-9);
                // surface normal (child +X) points back at the parent axis
                var normal = RotationMath.Rotate(new Vector(1, 0, 0), fins[k].Rotation);
                Assert.AreEqual(-Math.Sin(a), normal.X, 1e-9); Assert.AreEqual(-Math.Cos(a), normal.Z, 1e-9);
                Assert.AreEqual(1.0, RotationMath.Rotate(new Vector(0, 1, 0), fins[k].Rotation).Y, 1e-9, "part stays upright at n=" + n + " k=" + k);
            }
        }
    }
    [TestMethod]
    public void TwoFinsMatchTheS0bSavedCraftGeometry()
    {
        // S0b evidence: fins at z=+0.625 (rot 0,0.7071,0,0.7071) and z=-0.625 (rot 0,0.7071,0,-0.7071), 0.3 below the tank centre.
        var layout = CraftPlanner.Layout(FinGraph(2, false), Fx.TwinCatalog(), new PlannerOptions(), new List<PlanIssue>())!;
        var f = layout.Parts.Where(p => p.Source.Id == "fin").ToList();
        Assert.AreEqual(0.625, f[0].Position.Z - layout.Parts[1].Position.Z, 1e-9);
        Assert.AreEqual(0, RotationMath.AngleBetween(f[0].Rotation, new Rotation(0, 0.707106829, 0, 0.707106829)), 1e-6);
        Assert.AreEqual(0, RotationMath.AngleBetween(f[1].Rotation, new Rotation(0, 0.707106829, 0, -0.707106829)), 1e-6);
    }
    [TestMethod]
    public void SubtreeUnderSymmetricParentIsDuplicated()
    {
        var cat = Fx.TwinCatalog(); cat.Parts["basicFin"].StackNodes = new() { new() { Id = "bottom", Position = new(0, -0.1, 0), Orientation = new(0, -1, 0) } };
        cat.Parts["basicFin"].AttachRules.AllowStack = true;
        var issues = new List<PlanIssue>();
        var layout = CraftPlanner.Layout(FinGraph(3, true), cat, new PlannerOptions(), issues)!;
        Assert.AreEqual(0, issues.Count, string.Join(";", issues));
        Assert.AreEqual(3, layout.Parts.Count(p => p.Source.Id == "cap"));
        Assert.AreEqual(2, layout.SymmetryGroups.Count);
        foreach (var cap in layout.Parts.Where(p => p.Source.Id == "cap"))
            Assert.AreEqual("fin", layout.Parts[cap.ParentIndex].Source.Id);
        Assert.AreEqual(9, layout.Parts.Select(p => p.Cid).Distinct().Count());
    }
    [TestMethod]
    public void SurfaceDeniedAndNestedSymmetryRejected()
    {
        var cat = Fx.TwinCatalog(); cat.Parts["fuelTankSmall"].AttachRules.AllowSrf = false;
        Assert.IsTrue(CraftPlanner.Plan(FinGraph(2, false), cat).Issues.Any(i => i.Code == "surface_attachment_denied"));
        var g = FinGraph(2, true); g.Parts[4].Symmetry = 2;
        Assert.IsTrue(CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Any(i => i.Code is "unsupported_symmetry_attachment" or "unsupported_nested_symmetry"));
    }
}
