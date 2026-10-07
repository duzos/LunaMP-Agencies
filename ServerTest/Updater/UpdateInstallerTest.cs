using LmpAgenciesUpdater;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ServerTest.Updater
{
    [TestClass]
    public class UpdateInstallerTest
    {
        private const string ClientMod = "GameData/LunaMultiplayer/";
        private const string ClientHelper = ClientMod + "Updater/LmpAgenciesUpdater.exe";
        private const string ClientBuildTxt = ClientMod + "Updater/build.txt";
        private const string ClientDll = ClientMod + "Plugins/LmpClient.dll";
        private const string PayloadHarmonyDll = "GameData/000_Harmony/0Harmony.dll";
        private const string PayloadHarmonyVersion = "GameData/000_Harmony/Harmony.version";

        private string _root;
        private string _gameData;
        private string _bin;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "LMPUpdaterTest_" + Guid.NewGuid().ToString("N"));
            _gameData = Path.Combine(_root, "KSP", "GameData");
            _bin = Path.Combine(_root, "Server");
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { /* temp dir, best effort */ }
        }

        // ------------------------------------------------------------------ helpers

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        private static string Hex(byte[] hash) => string.Concat(hash.Select(b => b.ToString("x2")));

        private static string Sha256OfFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(stream));
        }

        private static void WriteFile(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }

        private static void WriteFile(string path, string text) => WriteFile(path, B(text));

        private static string Rel(string root, string path) => path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');

        private static void BuildZip(string zipPath, IDictionary<string, byte[]> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var entry in entries)
                {
                    var e = zip.CreateEntry(entry.Key, CompressionLevel.Fastest);
                    using (var es = e.Open()) es.Write(entry.Value, 0, entry.Value.Length);
                }
            }
        }

        /// <summary>Every file below dir (relative path to content hash), optionally leaving out one top-level name.</summary>
        private static SortedDictionary<string, string> Snapshot(string dir, string excludeTop = null)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(dir)) return map;
            var root = Path.GetFullPath(dir);
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Rel(root, file);
                if (excludeTop != null && (rel == excludeTop || rel.StartsWith(excludeTop + "/", StringComparison.Ordinal))) continue;
                map[rel] = Sha256OfFile(file);
            }
            return map;
        }

        private static void AssertSame(IDictionary<string, string> expected, IDictionary<string, string> actual, string what)
        {
            var problems = new List<string>();
            foreach (var key in expected.Keys.Except(actual.Keys)) problems.Add("missing: " + key);
            foreach (var key in actual.Keys.Except(expected.Keys)) problems.Add("unexpected: " + key);
            foreach (var key in expected.Keys.Intersect(actual.Keys))
                if (expected[key] != actual[key]) problems.Add("changed: " + key);
            if (problems.Count > 0) Assert.Fail(what + " differs: " + string.Join("; ", problems));
        }

        private static string HarmonyObject(int major, int minor, int patch, int build = 0) =>
            "{\n  \"NAME\": \"Harmony\",\n  \"VERSION\": {\"MAJOR\": " + major + ", \"MINOR\": " + minor + ", \"PATCH\": " + patch + ", \"BUILD\": " + build +
            "},\n  \"KSP_VERSION\": {\"MAJOR\": 1, \"MINOR\": 8, \"PATCH\": 0}\n}";

        private static string HarmonyString(string version) => "{ \"NAME\": \"Harmony\", \"VERSION\": \"" + version + "\" }";

        private static bool Exited(int pid, TimeSpan? timeout) => true;

        private static readonly byte[] SettingsBytes = { 0, 1, 2, 3, 254, 255, 13, 10, 65, 66 };

        private Dictionary<string, byte[]> ClientPayload(int build, string harmonyVersion = null)
        {
            return new Dictionary<string, byte[]>
            {
                [ClientDll] = B("new-client-dll"),
                [ClientHelper] = B("new-helper"),
                [ClientBuildTxt] = B(build.ToString()),
                [ClientMod + "Data/payload.txt"] = B("payload data"),
                [ClientMod + "Flags/default.png"] = B("payload default flag"),
                [PayloadHarmonyDll] = B("payload-harmony"),
                [PayloadHarmonyVersion] = B(harmonyVersion ?? HarmonyObject(2, 2, 1))
            };
        }

        /// <summary>An installed agencies.2 client plus an installed Harmony.</summary>
        private void InstallClient(string buildTxt = "2", string harmonyVersion = "default", bool withHarmony = true)
        {
            var mod = Path.Combine(_gameData, "LunaMultiplayer");
            WriteFile(Path.Combine(mod, "Plugins", "LmpClient.dll"), "old-client-dll");
            WriteFile(Path.Combine(mod, "Plugins", "Harmony", "0Harmony.dll"), "nested-harmony");
            WriteFile(Path.Combine(mod, "Updater", "LmpAgenciesUpdater.exe"), "old-helper");
            if (buildTxt != null) WriteFile(Path.Combine(mod, "Updater", "build.txt"), buildTxt);
            WriteFile(Path.Combine(mod, "Data", "settings.xml"), SettingsBytes);
            WriteFile(Path.Combine(mod, "Flags", "custom.png"), "custom flag");
            WriteFile(Path.Combine(mod, "Flags", "default.png"), "old default flag");
            WriteFile(Path.Combine(mod, "Screenshots", "shot.png"), "screenshot");
            if (withHarmony)
            {
                WriteFile(Path.Combine(_gameData, "000_Harmony", "0Harmony.dll"), "installed-harmony");
                WriteFile(Path.Combine(_gameData, "000_Harmony", "Harmony.version"), harmonyVersion == "default" ? HarmonyObject(2, 2, 1) : harmonyVersion);
            }
        }

        private HelperOptions ClientOptions(Dictionary<string, byte[]> payload, int build = 3)
        {
            var stage = Path.Combine(_root, "KSP", "LunaMultiplayer-update");
            var zip = Path.Combine(stage, build.ToString(), "client.zip");
            BuildZip(zip, payload);
            return new HelperOptions
            {
                Mode = HelperMode.Client,
                Zip = zip,
                Sha256 = Sha256OfFile(zip),
                Target = _gameData,
                Extract = Path.Combine(stage, build.ToString(), "extract"),
                Backup = Path.Combine(stage, "backup"),
                Result = Path.Combine(stage, "last-result.txt"),
                Pid = 4242,
                Build = build
            };
        }

        private void InstallServer(string buildTxt = "2")
        {
            WriteFile(Path.Combine(_bin, "Server.dll"), "old-server-dll");
            WriteFile(Path.Combine(_bin, "Server.exe"), "old-server-exe");
            WriteFile(Path.Combine(_bin, "Server.runtimeconfig.json"), "old-runtime-config");
            WriteFile(Path.Combine(_bin, "Updater", "LmpAgenciesUpdater.exe"), "old-helper");
            if (buildTxt != null) WriteFile(Path.Combine(_bin, "Updater", "build.txt"), buildTxt);
            WriteFile(Path.Combine(_bin, "Universe", "x.txt"), "universe");
            WriteFile(Path.Combine(_bin, "Config", "GeneralSettings.xml"), "<settings />");
            WriteFile(Path.Combine(_bin, "logs", "a.log"), "log line");
            WriteFile(Path.Combine(_bin, "Universe-backup", "y.txt"), "universe backup");
            WriteFile(Path.Combine(_bin, "LMPPlayerBans.txt"), "bans");
            WriteFile(Path.Combine(_bin, "Plugins", "plugin.dll"), "plugin");
        }

        private static Dictionary<string, byte[]> ServerPayload(int build)
        {
            return new Dictionary<string, byte[]>
            {
                ["Server.dll"] = B("new-server-dll"),
                ["Server.exe"] = B("new-server-exe"),
                ["Server.runtimeconfig.json"] = B("new-runtime-config"),
                ["NewLib.dll"] = B("added lib"),
                ["Sub/Added.txt"] = B("added in a new folder"),
                ["Updater/LmpAgenciesUpdater.exe"] = B("new-helper"),
                ["Updater/build.txt"] = B(build.ToString())
            };
        }

        private HelperOptions ServerOptions(Dictionary<string, byte[]> payload, int build = 3)
        {
            var stage = Path.Combine(_bin, "update-staging");
            var zip = Path.Combine(stage, build.ToString(), "server.zip");
            BuildZip(zip, payload);
            return new HelperOptions
            {
                Mode = HelperMode.Server,
                Zip = zip,
                Sha256 = Sha256OfFile(zip),
                Target = _bin,
                Extract = Path.Combine(stage, build.ToString(), "extract"),
                Backup = Path.Combine(stage, "backup"),
                Result = Path.Combine(stage, "last-result.txt"),
                Pid = 4243,
                Build = build,
                WaitTimeoutSeconds = 600
            };
        }

        private static void AssertResultFile(HelperOptions o, InstallResult result)
        {
            bool success;
            int build, previous;
            string message;
            Assert.IsTrue(HelperCommandLine.TryReadResult(o.Result, out success, out build, out previous, out message), "no result file was written");
            Assert.AreEqual(result.Success, success);
            Assert.AreEqual(o.Build, build);
            Assert.AreEqual(result.PreviousBuild, previous);
            Assert.AreEqual(result.Message, message);
        }

        private static void AssertRejected(HelperOptions o, InstallResult result, string messageFragment)
        {
            Assert.IsFalse(result.Success, result.Message);
            StringAssert.Contains(result.Message, messageFragment);
            AssertResultFile(o, result);
            Assert.IsFalse(Directory.Exists(o.Backup), "a rejected install must not touch the backup folder");
        }

        private static Action<string> FailAt(string point) => stage =>
        {
            if (stage == point) throw new IOException("injected failure at " + point);
        };

        // ------------------------------------------------------------------ client

        [TestMethod]
        public void Client_HappyPath_ReplacesFilesAndKeepsUserData()
        {
            InstallClient();
            var o = ClientOptions(ClientPayload(3));
            var mod = Path.Combine(_gameData, "LunaMultiplayer");

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual("Updated agencies.2 -> agencies.3", result.Message);
            Assert.AreEqual(3, result.Build);
            Assert.AreEqual(2, result.PreviousBuild);
            AssertResultFile(o, result);

            Assert.AreEqual("new-client-dll", File.ReadAllText(Path.Combine(mod, "Plugins", "LmpClient.dll")));
            Assert.AreEqual("new-helper", File.ReadAllText(Path.Combine(mod, "Updater", "LmpAgenciesUpdater.exe")));
            Assert.AreEqual("3", File.ReadAllText(Path.Combine(mod, "Updater", "build.txt")));
            Assert.IsFalse(Directory.Exists(Path.Combine(mod, "Plugins", "Harmony")), "a nested Plugins/Harmony must disappear");

            CollectionAssert.AreEqual(SettingsBytes, File.ReadAllBytes(Path.Combine(mod, "Data", "settings.xml")));
            Assert.IsFalse(File.Exists(Path.Combine(mod, "Data", "payload.txt")), "the old Data replaces the payload Data");
            Assert.AreEqual("custom flag", File.ReadAllText(Path.Combine(mod, "Flags", "custom.png")));
            Assert.AreEqual("payload default flag", File.ReadAllText(Path.Combine(mod, "Flags", "default.png")), "payload flags win");
            Assert.AreEqual("screenshot", File.ReadAllText(Path.Combine(mod, "Screenshots", "shot.png")));

            var backup = Path.Combine(o.Backup, "LunaMultiplayer");
            Assert.AreEqual("old-client-dll", File.ReadAllText(Path.Combine(backup, "Plugins", "LmpClient.dll")));
            Assert.AreEqual("nested-harmony", File.ReadAllText(Path.Combine(backup, "Plugins", "Harmony", "0Harmony.dll")));
            CollectionAssert.AreEqual(SettingsBytes, File.ReadAllBytes(Path.Combine(backup, "Data", "settings.xml")));
            Assert.IsFalse(Directory.Exists(o.Extract), "the extract folder is removed");
        }

        [TestMethod]
        public void Client_PassesPidAndTimeoutToWaitForExit()
        {
            InstallClient();
            int seenPid = -1;
            TimeSpan? seenTimeout = TimeSpan.Zero;

            var o = ClientOptions(ClientPayload(3));
            o.Pid = 777;
            o.WaitTimeoutSeconds = 0;
            UpdateInstaller.Run(o, (pid, timeout) => { seenPid = pid; seenTimeout = timeout; return true; });
            Assert.AreEqual(777, seenPid);
            Assert.IsNull(seenTimeout, "0 means wait forever");

            var o2 = ClientOptions(ClientPayload(4), 4);
            o2.WaitTimeoutSeconds = 90;
            UpdateInstaller.Run(o2, (pid, timeout) => { seenTimeout = timeout; return false; });
            Assert.AreEqual(TimeSpan.FromSeconds(90), seenTimeout);
        }

        [TestMethod]
        public void Client_Rejects_WrongHash()
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var o = ClientOptions(ClientPayload(3));
            o.Sha256 = new string('0', 64);

            var result = UpdateInstaller.Run(o, Exited);

            AssertRejected(o, result, "verification");
            AssertSame(before, Snapshot(_gameData), "the install");
        }

        [TestMethod]
        public void Client_HashComparisonIgnoresCase()
        {
            InstallClient();
            var o = ClientOptions(ClientPayload(3));
            o.Sha256 = o.Sha256.ToUpperInvariant();

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsTrue(result.Success, result.Message);
        }

        [TestMethod]
        public void Client_Rejects_WaitTimeout()
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var o = ClientOptions(ClientPayload(3));
            o.WaitTimeoutSeconds = 5;

            var result = UpdateInstaller.Run(o, (pid, timeout) => false);

            AssertRejected(o, result, "Timed out waiting for exit.");
            Assert.IsTrue(result.TimedOut, "a wait timeout is flagged so the helper does not relaunch");
            AssertSame(before, Snapshot(_gameData), "the install");
        }

        [DataTestMethod]
        [DataRow(3, 3)]
        [DataRow(2, 3)]
        public void Client_Rejects_Downgrade(int build, int installed)
        {
            InstallClient(buildTxt: installed.ToString());
            var before = Snapshot(_gameData);
            var o = ClientOptions(ClientPayload(build), build);

            var result = UpdateInstaller.Run(o, Exited);

            AssertRejected(o, result, "Refusing to install agencies." + build + " over agencies." + installed + ".");
            Assert.AreEqual(installed, result.PreviousBuild);
            AssertSame(before, Snapshot(_gameData), "the install");
        }

        [DataTestMethod]
        [DataRow("../evil.txt")]
        [DataRow("GameData/../../evil.txt")]
        public void Client_Rejects_ZipSlip(string evilEntry)
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var payload = ClientPayload(3);
            payload[evilEntry] = B("evil");
            var o = ClientOptions(payload);

            var result = UpdateInstaller.Run(o, Exited);

            AssertRejected(o, result, "Invalid update package.");
            AssertSame(before, Snapshot(_gameData), "the install");
            var stage = Path.GetDirectoryName(o.Extract);
            Assert.IsFalse(File.Exists(Path.Combine(stage, "evil.txt")), "nothing may be written outside the extract folder");
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(stage), "evil.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(stage)), "evil.txt")));
        }

        [DataTestMethod]
        [DataRow("GameData/LunaMultiplayer/Plugins/0Harmony.dll")]
        [DataRow("GameData/Other/0Harmony.dll")]
        [DataRow("GameData/000_Harmony/Nested/0harmony.dll")]
        public void Client_Rejects_MisplacedHarmonyDll(string extraHarmony)
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var payload = ClientPayload(3);
            payload[extraHarmony] = B("stray harmony");
            var o = ClientOptions(payload);

            var result = UpdateInstaller.Run(o, Exited);

            AssertRejected(o, result, "Invalid update package");
            AssertSame(before, Snapshot(_gameData), "the install");
        }

        [DataTestMethod]
        [DataRow(ClientHelper)]
        [DataRow(ClientBuildTxt)]
        [DataRow(ClientDll)]
        [DataRow(PayloadHarmonyDll)]
        public void Client_Rejects_PackageMissingRequiredFile(string missing)
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var payload = ClientPayload(3);
            Assert.IsTrue(payload.Remove(missing));
            var o = ClientOptions(payload);

            var result = UpdateInstaller.Run(o, Exited);

            AssertRejected(o, result, "Invalid update package");
            AssertSame(before, Snapshot(_gameData), "the install");
        }

        [TestMethod]
        public void Client_Rejects_PackageWhoseBuildTxtDisagrees()
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var payload = ClientPayload(3);
            payload[ClientBuildTxt] = B("9");
            var o = ClientOptions(payload);

            var result = UpdateInstaller.Run(o, Exited);

            AssertRejected(o, result, "Invalid update package");
            AssertSame(before, Snapshot(_gameData), "the install");
        }

        [TestMethod]
        public void Client_Rejects_BackupFolderThatContainsTheTarget()
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var o = ClientOptions(ClientPayload(3));
            o.Backup = Path.Combine(_root, "KSP");

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "Invalid helper paths");
            AssertSame(before, Snapshot(_gameData), "the install");
        }

        [TestMethod]
        public void Client_MissingInstalledBuildTxt_ReadsAsZeroAndAllowsInstall()
        {
            InstallClient(buildTxt: null);
            var o = ClientOptions(ClientPayload(1), 1);

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(0, result.PreviousBuild);
            Assert.AreEqual("Updated agencies.0 -> agencies.1", result.Message);
            Assert.AreEqual("1", File.ReadAllText(Path.Combine(_gameData, "LunaMultiplayer", "Updater", "build.txt")));
        }

        [TestMethod]
        public void InstalledBuild_ReadsEachModeAndTreatsMissingOrGarbageAsZero()
        {
            var o = new HelperOptions { Mode = HelperMode.Client, Target = _gameData };
            Assert.AreEqual(0, UpdateInstaller.InstalledBuild(o));

            var clientBuild = Path.Combine(_gameData, "LunaMultiplayer", "Updater", "build.txt");
            WriteFile(clientBuild, "7\r\n");
            Assert.AreEqual(7, UpdateInstaller.InstalledBuild(o));
            WriteFile(clientBuild, "seven");
            Assert.AreEqual(0, UpdateInstaller.InstalledBuild(o));

            var s = new HelperOptions { Mode = HelperMode.Server, Target = _bin };
            Assert.AreEqual(0, UpdateInstaller.InstalledBuild(s));
            WriteFile(Path.Combine(_bin, "Updater", "build.txt"), "12");
            Assert.AreEqual(12, UpdateInstaller.InstalledBuild(s));
        }

        // ------------------------------------------------------------------ client: Harmony gate

        [TestMethod]
        public void Client_Harmony_NewerInstalledVersionIsKept()
        {
            InstallClient(harmonyVersion: HarmonyObject(2, 3, 0));
            var before = Snapshot(Path.Combine(_gameData, "000_Harmony"));
            var o = ClientOptions(ClientPayload(3, HarmonyObject(2, 2, 1)));

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.Contains(result.Message, "kept installed Harmony");
            AssertSame(before, Snapshot(Path.Combine(_gameData, "000_Harmony")), "000_Harmony");
            Assert.IsFalse(Directory.Exists(Path.Combine(o.Backup, "000_Harmony")), "an untouched Harmony is not backed up");
        }

        [TestMethod]
        public void Client_Harmony_EqualVersionIsReplaced()
        {
            InstallClient();
            var o = ClientOptions(ClientPayload(3, HarmonyObject(2, 2, 1)));

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsTrue(result.Success, result.Message);
            Assert.IsFalse(result.Message.Contains("kept installed Harmony"));
            Assert.AreEqual("payload-harmony", File.ReadAllText(Path.Combine(_gameData, "000_Harmony", "0Harmony.dll")));
            Assert.AreEqual("installed-harmony", File.ReadAllText(Path.Combine(o.Backup, "000_Harmony", "0Harmony.dll")));
        }

        [TestMethod]
        public void Client_Harmony_StringFormVersionIsParsed()
        {
            InstallClient(harmonyVersion: HarmonyString("2.5.0"));
            var o = ClientOptions(ClientPayload(3, HarmonyObject(2, 2, 1)));

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsTrue(result.Success, result.Message);
            StringAssert.Contains(result.Message, "kept installed Harmony");
            Assert.AreEqual("installed-harmony", File.ReadAllText(Path.Combine(_gameData, "000_Harmony", "0Harmony.dll")));
        }

        [TestMethod]
        public void Client_Harmony_NothingInstalled_InstallsThePayloadCopy()
        {
            InstallClient(withHarmony: false);
            var o = ClientOptions(ClientPayload(3));

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual("payload-harmony", File.ReadAllText(Path.Combine(_gameData, "000_Harmony", "0Harmony.dll")));
            Assert.IsFalse(Directory.Exists(Path.Combine(o.Backup, "000_Harmony")));
        }

        private bool ReplaceHarmony(string installedVersionFile, string payloadVersionFile, bool installedDll = true)
        {
            var installed = Path.Combine(_root, "installed-" + Guid.NewGuid().ToString("N"));
            var payload = Path.Combine(_root, "payload-" + Guid.NewGuid().ToString("N"));
            if (installedDll) WriteFile(Path.Combine(installed, "0Harmony.dll"), "dll");
            else Directory.CreateDirectory(installed);
            WriteFile(Path.Combine(payload, "0Harmony.dll"), "dll");
            if (installedVersionFile != null) WriteFile(Path.Combine(installed, "Harmony.version"), installedVersionFile);
            if (payloadVersionFile != null) WriteFile(Path.Combine(payload, "Harmony.version"), payloadVersionFile);
            return UpdateInstaller.ShouldReplaceHarmony(installed, payload);
        }

        [TestMethod]
        public void ShouldReplaceHarmony_MissingInstalledFolderOrDll_IsTrue()
        {
            var payload = Path.Combine(_root, "payload");
            WriteFile(Path.Combine(payload, "0Harmony.dll"), "dll");
            WriteFile(Path.Combine(payload, "Harmony.version"), HarmonyObject(2, 2, 1));

            Assert.IsTrue(UpdateInstaller.ShouldReplaceHarmony(Path.Combine(_root, "no-such-folder"), payload));
            Assert.IsTrue(ReplaceHarmony(HarmonyObject(9, 9, 9), HarmonyObject(2, 2, 1), installedDll: false));
        }

        [TestMethod]
        public void ShouldReplaceHarmony_ComparesObjectAndStringVersions()
        {
            Assert.IsFalse(ReplaceHarmony(HarmonyObject(2, 3, 0), HarmonyObject(2, 2, 9)), "newer installed is kept");
            Assert.IsTrue(ReplaceHarmony(HarmonyObject(2, 2, 1), HarmonyObject(2, 2, 1)), "equal is replaced");
            Assert.IsTrue(ReplaceHarmony(HarmonyObject(2, 2, 1), HarmonyObject(2, 2, 2)), "newer payload is installed");
            Assert.IsFalse(ReplaceHarmony(HarmonyObject(2, 2, 1, 5), HarmonyObject(2, 2, 1, 4)), "the BUILD part counts");
            Assert.IsTrue(ReplaceHarmony(HarmonyString("2.2.1"), HarmonyObject(2, 2, 2)));
            Assert.IsFalse(ReplaceHarmony(HarmonyString("2.4.0"), HarmonyObject(2, 2, 2)));
            Assert.IsFalse(ReplaceHarmony(HarmonyObject(2, 4, 0), HarmonyString("2.2.2")));
            Assert.IsTrue(ReplaceHarmony(HarmonyString("2.2.0"), HarmonyString("2.2")), "missing parts are 0");
        }

        [TestMethod]
        public void ShouldReplaceHarmony_MissingPartsReadAsZero()
        {
            Assert.IsTrue(ReplaceHarmony("{ \"VERSION\": {\"MAJOR\": 2, \"MINOR\": 2} }", HarmonyString("2.2.0")));
            Assert.IsFalse(ReplaceHarmony("{ \"VERSION\": {\"MAJOR\": 2, \"MINOR\": 3} }", "{ \"VERSION\": {\"MAJOR\": 2, \"MINOR\": 2, \"PATCH\": 9} }"));
        }

        [TestMethod]
        public void ShouldReplaceHarmony_ReadsTheVersionKeyNotTheKspVersion()
        {
            var decoy = "{ \"KSP_VERSION\": {\"MAJOR\": 99, \"MINOR\": 0, \"PATCH\": 0}, \"KSP_VERSION_MIN\": {\"MAJOR\": 99}, " +
                        "\"VERSION\": {\"MAJOR\": 2, \"MINOR\": 2, \"PATCH\": 1, \"BUILD\": 0} }";
            Assert.IsTrue(ReplaceHarmony(decoy, HarmonyObject(2, 2, 2)), "a KSP_VERSION of 99 must not make the installed copy look newer");
        }

        [TestMethod]
        public void ShouldReplaceHarmony_UnknownVersionOnEitherSide_IsTrue()
        {
            Assert.IsTrue(ReplaceHarmony(null, null), "no version files and a dll without a version");
            Assert.IsTrue(ReplaceHarmony(HarmonyObject(9, 9, 9), null));
            Assert.IsTrue(ReplaceHarmony(null, HarmonyObject(1, 0, 0)));
            Assert.IsTrue(ReplaceHarmony("not json at all", HarmonyObject(1, 0, 0)));
        }

        [TestMethod]
        public void ShouldReplaceHarmony_FallsBackToTheDllFileVersion()
        {
            // The helper is Windows-only and other platforms read file versions differently, so this is checked on Windows.
            if (Path.DirectorySeparatorChar != '\\') return;

            var older = typeof(object).Assembly.Location;
            var newer = typeof(Newtonsoft.Json.JsonConvert).Assembly.Location;
            var olderVersion = FileVersionInfo.GetVersionInfo(older);
            var newerVersion = FileVersionInfo.GetVersionInfo(newer);
            var olderParsed = new Version(olderVersion.FileMajorPart, olderVersion.FileMinorPart, olderVersion.FileBuildPart, olderVersion.FilePrivatePart);
            var newerParsed = new Version(newerVersion.FileMajorPart, newerVersion.FileMinorPart, newerVersion.FileBuildPart, newerVersion.FilePrivatePart);
            Assert.IsTrue(newerParsed > olderParsed, "test precondition: " + newerParsed + " vs " + olderParsed);

            var installed = Path.Combine(_root, "dll-installed");
            var payload = Path.Combine(_root, "dll-payload");
            Directory.CreateDirectory(installed);
            Directory.CreateDirectory(payload);

            File.Copy(newer, Path.Combine(installed, "0Harmony.dll"));
            File.Copy(older, Path.Combine(payload, "0Harmony.dll"));
            Assert.IsFalse(UpdateInstaller.ShouldReplaceHarmony(installed, payload), "newer installed dll is kept");

            File.Copy(older, Path.Combine(installed, "0Harmony.dll"), true);
            File.Copy(newer, Path.Combine(payload, "0Harmony.dll"), true);
            Assert.IsTrue(UpdateInstaller.ShouldReplaceHarmony(installed, payload), "newer payload dll is installed");

            File.Copy(older, Path.Combine(payload, "0Harmony.dll"), true);
            Assert.IsTrue(UpdateInstaller.ShouldReplaceHarmony(installed, payload), "equal dll versions are replaced");
        }

        // ------------------------------------------------------------------ client: rollback

        [TestMethod]
        public void Client_FaultAfterNewFolderInstalled_RestoresTheOldFolderAndLeavesHarmonyAlone()
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var o = ClientOptions(ClientPayload(3));

            var result = UpdateInstaller.Run(o, Exited, FailAt(UpdateInstaller.FaultClientNewInstalled));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Message.IndexOf("rolled back", StringComparison.OrdinalIgnoreCase) >= 0, result.Message);
            StringAssert.Contains(result.Message, "injected failure");
            AssertResultFile(o, result);
            AssertSame(before, Snapshot(_gameData), "the install (including 000_Harmony)");
            Assert.IsFalse(Directory.Exists(Path.Combine(o.Backup, "LunaMultiplayer")), "the backup folder was moved back");
        }

        [TestMethod]
        public void Client_FaultAfterHarmonySwapped_RestoresEverything()
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var o = ClientOptions(ClientPayload(3, HarmonyObject(2, 2, 1)));

            var result = UpdateInstaller.Run(o, Exited, FailAt(UpdateInstaller.FaultClientHarmonySwapped));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Message.IndexOf("rolled back", StringComparison.OrdinalIgnoreCase) >= 0, result.Message);
            AssertSame(before, Snapshot(_gameData), "the install (including 000_Harmony)");
        }

        [TestMethod]
        public void Client_FaultAfterOldFolderMoved_RestoresTheOldFolder()
        {
            InstallClient();
            var before = Snapshot(_gameData);
            var o = ClientOptions(ClientPayload(3));

            var result = UpdateInstaller.Run(o, Exited, FailAt(UpdateInstaller.FaultClientOldMoved));

            Assert.IsFalse(result.Success);
            AssertSame(before, Snapshot(_gameData), "the install");
        }

        [TestMethod]
        public void Client_FaultWithNoHarmonyInstalled_RemovesTheNewHarmony()
        {
            InstallClient(withHarmony: false);
            var before = Snapshot(_gameData);
            var o = ClientOptions(ClientPayload(3));

            var result = UpdateInstaller.Run(o, Exited, FailAt(UpdateInstaller.FaultClientHarmonySwapped));

            Assert.IsFalse(result.Success);
            AssertSame(before, Snapshot(_gameData), "the install");
            Assert.IsFalse(Directory.Exists(Path.Combine(_gameData, "000_Harmony")), "the freshly placed Harmony is removed again");
        }

        // ------------------------------------------------------------------ server

        [TestMethod]
        public void Server_HappyPath_ReplacesBinariesAndLeavesUserDataAlone()
        {
            InstallServer();
            var protectedBefore = Snapshot(_bin, excludeTop: "update-staging");
            foreach (var replaced in new[] { "Server.dll", "Server.exe", "Server.runtimeconfig.json", "Updater/LmpAgenciesUpdater.exe", "Updater/build.txt" })
                protectedBefore.Remove(replaced);
            var o = ServerOptions(ServerPayload(3));

            var result = UpdateInstaller.Run(o, Exited);

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual("Updated agencies.2 -> agencies.3", result.Message);
            AssertResultFile(o, result);

            Assert.AreEqual("new-server-dll", File.ReadAllText(Path.Combine(_bin, "Server.dll")));
            Assert.AreEqual("new-server-exe", File.ReadAllText(Path.Combine(_bin, "Server.exe")));
            Assert.AreEqual("added lib", File.ReadAllText(Path.Combine(_bin, "NewLib.dll")));
            Assert.AreEqual("added in a new folder", File.ReadAllText(Path.Combine(_bin, "Sub", "Added.txt")));
            Assert.AreEqual("3", File.ReadAllText(Path.Combine(_bin, "Updater", "build.txt")));

            var protectedAfter = Snapshot(_bin, excludeTop: "update-staging");
            foreach (var touched in new[] { "Server.dll", "Server.exe", "Server.runtimeconfig.json", "NewLib.dll", "Sub/Added.txt", "Updater/LmpAgenciesUpdater.exe", "Updater/build.txt" })
                protectedAfter.Remove(touched);
            AssertSame(protectedBefore, protectedAfter, "the files outside the payload");

            Assert.AreEqual("universe", File.ReadAllText(Path.Combine(_bin, "Universe", "x.txt")));
            Assert.AreEqual("<settings />", File.ReadAllText(Path.Combine(_bin, "Config", "GeneralSettings.xml")));
            Assert.AreEqual("log line", File.ReadAllText(Path.Combine(_bin, "logs", "a.log")));
            Assert.AreEqual("universe backup", File.ReadAllText(Path.Combine(_bin, "Universe-backup", "y.txt")));
            Assert.AreEqual("bans", File.ReadAllText(Path.Combine(_bin, "LMPPlayerBans.txt")));

            // only the overwritten files were backed up: not user data, not the added files
            var backedUp = Snapshot(o.Backup).Keys.ToArray();
            CollectionAssert.AreEquivalent(
                new[] { "Server.dll", "Server.exe", "Server.runtimeconfig.json", "Updater/LmpAgenciesUpdater.exe", "Updater/build.txt" },
                backedUp);
            Assert.AreEqual("old-server-dll", File.ReadAllText(Path.Combine(o.Backup, "Server.dll")));
            Assert.IsFalse(Directory.Exists(o.Extract));
        }

        [DataTestMethod]
        [DataRow("Config/GeneralSettings.xml")]
        [DataRow("config/GeneralSettings.xml")]
        [DataRow("Universe/x.txt")]
        [DataRow("logs/a.log")]
        [DataRow("Plugins/plugin.dll")]
        [DataRow("update-staging/anything.txt")]
        [DataRow("UPDATE-STAGING/anything.txt")]
        public void Server_Rejects_PackageWithProtectedTopLevelFolder(string protectedEntry)
        {
            InstallServer();
            var before = Snapshot(_bin, excludeTop: "update-staging");
            var payload = ServerPayload(3);
            payload[protectedEntry] = B("from the package");
            var o = ServerOptions(payload);

            var result = UpdateInstaller.Run(o, Exited);

            AssertRejected(o, result, "Invalid update package");
            AssertSame(before, Snapshot(_bin, excludeTop: "update-staging"), "the server folder");
        }

        [TestMethod]
        public void Server_ProtectedNamesAreTheDocumentedOnes()
        {
            CollectionAssert.AreEquivalent(new[] { "Universe", "Config", "logs", "Plugins", "update-staging" }, UpdateInstaller.ServerProtected);
        }

        [DataTestMethod]
        [DataRow("Server.dll")]
        [DataRow("Server.exe")]
        [DataRow("Updater/LmpAgenciesUpdater.exe")]
        [DataRow("Updater/build.txt")]
        public void Server_Rejects_PackageMissingRequiredFile(string missing)
        {
            InstallServer();
            var before = Snapshot(_bin, excludeTop: "update-staging");
            var payload = ServerPayload(3);
            Assert.IsTrue(payload.Remove(missing));
            var o = ServerOptions(payload);

            var result = UpdateInstaller.Run(o, Exited);

            AssertRejected(o, result, "Invalid update package");
            AssertSame(before, Snapshot(_bin, excludeTop: "update-staging"), "the server folder");
        }

        [TestMethod]
        public void Server_Rejects_WrongBuildTxtAndDowngrade()
        {
            InstallServer();
            var before = Snapshot(_bin, excludeTop: "update-staging");

            var payload = ServerPayload(3);
            payload["Updater/build.txt"] = B("4");
            var o = ServerOptions(payload);
            AssertRejected(o, UpdateInstaller.Run(o, Exited), "Invalid update package");

            var downgrade = ServerOptions(ServerPayload(2), 2);
            AssertRejected(downgrade, UpdateInstaller.Run(downgrade, Exited), "Refusing to install agencies.2 over agencies.2.");

            AssertSame(before, Snapshot(_bin, excludeTop: "update-staging"), "the server folder");
        }

        [DataTestMethod]
        [DataRow("server-backed-up")]
        [DataRow("server-copied")]
        public void Server_Fault_RestoresBackedUpFilesAndDeletesAddedOnes(string faultPoint)
        {
            InstallServer();
            var before = Snapshot(_bin, excludeTop: "update-staging");
            var o = ServerOptions(ServerPayload(3));

            var result = UpdateInstaller.Run(o, Exited, FailAt(faultPoint));

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.Message.IndexOf("rolled back", StringComparison.OrdinalIgnoreCase) >= 0, result.Message);
            AssertResultFile(o, result);
            AssertSame(before, Snapshot(_bin, excludeTop: "update-staging"), "the server folder");
            Assert.IsFalse(File.Exists(Path.Combine(_bin, "NewLib.dll")), "added files are deleted");
            Assert.IsFalse(Directory.Exists(Path.Combine(_bin, "Sub")), "folders the update created are removed again");
        }

        [TestMethod]
        public void FaultPointNamesAreStable()
        {
            Assert.AreEqual("server-backed-up", UpdateInstaller.FaultServerBackedUp);
            Assert.AreEqual("server-copied", UpdateInstaller.FaultServerCopied);
        }

        // ------------------------------------------------------------------ single instance

        [TestMethod]
        public void ManualInstallerRepairsSameBuildPreservesUserFilesButNotObsoleteBinaries()
        {
            InstallClient("3");
            var mod = Path.Combine(_gameData, "LunaMultiplayer");
            WriteFile(Path.Combine(mod, "Notes", "custom.txt"), "personal note");
            WriteFile(Path.Combine(mod, "Plugins", "obsolete.dll"), "old binary");
            var options = ClientOptions(ClientPayload(3));
            var result = UpdateInstaller.Run(options, Exited, clientInstaller: true);
            Assert.IsTrue(result.Success, result.Message);
            CollectionAssert.AreEqual(SettingsBytes, File.ReadAllBytes(Path.Combine(mod, "Data", "settings.xml")));
            Assert.AreEqual("personal note", File.ReadAllText(Path.Combine(mod, "Notes", "custom.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(mod, "Plugins", "obsolete.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(options.Backup, "LunaMultiplayer", "Plugins", "obsolete.dll")));
        }

        [TestMethod]
        public void ManualInstallerNeverDowngrades()
        {
            InstallClient("4"); var before = Snapshot(_gameData);
            var result = UpdateInstaller.Run(ClientOptions(ClientPayload(3)), Exited, clientInstaller: true);
            Assert.IsFalse(result.Success); AssertSame(before, Snapshot(_gameData), "live GameData");
        }

        [TestMethod]
        public void FinalGuardRunsAfterExtractionAndBeforeAnyLiveDirectoryChanges()
        {
            InstallClient(); var before = Snapshot(_gameData); var options = ClientOptions(ClientPayload(3)); var called = false;
            var result = UpdateInstaller.Run(options, Exited, clientInstaller: true, preSwap: () =>
            {
                called = true;
                Assert.IsTrue(File.Exists(Path.Combine(options.Extract, "GameData", "LunaMultiplayer", "Plugins", "LmpClient.dll")));
                throw new InvalidOperationException("KSP started while extracting");
            });
            Assert.IsTrue(called); Assert.IsFalse(result.Success);
            AssertSame(before, Snapshot(_gameData), "both live mod trees");
            Assert.IsFalse(Directory.Exists(options.Backup), "Nothing was moved before the late guard.");
        }

        [TestMethod]
        public void ManualInstallerFailureRollsBackCustomFilesAndSettings()
        {
            InstallClient(); WriteFile(Path.Combine(_gameData, "LunaMultiplayer", "my-note.txt"), "keep"); var before = Snapshot(_gameData);
            var result = UpdateInstaller.Run(ClientOptions(ClientPayload(3)), Exited,
                point => { if (point == UpdateInstaller.FaultClientHarmonySwapped) throw new IOException("injected"); }, clientInstaller: true);
            Assert.IsFalse(result.Success); AssertSame(before, Snapshot(_gameData), "rolled back client");
        }

        private static bool OnOtherThread(Func<bool> action)
        {
            var result = false;
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { result = action(); }
                catch (Exception e) { failure = e; }
            });
            thread.Start();
            thread.Join();
            if (failure != null) throw new AssertFailedException("thread failed: " + failure);
            return result;
        }

        private static bool AcquireAndRelease(string target)
        {
            Mutex m;
            if (!UpdateInstaller.TryAcquire(target, out m)) return false;
            m.ReleaseMutex();
            m.Dispose();
            return true;
        }

        [TestMethod]
        public void TryAcquire_SecondCallForTheSameTargetFailsUntilTheFirstIsReleased()
        {
            var target = Path.Combine(_root, "KSP", "GameData");

            Mutex first;
            Assert.IsTrue(UpdateInstaller.TryAcquire(target, out first));
            Assert.IsNotNull(first);

            Assert.IsFalse(OnOtherThread(() => AcquireAndRelease(target)), "same target");
            Assert.IsFalse(OnOtherThread(() => AcquireAndRelease(target + Path.DirectorySeparatorChar)), "trailing separator");
            Assert.IsFalse(OnOtherThread(() => AcquireAndRelease(target.ToUpperInvariant())), "case difference");
            Assert.IsTrue(OnOtherThread(() => AcquireAndRelease(Path.Combine(_root, "Other", "GameData"))), "a different target is independent");

            first.ReleaseMutex();
            first.Dispose();

            Assert.IsTrue(OnOtherThread(() => AcquireAndRelease(target)), "free again after release");
        }

        [TestMethod]
        public void MutexName_IsGlobalAndDerivedFromTheNormalizedTarget()
        {
            var target = Path.Combine(_root, "KSP", "GameData");
            var name = UpdateInstaller.MutexName(target);
            StringAssert.StartsWith(name, @"Global\LmpAgenciesUpdater-");
            var suffix = name.Substring(@"Global\LmpAgenciesUpdater-".Length);
            Assert.AreEqual(16, suffix.Length);
            Assert.IsTrue(suffix.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), suffix);

            Assert.AreEqual(name, UpdateInstaller.MutexName(target + Path.DirectorySeparatorChar));
            Assert.AreEqual(name, UpdateInstaller.MutexName(target.ToLowerInvariant()));
            Assert.AreEqual(name, UpdateInstaller.MutexName(Path.Combine(_root, "KSP", "Other", "..", "GameData")));
            Assert.AreNotEqual(name, UpdateInstaller.MutexName(Path.Combine(_root, "KSP", "Other")));
        }
    }
}
