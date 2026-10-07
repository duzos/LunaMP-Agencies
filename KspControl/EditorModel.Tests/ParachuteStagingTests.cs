using System.Text;
using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

/// <summary>
/// The Mk16 parachute (parachuteSingle) on the command pod's top node. Oracle: stock Orbiter One, read from the install (never committed),
/// where the parachute is istg=0 dstg=0 sidx=0 sqor=0 sepI=-1 attm=0 and every other group is numbered one higher than it would be without it.
/// </summary>
[TestClass]
public class ParachuteStagingTests
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
        c.Add(Part("mk1pod.v2", PartCategories.Command, new[] { ("bottom", -0.4050379, -1.0), ("top", 0.6423756, 1.0) }));
        c.Add(Part("parachuteSingle", PartCategories.Parachute, new[] { ("bottom", -0.01508113, -1.0) }));
        c.Add(Part("Decoupler.1", PartCategories.Decoupler, new[] { ("top", 0.05, 1.0), ("bottom", -0.05, -1.0) }));
        c.Add(Part("fuelTankSmall", PartCategories.Tank, new[] { ("top", 0.55525, 1.0), ("bottom", -0.55525, -1.0) }));
        c.Add(Part("Rockomax64.BW", PartCategories.Tank, new[] { ("top", 3.73, 1.0), ("bottom", -3.73, -1.0) }));
        c.Add(Part("liquidEngine2", PartCategories.Engine, new[] { ("top", 0.9018263, 1.0), ("bottom", -0.7179225, -1.0) }));
        c.Add(Part("engineLargeSkipper", PartCategories.Engine, new[] { ("top", 0.0, 1.0), ("bottom", -1.6, -1.0) }));
        c.Add(Part("radialDecoupler", PartCategories.Decoupler, new (string, double, double)[0], srf: true));
        c.Add(Part("solidBooster1-1", PartCategories.Engine, new[] { ("top", 3.92, 1.0) }, srf: true));
        return c;
    }
    static GraphPartDto G(string id, string part, string? parent = null, string? pn = null, string? n = null) => new() { Id = id, Part = part, Parent = parent, ParentNode = pn, Node = n };
    static GraphPartDto Chute(string parent = "pod", string pn = "top") => G("chute", "parachuteSingle", parent, pn, "bottom");

    static GraphDto OrbiterChain(bool chute = true)
    {
        var g = new GraphDto { Name = "orbiter", Facility = "VAB", Root = "pod" };
        g.Parts.Add(G("pod", "mk1pod.v2"));
        if (chute) g.Parts.Add(Chute());
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
    public void T2WithTopParachuteReproducesOrbiterOne()
    {
        var oracle = Read("Orbiter One");
        var plan = CraftPlanner.Plan(OrbiterChain(), Catalog());
        Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues));
        Assert.AreEqual("T2", plan.Topology);
        // Same parts, same order, except the radial group (fins, radial decoupler, booster, nose cone), which the planned craft does not carry.
        var skip = new HashSet<string> { "basicFin", "radialDecoupler", "solidBooster.v2", "noseCone" };
        var expect = oracle.Where(o => !skip.Contains(o.Name)).ToList();
        var planned = plan.Craft!.Parts;
        Assert.AreEqual(expect.Count, planned.Count);
        // The oracle lists the parachute after the first decoupler (its pod-side order); match by name and chain position instead of file order.
        var byName = new Func<string, List<Oracle>>(n => expect.Where(o => o.Name == n).ToList());
        var chute = planned.Single(p => p.Name == "parachuteSingle").Staging;
        var oc = byName("parachuteSingle").Single();
        Assert.AreEqual((oc.Istg, oc.Dstg, oc.Sidx, oc.Sqor, oc.SepI, oc.Attm), (chute.Istg, chute.Dstg, chute.Sidx, chute.Sqor, chute.SepI, chute.Attm), "parachute row");
        Assert.AreEqual((0, 0, 0, 0, -1, 0), (chute.Istg, chute.Dstg, chute.Sidx, chute.Sqor, chute.SepI, chute.Attm));
        // Every other part, in chain order, equals the oracle row; only the bottom engine differs, because the oracle's radial booster shares its ignition group
        // (istg and sqor 5 against 4 here), which is the radial group's slot and not the parachute's.
        var oracleChain = expect.Where(o => o.Name != "parachuteSingle").ToList();
        var plannedChain = planned.Where(p => p.Name != "parachuteSingle").ToList();
        for (int i = 0; i < oracleChain.Count; i++)
        {
            var o = oracleChain[i]; var p = plannedChain[i].Staging; var label = i + ":" + o.Name;
            Assert.AreEqual(o.Name, plannedChain[i].Name, label);
            Assert.AreEqual(o.Dstg, p.Dstg, label + " dstg"); Assert.AreEqual(o.Sidx, p.Sidx, label + " sidx");
            Assert.AreEqual(o.SepI, p.SepI, label + " sepI"); Assert.AreEqual(o.Attm, p.Attm, label + " attm");
            if (i == oracleChain.Count - 1) { Assert.AreEqual(4, p.Istg); Assert.AreEqual(4, p.Sqor); Assert.AreEqual(5, o.Istg); }
            else { Assert.AreEqual(o.Istg, p.Istg, label + " istg"); Assert.AreEqual(o.Sqor, p.Sqor, label + " sqor"); }
        }
    }

    [TestMethod]
    public void ParachuteShiftsEveryOtherGroupUpByOne()
    {
        var with = CraftPlanner.Plan(OrbiterChain(true), Catalog()).Craft!.Parts.Where(p => p.Name != "parachuteSingle").ToList();
        var without = CraftPlanner.Plan(OrbiterChain(false), Catalog()).Craft!.Parts.ToList();
        Assert.AreEqual(without.Count, with.Count);
        for (int i = 0; i < with.Count; i++)
        {
            var a = without[i].Staging; var b = with[i].Staging;
            Assert.AreEqual(a.Istg < 0 ? -1 : a.Istg + 1, b.Istg, i + " istg"); Assert.AreEqual(a.Sqor < 0 ? -1 : a.Sqor + 1, b.Sqor, i + " sqor");
            Assert.AreEqual(a.SepI < 0 ? -1 : a.SepI + 1, b.SepI, i + " sepI");
            Assert.AreEqual(a.Dstg, b.Dstg); Assert.AreEqual(a.Sidx, b.Sidx); Assert.AreEqual(a.Attm, b.Attm);
        }
    }

    [TestMethod]
    public void T1WithParachute()
    {
        var g = new GraphDto { Name = "t1", Facility = "VAB", Root = "pod" };
        g.Parts.Add(G("pod", "mk1pod.v2")); g.Parts.Add(Chute()); g.Parts.Add(G("t", "fuelTankSmall", "pod", "bottom", "top")); g.Parts.Add(G("e", "liquidEngine2", "t", "bottom", "top"));
        var plan = CraftPlanner.Plan(g, Catalog());
        Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues)); Assert.AreEqual("T1", plan.Topology);
        var s = plan.Craft!.Parts.ToDictionary(p => p.SourceId!, p => p.Staging);
        Assert.AreEqual(1, s["e"].Istg); Assert.AreEqual(0, s["chute"].Istg); Assert.AreEqual(0, s["chute"].Sqor); Assert.AreEqual(0, s["chute"].Sidx);
        Assert.AreEqual(-1, s["chute"].SepI); Assert.AreEqual(0, s["chute"].Attm); Assert.AreEqual(-1, s["pod"].Istg);
        // The chute is the pod's stack child on its top node.
        var pod = plan.Craft.Parts.Single(p => p.SourceId == "pod");
        Assert.IsTrue(pod.AttachNodes.Any(n => n.NodeId == "top"));
    }

    [TestMethod]
    public void T3WithParachuteKeepsRadialGroupsAndAddsGroupZero()
    {
        var g = new GraphDto { Name = "t3", Facility = "VAB", Root = "core" };
        g.Parts.Add(G("core", "mk1pod.v2")); g.Parts.Add(Chute("core"));
        g.Parts.Add(G("tank", "Rockomax64.BW", "core", "bottom", "top")); g.Parts.Add(G("engine", "engineLargeSkipper", "tank", "bottom", "top"));
        g.Parts.Add(new() { Id = "rd", Part = "radialDecoupler", Parent = "tank", Symmetry = 2, Surface = new() { HeightOffset = 0, AngleDegrees = 0 } });
        g.Parts.Add(new() { Id = "srb", Part = "solidBooster1-1", Parent = "rd", Surface = new() { HeightOffset = 0, AngleDegrees = 270 } });
        var plan = CraftPlanner.Plan(g, Catalog());
        Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues)); Assert.AreEqual("T3", plan.Topology);
        var s = plan.Craft!.Parts;
        Assert.AreEqual(0, s.Single(p => p.Name == "parachuteSingle").Staging.Istg);
        Assert.AreEqual(1, s.First(p => p.Name == "radialDecoupler").Staging.Istg);
        Assert.AreEqual(2, s.First(p => p.Name == "solidBooster1-1").Staging.Istg);
        Assert.AreEqual(2, s.Single(p => p.Name == "engineLargeSkipper").Staging.Istg);
        Assert.AreEqual(1, s.Single(p => p.Name == "engineLargeSkipper").Staging.Sidx, "core engine keeps its slot in the ignition group");
    }

    [TestMethod]
    public void OnlyTheTopNodeParachuteOnTheCommandPodIsSupported()
    {
        void Refused(GraphDto g, string why)
        {
            var r = CraftPlanner.Plan(g, Catalog());
            Assert.IsNull(r.Craft, why);
            Assert.IsTrue(r.Issues.Any(i => i.Code == "unsupported_staging_topology"), why + ": " + string.Join(";", r.Issues));
        }
        // on the pod's bottom node (the tank takes the top, so the stack is mirrored): refused as not on the top node
        var bottom = new GraphDto { Name = "x", Facility = "VAB", Root = "pod" };
        bottom.Parts.Add(G("pod", "mk1pod.v2")); bottom.Parts.Add(Chute("pod", "bottom")); bottom.Parts.Add(G("t", "fuelTankSmall", "pod", "top", "top")); bottom.Parts.Add(G("e", "liquidEngine2", "t", "bottom", "top"));
        Refused(bottom, "bottom node");
        // two parachutes
        var two = OrbiterChain(); two.Parts.Add(G("chute2", "parachuteSingle", "e2", "bottom", "bottom")); Refused(two, "two parachutes");
        // a parachute lower in the stack
        var low = OrbiterChain(false); low.Parts.Add(G("chute", "parachuteSingle", "e2", "bottom", "bottom")); Refused(low, "off the pod");
        // a parachute alone is not a T1 core
        var alone = new GraphDto { Name = "x", Facility = "VAB", Root = "pod" }; alone.Parts.Add(G("pod", "mk1pod.v2")); alone.Parts.Add(Chute()); Refused(alone, "no engine");
    }

    [TestMethod]
    public void ParachuteStageOverrideMustMatchTheDerivedStage()
    {
        var g = OrbiterChain(); g.Parts.Single(p => p.Id == "chute").Stage = 3;
        Assert.IsTrue(CraftPlanner.Plan(g, Catalog()).Issues.Any(i => i.Code == "unsupported_staging_topology"));
        var ok = OrbiterChain(); ok.Parts.Single(p => p.Id == "chute").Stage = 0;
        Assert.IsTrue(CraftPlanner.Plan(ok, Catalog()).Ok);
    }
}
