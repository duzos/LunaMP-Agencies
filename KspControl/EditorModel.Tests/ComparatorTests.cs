using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

[TestClass]
public class ComparatorTests
{
    /// <summary>Two-part craft (pod over tank) with one module, one resource and some actions. Knobs replace tokens.</summary>
    internal static string Mini(Dictionary<string, string>? o = null)
    {
        var k = new Dictionary<string, string>
        {
            ["A"] = "100000", ["B"] = "100001", ["PA"] = "11", ["PB"] = "22", ["ISTG"] = "-1", ["TANKPOS"] = "0,14,0", ["MODVAL"] = "1.0",
            ["FUEL"] = "90", ["DESC"] = "hello", ["ROT"] = "0,0,0,0", ["EXTRAMOD"] = "", ["CRYO"] = "10", ["POS"] = "0,15,0", ["ATTN"] = "0|-0.4|0",
            ["MODCOST"] = "0", ["DUP2"] = "2", ["VDESC"] = "hello", ["LINK"] = "tank_{B}", ["ORDER"] = "0",
        };
        if (o != null) foreach (var kv in o) k[kv.Key] = kv.Value;
        var t = @"ship = t
version = 1.12.5
description = {DESC}
type = VAB
size = 1,1,1
steamPublishedFileId = 0
persistentId = 7
rot = {ROT}
missionFlag = flag
vesselType = Debris
PART
{
	part = pod_{A}
	persistentId = {PA}
	pos = {POS}
	attPos0 = 0,15,0
	rot = 0,0,0,1
	attRot0 = 0,0,0,1
	istg = -1
	dstg = 0
	modCost = {MODCOST}
	link = {LINK}
	attN = bottom,tank_{B}_{ATTN}
	MODULE
	{
		name = ModuleX
		value = {MODVAL}
		dup = 1
		dup = {DUP2}
	}
	MODULE
	{
		name = ModuleCryoTank
		LastUpdateTime = {CRYO}
	}
	MODULE
	{
		name = RasterPropMonitorComputer
		vesselDescription = {VDESC}
	}
{EXTRAMOD}	RESOURCE
	{
		name = LiquidFuel
		amount = {FUEL}
	}
}
PART
{
	part = tank_{B}
	persistentId = {PB}
	pos = {TANKPOS}
	attPos0 = 0,-1,0
	rot = 0,0,0,1
	attRot0 = 0,0,0,1
	istg = {ISTG}
	dstg = 0
	attN = top,pod_{A}_0|0.5|0
}
";
        t = t.Replace("\r\n", "\n");
        foreach (var kv in k) t = t.Replace("{" + kv.Key + "}", kv.Value);
        return t.Replace("{A}", k["A"]).Replace("{B}", k["B"]);
    }
    static ConfigNode N(string text) => ConfigText.Parse(text);
    static CraftComparison C(string a, string b, ComparatorOptions? o = null) => CraftComparator.Compare(N(a), N(b), o);
    static Dictionary<string, string> D(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [TestMethod] public void IdenticalIsEqual() { var c = C(Mini(), Mini()); Assert.IsTrue(c.Equal, string.Join("\n", c.Differences)); Assert.AreEqual("craftId", c.Mapping); }
    [TestMethod] public void ReorderedPartsAreEqual()
    {
        var a = N(Mini()); var b = N(Mini());
        var parts = b.Entries.Where(e => !e.IsValue).ToList(); b.Entries.Remove(parts[0]); b.Entries.Add(parts[0]);
        Assert.IsTrue(CraftComparator.Compare(a, b).Equal);
    }
    [TestMethod] public void ChangedLinkReported() { var c = C(Mini(), Mini(D(("LINK", "tank_999")))); Assert.IsTrue(c.Differences.Any(d => d.Kind == "link")); }
    [TestMethod] public void MissingModuleReported()
    {
        var b = N(Mini()); var pod = b.Children("PART").First(); pod.Entries.Remove(pod.Entries.First(e => !e.IsValue && e.Child.Name == "MODULE"));
        var c = CraftComparator.Compare(N(Mini()), b);
        Assert.IsTrue(c.Differences.Any(d => d.Kind == "module_sequence"), string.Join("\n", c.Differences));
    }
    [TestMethod] public void ResourceDriftReported() { var c = C(Mini(), Mini(D(("FUEL", "91")))); Assert.IsTrue(c.Differences.Any(d => d.Path.Contains("RESOURCE[LiquidFuel]") && d.Path.EndsWith("amount")), string.Join("\n", c.Differences)); }
    [TestMethod] public void ResourceMultisetCountReported()
    {
        var b = N(Mini()); var pod = b.Children("PART").First(); var r = pod.Children("RESOURCE").First();
        pod.AddNode("RESOURCE").AddValue("name", "LiquidFuel"); _ = r;
        Assert.IsTrue(CraftComparator.Compare(N(Mini()), b).Differences.Any(d => d.Kind == "resource_count"));
    }
    [TestMethod] public void IstgChangeReported() { var c = C(Mini(), Mini(D(("ISTG", "1")))); Assert.IsTrue(c.Differences.Any(d => d.Path == "istg" && d.PartRef == "tank_100001")); }
    [TestMethod] public void PersistentIdsAndHeaderVolatilesIgnored()
    {
        var c = C(Mini(), Mini(D(("PA", "999"), ("PB", "888")))); Assert.IsTrue(c.Equal);
        var b = N(Mini()); b.Entries.RemoveAll(e => e.IsValue && e.Key == "version"); Assert.IsTrue(CraftComparator.Compare(N(Mini()), b).Equal);
    }
    [TestMethod] public void RegistryExclusionAppliedAndReported()
    {
        var c = C(Mini(), Mini(D(("CRYO", "999999"))));
        Assert.IsTrue(c.Equal);
        var e = c.ExclusionsApplied.Single();
        Assert.AreEqual("ModuleCryoTank", e.Module); Assert.AreEqual("LastUpdateTime", e.KeyPath); Assert.AreEqual("Ignore", e.Rule);
        Assert.AreEqual("33-log 18:25", e.EvidenceRef);
        var empty = C(Mini(), Mini(D(("CRYO", "999999"))), new ComparatorOptions { Registry = RoundtripVolatileKeys.Empty() });
        Assert.IsFalse(empty.Equal);
    }
    [TestMethod] public void HeaderDerivedComparesEachSideToItsOwnDescription()
    {
        var c = C(Mini(), Mini(D(("DESC", "other"), ("VDESC", "other"))), new ComparatorOptions());
        Assert.IsFalse(c.Equal); // header description itself differs
        Assert.IsFalse(c.Differences.Any(d => d.Kind == "header_derived_mismatch"), string.Join("\n", c.Differences));
        Assert.IsTrue(c.ExclusionsApplied.Any(e => e.Rule == "HeaderDerived"));
        var bad = C(Mini(), Mini(D(("VDESC", "stale"))));
        Assert.IsTrue(bad.Differences.Any(d => d.Kind == "header_derived_mismatch"));
        var twin = C(Mini(), Mini(D(("DESC", "other"), ("VDESC", "other"))), new ComparatorOptions { TwinMode = true });
        Assert.AreEqual(1, twin.Differences.Count, "description is compared in twin mode; the derived module copy is not an extra difference");
        Assert.AreEqual("description", twin.Differences[0].Path);
        Assert.IsTrue(C(Mini(), Mini(D(("DESC", "hello"))), new ComparatorOptions { TwinMode = true }).Equal);
        // KSP stores header newlines as U+00A8; module copies may hold real newlines.
        var nl = C(Mini(D(("DESC", "a\u00A8b"), ("VDESC", "a\\nb"))), Mini(D(("DESC", "a\u00A8b"), ("VDESC", "a\u00A8b"))));
        Assert.IsTrue(nl.Equal, string.Join("\n", nl.Differences));
    }
    [TestMethod] public void TokenisedNumbersWithinTolerance()
    {
        Assert.IsTrue(ValueRule.Equal("(0, 15, 0)", "(0, 15.0000001, 0)"));
        Assert.IsFalse(ValueRule.Equal("(0, 15, 0)", "(0, 15.001, 0)"));
        Assert.IsTrue(ValueRule.Equal("1.0", "1"));
        Assert.IsFalse(ValueRule.Equal("True", "False"));
        Assert.IsFalse(ValueRule.Equal("1,2", "1,2,3"));
        var c = C(Mini(), Mini(D(("MODVAL", "1.0000001")))); Assert.IsTrue(c.Equal);
        c = C(Mini(), Mini(D(("MODVAL", "1.01")))); Assert.IsFalse(c.Equal);
    }
    [TestMethod] public void RotLiteralIsNotNormalised()
    {
        Assert.IsTrue(C(Mini(), Mini(D(("ROT", "0,0,0,0")))).Equal);
        var c = C(Mini(), Mini(D(("ROT", "0,0,0,1"))));
        Assert.IsTrue(c.Differences.Any(d => d.PartRef == "HEADER" && d.Path == "rot"), string.Join("\n", c.Differences));
    }
    [TestMethod] public void UnlistedKeyDifferenceReported() { var c = C(Mini(), Mini(D(("MODCOST", "5")))); Assert.IsTrue(c.Differences.Any(d => d.Path == "modCost")); }
    [TestMethod] public void OrdinalDuplicatesComparedByPosition()
    {
        var c = C(Mini(), Mini(D(("DUP2", "3"))));
        Assert.IsTrue(c.Differences.Any(d => d.Path.EndsWith("dup[1]")), string.Join("\n", c.Differences));
        Assert.IsFalse(c.Differences.Any(d => d.Path.EndsWith("dup[0]")));
    }
    [TestMethod] public void MissingKeyOnOneSideReported()
    {
        var b = N(Mini()); var pod = b.Children("PART").First(); pod.Entries.RemoveAll(e => e.IsValue && e.Key == "dstg");
        Assert.IsTrue(CraftComparator.Compare(N(Mini()), b).Differences.Any(d => d.Kind == "missing_in_b" && d.Path == "dstg"));
    }
    [TestMethod] public void PositionsAreRootRelativeWithMillimetreTolerance()
    {
        // Whole craft shifted: equal. One part 5 mm off: reported.
        Assert.IsTrue(C(Mini(D(("POS", "0,15,0"), ("TANKPOS", "0,14,0"))), Mini(D(("POS", "10,20,5"), ("TANKPOS", "10,19,5"), ("ATTN", "0|-0.4|0")))).Differences.All(d => d.Path != "pos"));
        var c = C(Mini(), Mini(D(("TANKPOS", "0,14.005,0"))));
        Assert.IsTrue(c.Differences.Any(d => d.PartRef == "tank_100001" && d.Path == "pos"));
        Assert.IsFalse(C(Mini(), Mini(D(("TANKPOS", "0,14.0005,0")))).Differences.Any(d => d.Path == "pos"));
    }
    [TestMethod] public void NodeVectorsCompareAt1e4()
    {
        Assert.IsFalse(C(Mini(), Mini(D(("ATTN", "0|-0.4002|0")))).Equal);
        Assert.IsTrue(C(Mini(), Mini(D(("ATTN", "0|-0.40001|0")))).Equal);
    }
    [TestMethod] public void TreeMappingFallbackWhenCidsDiffer()
    {
        var c = C(Mini(), Mini(D(("A", "5"), ("B", "6"))));
        Assert.AreEqual("tree", c.Mapping);
        Assert.IsTrue(c.Equal, string.Join("\n", c.Differences));
        var changed = C(Mini(), Mini(D(("A", "5"), ("B", "6"), ("ISTG", "2"))));
        Assert.IsFalse(changed.Equal);
    }
    [TestMethod] public void IntegerTokensCompareExactly()
    {
        Assert.IsFalse(ValueRule.Equal("100000000", "100000001"), "relative tolerance must not hide an off-by-one counter");
        Assert.IsTrue(ValueRule.Equal("100000000.0", "100000000.0000001"));
        Assert.IsFalse(ValueRule.Equal("-1", "0"));
        Assert.IsTrue(ValueRule.Equal("5", "5.0"));
        Assert.IsFalse(C(Mini(), Mini(D(("ISTG", "-2")))).Equal);
    }
    [TestMethod] public void OrphanPartsInEitherCraftAreReported()
    {
        var b = N(Mini() + "PART\n{\n\tpart = loose_900\n\tpos = 0,0,0\n\trot = 0,0,0,1\n}\n");
        var c = CraftComparator.Compare(N(Mini()), b, new ComparatorOptions { TwinMode = true });
        Assert.IsTrue(c.Differences.Any(d => d.Kind == "extra_part" && d.B == "loose_900"), string.Join("\n", c.Differences));
        var d2 = CraftComparator.Compare(b, N(Mini()), new ComparatorOptions { TwinMode = true });
        Assert.IsTrue(d2.Differences.Any(d => d.Kind == "missing_part" && d.A == "loose_900"));
        var cid = CraftComparator.Compare(N(Mini()), b);
        Assert.IsFalse(cid.Equal); Assert.AreEqual("tree", cid.Mapping);
    }
    [TestMethod] public void DuplicateAttNIdsAreReported()
    {
        var b = N(Mini()); var pod = b.Children("PART").First();
        pod.AddValue("attN", "bottom,tank_100001_0|-0.4|0");
        Assert.IsTrue(CraftComparator.Compare(N(Mini()), b).Differences.Any(d => d.Kind == "attN_duplicate"));
    }
    [TestMethod] public void VectorSegmentCountsMustAgree()
    {
        var full = Mini(D(("ATTN", "0|-0.4|0_0|-1|0")));
        Assert.IsFalse(C(Mini(), full).Equal);
        Assert.IsTrue(C(full, full).Equal);
        Assert.IsTrue(C(Mini(), Mini(), new ComparatorOptions { TwinMode = true }).Equal);
    }
    [TestMethod] public void TwinModeAlwaysUsesTreeMapping() { Assert.AreEqual("tree", C(Mini(), Mini(), new ComparatorOptions { TwinMode = true }).Mapping); }

    [TestMethod] public void SymmetricTiesMapByGeometryAndAmbiguousTiesFail()
    {
        // tank links to nothing in Mini; add fins as children of the tank via link lines.
        string Build(int f1, int f2, bool sym)
        {
            var t = Mini().Replace("\tattN = top,pod_100000_0|0.5|0\n", $"\tattN = top,pod_100000_0|0.5|0\n\tlink = fin_{f1}\n\tlink = fin_{f2}\n");
            string Fin(int cid, int other, string pos) => $"PART\n{{\n\tpart = fin_{cid}\n\tpos = {pos}\n\trot = 0,0,0,1\n\tsrfN = srfAttach,tank_100001\n" + (sym ? $"\tsym = fin_{other}\n" : "") + "}\n";
            return t + Fin(f1, f2, "0,14,0.6") + Fin(f2, f1, "0,14,-0.6");
        }
        var ok = C(Build(10, 11, true), Build(20, 21, true));
        Assert.AreEqual("tree", ok.Mapping); Assert.IsTrue(ok.Equal, string.Join("\n", ok.Differences));
        var swapped = C(Build(10, 11, true), Build(21, 20, true));
        Assert.IsTrue(swapped.Equal, "geometry decides, not file order");
        // fins exactly on the 0/180 degree axes, with negative zero and tiny jitter in x, still pair by angle
        string Jit(string t, string x1, string x2) => t.Replace("pos = 0,14,0.6", "pos = " + x1 + ",14,0.6").Replace("pos = 0,14,-0.6", "pos = " + x2 + ",14,-0.6");
        var jitter = C(Jit(Build(10, 11, true), "0", "0"), Jit(Build(20, 21, true), "-0", "0.0000001"), new ComparatorOptions { TwinMode = true });
        Assert.IsTrue(jitter.Equal, string.Join("\n", jitter.Differences));
        var amb = C(Build(10, 11, false), Build(20, 21, false));
        Assert.IsFalse(amb.Equal);
        Assert.IsTrue(amb.Differences.Any(d => d.Kind == "mapping_ambiguous"));
    }
    [TestMethod] public void DifferencesAreCappedAtOneHundred()
    {
        var b = N(Mini()); var pod = b.Children("PART").First();
        var mod = pod.Children("MODULE").First();
        for (int i = 0; i < 150; i++) mod.AddValue("extra" + i, "x");
        var c = CraftComparator.Compare(N(Mini()), b);
        Assert.AreEqual(100, c.Differences.Count); Assert.AreEqual(150, c.TotalDifferences);
    }
    [TestMethod] public void RootRelativeVectorRuleHonoured()
    {
        var reg = RoundtripVolatileKeys.Empty().Add(new VolatileKeyEntry { Module = "ModuleX", KeyPath = "originalPos", Rule = VolatileRule.RootRelativeVector, EvidenceRef = "test" });
        string Make(string pos, string orig) => Mini(D(("POS", pos), ("TANKPOS", pos.Replace(",15,", ",14,")), ("EXTRAMOD", ""))).Replace("\t\tdup = 1\n", "\t\tdup = 1\n\t\toriginalPos = " + orig + "\n");
        var shifted = C(Make("0,15,0", "0,15,0"), Make("5,15,0", "5,15,0"), new ComparatorOptions { Registry = reg });
        Assert.IsTrue(shifted.Equal, string.Join("\n", shifted.Differences));
        Assert.AreEqual(1, shifted.ExclusionsApplied.Count);
        var wrong = C(Make("0,15,0", "0,15,0"), Make("5,15,0", "9,15,0"), new ComparatorOptions { Registry = reg });
        Assert.IsFalse(wrong.Equal);
        var plain = C(Make("0,15,0", "0,15,0"), Make("5,15,0", "5,15,0"), new ComparatorOptions { Registry = RoundtripVolatileKeys.Empty() });
        Assert.IsFalse(plain.Equal);
    }
    [TestMethod] public void AbsentEqualsDefaultRule()
    {
        var reg = RoundtripVolatileKeys.Empty().Add(new VolatileKeyEntry { Module = "ACTIONS", KeyPath = "Foo/active", Rule = VolatileRule.AbsentEqualsDefault, DefaultValue = "False", EvidenceRef = "t" });
        string Make(string active) => Mini().Replace("\tRESOURCE", "\tACTIONS\n\t{\n\t\tFoo\n\t\t{\n\t\t\tactionGroup = None\n" + active + "\t\t}\n\t}\n\tRESOURCE");
        var o = new ComparatorOptions { Registry = reg };
        Assert.IsTrue(C(Make(""), Make("\t\t\tactive = False\n"), o).Equal);
        Assert.IsFalse(C(Make(""), Make("\t\t\tactive = True\n"), o).Equal);
        Assert.IsFalse(C(Make(""), Make("\t\t\tactive = False\n"), new ComparatorOptions { Registry = RoundtripVolatileKeys.Empty() }).Equal);
    }
    [TestMethod] public void ComparatorAndFingerprintApplyEveryRuleTypeIdentically()
    {
        string Make(string pos, string orig, string vdesc, string desc, string active, string cryo) =>
            Mini(D(("POS", pos), ("TANKPOS", pos.Replace(",15,", ",14,")), ("VDESC", vdesc), ("DESC", desc), ("CRYO", cryo)))
                .Replace("\t\tdup = 1\n", "\t\tdup = 1\n\t\toriginalPos = " + orig + "\n")
                .Replace("\tRESOURCE", "\tACTIONS\n\t{\n\t\tFoo\n\t\t{\n\t\t\tactionGroup = None\n" + active + "\t\t}\n\t}\n\tRESOURCE");
        var reg = RoundtripVolatileKeys.Default()
            .Add(new VolatileKeyEntry { Module = "ModuleX", KeyPath = "originalPos", Rule = VolatileRule.RootRelativeVector, EvidenceRef = "t" })
            .Add(new VolatileKeyEntry { Module = "ACTIONS", KeyPath = "Foo/active", Rule = VolatileRule.AbsentEqualsDefault, DefaultValue = "False", EvidenceRef = "t" });
        var a = N(Make("0,15,0", "0,15,0", "hello", "hello", "", "1"));
        // every volatile aspect differs at once: cryo (Ignore), shifted root with shifted vector (RootRelativeVector), active present (AbsentEqualsDefault)
        var b = N(Make("5,15,0", "5,15,0", "hello", "hello", "\t\t\tactive = False\n", "999"));
        var cmp = CraftComparator.Compare(a, b, new ComparatorOptions { Registry = reg });
        Assert.IsTrue(cmp.Equal, string.Join("\n", cmp.Differences));
        Assert.IsTrue(new[] { "Ignore", "RootRelativeVector", "AbsentEqualsDefault" }.All(r => cmp.ExclusionsApplied.Any(e => e.Rule == r)), string.Join(",", cmp.ExclusionsApplied.Select(e => e.Rule)));
        Assert.AreEqual(CraftFingerprint.Compute(a, reg), CraftFingerprint.Compute(b, reg));
        // HeaderDerived: each side follows its own header description.
        var h1 = N(Make("0,15,0", "0,15,0", "one", "one", "", "1")); var h2 = N(Make("0,15,0", "0,15,0", "two", "two", "", "1"));
        Assert.AreEqual(CraftFingerprint.Compute(h1, reg).Length, 64);
        // The header description is part of the fingerprint on its own; isolate the module copy by dropping that one line.
        string Strip(ConfigNode n) => string.Join("\n", CraftFingerprint.Project(n, reg, null, null, null).Split('\n').Where(l => !l.StartsWith("H|description")));
        Assert.AreEqual(Strip(h1), Strip(h2), "derived copy collapses to <header> in both");
        var stale = N(Make("0,15,0", "0,15,0", "stale", "two", "", "1"));
        Assert.AreNotEqual(Strip(h2), Strip(stale));
        Assert.IsTrue(CraftComparator.Compare(h1, stale, new ComparatorOptions { Registry = reg }).Differences.Any(d => d.Kind == "header_derived_mismatch"));
        // Genuine differences still show in both, and an empty registry separates both.
        var real = N(Make("5,15,0", "9,15,0", "hello", "hello", "", "1"));
        Assert.IsFalse(CraftComparator.Compare(a, real, new ComparatorOptions { Registry = reg }).Equal);
        Assert.AreNotEqual(CraftFingerprint.Compute(a, reg), CraftFingerprint.Compute(real, reg));
        var empty = RoundtripVolatileKeys.Empty();
        Assert.IsFalse(CraftComparator.Compare(a, b, new ComparatorOptions { Registry = empty }).Equal);
        Assert.AreNotEqual(CraftFingerprint.Compute(a, empty), CraftFingerprint.Compute(b, empty));
    }
    [TestMethod] public void FingerprintTreatsOppositeQuaternionsAsOneRotationButKeepsDistinctOnes()
    {
        string With(string rot) => Mini().Replace("\trot = 0,0,0,1\n\tattRot0 = 0,0,0,1\n\tistg = {ISTG}", "x").Replace("\tattPos0 = 0,-1,0\n\trot = 0,0,0,1", "\tattPos0 = 0,-1,0\n\trot = " + rot);
        var half = "0,0.70710678,0,0.70710678"; var neg = "0,-0.70710678,0,-0.70710678"; var other = "0,0.70710678,0,-0.70710678";
        Assert.AreEqual(CraftFingerprint.Compute(N(With(half))), CraftFingerprint.Compute(N(With(neg))));
        Assert.AreNotEqual(CraftFingerprint.Compute(N(With(half))), CraftFingerprint.Compute(N(With(other))), "Ry(90) and Ry(-90) must not collide");
    }
    [TestMethod] public void DefaultRegistryHoldsEvidencedEntries()
    {
        var r = RoundtripVolatileKeys.Default();
        Assert.IsNotNull(r.Find("ModuleCryoTank", "LastUpdateTime"));
        var h = r.Find("RasterPropMonitorComputer", "vesselDescription")!; Assert.AreEqual("description", h.HeaderKey);
        foreach (var a in new[] { "AutostrutOff", "AutostrutRoot", "AutostrutHeaviest", "AutostrutGrandparent" })
        { var e = r.Find("ACTIONS", a + "/active")!; Assert.AreEqual("False", e.DefaultValue); Assert.AreEqual("33-log 18:51", e.EvidenceRef); }
    }

    // ---- real twins ----
    [TestMethod] public void SavedStructuralLoadsEqualTheManualTwinUnderRegistry()
    {
        var twin = Fx.Node("S0a-manual.craft");
        foreach (var f in new[] { "S0a-saved1.craft", "S0a2-saved.craft" })
        {
            var c = CraftComparator.Compare(twin, Fx.Node(f), new ComparatorOptions { TwinMode = true });
            // rot (0,0,0,0 vs 0,0,0,1) and description are real header differences; nothing else may differ.
            CollectionAssert.AreEquivalent(new[] { "description", "rot" }, c.Differences.Select(d => d.Path).ToArray(), f + "\n" + string.Join("\n", c.Differences));
            Assert.IsTrue(c.Differences.All(d => d.PartRef == "HEADER"), f);
            Assert.IsTrue(c.ExclusionsApplied.Any(e => e.KeyPath == "LastUpdateTime"), f);
            Assert.IsTrue(c.ExclusionsApplied.Any(e => e.Rule == "HeaderDerived"), f);
        }
        var s2 = CraftComparator.Compare(twin, Fx.Node("S0a2-saved.craft"), new ComparatorOptions { TwinMode = true });
        Assert.IsTrue(s2.ExclusionsApplied.Any(e => e.KeyPath == "AutostrutOff/active"), "absent active=False is explained");
        // Without the registry the same pair has more differences, so the entries do real work.
        var bare = CraftComparator.Compare(twin, Fx.Node("S0a2-saved.craft"), new ComparatorOptions { TwinMode = true, Registry = RoundtripVolatileKeys.Empty() });
        Assert.IsTrue(bare.TotalDifferences > s2.TotalDifferences);
        // A header made to match leaves a genuinely equal craft.
        var aligned = Fx.Node("S0a2-saved.craft");
        aligned.Entries.RemoveAll(e => e.IsValue && (e.Key == "rot" || e.Key == "description"));
        aligned.Entries.Insert(0, new ConfigEntry("rot", "0,0,0,0")); aligned.Entries.Insert(0, new ConfigEntry("description", twin.First("description")));
        var t2 = Fx.Node("S0a-manual.craft");
        var eq = CraftComparator.Compare(t2, aligned, new ComparatorOptions { TwinMode = true });
        Assert.IsTrue(eq.Differences.All(d => d.Path != "rot" && d.Path != "description"), string.Join("\n", eq.Differences));
        var changed = Fx.Node("S0a2-saved.craft");
        changed.Children("PART").Last().Entries.RemoveAll(e => e.IsValue && e.Key == "istg");
        changed.Children("PART").Last().AddValue("istg", "3");
        Assert.IsTrue(CraftComparator.Compare(twin, changed, new ComparatorOptions { TwinMode = true }).Differences.Any(d => d.Path == "istg"));
    }
    [TestMethod] public void SameFileWithCidMappingIsEqualAndHeaderMattersOutsideTwinMode()
    {
        var a = Fx.Node("S0a2-saved.craft");
        var c = CraftComparator.Compare(a, Fx.Node("S0a2-saved.craft")); Assert.IsTrue(c.Equal); Assert.AreEqual("craftId", c.Mapping);
        var d = CraftComparator.Compare(Fx.Node("S0a-manual.craft"), a);
        Assert.IsFalse(d.Equal); Assert.IsTrue(d.Differences.Any(x => x.Path == "ship"));
    }

    // ---- fingerprint ----
    [TestMethod] public void FingerprintIgnoresVolatilesIdsAndOrder()
    {
        var f = CraftFingerprint.Compute(N(Mini()));
        Assert.AreEqual(64, f.Length);
        Assert.AreEqual(f, CraftFingerprint.Compute(N(Mini(D(("CRYO", "5"))))));
        Assert.AreEqual(f, CraftFingerprint.Compute(N(Mini(D(("A", "9"), ("B", "10"), ("PA", "1"), ("PB", "2"))))));
        Assert.AreEqual(f, CraftFingerprint.Compute(N(Mini(D(("POS", "3,18,3"), ("TANKPOS", "3,17,3"))))), "absolute position excluded");
        var b = N(Mini()); var parts = b.Entries.Where(e => !e.IsValue).ToList(); b.Entries.Remove(parts[0]); b.Entries.Add(parts[0]);
        Assert.AreEqual(f, CraftFingerprint.Compute(b));
        Assert.AreNotEqual(f, CraftFingerprint.Compute(N(Mini(D(("ISTG", "1"))))));
        Assert.AreNotEqual(f, CraftFingerprint.Compute(N(Mini(D(("FUEL", "10"))))));
        Assert.AreNotEqual(f, CraftFingerprint.Compute(N(Mini(D(("TANKPOS", "0,13,0"))))));
        Assert.AreNotEqual(f, CraftFingerprint.Compute(N(Mini(D(("CRYO", "5")))), RoundtripVolatileKeys.Empty()));
    }
    [TestMethod] public void FingerprintIncludesUiFieldsAndRegistryMatchesComparator()
    {
        var n = N(Mini());
        var plain = CraftFingerprint.Compute(n);
        var named = CraftFingerprint.Compute(n, null, "name", "desc", "flag");
        Assert.AreNotEqual(plain, named);
        Assert.AreNotEqual(named, CraftFingerprint.Compute(n, null, "name2", "desc", "flag"));
        Assert.AreNotEqual(named, CraftFingerprint.Compute(n, null, "name", "desc2", "flag"));
        Assert.AreNotEqual(named, CraftFingerprint.Compute(n, null, "name", "desc", "flag2"));
        Assert.AreEqual(named, CraftFingerprint.Compute(N(Mini(D(("CRYO", "77")))), null, "name", "desc", "flag"));
        var twin = Fx.Node("S0a-manual.craft");
        Assert.AreEqual(CraftFingerprint.Compute(twin), CraftFingerprint.Compute(Fx.Node("S0a-manual.craft")));
    }
}
