using System;
using System.Linq;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace LmpCommonTest
{
    [TestClass]
    public class ToolingPolicyTest
    {
        private static ToolingPart Part(string name, double cost, bool science = false) => new ToolingPart { Name = name, UnitCost = cost, IsScience = science };
        private static ToolingManifest Craft(params ToolingPart[] parts) => new ToolingManifest { Parts = parts };
        private static ToolingDesign Design(ToolingManifest manifest, double basis) => new ToolingDesign { Fingerprint = ToolingPolicy.Fingerprint(manifest), Manifest = manifest, ToolingBasis = basis };
        [TestMethod]
        public void FingerprintCountsNamesOnlyAndPriceHashTracksLaunchManifest()
        {
            var first = Craft(Part("tank", 100), Part("engine", 200), Part("tank", 50));
            var second = Craft(Part("tank", 3), Part("tank", 8), Part("engine", 0));
            Assert.AreEqual(ToolingPolicy.Fingerprint(first), ToolingPolicy.Fingerprint(second));
            Assert.AreNotEqual(ToolingPolicy.ManifestHash(first), ToolingPolicy.ManifestHash(second));
            Assert.AreNotEqual(ToolingPolicy.Fingerprint(first), ToolingPolicy.Fingerprint(Craft(Part("tank", 100), Part("engine", 200))));
            Assert.AreNotEqual(ToolingPolicy.Fingerprint(Craft(Part("a|b", 1))), ToolingPolicy.Fingerprint(Craft(Part("a", 1), Part("b", 1))));
        }
        [TestMethod]
        public void CargoContainerAndCrewChangePriceIdentityButNotDesignFingerprint()
        {
            var craft = Craft(Part("pod", 100), Part("box", 20));
            craft.Cargo = new[] { new ToolingCargo { Name = "kit", Count = 1, UnitCost = 10, ContainerPartIndex = 0, CrewName = "Jeb" } };
            var design = ToolingPolicy.Fingerprint(craft); var price = ToolingPolicy.ManifestHash(craft);
            craft.Cargo[0].ContainerPartIndex = 1;
            Assert.AreEqual(design, ToolingPolicy.Fingerprint(craft)); Assert.AreNotEqual(price, ToolingPolicy.ManifestHash(craft));
            price = ToolingPolicy.ManifestHash(craft); craft.Cargo[0].CrewName = "Val";
            Assert.AreNotEqual(price, ToolingPolicy.ManifestHash(craft));
        }
        [TestMethod]        public void ScienceAndCargoStayFullPriceAndExactToolingIsFree()
        {
            var craft = Craft(Part("engine", 100), Part("experiment", 700, true));
            craft.Cargo = new[] { new ToolingCargo { Name = "engine", Count = 2, UnitCost = 100 } };
            var fresh = ToolingPolicy.Quote(craft, Array.Empty<ToolingDesign>(), 10, .1, .1);
            Assert.IsTrue(fresh.Success, fresh.Reason); Assert.AreEqual(1000d, fresh.ToolingCost); Assert.AreEqual(1000d, fresh.LaunchCost);
            var tooled = ToolingPolicy.Quote(craft, new[] { Design(craft, 1000) }, 10, .1, .1);
            Assert.AreEqual(0d, tooled.ToolingCost); Assert.AreEqual(910d, tooled.LaunchCost); Assert.AreEqual(700d, tooled.ScienceCost); Assert.AreEqual(200d, tooled.CargoCost);
        }
        [TestMethod]
        public void CoverConsumesDisjointCountsAndFindsCheapestOrderIndependentCombination()
        {
            var craft = Craft(Part("a", 100), Part("a", 100), Part("b", 100), Part("c", 100));
            var designs = new[] { Design(Craft(Part("a",100),Part("b",100)),2000), Design(Craft(Part("a",100),Part("c",100)),2000), Design(Craft(Part("b",100),Part("c",100)),2000) };
            var quote = ToolingPolicy.Quote(craft, designs, 10, .1, .1);
            Assert.AreEqual(400d, quote.ToolingCost);
            Assert.AreEqual(quote.ToolingCost, ToolingPolicy.Quote(craft, designs.Reverse(), 10, .1, .1).ToolingCost);
            var single = Craft(Part("a",100),Part("b",100),Part("c",100));
            Assert.AreEqual(1200d, ToolingPolicy.Quote(single, designs, 10, .1, .1).ToolingCost);
        }
        [TestMethod]
        public void RepeatedSubassemblyUsesHighestCostMatchingInstancesWithoutDoubleDiscount()
        {
            var small = Craft(Part("a", 1));
            var result = ToolingPolicy.Quote(Craft(Part("a", 100), Part("a", 40), Part("b",10)), new[] { Design(small,10) }, 10,.1,.1);
            Assert.IsTrue(result.Success); Assert.AreEqual(102d,result.ToolingCost); Assert.AreEqual(2,result.Matches.Sum(m=>m.Count));
        }
        [TestMethod]
        public void OverflowAndSearchLimitReturnFailureInsteadOfPartialQuote()
        {
            var overflow = ToolingPolicy.Quote(Craft(Part("a", ToolingPolicy.MaxCost)), Array.Empty<ToolingDesign>(), 10, .1, .1);
            Assert.IsFalse(overflow.Success);
            var many = Craft(Enumerable.Range(0, 300).Select(_ => Part("a", 100)).ToArray());
            var complex = ToolingPolicy.Quote(many, new[] { Design(Craft(Part("a", 100)), 1000) }, 10,.1,.1);
            Assert.IsFalse(complex.Success);
            StringAssert.Contains(complex.Reason, "complex");
        }
        [TestMethod]
        public void RejectsNonfiniteNegativeAndOversizedInputsWithoutCharges()
        {
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1d })
                Assert.IsFalse(ToolingPolicy.Quote(Craft(Part("a", invalid)), Array.Empty<ToolingDesign>(),10,.1,.1).Success);
            Assert.IsFalse(ToolingPolicy.Quote(Craft(Part("a", 1)),Array.Empty<ToolingDesign>(),double.NaN,.1,.1).Success);
            Assert.IsFalse(ToolingPolicy.Quote(Craft(Part("",1)),Array.Empty<ToolingDesign>(),10,.1,.1).Success);
            Assert.IsFalse(ToolingPolicy.Quote(Craft(Enumerable.Repeat(Part("a",1),ToolingPolicy.MaxParts+1).ToArray()),Array.Empty<ToolingDesign>(),10,.1,.1).Success);
        }
    }
}
