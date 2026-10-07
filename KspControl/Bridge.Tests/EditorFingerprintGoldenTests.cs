using System;
using System.IO;
using KspControl.Bridge;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>
    /// The fingerprint is computed by the linked EditorModel sources inside the net472 bridge (KSP's Mono) and by the net10 tests.
    /// These constants were produced once on net10 and must hold on both runtimes: any invariant-formatting drift (G9, 0.### rounding,
    /// culture) between runtimes would change the hash and make every token stale.
    /// </summary>
    [TestClass]
    public class EditorFingerprintGoldenTests
    {
        private static string Fixture()
        { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "S0a-manual.craft")); }

        // Tricky numerics: negative zero, float-rounded vectors, exponent forms, many digits, and quaternions that need sign canonicalisation.
        private const string Tricky =
            "ship = Tricky\ndescription = a¨b\ntype = VAB\nrot = 0,0,0,0\n" +
            "PART\n{\n part = root_100000\n pos = -0,15.0000001,1e-7\n attPos0 = 0,15,0\n rot = -0.7071068,0,0,-0.7071068\n istg = -1\n" +
            " attN = bottom,child_100001_0|-0.4050379|0.1234567891\n link = child_100001\n MODULE\n {\n  name = ModuleX\n  amount = 0.30000001\n  big = 123456789.123456789\n  neg = -0\n  flag = True\n }\n}\n" +
            "PART\n{\n part = child_100001\n pos = 0,14.2,0\n attPos0 = 0,-0.8,0\n rot = 0,0,0,1\n istg = 2\n attN = top,root_100000_0|0.4050379|0\n}\n";

        [TestMethod] public void StockStyleCraftHasTheSameFingerprintOnEveryRuntime()
        {
            var craft = Pure.ConfigText.Parse(Fixture());
            Assert.AreEqual("e57b0dcf27711331f3daaa35a0b2e074b4752952979d535ae386b71a0bbe426d", Pure.CraftFingerprint.Compute(craft));
            Assert.AreEqual("2642dd573aaa44197bce0ca406b4fdfbceef0bb60477d680e335721be131708e", Pure.CraftFingerprint.Compute(craft, null, "Name", "Description", "Squad/Flags/default"));
        }

        [TestMethod] public void TrickyNumericsHaveTheSameFingerprintOnEveryRuntime()
        {
            var craft = Pure.ConfigText.Parse(Tricky);
            Assert.AreEqual("610ed737ed034582a74bdfae9da0bbe82989a03846efdb9a874b19be01cee87f", Pure.CraftFingerprint.Compute(craft));
        }

        [TestMethod] public void NegativeZeroAndZeroFingerprintTheSameBecauseTheRuntimesPrintThemDifferently()
        {
            var positive = Pure.ConfigText.Parse("PART\n{\n part = a_1\n x = 0\n y = 0.0\n}\n");
            var negative = Pure.ConfigText.Parse("PART\n{\n part = a_1\n x = -0\n y = -0.0\n}\n");
            Assert.AreEqual(Pure.CraftFingerprint.Compute(positive), Pure.CraftFingerprint.Compute(negative));
        }

        [TestMethod] public void ProjectionTextIsRuntimeIndependentForTheFixture()
        {
            var projection = Pure.CraftFingerprint.Project(Pure.ConfigText.Parse(Fixture()), Pure.RoundtripVolatileKeys.Default(), null, null, null);
            Assert.AreEqual(33288, projection.Length);
        }

        [TestMethod] public void UiPrefixAndHashPartsComposeToCompute()
        {
            var craft = Pure.ConfigText.Parse(Fixture());
            var registry = Pure.RoundtripVolatileKeys.Default();
            var body = Pure.CraftFingerprint.Project(craft, registry, null, null, null);
            Assert.AreEqual(Pure.CraftFingerprint.Compute(craft, registry, "N", "D", "F"), Pure.CraftFingerprint.HashProjection(Pure.CraftFingerprint.UiPrefix("N", "D", "F") + body));
            Assert.AreEqual(Pure.CraftFingerprint.Compute(craft, registry), Pure.CraftFingerprint.HashProjection(Pure.CraftFingerprint.UiPrefix(null, null, null) + body));
        }
    }
}
