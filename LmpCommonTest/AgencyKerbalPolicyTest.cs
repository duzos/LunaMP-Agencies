using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyKerbalPolicyTest
    {
        [DataTestMethod]
        [DataRow(false, false, false, false, false)]
        [DataRow(false, false, false, true, false)]
        [DataRow(false, false, true, false, true)]
        [DataRow(false, false, true, true, true)]
        [DataRow(false, true, false, false, false)]
        [DataRow(false, true, false, true, false)]
        [DataRow(false, true, true, false, true)]
        [DataRow(false, true, true, true, false)]
        [DataRow(true, false, false, false, false)]
        [DataRow(true, false, false, true, false)]
        [DataRow(true, false, true, false, false)]
        [DataRow(true, false, true, true, false)]
        [DataRow(true, true, false, false, false)]
        [DataRow(true, true, false, true, false)]
        [DataRow(true, true, true, false, false)]
        [DataRow(true, true, true, true, false)]
        public void SeedDecisionPreservesExistingRostersAndLegacyDefaults(bool rosterExists, bool isNewAgency, bool perAgencyEnabled, bool zeroStartingEnabled, bool expected)
        {
            Assert.AreEqual(expected, AgencyKerbalPolicy.ShouldSeedDefaults(rosterExists, isNewAgency, perAgencyEnabled, zeroStartingEnabled));
        }
    }
}
