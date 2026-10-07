using KspControl.Bridge;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.BridgeTests
{
    [TestClass] public class PartFiltersTests
    {
        [DataTestMethod]
        [DataRow(false, "start", false, true)]
        [DataRow(true, "start", false, false)]
        [DataRow(false, "", false, false)]
        [DataRow(false, null, false, false)]
        [DataRow(false, "Unresearcheable", false, false)]
        [DataRow(false, "start", true, false)]
        public void BuildableRequiresRealCategoryTechAndVisibility(bool categoryNone, string tech, bool hidden, bool expected)
            => Assert.AreEqual(expected, PartFilters.IsBuildable(categoryNone, tech, hidden));
    }
}
