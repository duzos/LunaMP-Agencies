using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;

[TestClass]
public class FingerprintHelpersTests
{
    private static ConfigNode Craft(string zero) => ConfigText.Parse("ship = A\nPART\n{\n part = a_1\n pos = " + zero + ",1,0\n x = " + zero + "\n}\n");

    [TestMethod] public void ComputeIsTheHashOfTheProjectionWithItsUiPrefix()
    {
        var craft = Craft("0"); var registry = RoundtripVolatileKeys.Default();
        var body = CraftFingerprint.Project(craft, registry, null, null, null);
        Assert.AreEqual(CraftFingerprint.Compute(craft, registry, "N", "D", "F"), CraftFingerprint.HashProjection(CraftFingerprint.UiPrefix("N", "D", "F") + body));
        Assert.AreEqual(CraftFingerprint.Compute(craft, registry), CraftFingerprint.HashProjection(body));
        Assert.AreEqual("", CraftFingerprint.UiPrefix(null, null, null));
        Assert.AreNotEqual("", CraftFingerprint.UiPrefix("", null, null), "an empty name is still a supplied UI field");
    }

    [TestMethod] public void NegativeZeroFingerprintsAsZeroBecauseFrameworkAndCorePrintItDifferently()
    {
        Assert.AreEqual(CraftFingerprint.Compute(Craft("0")), CraftFingerprint.Compute(Craft("-0")));
        Assert.AreEqual(CraftFingerprint.Compute(Craft("0")), CraftFingerprint.Compute(Craft("-0.0")));
    }
}
