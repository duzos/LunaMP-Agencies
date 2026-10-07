using System;
using System.IO;
using System.Linq;
using LmpAgenciesInstaller;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ServerTest.Updater
{
    [TestClass]
    public class KspDiscoveryTest
    {
        private string root;
        [TestInitialize] public void Setup() { root = Path.Combine(Path.GetTempPath(), "LmpInstallerTest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
        [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\") + "\"";
        private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text); }
        private static void Ksp(string path)
        { Directory.CreateDirectory(Path.Combine(path, "GameData")); Directory.CreateDirectory(Path.Combine(path, "KSP_x64_Data")); Write(Path.Combine(path, "KSP_x64.exe"), "test executable"); }

        [TestMethod]
        public void DetectsModernAndLegacySteamLibrariesAndCustomManifestName()
        {
            var steam = Path.Combine(root, "Steam"); var modern = Path.Combine(root, "Modern Library"); var legacy = Path.Combine(root, "Legacy Library");
            Write(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" " + Quote(steam) + " } \"1\" { \"path\" " + Quote(modern) + " } \"2\" " + Quote(legacy) + " }");
            var first = Path.Combine(modern, "steamapps", "common", "Custom KSP"); Ksp(first);
            Write(Path.Combine(modern, "steamapps", "appmanifest_220200.acf"), "\"AppState\" { \"installdir\" \"Custom KSP\" }");
            var second = Path.Combine(legacy, "steamapps", "common", "Kerbal Space Program"); Ksp(second);
            var detected = KspDiscovery.Detect(new[] { steam, steam }, new[] { first });
            Assert.AreEqual(2, detected.Length); CollectionAssert.AreEquivalent(new[] { first, second }, detected);
        }

        [TestMethod]
        public void RejectsIncompleteInstallsAndManifestTraversal()
        {
            var steam = Path.Combine(root, "Steam"); var external = Path.Combine(root, "Escape"); Ksp(external);
            Write(Path.Combine(steam, "steamapps", "appmanifest_220200.acf"), "\"installdir\" \"../../Escape\"");
            var incomplete = Path.Combine(steam, "steamapps", "common", "Kerbal Space Program"); Write(Path.Combine(incomplete, "KSP_x64.exe"), "not enough");
            Assert.AreEqual(0, KspDiscovery.Detect(new[] { steam }, new string[0]).Length);
            Assert.IsFalse(KspDiscovery.IsKspDirectory(incomplete));
        }

        [TestMethod]
        public void SafetyRejectsInvalidTargetAndInstallerInsideReplacedMod()
        {
            Assert.ThrowsException<InvalidOperationException>(() => InstallerSafety.ValidateTarget(root));
            Ksp(root); InstallerSafety.ValidateTarget(root, Path.Combine(root, "Downloads", "installer.exe"));
            Assert.ThrowsException<InvalidOperationException>(() => InstallerSafety.ValidateTarget(root, Path.Combine(root, "GameData", "LunaMultiplayer", "installer.exe")));
        }

        [TestMethod]
        public void RunningProcessCheckDistinguishesOtherCopiesAndUnknownPaths()
        {
            Assert.IsTrue(InstallerSafety.BlocksTarget(null, root));
            Assert.IsTrue(InstallerSafety.BlocksTarget(Path.Combine(root, "KSP_x64.exe"), root));
            Assert.IsFalse(InstallerSafety.BlocksTarget(Path.Combine(root + "-other", "KSP_x64.exe"), root));
        }

        [TestMethod]
        public void ParserHandlesEscapedWindowsPathsAndSpaces()
        {
            var pair = KspDiscovery.ReadPairs("\"path\" \"D:\\\\Steam Library\\\\Games\"").Single();
            Assert.AreEqual("path", pair.Key); Assert.AreEqual(@"D:\Steam Library\Games", pair.Value);
        }

        [TestMethod]
        public void SafetyRejectsLinkedModTreeBeforeFollowingIt()
        {
            Ksp(root);
            var outside = Path.Combine(root, "external-data"); Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "keep.txt"); File.WriteAllText(sentinel, "untouched");
            var link = Path.Combine(root, "GameData", "LunaMultiplayer");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) { Assert.Inconclusive("Creating directory links requires Windows developer mode or the appropriate privilege."); }
            catch (PlatformNotSupportedException) { Assert.Inconclusive("Directory links are unsupported by this test filesystem."); }
            try
            {
                Assert.ThrowsException<InvalidOperationException>(() => InstallerSafety.ValidateTarget(root));
                Assert.AreEqual("untouched", File.ReadAllText(sentinel));
            }
            finally { if (Directory.Exists(link)) Directory.Delete(link); }
        }
    }
}
