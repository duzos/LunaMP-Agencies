using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace LmpCommonTest
{
    [TestClass]
    public class ToolingDesignNamesTest
    {
        private const string Craft = "ship = Mun Flyer 1\nversion = 1.12.5\ndescription = A ship = not this\ntype = VAB\nPART\n{\n\tpart = probeCoreOcto_4294\n\tship = Inner\n\tMODULE\n\t{\n\t\tname = ModuleInventoryPart\n\t\tSTOREDPARTS\n\t\t{\n\t\t\tSTOREDPART\n\t\t\t{\n\t\t\t\tPART\n\t\t\t\t{\n\t\t\t\t\tpart = evaJetpack_1\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\nPART\n{\n\tpart = fuelTank_12\n}\nPART\n{\n\tpart = fuelTank_13\n}\n";

        [TestMethod]
        public void SanitizeTrimsCollapsesAndCuts()
        {
            Assert.IsNull(ToolingDesignNames.Sanitize(null));
            Assert.IsNull(ToolingDesignNames.Sanitize("  \t\u0001 "));
            Assert.AreEqual("My Probe", ToolingDesignNames.Sanitize("  My \r\n Probe  "));
            Assert.AreEqual(new string('a', ToolingDesignNames.MaxLength), ToolingDesignNames.Sanitize(new string('a', 300)));
            // Never splits a surrogate pair at the cut.
            var emoji = new string('a', ToolingDesignNames.MaxLength - 1) + "\U0001F680";
            var cut = ToolingDesignNames.Sanitize(emoji);
            Assert.IsFalse(char.IsHighSurrogate(cut[cut.Length - 1]));
        }

        [TestMethod]
        public void ShipNameIsTheTopLevelValueOnly()
        {
            Assert.AreEqual("Mun Flyer 1", ToolingDesignNames.ShipNameFromCraft(Craft));
            Assert.AreEqual("Mun Flyer 1", ToolingDesignNames.ShipNameFromCraft(Encoding.UTF8.GetBytes(Craft.Replace("\n", "\r\n"))));
            Assert.IsNull(ToolingDesignNames.ShipNameFromCraft("type = VAB\nPART\n{\nship = Inner\n}\n"));
            Assert.IsNull(ToolingDesignNames.ShipNameFromCraft("ship = \ntype = VAB\n"));
            Assert.IsNull(ToolingDesignNames.ShipNameFromCraft((byte[])null));
            Assert.AreEqual(ToolingDesignNames.MaxLength, ToolingDesignNames.ShipNameFromCraft("ship = " + new string('z', 500)).Length);
        }

        [TestMethod]
        public void CraftFingerprintMatchesTheToolingFingerprintOfItsPhysicalParts()
        {
            var manifest = new ToolingManifest
            {
                Parts = new[] { new ToolingPart { Name = "fuelTank", UnitCost = 5 }, new ToolingPart { Name = "probeCoreOcto", UnitCost = 9 }, new ToolingPart { Name = "fuelTank", UnitCost = 5 } },
                Cargo = new[] { new ToolingCargo { Name = "evaJetpack", Count = 1, UnitCost = 1, ContainerPartIndex = 1 } }
            };
            CollectionAssert.AreEqual(new[] { "probeCoreOcto_4294", "fuelTank_12", "fuelTank_13" }, ToolingDesignNames.PartIdsFromCraft(Craft));
            Assert.AreEqual(ToolingPolicy.Fingerprint(manifest), ToolingDesignNames.CraftFingerprint(Craft), "Stored inventory parts are not physical parts.");
            Assert.IsNull(ToolingDesignNames.CraftFingerprint("ship = Empty\ntype = VAB\n"));
            var probeOnly = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "fuelTank" }, new ToolingPart { Name = "fuelTank" } } };
            Assert.AreEqual(ToolingPolicy.Fingerprint(probeOnly), ToolingDesignNames.CraftFingerprint(Craft, n => n == "probeCoreOcto"), "Names the classifier marks as science are left out.");
        }

        [TestMethod]
        public void ResolvePicksTheFirstUsableNameAndFallsBackToTheFingerprint()
        {
            Assert.AreEqual("Blueprint", ToolingDesignNames.Resolve(null, "  ", "Blueprint", "Local"));
            Assert.AreEqual("Stored", ToolingDesignNames.Resolve("Stored", "Blueprint"));
            Assert.IsNull(ToolingDesignNames.Resolve(null, ""));
            Assert.AreEqual("Design 436dada0", ToolingDesignNames.Fallback("436dada0ffeeddcc"));
            Assert.AreEqual("abc", ToolingDesignNames.ShortFingerprint("abc"));
        }
    }
}
