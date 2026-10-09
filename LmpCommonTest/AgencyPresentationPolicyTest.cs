using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class AgencyPresentationPolicyTest
    {
        private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

        [TestMethod]
        public void StylingNeedsTheFeatureAnOwnerAndVisibility()
        {
            Assert.IsTrue(AgencyPresentationPolicy.ShouldStyle(true, A, true));
            Assert.IsFalse(AgencyPresentationPolicy.ShouldStyle(false, A, true));
            Assert.IsFalse(AgencyPresentationPolicy.ShouldStyle(true, Guid.Empty, true));
            Assert.IsFalse(AgencyPresentationPolicy.ShouldStyle(true, A, false));
        }

        [TestMethod]
        public void DefaultAndUnsafeFlagsAreSuppressed()
        {
            Assert.IsFalse(AgencyPresentationPolicy.ShowFlag(null));
            Assert.IsFalse(AgencyPresentationPolicy.ShowFlag(new AgencyIdentityInfo { FlagUrl = AgencyIdentityDefaults.DefaultFlagUrl }));
            Assert.IsFalse(AgencyPresentationPolicy.ShowFlag(new AgencyIdentityInfo { FlagUrl = "squad/flags/DEFAULT" }));
            Assert.IsFalse(AgencyPresentationPolicy.ShowFlag(new AgencyIdentityInfo { FlagUrl = null }));
            Assert.IsFalse(AgencyPresentationPolicy.ShowFlag(new AgencyIdentityInfo { FlagUrl = "" }));
            Assert.IsFalse(AgencyPresentationPolicy.ShowFlag(new AgencyIdentityInfo { FlagUrl = "../evil" }));
            Assert.IsFalse(AgencyPresentationPolicy.ShowFlag(new AgencyIdentityInfo { FlagUrl = "a//b" }));
            Assert.IsTrue(AgencyPresentationPolicy.ShowFlag(new AgencyIdentityInfo { FlagUrl = "Squad/Flags/minimalistic" }));
            Assert.IsTrue(AgencyPresentationPolicy.ShowFlag(new AgencyIdentityInfo { FlagUrl = "LunaMultiplayer/Flags/my_flag-1" }));
        }

        [TestMethod]
        public void ColourOnlyWhenChosen()
        {
            Assert.IsFalse(AgencyPresentationPolicy.ShowColour(null));
            Assert.IsFalse(AgencyPresentationPolicy.ShowColour(new AgencyIdentityInfo { Red = 255 }));
            Assert.IsTrue(AgencyPresentationPolicy.ShowColour(new AgencyIdentityInfo { HasColour = true }));
        }

        [TestMethod]
        public void HexIsUpperCaseRrGgBb()
        {
            Assert.AreEqual("#000000", AgencyPresentationPolicy.ColourHex(0, 0, 0));
            Assert.AreEqual("#FFFFFF", AgencyPresentationPolicy.ColourHex(255, 255, 255));
            Assert.AreEqual("#0A80F1", AgencyPresentationPolicy.ColourHex(10, 128, 241));
        }

        [TestMethod]
        public void BlackIsLiftedToTheFloorAsGrey()
        {
            var (r, g, b) = AgencyPresentationPolicy.ReadableTextColour(0, 0, 0);
            Assert.AreEqual(r, g); Assert.AreEqual(g, b);
            Assert.IsTrue(AgencyPresentationPolicy.Luma(r, g, b) >= AgencyPresentationPolicy.DefaultMinLuminance);
            Assert.IsTrue(AgencyPresentationPolicy.Luma(r, g, b) < AgencyPresentationPolicy.DefaultMinLuminance + 0.01);
        }

        [TestMethod]
        public void BrightColoursAreUnchanged()
        {
            Assert.AreEqual(((byte)255, (byte)255, (byte)255), AgencyPresentationPolicy.ReadableTextColour(255, 255, 255));
            Assert.AreEqual(((byte)255, (byte)255, (byte)0), AgencyPresentationPolicy.ReadableTextColour(255, 255, 0));
            Assert.AreEqual(((byte)0, (byte)255, (byte)0), AgencyPresentationPolicy.ReadableTextColour(0, 255, 0));
        }

        [TestMethod]
        public void SaturatedDarkColoursKeepTheirHue()
        {
            var (r, g, b) = AgencyPresentationPolicy.ReadableTextColour(0, 0, 255);
            Assert.IsTrue(AgencyPresentationPolicy.Luma(r, g, b) >= AgencyPresentationPolicy.DefaultMinLuminance);
            Assert.AreEqual(255, b);
            Assert.AreEqual(r, g);
            Assert.IsTrue(b > r);

            (r, g, b) = AgencyPresentationPolicy.ReadableTextColour(128, 0, 0);
            Assert.IsTrue(AgencyPresentationPolicy.Luma(r, g, b) >= AgencyPresentationPolicy.DefaultMinLuminance);
            Assert.IsTrue(r > g && g == b);
        }

        [TestMethod]
        public void LuminanceFloorIsClamped()
        {
            Assert.AreEqual(((byte)255, (byte)255, (byte)255), AgencyPresentationPolicy.ReadableTextColour(10, 20, 30, 5));
            Assert.AreEqual(((byte)10, (byte)20, (byte)30), AgencyPresentationPolicy.ReadableTextColour(10, 20, 30, -1));
        }

        [TestMethod]
        public void EscapeWrapsPlainText()
        {
            Assert.AreEqual("<noparse>Kerbin Space</noparse>", AgencyPresentationPolicy.EscapeTmp("Kerbin Space"));
            Assert.AreEqual("<noparse></noparse>", AgencyPresentationPolicy.EscapeTmp(null));
            Assert.AreEqual("<noparse><b>x</b></noparse>", AgencyPresentationPolicy.EscapeTmp("<b>x</b>"));
        }

        [TestMethod]
        public void EscapeStripsClosingTagsInAnyCase()
        {
            Assert.AreEqual("<noparse>a<color=red>b</noparse>", AgencyPresentationPolicy.EscapeTmp("a</noparse><color=red>b"));
            Assert.AreEqual("<noparse>ab</noparse>", AgencyPresentationPolicy.EscapeTmp("a</NoParse>b"));
            Assert.AreEqual("<noparse>ab</noparse>", AgencyPresentationPolicy.EscapeTmp("a</NOPARSE></noparse>b"));
        }

        [TestMethod]
        public void EscapeDefeatsNestedReassembly()
        {
            var escaped = AgencyPresentationPolicy.EscapeTmp("</no</noparse>parse><size=999>big");
            Assert.AreEqual("<noparse><size=999>big</noparse>", escaped);
            escaped = AgencyPresentationPolicy.EscapeTmp("x</</No</noparse>parse>noPARSE>y");
            Assert.AreEqual("<noparse>xy</noparse>", escaped);
            var inner = escaped.Substring("<noparse>".Length, escaped.Length - "<noparse></noparse>".Length);
            Assert.AreEqual(-1, inner.IndexOf("</noparse>", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void EscapeCapsLengthAndDropsControlCharacters()
        {
            var escaped = AgencyPresentationPolicy.EscapeTmp(new string('x', 200));
            Assert.AreEqual("<noparse>".Length + AgencyPresentationPolicy.MaxEscapedNameLength + "</noparse>".Length, escaped.Length);
            Assert.AreEqual("<noparse>a b</noparse>", AgencyPresentationPolicy.EscapeTmp("a\nb"));
            var surrogate = new string('x', AgencyPresentationPolicy.MaxEscapedNameLength - 1) + "\U0001F680";
            escaped = AgencyPresentationPolicy.EscapeTmp(surrogate);
            Assert.IsFalse(escaped.Contains("\uD83D"));
        }

        [TestMethod]
        public void PlayerNamesMapToTheirAgency()
        {
            var map = AgencyPresentationPolicy.ResolvePlayerAgencies(new[] { (A, new[] { "Jeb", "Bill" }), (B, new[] { "Val" }) });
            Assert.AreEqual(A, map["Jeb"]);
            Assert.AreEqual(A, map["Bill"]);
            Assert.AreEqual(B, map["Val"]);
            Assert.IsFalse(map.ContainsKey("jeb"));
        }

        [TestMethod]
        public void AmbiguousNamesAreDropped()
        {
            var map = AgencyPresentationPolicy.ResolvePlayerAgencies(new[] { (A, new[] { "Jeb", "Jeb", "Bob" }), (B, new[] { "Jeb", null, "" }), (A, new[] { "Bob" }) });
            Assert.IsFalse(map.ContainsKey("Jeb"));
            Assert.AreEqual(A, map["Bob"]);
            Assert.AreEqual(1, map.Count);
            var third = Guid.NewGuid();
            map = AgencyPresentationPolicy.ResolvePlayerAgencies(new[] { (A, new[] { "Jeb" }), (B, new[] { "Jeb" }), (third, new[] { "Jeb" }) });
            Assert.AreEqual(0, map.Count);
        }

        [TestMethod]
        public void EmptyAgenciesAndNullInputsAreIgnored()
        {
            Assert.AreEqual(0, AgencyPresentationPolicy.ResolvePlayerAgencies(null).Count);
            var map = AgencyPresentationPolicy.ResolvePlayerAgencies(new[] { (Guid.Empty, new[] { "Jeb" }), (A, (string[])null) });
            Assert.AreEqual(0, map.Count);
        }

        [TestMethod]
        public void NameplateRangeIsClamped()
        {
            Assert.AreEqual(2.5f, AgencyPresentationPolicy.ClampNameplateRangeKm(2.5f));
            Assert.AreEqual(0.2f, AgencyPresentationPolicy.ClampNameplateRangeKm(0));
            Assert.AreEqual(25f, AgencyPresentationPolicy.ClampNameplateRangeKm(1000));
            Assert.AreEqual(2.5f, AgencyPresentationPolicy.ClampNameplateRangeKm(float.NaN));
            Assert.AreEqual(2.5f, AgencyPresentationPolicy.ClampNameplateRangeKm(float.PositiveInfinity));
        }

        [TestMethod]
        public void TextureRetryWaitsForTimeOrADatabaseChange()
        {
            var five = AgencyPresentationPolicy.TextureRetryTicks;
            Assert.IsFalse(AgencyPresentationPolicy.ShouldRetryTexture(1000, 1000, 10, 10));
            Assert.IsFalse(AgencyPresentationPolicy.ShouldRetryTexture(1000, 1000 + five - 1, 10, 10));
            Assert.IsTrue(AgencyPresentationPolicy.ShouldRetryTexture(1000, 1000 + five, 10, 10));
            Assert.IsTrue(AgencyPresentationPolicy.ShouldRetryTexture(1000, 1001, 10, 11));
            Assert.IsTrue(AgencyPresentationPolicy.ShouldRetryTexture(1000, 1001, 10, 9));
            Assert.IsTrue(AgencyPresentationPolicy.ShouldRetryTexture(1000, 500, 10, 10));
        }
    }
}
