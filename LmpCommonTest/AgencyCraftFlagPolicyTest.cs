using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyCraftFlagPolicyTest
    {
        private static readonly Guid Agency = Guid.NewGuid();
        private const string Custom = "LunaMultiPlayer/Flags/esa", Mine = "Squad/Flags/kerbalx", Default = "Squad/Flags/default";

        private static bool Apply(bool on = true, Guid? agency = null, string flag = Custom, bool installed = true,
            string incoming = Default, string craft = Default) =>
            AgencyCraftFlagPolicy.ShouldApply(on, agency ?? Agency, flag, installed, incoming, craft);

        [TestMethod] public void AppliesForStockDefaultCraftFlag() => Assert.IsTrue(Apply());
        [TestMethod] public void AppliesCaseInsensitivelyToStockDefault() => Assert.IsTrue(Apply(incoming: "squad/flags/DEFAULT", craft: "SQUAD/Flags/default"));
        [TestMethod] public void AppliesForNullOrEmptyFlags()
        {
            Assert.IsTrue(Apply(incoming: null, craft: null));
            Assert.IsTrue(Apply(incoming: "", craft: ""));
            Assert.IsTrue(Apply(incoming: Default, craft: ""));
        }

        // Regression (agencies.10 report): a VAB mission-flag pick becomes LMP's SelectedFlag, so "equals my default"
        // used to mean "unchosen" and every chosen flag was replaced by the agency flag.
        [TestMethod] public void KeepsAFlagThePlayerPickedInTheEditor() => Assert.IsFalse(Apply(incoming: Mine, craft: Mine));
        [TestMethod] public void KeepsThePlayersOwnDefaultFlag() => Assert.IsFalse(Apply(incoming: Mine, craft: ""));

        // KSC launch dialog passes the game flag, not the craft's saved flag.
        [TestMethod] public void KeepsTheCraftsSavedFlagWhenTheLaunchDialogPassesTheDefault() => Assert.IsFalse(Apply(incoming: Default, craft: Mine));
        [TestMethod] public void KeepsAFlagPickedInTheLaunchDialog() => Assert.IsFalse(Apply(incoming: Mine, craft: Default));

        [TestMethod] public void FeatureOffNeverApplies() => Assert.IsFalse(Apply(on: false));
        [TestMethod] public void NoAgencyNeverApplies() => Assert.IsFalse(Apply(agency: Guid.Empty));
        [TestMethod] public void DefaultAgencyFlagNeverApplies() => Assert.IsFalse(Apply(flag: "squad/flags/default"));
        [TestMethod] public void NotInstalledNeverApplies() => Assert.IsFalse(Apply(installed: false));
        [TestMethod] public void ModReferenceAgencyFlagAppliesWhenInstalled() => Assert.IsTrue(Apply(flag: "FlagPack/Flags/Kerbin flag (blue)"));
        [TestMethod] public void UnsafeOrMissingAgencyFlagNeverApplies()
        {
            Assert.IsFalse(Apply(flag: null));
            Assert.IsFalse(Apply(flag: ""));
            Assert.IsFalse(Apply(flag: "../evil"));
        }
    }
}
