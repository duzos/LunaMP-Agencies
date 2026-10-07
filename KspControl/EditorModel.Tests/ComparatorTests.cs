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
        Assert.IsTrue(twin.Equal, string.Join("\n", twin.Differences));
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
            Assert.IsTrue(c.Equal, f + "\n" + string.Join("\n", c.Differences));
            Assert.IsTrue(c.ExclusionsApplied.Any(e => e.KeyPath == "LastUpdateTime"), f);
            Assert.IsTrue(c.ExclusionsApplied.Any(e => e.Rule == "HeaderDerived"), f);
        }
        var s2 = CraftComparator.Compare(twin, Fx.Node("S0a2-saved.craft"), new ComparatorOptions { TwinMode = true });
        Assert.IsTrue(s2.ExclusionsApplied.Any(e => e.KeyPath == "AutostrutOff/active"), "absent active=False is explained");
        // Without the registry the same pair differs, so the entries are doing real work.
        Assert.IsFalse(CraftComparator.Compare(twin, Fx.Node("S0a2-saved.craft"), new ComparatorOptions { TwinMode = true, Registry = RoundtripVolatileKeys.Empty() }).Equal);
        // And a genuinely changed twin is caught.
        var changed = Fx.Node("S0a2-saved.craft");
        changed.Children("PART").Last().Entries.RemoveAll(e => e.IsValue && e.Key == "istg");
        changed.Children("PART").Last().AddValue("istg", "3");
        Assert.IsFalse(CraftComparator.Compare(twin, changed, new ComparatorOptions { TwinMode = true }).Equal);
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
