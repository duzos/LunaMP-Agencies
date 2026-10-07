using System.Text;
using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

[TestClass]
public class ConfigTextTests
{
    [TestMethod]
    public void FixturesRoundTripByteForByte()
    {
        foreach (var f in new[] { "S0a-manual.craft", "S0a-saved1.craft", "S0a2-saved.craft" })
        {
            var text = Fx.Text(f);
            var node = ConfigText.Parse(text);
            Assert.AreEqual(text, ConfigText.Print(node, text.Contains("\r\n") ? "\r\n" : "\n"), f);
            Assert.AreEqual(3, node.Children("PART").Count());
        }
    }
    [TestMethod]
    public void ParsesLfCommentsInlineBraceAndEmptyValues()
    {
        var n = ConfigText.Parse("// c\na = 1 // tail\nurl = http://x/y\nempty =\nNODE {\n k = v = w\n INNER\n {\n }\n}\n");
        Assert.AreEqual("1", n.First("a")); Assert.AreEqual("http://x/y", n.First("url")); Assert.AreEqual("", n.First("empty"));
        var node = n.Children("NODE").Single(); Assert.AreEqual("v = w", node.First("k")); Assert.AreEqual(1, node.Children("INNER").Count());
        var printed = ConfigText.Print(n, "\n");
        Assert.AreEqual(printed, ConfigText.Print(ConfigText.Parse(printed), "\n"), "idempotent");
    }
    [TestMethod]
    public void BomIsStrippedAndInlineBracesRejected()
    {
        Assert.AreEqual("1", ConfigText.Parse("\uFEFFa = 1\n").First("a"));
        Assert.AreEqual("inline_braces", Assert.ThrowsException<ConfigParseException>(() => ConfigText.Parse("NAME { k = v }\n")).Code);
        Assert.ThrowsException<ConfigParseException>(() => ConfigText.Parse("NAME { }\n"));
        Assert.AreEqual("{x}", ConfigText.Parse("k = {x}\n").First("k"));
    }
    [TestMethod]
    public void MalformedAndBoundsRejected()
    {
        foreach (var bad in new[] { "}\n", "A\n{\n", "A\nB\n", "{\n}\n", " = 3\n" })
            Assert.ThrowsException<ConfigParseException>(() => ConfigText.Parse(bad), bad);
        string Nest(int d) { var sb = new StringBuilder(); for (int i = 0; i < d; i++) sb.Append("N\n{\n"); for (int i = 0; i < d; i++) sb.Append("}\n"); return sb.ToString(); }
        ConfigText.Parse(Nest(32));
        Assert.AreEqual("config_too_deep", Assert.ThrowsException<ConfigParseException>(() => ConfigText.Parse(Nest(33))).Code);
        Assert.AreEqual("config_too_large", Assert.ThrowsException<ConfigParseException>(() => ConfigText.Parse("k = " + new string('x', 4 * 1024 * 1024))).Code);
        ConfigText.Parse("k = " + new string('x', 4 * 1024 * 1024 - 4));
    }
    [TestMethod]
    public void InstalledStockCraftParsesAndPrintsStably()
    {
        var dir = @"D:\SteamLibrary\steamapps\common\Kerbal Space Program\Ships\VAB";
        if (!Directory.Exists(dir)) Assert.Inconclusive("stock craft not installed");
        var files = Directory.GetFiles(dir, "*.craft").Take(5).ToList();
        if (files.Count == 0) Assert.Inconclusive("no stock craft");
        foreach (var f in files)
        {
            var node = ConfigText.Parse(File.ReadAllText(f, Encoding.Latin1));
            var once = ConfigText.Print(node);
            Assert.AreEqual(once, ConfigText.Print(ConfigText.Parse(once)), f);
        }
    }
}

[TestClass]
public class ValidatorTests
{
    static ConfigNode Good() => CraftPlanner.Plan(Fx.TwinGraph(), Fx.TwinCatalog(), new PlannerOptions { ModVersions = "a=1" }).Craft!.ToConfigNode();
    static ConfigNode Edit(Func<string, string> f) => ConfigText.Parse(f(ConfigText.Print(Good(), "\n")));
    static void Has(ConfigNode n, string code, ConstructionCatalog? c = null)
    { var issues = StructuralCraftValidator.Validate(n, c); Assert.IsTrue(issues.Any(i => i.Code == code), code + " in: " + string.Join("; ", issues)); }

    [TestMethod] public void PlannedCraftIsValid() { Assert.AreEqual(0, StructuralCraftValidator.Validate(Good(), Fx.TwinCatalog()).Count, string.Join(";", StructuralCraftValidator.Validate(Good(), Fx.TwinCatalog()))); }
    [TestMethod] public void DanglingReferences()
    {
        Has(Edit(t => t.Replace("link = fuelTankSmall_100001", "link = fuelTankSmall_424242")), "dangling_link");
        Has(Edit(t => t.Replace("attN = top,mk1pod.v2_100000", "attN = top,mk1pod.v2_424242")), "dangling_attn");
    }
    [TestMethod] public void UnknownAttachNode() { Has(Edit(t => t.Replace("attN = bottom,fuelTankSmall", "attN = sideways,fuelTankSmall")), "unknown_attach_node", Fx.TwinCatalog()); }
    [TestMethod] public void DuplicateCid() { Has(Edit(t => t.Replace("part = fuelTankSmall_100001", "part = fuelTankSmall_100000")), "duplicate_cid"); }
    [TestMethod] public void DoublyOccupiedNode()
    {
        Has(Edit(t => t.Replace("attN = top,mk1pod.v2_100000", "attN = bottom,mk1pod.v2_100000")), "node_doubly_occupied");
    }
    [TestMethod] public void CycleAndRoots()
    {
        var cyc = Edit(t => t.Replace("part = mk1pod.v2_100000", "part = mk1pod.v2_100000\n\tlink = liquidEngine.v2_100002"));
        var codes = StructuralCraftValidator.Validate(cyc).Select(i => i.Code).ToList();
        Assert.IsTrue(codes.Contains("cycle") || codes.Contains("multiple_parents"), string.Join(",", codes));
        var loop = ConfigText.Parse(ConfigText.Print(Good(), "\n").Replace("\tattN = bottom,liquidEngine.v2_100002", "\tlink = mk1pod.v2_100000\n\tattN = bottom,liquidEngine.v2_100002"));
        Has(loop, "cycle");
    }
    [TestMethod] public void SurfaceOntoNonAcceptingParent()
    {
        var g = Fx.TwinGraph(); g.Parts.Add(new() { Id = "fin", Part = "basicFin", Parent = "tank", Surface = new() { HeightOffset = 0, AngleDegrees = 0 } });
        var cat = Fx.TwinCatalog();
        var layout = CraftPlanner.Layout(g, cat, new PlannerOptions(), new List<PlanIssue>())!;
        Assert.IsNotNull(layout);
        var text = ConfigText.Print(Good(), "\n").Replace("link = liquidEngine.v2_100002", "link = liquidEngine.v2_100002\n\tlink = basicFin_100003")
            + "PART\n{\n\tpart = basicFin_100003\n\tpos = 0,14,0.6\n\trot = 0,0,0,1\n\tattPos0 = 0,0,0.6\n\tattRot0 = 0,0,0,1\n\tsrfN = srfAttach,fuelTankSmall_100001\n}\n";
        Assert.AreEqual(0, StructuralCraftValidator.Validate(ConfigText.Parse(text), cat).Count);
        cat.Parts["fuelTankSmall"].AttachRules.AllowSrf = false;
        Has(ConfigText.Parse(text), "surface_parent_not_accepting", cat);
    }
    [TestMethod] public void AsymmetricSym()
    {
        var text = ConfigText.Print(Good(), "\n").Replace("part = fuelTankSmall_100001", "part = fuelTankSmall_100001\n\tsym = liquidEngine.v2_100002");
        Has(ConfigText.Parse(text), "asymmetric_sym");
    }
    [TestMethod] public void ModulesAndOtherNodesForbidden()
    {
        var n = Good(); n.Children("PART").First().AddNode("MODULE").AddValue("name", "X");
        Has(n, "module_in_structural_craft");
        n = Good(); n.Children("PART").First().AddNode("RESOURCE");
        Has(n, "forbidden_node_in_structural_craft");
    }
    [TestMethod] public void NamesControlCharsAndNumbers()
    {
        Has(Edit(t => t.Replace("part = fuelTankSmall_100001", "part = fuel_Tank_100001").Replace("link = fuelTankSmall_100001", "link = fuel_Tank_100001").Replace("part = fuelTankSmall_100001", "part = fuel_Tank_100001")), "part_name_underscore");
        var n = Good(); n.Children("PART").First().AddValue("note", "bad\u0007char");
        Has(n, "control_character");
        Has(Edit(t => t.Replace("pos = 0,15,0", "pos = NaN,15,0")), "non_finite_number");
        Has(Edit(t => t.Replace("pos = 0,15,0", "pos = 0,Infinity,0")), "non_finite_number");
        Has(Edit(t => t.Replace("pos = 0,15,0", "pos = 0,1e999,0")), "non_finite_number");
        Has(Edit(t => t.Replace("pos = 0,15,0", "pos = 0,abc,0")), "invalid_number");
        Has(Edit(t => t.Replace("istg = 0", "istg = zero")), "invalid_integer");
    }
    [TestMethod] public void AttachmentPartnersMustMatchTheLinkTree()
    {
        Has(Edit(t => t.Replace("attN = bottom,fuelTankSmall_100001", "attN = bottom,liquidEngine.v2_100002")), "attach_partner_not_linked");
        var g = Fx.TwinGraph(); g.Parts.Add(new() { Id = "fin", Part = "basicFin", Parent = "tank", Surface = new() { AngleDegrees = 0 } });
        var cat = Fx.TwinCatalog(); var good = ConfigText.Print(Good(), "\n")
            .Replace("link = liquidEngine.v2_100002", "link = liquidEngine.v2_100002\n\tlink = basicFin_100003")
            + "PART\n{\n\tpart = basicFin_100003\n\tpos = 0,14,0.6\n\trot = 0,0,0,1\n\tattPos0 = 0,0,0.6\n\tattRot0 = 0,0,0,1\n\tsrfN = srfAttach,fuelTankSmall_100001\n}\n";
        Assert.AreEqual(0, StructuralCraftValidator.Validate(ConfigText.Parse(good), cat).Count(i => i.Code == "attach_partner_not_linked"), "srfN names a part that does not link it? " + string.Join(";", StructuralCraftValidator.Validate(ConfigText.Parse(good), cat)));
    }
    [TestMethod] public void ModVersionsHeaderRequired()
    {
        var n = Good(); n.Entries.RemoveAll(e => e.IsValue && e.Key == "_modVersions");
        Has(n, "missing_mod_versions");
        var noMods = CraftPlanner.Plan(Fx.TwinGraph(), Fx.TwinCatalog()).Craft!.ToConfigNode();
        Has(noMods, "missing_mod_versions");
    }
    [TestMethod] public void GraphNameRejectsConfigSyntax()
    {
        foreach (var bad in new[] { "a//b", "a{b", "a}b", "a\nb" })
        { var g = Fx.TwinGraph(); g.Name = bad; Assert.IsTrue(CraftPlanner.Plan(g, Fx.TwinCatalog()).Issues.Any(i => i.Code == "invalid_craft_name"), bad); }
    }
    [TestMethod] public void Oversize()
    {
        var n = new ConfigNode("");
        for (int i = 0; i < 251; i++) n.AddNode("PART").AddValue("part", "a_" + (100000 + i));
        Has(n, "too_many_parts");
        Has(new ConfigNode(""), "no_parts");
    }
}

[TestClass]
public class PathsTests
{
    static string Root => OperatingSystem.IsWindows() ? @"C:\Games\KSP" : "/games/ksp";
    static CraftPaths Make(Func<string, bool>? reparse = null) => new(Root, "Sandbox", reparse);
    [TestMethod] public void ValidNewShipResolvesFlatInShips()
    {
        var r = Make().ResolveNewShip("VAB", "My Rocket-1.v2");
        Assert.IsTrue(r.Ok, r.ReasonCode);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Root, "saves", "Sandbox", "Ships", "VAB", "My Rocket-1.v2.craft")), r.FullPath);
    }
    [TestMethod] public void TraversalAbsoluteAndSeparatorsRejected()
    {
        var p = Make();
        foreach (var bad in new[] { "..", "../x", "a/b", @"a\b", "..\\..\\x", "/etc/passwd", @"C:\x", "a..b", ".hidden", "x.", "x ", "", new string('a', 65), "a:b", "a\u0000b", "CON", "nul.txt" })
            Assert.IsFalse(p.ResolveNewShip("VAB", bad).Ok, bad);
        foreach (var bad in new[] { @"..\x.craft", "../x.craft", "sub/x.craft", @"C:\x.craft", "/abs.craft" })
            Assert.AreEqual("path_outside_save", p.ResolveExistingShip("VAB", bad).ReasonCode, bad);
        Assert.IsFalse(p.ResolveNewShip("VAB", null!).Ok);
    }
    [TestMethod] public void ExtensionAndFacility()
    {
        var p = Make();
        Assert.AreEqual("invalid_extension", p.ResolveExistingShip("VAB", "x.txt").ReasonCode);
        Assert.AreEqual("invalid_extension", p.ResolveExistingShip("VAB", "x.craft.original").ReasonCode);
        Assert.IsTrue(p.ResolveExistingShip("VAB", "x.craft").Ok);
        Assert.IsTrue(p.ResolveExistingShip("SPH", "x.craft").Ok);
        Assert.AreEqual("facility_mismatch", p.ResolveExistingShip("Launchpad", "x.craft").ReasonCode);
        Assert.AreEqual("facility_mismatch", p.ResolveNewShip("../VAB", "x").ReasonCode);
        Assert.AreEqual("invalid_file_name", p.ResolveNewShip("VAB", "x.craft").ReasonCode);
    }
    [TestMethod] public void ReparsePointsOnFileOrAncestorsRejected()
    {
        var ships = Path.Combine(Root, "saves", "Sandbox", "Ships");
        foreach (var hit in new[] { Path.Combine(ships, "VAB", "x.craft"), Path.Combine(ships, "VAB"), ships, Path.Combine(Root, "saves", "Sandbox"), Path.Combine(Root, "saves") })
        {
            var p = Make(path => string.Equals(path.TrimEnd('\\', '/'), hit, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("reparse_point", p.ResolveExistingShip("VAB", "x.craft").ReasonCode, hit);
        }
        Assert.IsTrue(Make(path => string.Equals(path, Root, StringComparison.OrdinalIgnoreCase)).ResolveExistingShip("VAB", "x.craft").Ok, "above saves is not checked");
    }
    [TestMethod] public void Sanitising()
    {
        Assert.AreEqual("My_Rocket", CraftPaths.Sanitize("My/Rocket"));
        Assert.AreEqual("craft", CraftPaths.Sanitize("..."));
        Assert.AreEqual("craft", CraftPaths.Sanitize(null!));
        Assert.AreEqual("a.b", CraftPaths.Sanitize("a..b"));
        Assert.AreEqual("x", CraftPaths.Sanitize("  .x"));
        Assert.AreEqual(64, CraftPaths.Sanitize(new string('z', 100)).Length);
        Assert.AreEqual("craft", CraftPaths.Sanitize("CON"));
        foreach (var s in new[] { "a\\b", "\u202etxt", "tab\t", "emoji\U0001F680", "a:b*c?" }) Assert.IsTrue(CraftPaths.IsName(CraftPaths.Sanitize(s)), s);
    }
    [TestMethod] public void WorkspacePaths()
    {
        var p = Make();
        var s = p.StagingPath("req_12345678"); Assert.IsTrue(s.Ok);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Root, "KspControlData", "Sandbox", "staging", "kc-req_12345678.craft")), s.FullPath);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(Root, "KspControlData", "Sandbox", "recovery", "kc-snap-snapshot01.craft")), p.RecoveryPath("snapshot01").FullPath);
        Assert.IsTrue(p.UpgradedStagingPath("req_12345678").FullPath!.EndsWith("kc-req_12345678.upgraded.craft"));
        foreach (var bad in new[] { "short", "../../../evil1", "a b c d e f g h", "x/yzzzzzzz", null!, new string('a', 129) })
        { Assert.IsFalse(p.StagingPath(bad).Ok, bad); Assert.IsFalse(p.RecoveryPath(bad).Ok, bad); }
        Assert.IsTrue(p.StagingPath("req_12345678").FullPath!.StartsWith(Path.Combine(Root, "KspControlData")));
        Assert.AreEqual("reparse_point", Make(x => x.EndsWith("staging")).StagingPath("req_12345678").ReasonCode);
        Assert.IsTrue(p.SuspensionsFile.EndsWith("suspensions.json")); Assert.IsTrue(p.LedgerFile.EndsWith("ledger.json"));
    }
    [TestMethod] public void ConstructorRejectsBadRoots()
    {
        Assert.ThrowsException<ArgumentException>(() => new CraftPaths("relative/path", "Sandbox", null));
        Assert.ThrowsException<ArgumentException>(() => new CraftPaths(Root, "../x", null));
    }
}

[TestClass]
public class TokenTests
{
    const string Fp = "0123456789abcdef0123456789abcdef";
    [TestMethod] public void RoundTripAndFormat()
    {
        var t = EditorRevisionToken.Create("epoch-1", 3, 42, Fp);
        var s = t.Encode();
        Assert.IsTrue(s.Length <= 128); Assert.IsFalse(s.Contains('=') || s.Contains('+') || s.Contains('/'));
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4)));
        Assert.AreEqual("v1|epoch-1|3|42|0123456789ab", raw);
        Assert.IsTrue(EditorRevisionToken.TryParse(s, out var p));
        Assert.AreEqual("epoch-1", p!.WorldEpoch); Assert.AreEqual(3, p.Generation); Assert.AreEqual(42, p.EditRevision); Assert.AreEqual("0123456789ab", p.FingerprintPrefix);
        Assert.IsTrue(p.Matches("epoch-1", 3, 42, Fp)); Assert.IsFalse(p.Matches("epoch-1", 3, 43, Fp)); Assert.IsFalse(p.Matches("epoch-1", 3, 42, "ffffffffffffffff"));
    }
    static string B64(string raw) => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    [TestMethod] public void ForgedOrMalformedRejected()
    {
        foreach (var bad in new[] { "", null!, "!!!", new string('A', 129), B64("v2|e|1|1|0123456789ab"), B64("v1|e|1|1"), B64("v1|e|1|1|0123456789ab|x"),
            B64("v1|e|-1|1|0123456789ab"), B64("v1|e|01|1|0123456789ab"), B64("v1|e|1|1|0123456789a"), B64("v1|e|1|1|0123456789abc"), B64("v1|e|1|1|0123456789AB"), B64("v1|e|x|1|0123456789ab"), B64("v1||1|1|0123456789ab"),
            B64("v1|e|1|99999999999999999999|0123456789ab"), B64("v1|e|1|1|0123456789ab") + "=", B64("v1|e|1|1|0123456789ab").Replace('-', '+') + "A" })
            Assert.IsFalse(EditorRevisionToken.TryParse(bad, out _), bad);
        // Non-canonical base64 (trailing bits flipped) is rejected.
        var good = EditorRevisionToken.Create("e", 1, 1, Fp).Encode();
        Assert.IsTrue(EditorRevisionToken.TryParse(good, out _));
        // Non-canonical base64: flip an unused trailing bit of an unpadded token. The bytes decode identically but the text is not what Encode produces.
        string tailEpoch = null!;
        foreach (var e in new[] { "e", "ee", "eee", "eeee" }) { var t = EditorRevisionToken.Create(e, 1, 1, Fp).Encode(); if (t.Length % 4 != 0) { tailEpoch = e; break; } }
        var token = EditorRevisionToken.Create(tailEpoch, 1, 1, Fp).Encode();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var forged = token[..^1] + alphabet[alphabet.IndexOf(token[^1]) ^ 1];
        Assert.AreNotEqual(token, forged);
        Func<string, byte[]> raw64 = s2 => Convert.FromBase64String(s2.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s2.Length % 4) % 4));
        CollectionAssert.AreEqual(raw64(token), raw64(forged), "same decoded bytes");
        Assert.IsFalse(EditorRevisionToken.TryParse(forged, out _));
        // Encoded size is bounded for every valid input.
        var max = EditorRevisionToken.Create(new string('e', 40), long.MaxValue, long.MaxValue, Fp).Encode();
        Assert.IsTrue(max.Length <= 128 && EditorRevisionToken.TryParse(max, out _));
        Assert.ThrowsException<ArgumentException>(() => EditorRevisionToken.Create(new string('e', 41), 1, 1, Fp));
        Assert.ThrowsException<ArgumentException>(() => EditorRevisionToken.Create("a|b", 1, 1, Fp));
        Assert.ThrowsException<ArgumentException>(() => EditorRevisionToken.Create("e", -1, 1, Fp));
        Assert.ThrowsException<ArgumentException>(() => EditorRevisionToken.Create("e", 1, 1, "short"));
    }
}
