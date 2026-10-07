using System.Text;
using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

/// <summary>
/// The crewed stack: Mk1 pod, Mk16 parachute on its top node, an optional heat shield on its bottom node, stack decouplers and tank/engine sections,
/// with non-staged utility parts ("other", e.g. a science module) allowed inside a section. Oracle: stock Science Jr, read from the install (never committed):
/// pod, top parachute, HeatShield1 (unstaged: istg=-1 dstg=1 sidx=-1 sqor=-1 sepI=-1) whose "direct" node carries a TD-12, a science module, an FL-T800,
/// an LV-909, a second TD-12 and a lower FL-T800 x2 + LV-T45 stage.
/// </summary>
[TestClass]
public class CrewedStackStagingTests
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

    static ConstructionPart Part(string name, string cat, params (string id, double y, double oy)[] nodes) => new()
    {
        Name = name, Category = cat, Buildable = true, ConstructionSupport = "unverified",
        AttachRules = new() { Stack = true, AllowStack = true, AllowSrf = true },
        StackNodes = nodes.Select(n => new ConstructionNode { Id = n.id, Position = new(0, n.y, 0), Orientation = new(0, n.oy, 0), Size = 1 }).ToList()
    };
    /// <summary>Stock node values (part cfgs as patched in the live modpack).</summary>
    static ConstructionCatalog Catalog()
    {
        var c = new ConstructionCatalog();
        c.Add(Part("mk1pod.v2", PartCategories.Command, ("bottom", -0.4050379, -1), ("top", 0.6423756, 1)));
        c.Add(Part("parachuteSingle", PartCategories.Parachute, ("bottom", -0.0120649, -1)));
        c.Add(Part("HeatShield1", PartCategories.HeatShield, ("direct", 0, -1), ("bottom", -0.17, -1), ("top", 0.022, 1)));
        c.Add(Part("Decoupler.1", PartCategories.Decoupler, ("top", 0.05, 1), ("bottom", -0.05, -1)));
        c.Add(Part("science.module", PartCategories.Other, ("top", 0.6125, 1), ("bottom", -0.5125, -1)));
        c.Add(Part("fuelTankSmall", PartCategories.Tank, ("top", 0.55525, 1), ("bottom", -0.55525, -1)));
        c.Add(Part("fuelTank", PartCategories.Tank, ("top", 0.981725, 1), ("bottom", -0.9125, -1)));
        c.Add(Part("fuelTank.long", PartCategories.Tank, ("top", 1.875, 1), ("bottom", -1.8875, -1)));
        c.Add(Part("liquidEngine3.v2", PartCategories.Engine, ("top", 0, 1), ("bottom", -0.83, -1)));
        c.Add(Part("liquidEngine2", PartCategories.Engine, ("top", 0.9018263, 1), ("bottom", -0.7179225, -1)));
        c.Add(Part("liquidEngine2.v2", PartCategories.Engine, ("top", 0, 1), ("bottom", -1.63, -1)));
        c.Add(Part("liquidEngine.v2", PartCategories.Engine, ("top", 0, 1), ("bottom", -1.63, -1)));
        return c;
    }
    static GraphPartDto G(string id, string part, string? parent = null, string? pn = null, string? n = null) => new() { Id = id, Part = part, Parent = parent, ParentNode = pn, Node = n };

    static GraphDto ScienceJrStack()
    {
        var g = new GraphDto { Name = "sciencejr", Facility = "VAB", Root = "pod" };
        g.Parts.Add(G("pod", "mk1pod.v2"));
        g.Parts.Add(G("chute", "parachuteSingle", "pod", "top", "bottom"));
        g.Parts.Add(G("hs", "HeatShield1", "pod", "bottom", "top"));
        g.Parts.Add(G("d1", "Decoupler.1", "hs", "direct", "top"));
        g.Parts.Add(G("sci", "science.module", "d1", "bottom", "top"));
        g.Parts.Add(G("t1", "fuelTank.long", "sci", "bottom", "top"));
        g.Parts.Add(G("e1", "liquidEngine3.v2", "t1", "bottom", "top"));
        g.Parts.Add(G("d2", "Decoupler.1", "e1", "bottom", "top"));
        g.Parts.Add(G("t2", "fuelTank.long", "d2", "bottom", "top"));
        g.Parts.Add(G("t3", "fuelTank.long", "t2", "bottom", "top"));
        g.Parts.Add(G("e2", "liquidEngine2", "t3", "bottom", "top"));
        return g;
    }

    [TestMethod]
    public void HeatShieldParachuteAndUtilityStackReproducesScienceJr()
    {
        var oracle = Read("Science Jr");
        var plan = CraftPlanner.Plan(ScienceJrStack(), Catalog());
        Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues));
        Assert.AreEqual("T2", plan.Topology);
        // The planned stack omits the oracle's surface parts (solar panels, goo, antennas, sensors, batteries, struts, fins, radial boosters).
        var stack = new[] { "mk1pod.v2", "parachuteSingle", "HeatShield1", "Decoupler.1", "science.module", "fuelTank.long", "liquidEngine3.v2", "Decoupler.1", "fuelTank.long", "fuelTank.long", "liquidEngine2" };
        var expect = oracle.Where(o => o.Attm == 0 && stack.Contains(o.Name)).ToList();
        Assert.AreEqual(stack.Length, expect.Count, "oracle stack parts");
        var planned = plan.Craft!.Parts.ToList();
        for (int i = 0; i < expect.Count; i++)
        {
            var o = expect[i]; var p = planned[i]; var s = p.Staging; var label = i + ":" + o.Name;
            Assert.AreEqual(o.Name, p.Name, label);
            Assert.AreEqual(o.Dstg, s.Dstg, label + " dstg"); Assert.AreEqual(o.SepI, s.SepI, label + " sepI"); Assert.AreEqual(o.Attm, s.Attm, label + " attm");
            if (o.Name == "liquidEngine2")
            {
                // The oracle's radial boosters share the lower engine's ignition group: istg/sqor 5 and sidx 1 there, 4 and 0 for the stack alone.
                Assert.AreEqual((5, 5, 1), (o.Istg, o.Sqor, o.Sidx)); Assert.AreEqual((4, 4, 0), (s.Istg, s.Sqor, s.Sidx));
            }
            else
            {
                Assert.AreEqual(o.Istg, s.Istg, label + " istg"); Assert.AreEqual(o.Sqor, s.Sqor, label + " sqor"); Assert.AreEqual(o.Sidx, s.Sidx, label + " sidx");
            }
        }
        var hs = planned.Single(p => p.Name == "HeatShield1").Staging;
        Assert.AreEqual((-1, 1, -1, -1, -1, 0), (hs.Istg, hs.Dstg, hs.Sidx, hs.Sqor, hs.SepI, hs.Attm), "the heat shield is never a stage group");
    }

    /// <summary>The crewed P3 rocket: pod + chute + heat shield, TD-12, FL-T400 + FL-T200 + LV-909, TD-12, FL-T800 x2 + LV-T45.</summary>
    public static GraphDto CrewedTwoStage(bool heatShield = true)
    {
        var g = new GraphDto { Name = "crewed-orbiter", Facility = "VAB", Root = "pod" };
        g.Parts.Add(G("pod", "mk1pod.v2"));
        g.Parts.Add(G("chute", "parachuteSingle", "pod", "top", "bottom"));
        if (heatShield) { g.Parts.Add(G("hs", "HeatShield1", "pod", "bottom", "top")); g.Parts.Add(G("d1", "Decoupler.1", "hs", "direct", "top")); }
        else g.Parts.Add(G("d1", "Decoupler.1", "pod", "bottom", "top"));
        g.Parts.Add(G("u1", "fuelTank", "d1", "bottom", "top"));
        g.Parts.Add(G("u2", "fuelTankSmall", "u1", "bottom", "top"));
        g.Parts.Add(G("terrier", "liquidEngine3.v2", "u2", "bottom", "top"));
        g.Parts.Add(G("d2", "Decoupler.1", "terrier", "bottom", "top"));
        g.Parts.Add(G("l1", "fuelTank.long", "d2", "bottom", "top"));
        g.Parts.Add(G("l2", "fuelTank.long", "l1", "bottom", "top"));
        g.Parts.Add(G("swivel", "liquidEngine2.v2", "l2", "bottom", "top"));
        return g;
    }

    [TestMethod]
    public void CrewedTwoStageRocketPlansWithOrbiterOneStaging()
    {
        foreach (var hs in new[] { true, false })
        {
            var plan = CraftPlanner.Plan(CrewedTwoStage(hs), Catalog());
            Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues)); Assert.AreEqual("T2", plan.Topology);
            var s = plan.Craft!.Parts.ToDictionary(p => p.SourceId!, p => p.Staging);
            // Orbiter One group order: chute 0, upper decoupler 1, upper engine 2, lower decoupler 3, lower engine 4.
            Assert.AreEqual(0, s["chute"].Istg); Assert.AreEqual(1, s["d1"].Istg); Assert.AreEqual(2, s["terrier"].Istg);
            Assert.AreEqual(3, s["d2"].Istg); Assert.AreEqual(4, s["swivel"].Istg);
            Assert.AreEqual(1, s["u1"].Istg); Assert.AreEqual(3, s["l1"].Istg); Assert.AreEqual(1, s["u1"].SepI); Assert.AreEqual(3, s["l2"].SepI);
            int shift = hs ? 2 : 0; // the heat shield adds 1 to its own dstg and 1 more to the decoupler under it, like Science Jr
            Assert.AreEqual(1 + shift, s["d1"].Dstg); Assert.AreEqual(2 + shift, s["u1"].Dstg); Assert.AreEqual(3 + shift, s["d2"].Dstg); Assert.AreEqual(4 + shift, s["swivel"].Dstg);
            if (hs) Assert.AreEqual((-1, 1, -1, -1, -1), (s["hs"].Istg, s["hs"].Dstg, s["hs"].Sidx, s["hs"].Sqor, s["hs"].SepI));
        }
    }

    [TestMethod]
    public void ThreeStageStackPlans()
    {
        var g = CrewedTwoStage();
        g.Parts.Add(G("d3", "Decoupler.1", "swivel", "bottom", "top"));
        g.Parts.Add(G("b1", "fuelTank.long", "d3", "bottom", "top"));
        g.Parts.Add(G("reliant", "liquidEngine.v2", "b1", "bottom", "top"));
        var plan = CraftPlanner.Plan(g, Catalog());
        Assert.IsTrue(plan.Ok, string.Join(";", plan.Issues)); Assert.AreEqual("T2", plan.Topology);
        var s = plan.Craft!.Parts.ToDictionary(p => p.SourceId!, p => p.Staging);
        Assert.AreEqual(4, s["swivel"].Istg); Assert.AreEqual(5, s["d3"].Istg); Assert.AreEqual(6, s["reliant"].Istg); Assert.AreEqual(5, s["b1"].SepI);
    }

    [TestMethod]
    public void HeatShieldIsSupportedOnlyDirectlyUnderTheCommandPod()
    {
        void Refused(GraphDto g, string why)
        {
            var r = CraftPlanner.Plan(g, Catalog());
            Assert.IsNull(r.Craft, why);
            Assert.IsTrue(r.Issues.Any(i => i.Code == "unsupported_staging_topology"), why + ": " + string.Join(";", r.Issues));
        }
        var low = CrewedTwoStage(false);
        low.Parts.Single(p => p.Id == "d2").Parent = "hs"; low.Parts.Single(p => p.Id == "d2").ParentNode = "direct";
        low.Parts.Add(G("hs", "HeatShield1", "terrier", "bottom", "top"));
        Refused(low, "heat shield between stages");
        var two = CrewedTwoStage();
        two.Parts.Single(p => p.Id == "d1").Parent = "hs2";
        two.Parts.Add(G("hs2", "HeatShield1", "hs", "direct", "top"));
        Refused(two, "two heat shields");
    }
}
