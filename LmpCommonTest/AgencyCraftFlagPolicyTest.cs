using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyCraftFlagPolicyTest
    {
        private static readonly Guid Agency = Guid.NewGuid();
        private const string Custom = "LunaMultiPlayer/Flags/esa", Mine = "Squad/Flags/kerbalx";

        private static bool Apply(bool on = true, Guid? agency = null, string flag = Custom, bool installed = true,
            string incoming = "Squad/Flags/default", string player = "Squad/Flags/default", string game = "Squad/Flags/default") =>
            AgencyCraftFlagPolicy.ShouldApply(on, agency ?? Agency, flag, installed, incoming, player, game);

        [TestMethod] public void AppliesForDefaultCraftFlag() => Assert.IsTrue(Apply());
        [TestMethod] public void AppliesForNullOrEmptyIncoming() { Assert.IsTrue(Apply(incoming: null)); Assert.IsTrue(Apply(incoming: "")); }
        [TestMethod] public void AppliesWhenIncomingMatchesPlayerDefaultCaseInsensitive() => Assert.IsTrue(Apply(incoming: "squad/flags/KERBALX", player: Mine));
        [TestMethod] public void AppliesWhenIncomingMatchesGameFlag() => Assert.IsTrue(Apply(incoming: Mine, game: Mine));
        [TestMethod] public void KeepsExplicitPerCraftFlag() => Assert.IsFalse(Apply(incoming: "Squad/Flags/other", player: Mine, game: "Squad/Flags/default"));
        [TestMethod] public void FeatureOffNeverApplies() => Assert.IsFalse(Apply(on: false));
        [TestMethod] public void NoAgencyNeverApplies() => Assert.IsFalse(Apply(agency: Guid.Empty));
        [TestMethod] public void DefaultAgencyFlagNeverApplies() => Assert.IsFalse(Apply(flag: "squad/flags/default"));
        [TestMethod] public void NotInstalledNeverApplies() => Assert.IsFalse(Apply(installed: false));
        [TestMethod] public void UnsafeOrMissingAgencyFlagNeverApplies()
        {
            Assert.IsFalse(Apply(flag: null));
            Assert.IsFalse(Apply(flag: ""));
            Assert.IsFalse(Apply(flag: "../evil"));
        }
        [TestMethod] public void EmptyPlayerDefaultDoesNotMatchEmptyIncomingSpuriously() => Assert.IsFalse(Apply(incoming: "Squad/Flags/other", player: null, game: ""));
    }
}
