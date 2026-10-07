using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

/// <summary>P2.8 surface calibration: pass-2 transforms from measured parent radii and live srfAttachNodes. Pure.</summary>
[TestClass]
public class SurfaceCalibrationTests
{
    static ConstructionPart Part(string name, string cat, (string id, double y, double oy)[] nodes, bool srf = false) => new()
    {
        Name = name, Category = cat, Buildable = true, ConstructionSupport = "verified",
        AttachRules = new() { Stack = true, AllowStack = true, Srf = srf, AllowSrf = true },
        StackNodes = nodes.Select(n => new ConstructionNode { Id = n.id, Position = new(0, n.y, 0), Orientation = new(0, n.oy, 0), Size = 1 }).ToList(),
        SurfaceNode = srf ? new SurfaceNodeDefinition { Position = new(0, 0, 0), Orientation = new(1, 0, 0) } : null
    };
    static ConstructionCatalog Catalog() => new ConstructionCatalog()
        .Add(Part("probeCoreOcto.v2", PartCategories.Command, new[] { ("bottom", -0.19, -1.0), ("top", 0.19, 1.0) }))
        .Add(Part("Rockomax64.BW", PartCategories.Tank, new[] { ("top", 3.73, 1.0), ("bottom", -3.73, -1.0) }))
        .Add(Part("engineLargeSkipper", PartCategories.Engine, new[] { ("top", 0.0, 1.0), ("bottom", -1.6, -1.0) }))
        .Add(Part("radialDecoupler", PartCategories.Decoupler, new (string, double, double)[0], srf: true))
        .Add(Part("solidBooster1-1", PartCategories.Engine, new[] { ("top", 3.92, 1.0) }, srf: true));
    static GraphPartDto G(string id, string part, string? parent = null, string? pn = null, string? n = null) => new() { Id = id, Part = part, Parent = parent, ParentNode = pn, Node = n };
    static GraphDto Radial(int n, double height = 0)
    {
        var g = new GraphDto { Name = "gdl", Facility = "VAB", Root = "core" };
        g.Parts.Add(G("core", "probeCoreOcto.v2")); g.Parts.Add(G("tank", "Rockomax64.BW", "core", "bottom", "top")); g.Parts.Add(G("engine", "engineLargeSkipper", "tank", "bottom", "top"));
        g.Parts.Add(new() { Id = "rd", Part = "radialDecoupler", Parent = "tank", Symmetry = n, Surface = new() { HeightOffset = height, AngleDegrees = 0 } });
        g.Parts.Add(new() { Id = "srb", Part = "solidBooster1-1", Parent = "rd", Surface = new() { HeightOffset = 0, AngleDegrees = 270 } });
        return g;
    }
    static double Radius(LayoutPart p, LayoutPart tank) => Math.Sqrt(Math.Pow(p.Position.X - tank.Position.X, 2) + Math.Pow(p.Position.Z - tank.Position.Z, 2));

    static SurfaceMeasurements Measure(StructuralLayout layout, Func<LayoutPart, double> radiusOf)
    {
        var m = new SurfaceMeasurements();
        foreach (var site in SurfaceCalibration.MeasurementSites(layout)) m.SetRadius(site.ParentCid, site.Height, radiusOf(layout.Parts.Single(p => p.Cid == site.ParentCid)));
        return m;
    }

    [TestMethod]
    public void RadiusAtHeightUsesOnlyBoxesThatSpanTheHeight()
    {
        Vector[] Box(double r, double y0, double y1) => new[]
        {
            new Vector(-r, y0, -r), new Vector(r, y0, -r), new Vector(-r, y1, -r), new Vector(r, y1, -r),
            new Vector(-r, y0, r), new Vector(r, y0, r), new Vector(-r, y1, r), new Vector(r, y1, r),
        };
        var boxes = new[] { Box(1.25, -3.7, 3.7), Box(0.4, 3.7, 4.2), Box(2.0, -9, -8) };
        Assert.AreEqual(1.25, SurfaceCalibration.RadiusAtHeight(boxes, 0)!.Value, 1e-12);
        Assert.AreEqual(1.25, SurfaceCalibration.RadiusAtHeight(boxes, 3.7005)!.Value, 1e-12, "boundary slack");
        Assert.AreEqual(0.4, SurfaceCalibration.RadiusAtHeight(boxes, 4.0)!.Value, 1e-12);
        Assert.IsNull(SurfaceCalibration.RadiusAtHeight(boxes, 20));
        Assert.IsNull(SurfaceCalibration.RadiusAtHeight(new[] { new[] { new Vector(double.NaN, 0, 0) } }, 0));
        Assert.IsNull(SurfaceCalibration.RadiusAtHeight(null, 0));
        // overlapping boxes at one height: the widest wins
        Assert.AreEqual(2.0, SurfaceCalibration.RadiusAtHeight(new[] { Box(1.25, -1, 1), Box(2.0, -1, 1) }, 0)!.Value, 1e-12);
    }

    [TestMethod]
    public void SitesAndMeasurementSitesListEachSurfacePlacement()
    {
        var plan = CraftPlanner.Plan(Radial(4), Catalog());
        Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues));
        Assert.IsTrue(SurfaceCalibration.NeedsCalibration(plan.Layout));
        var sites = SurfaceCalibration.Sites(plan.Layout);
        Assert.AreEqual(8, sites.Count, "four decouplers and four boosters");
        Assert.AreEqual(5, SurfaceCalibration.MeasurementSites(plan.Layout).Count, "one tank site plus one decoupler site per booster");
        Assert.IsTrue(sites.All(s => s.RadiusUsed == 0.625), "provisional radius recorded");
        var t1 = new GraphDto { Name = "x", Facility = "VAB", Root = "core" };
        t1.Parts.Add(G("core", "probeCoreOcto.v2")); t1.Parts.Add(G("tank", "Rockomax64.BW", "core", "bottom", "top")); t1.Parts.Add(G("engine", "engineLargeSkipper", "tank", "bottom", "top"));
        Assert.IsFalse(SurfaceCalibration.NeedsCalibration(CraftPlanner.Plan(t1, Catalog()).Layout));
    }

    [TestMethod]
    public void CounterpartsFollowTheMeasuredRadiusAboutTheParentAxisForEverySymmetry()
    {
        foreach (var n in new[] { 2, 3, 4, 5, 6, 7, 8 })
        {
            var first = CraftPlanner.Plan(Radial(n), Catalog());
            Assert.IsTrue(first.Ok, n + ": " + string.Join(";", first.Issues));
            var m = Measure(first.Layout!, p => p.Source.Id == "tank" ? 1.25 : 0.3125);
            var second = SurfaceCalibration.Recalibrate(Radial(n), Catalog(), null, first.Layout!, m);
            Assert.IsTrue(second.Ok, n + ": " + string.Join(";", second.Issues));
            Assert.AreEqual(first.Craft!.Parts.Count, second.Craft!.Parts.Count);
            var tank = second.Layout!.Parts.First(p => p.Source.Id == "tank");
            var rds = second.Layout.Parts.Where(p => p.Source.Id == "rd").ToList();
            var srbs = second.Layout.Parts.Where(p => p.Source.Id == "srb").ToList();
            Assert.AreEqual(n, rds.Count);
            var angles = new HashSet<long>();
            for (int k = 0; k < n; k++)
            {
                Assert.AreEqual(1.25, Radius(rds[k], tank), 1e-9, n + "/" + k + " decoupler on the measured tank surface");
                Assert.AreEqual(1.25 + 0.3125, Radius(srbs[k], tank), 1e-9, n + "/" + k + " booster on the measured decoupler surface");
                var a = 2 * Math.PI * k / n;
                Assert.AreEqual(1.25 * Math.Sin(a), rds[k].Position.X - tank.Position.X, 1e-9);
                Assert.AreEqual(1.25 * Math.Cos(a), rds[k].Position.Z - tank.Position.Z, 1e-9);
                Assert.AreEqual(1.0, RotationMath.Rotate(new Vector(0, 1, 0), rds[k].Rotation).Y, 1e-9, "stays upright");
                Assert.AreEqual(1.25, rds[k].SurfaceRadius, 1e-12); Assert.AreEqual(0.3125, srbs[k].SurfaceRadius, 1e-12);
                angles.Add((long)Math.Round(Math.Atan2(rds[k].Position.X - tank.Position.X, rds[k].Position.Z - tank.Position.Z) * 1000));
            }
            Assert.AreEqual(n, angles.Count, "counterparts at distinct angles");
            Assert.AreEqual(first.Craft.Parts.Count(p => p.Name == "radialDecoupler"), n);
            Assert.IsTrue(second.Craft.Parts.Where(p => p.Name == "radialDecoupler").All(p => p.Sym.Count == n - 1));
            // structure is unchanged: same staging, same links, same ids; only positions move
            for (int i = 0; i < first.Craft.Parts.Count; i++)
            {
                Assert.AreEqual(first.Craft.Parts[i].Cid, second.Craft.Parts[i].Cid);
                Assert.AreEqual(first.Craft.Parts[i].Staging, second.Craft.Parts[i].Staging);
                Assert.AreEqual(first.Craft.Parts[i].Links.Count, second.Craft.Parts[i].Links.Count);
            }
        }
    }

    [TestMethod]
    public void SymmetryOneAndHeightOffsetsAreHonoured()
    {
        var g = Radial(2, height: 1.5);
        var first = CraftPlanner.Plan(g, Catalog()); Assert.IsTrue(first.Ok, string.Join(";", first.Issues));
        var m = new SurfaceMeasurements();
        foreach (var s in SurfaceCalibration.MeasurementSites(first.Layout!)) m.SetRadius(s.ParentCid, s.Height, s.ParentCid == first.Layout!.Parts.First(p => p.Source.Id == "tank").Cid ? 1.9 : 0.4);
        var second = SurfaceCalibration.Recalibrate(g, Catalog(), null, first.Layout!, m);
        Assert.IsTrue(second.Ok, string.Join(";", second.Issues));
        var tank = second.Layout!.Parts.First(p => p.Source.Id == "tank");
        var rd = second.Layout.Parts.First(p => p.Source.Id == "rd");
        Assert.AreEqual(tank.Position.Y + 1.5, rd.Position.Y, 1e-9, "decoupler sits at the requested attach height (its srf node is at its origin)");
        Assert.AreEqual(1.9, Radius(rd, tank), 1e-9);
    }

    [TestMethod]
    public void TheLiveChildSurfaceNodeReplacesTheCatalogOneAndTheCatalogIsNotMutated()
    {
        var catalog = Catalog();
        var first = CraftPlanner.Plan(Radial(2), catalog); Assert.IsTrue(first.Ok);
        var hash = catalog.Hash();
        var m = Measure(first.Layout!, p => p.Source.Id == "tank" ? 1.25 : 0.3125);
        // live node 0.2 m inside the part's origin along the part's x axis (its outward-facing orientation is unchanged)
        m.ChildNodes["radialDecoupler"] = new SurfaceNodeDefinition { Position = new(0.2, 0, 0), Orientation = new(1, 0, 0) };
        var second = SurfaceCalibration.Recalibrate(Radial(2), catalog, null, first.Layout!, m);
        Assert.IsTrue(second.Ok, string.Join(";", second.Issues));
        var tank = second.Layout!.Parts.First(p => p.Source.Id == "tank");
        var rd = second.Layout.Parts.First(p => p.Source.Id == "rd");
        Assert.AreEqual(1.25 + 0.2, Radius(rd, tank), 1e-9, "the origin sits 0.2 m further out so that the live node touches the surface");
        Assert.AreEqual(hash, catalog.Hash());
        Assert.AreEqual(0.0, catalog.Find("radialDecoupler")!.SurfaceNode!.Position.X);
    }

    [TestMethod]
    public void MissingOrUnusableMeasurementsAreRefused()
    {
        var first = CraftPlanner.Plan(Radial(2), Catalog()); Assert.IsTrue(first.Ok);
        var empty = SurfaceCalibration.Recalibrate(Radial(2), Catalog(), null, first.Layout!, new SurfaceMeasurements());
        Assert.IsFalse(empty.Ok); Assert.IsTrue(empty.Issues.All(i => i.Code == "surface_measurement_invalid"));
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, 0.0, -1.0, 0.001, 1000.0 })
        {
            var m = Measure(first.Layout!, _ => bad);
            Assert.IsFalse(SurfaceCalibration.Recalibrate(Radial(2), Catalog(), null, first.Layout!, m).Ok, "radius " + bad);
        }
        var nodes = Measure(first.Layout!, _ => 1.0);
        nodes.ChildNodes["radialDecoupler"] = new SurfaceNodeDefinition { Position = new(0, 0, 0), Orientation = new(0, 0, 0) };
        Assert.IsFalse(SurfaceCalibration.Recalibrate(Radial(2), Catalog(), null, first.Layout!, nodes).Ok);
    }

    [TestMethod]
    public void ClearanceIsFiveCentimetres()
    {
        var plan = CraftPlanner.Plan(Radial(2), Catalog()); Assert.IsTrue(plan.Ok);
        var sites = SurfaceCalibration.Sites(plan.Layout!);
        Assert.AreEqual(0, SurfaceCalibration.Clearance(sites, (c, h) => 0.625 + 0.0499).Count);
        Assert.AreEqual(0, SurfaceCalibration.Clearance(sites, (c, h) => 0.625 - 0.05).Count, "5 cm itself passes");
        var off = SurfaceCalibration.Clearance(sites, (c, h) => 0.625 + 0.0501);
        Assert.AreEqual(sites.Count, off.Count); StringAssert.Contains(off[0], "surface_clearance");
        var missing = SurfaceCalibration.Clearance(sites, (c, h) => null);
        Assert.IsTrue(missing.All(p => p.StartsWith("surface_unmeasured")));
    }

    [TestMethod]
    public void NonSurfaceCraftIsNotAffectedByTheRadiusProvider()
    {
        var g = new GraphDto { Name = "x", Facility = "VAB", Root = "core" };
        g.Parts.Add(G("core", "probeCoreOcto.v2")); g.Parts.Add(G("tank", "Rockomax64.BW", "core", "bottom", "top")); g.Parts.Add(G("engine", "engineLargeSkipper", "tank", "bottom", "top"));
        var a = CraftPlanner.Plan(g, Catalog());
        var b = CraftPlanner.Plan(g, Catalog(), new PlannerOptions { SurfaceRadiusProvider = (c, h) => 9.0 });
        Assert.AreEqual(a.Craft!.ToText(), b.Craft!.ToText());
    }
}
