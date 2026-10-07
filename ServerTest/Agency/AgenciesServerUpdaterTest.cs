using LmpAgenciesUpdater;
using LmpCommon.Agency;
using LmpCommon.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Server.Agency;
using Server.Settings.Definition;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ServerTest.Agency
{
    // The updater reads GeneralSettings.SettingsStore and writes to the shared console logger, so these tests are serial.
    [TestClass, DoNotParallelize]
    public class AgenciesServerUpdaterTest
    {
        private const string HelperEntry = "Updater/LmpAgenciesUpdater.exe";
        private static readonly byte[] HelperBytes = Encoding.ASCII.GetBytes("pretend this is the updater helper exe");

        private static int Newer => AgenciesBuild.Number + 1;

        private string _binaries;
        private string _staging;
        private bool _previousAutoUpdate;
        private TextWriter _previousOut;
        private StringWriter _log;

        [TestInitialize]
        public void Setup()
        {
            _binaries = Path.Combine(Path.GetTempPath(), "LMPUpdaterTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_binaries);
            _staging = Path.Combine(_binaries, "update-staging");
            _previousAutoUpdate = GeneralSettings.SettingsStore.AgencyAutoUpdate;
            _previousOut = Console.Out;
            _log = new StringWriter();
            Console.SetOut(_log);
        }

        [TestCleanup]
        public void Teardown()
        {
            Console.SetOut(_previousOut);
            GeneralSettings.SettingsStore.AgencyAutoUpdate = _previousAutoUpdate;
            try { Directory.Delete(_binaries, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ---- Fake release host ----

        private sealed class Fake
        {
            public int Build;
            public string Tag;
            public byte[] Zip;
            public string ZipUrl, SumsUrl, ApiJson;
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public readonly List<string> Requested = new List<string>();
            public Exception FailWith;
            public ProcessStartInfo Started;
            public int StartCount;

            public Task<byte[]> Fetch(string url)
            {
                Requested.Add(url);
                if (FailWith != null) throw FailWith;
                byte[] body;
                if (!Files.TryGetValue(url, out body)) throw new InvalidOperationException("Unexpected request: " + url);
                return Task.FromResult(body);
            }

            public void Start(ProcessStartInfo psi)
            {
                StartCount++;
                Started = psi;
            }
        }

        private static byte[] BuildZip(int build, bool includeHelper = true)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Add(zip, "Server.dll", "new server dll");
                    Add(zip, "Server.exe", "new server exe");
                    if (includeHelper) Add(zip, HelperEntry, HelperBytes);
                    Add(zip, "Updater/build.txt", build.ToString());
                }
                return ms.ToArray();
            }
        }

        private static void Add(ZipArchive zip, string name, string text) => Add(zip, name, Encoding.UTF8.GetBytes(text));

        private static void Add(ZipArchive zip, string name, byte[] bytes)
        {
            using (var s = zip.CreateEntry(name).Open()) s.Write(bytes, 0, bytes.Length);
        }

        private static string Sha(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

        /// <param name="sumsHash">What SHA256SUMS.txt lists for the server zip; the real hash when null.</param>
        /// <param name="apiDigest">The API digest for the server zip: the real hash when null, no digest at all when "".</param>
        private static Fake Release(int build, bool includeHelper = true, string sumsHash = null, string apiDigest = null)
        {
            var fake = new Fake { Build = build, Tag = "v" + AgenciesBuild.UpstreamVersion + "-agencies." + build };
            fake.Zip = BuildZip(build, includeHelper);
            var real = Sha(fake.Zip);

            var clientName = AgenciesBuild.AssetName(AgenciesBuild.ClientAssetPrefix, fake.Tag);
            var serverName = AgenciesBuild.AssetName(AgenciesBuild.ServerAssetPrefix, fake.Tag);
            string Url(string name) => AgenciesBuild.DownloadPrefix + fake.Tag + "/" + name;

            fake.ZipUrl = Url(serverName);
            fake.SumsUrl = Url(AgenciesBuild.SumsAsset);

            var serverAsset = new JObject { ["name"] = serverName, ["browser_download_url"] = fake.ZipUrl };
            if (apiDigest != "") serverAsset["digest"] = "sha256:" + (apiDigest ?? real);
            var json = new JObject
            {
                ["tag_name"] = fake.Tag,
                ["draft"] = false,
                ["prerelease"] = false,
                ["body"] = "release notes",
                ["assets"] = new JArray
                {
                    new JObject { ["name"] = clientName, ["browser_download_url"] = Url(clientName), ["digest"] = "sha256:" + new string('a', 64) },
                    serverAsset,
                    new JObject { ["name"] = AgenciesBuild.SumsAsset, ["browser_download_url"] = fake.SumsUrl }
                }
            };

            fake.ApiJson = json.ToString();
            fake.Files[AgenciesBuild.LatestReleaseApi] = Encoding.UTF8.GetBytes(fake.ApiJson);
            fake.Files[fake.SumsUrl] = Encoding.UTF8.GetBytes((sumsHash ?? real) + "  " + serverName + "\n" + new string('b', 64) + "  " + clientName + "\n");
            fake.Files[fake.ZipUrl] = fake.Zip;
            return fake;
        }

        private Task<bool> Boot(Fake fake, string[] args = null, bool? isWindows = true, int? pid = 4242, string workingDir = null)
            => AgenciesServerUpdater.TryUpdateAtBootAsync(fake.Fetch, _binaries, args ?? new string[0], workingDir ?? _binaries, fake.Start, isWindows, pid);

        private string StagingDirFor(int build) => AgenciesUpdatePaths.StagingDir(_staging, build);
        private string FailedBuildPath => Path.Combine(_staging, "failed-build.txt");
        private string ResultPath => AgenciesUpdatePaths.ResultPath(_staging);

        private void WriteResult(bool success, int build, string message = "something happened")
        {
            HelperCommandLine.WriteResult(ResultPath, success, build, AgenciesBuild.Number, message);
        }

        private static string ConsoleText(StringWriter writer)
        {
            lock (writer) return writer.ToString();
        }

        // ---- Boot behaviour ----

        [TestMethod]
        public async Task DisabledButNewerOnlyLogsAndStartsNothing()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = false;
            var fake = Release(Newer);

            Assert.IsFalse(await Boot(fake));

            Assert.AreEqual(0, fake.StartCount);
            CollectionAssert.AreEqual(new[] { AgenciesBuild.LatestReleaseApi }, fake.Requested, "nothing is downloaded while the setting is off");
            Assert.IsFalse(Directory.Exists(_staging), "a disabled server writes nothing");
            var log = ConsoleText(_log);
            StringAssert.Contains(log, "Agencies update available: agencies." + Newer);
            StringAssert.Contains(log, "AgencyAutoUpdate");
        }

        [TestMethod]
        public async Task EnabledAndNewerStagesTheVerifiedZipAndStartsTheHelper()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(Newer);
            var originalArgs = new[] { "-d", @"D:\My Server\", "--x", "a \"b\"" };
            var workingDir = Path.Combine(_binaries, "work dir");

            Assert.IsTrue(await Boot(fake, originalArgs, workingDir: workingDir));

            Assert.AreEqual(1, fake.StartCount);
            // Only the API, the sums and the server zip are fetched, at exactly the trusted URLs.
            CollectionAssert.AreEqual(new[] { AgenciesBuild.LatestReleaseApi, fake.SumsUrl, fake.ZipUrl }, fake.Requested);

            var staging = StagingDirFor(Newer);
            var helper = Path.Combine(staging, "LmpAgenciesUpdater.exe");
            var zipPath = Path.Combine(staging, AgenciesBuild.AssetName(AgenciesBuild.ServerAssetPrefix, fake.Tag));
            Assert.AreEqual(helper, fake.Started.FileName);
            Assert.IsFalse(fake.Started.UseShellExecute);
            Assert.IsTrue(fake.Started.CreateNoWindow);
            CollectionAssert.AreEqual(HelperBytes, File.ReadAllBytes(helper));
            CollectionAssert.AreEqual(fake.Zip, File.ReadAllBytes(zipPath));
            // The helper is the only thing taken out of the zip.
            CollectionAssert.AreEquivalent(
                new[] { "LmpAgenciesUpdater.exe", "SHA256SUMS.txt", Path.GetFileName(zipPath) },
                Directory.GetFileSystemEntries(staging).Select(Path.GetFileName).ToArray());

            var o = HelperCommandLine.Parse(Split(fake.Started.Arguments).ToArray());
            Assert.AreEqual(HelperMode.Server, o.Mode);
            Assert.AreEqual(zipPath, o.Zip);
            Assert.AreEqual(Sha(fake.Zip), o.Sha256);
            Assert.AreEqual(Path.GetFullPath(_binaries), o.Target);
            Assert.AreEqual(AgenciesUpdatePaths.ExtractDir(_staging, Newer), o.Extract);
            Assert.AreEqual(AgenciesUpdatePaths.BackupDir(_staging), o.Backup);
            Assert.AreEqual(ResultPath, o.Result);
            Assert.AreEqual(4242, o.Pid);
            Assert.AreEqual(Newer, o.Build);
            Assert.AreEqual(600, o.WaitTimeoutSeconds);
            Assert.AreEqual(Path.Combine(Path.GetFullPath(_binaries), "Server.exe"), o.Relaunch);
            Assert.AreEqual(workingDir, o.RelaunchDir);
            // -d "D:\My Server\" (trailing backslash) and an embedded quote survive the base64 hop.
            CollectionAssert.AreEqual(originalArgs, Split(o.RelaunchArgs).ToArray());
            StringAssert.Contains(ConsoleText(_log), "Installing agencies." + Newer);
        }

        [TestMethod]
        public async Task ZipThatDoesNotMatchTheSumsIsDeletedAndNothingStarts()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var wrong = Sha(Encoding.ASCII.GetBytes("some other file"));
            var fake = Release(Newer, sumsHash: wrong, apiDigest: wrong);

            Assert.IsFalse(await Boot(fake));

            Assert.AreEqual(0, fake.StartCount);
            var staging = StagingDirFor(Newer);
            Assert.IsFalse(File.Exists(Path.Combine(staging, AgenciesBuild.AssetName(AgenciesBuild.ServerAssetPrefix, fake.Tag))), "the unverified zip is removed");
            Assert.IsFalse(File.Exists(Path.Combine(staging, "LmpAgenciesUpdater.exe")), "nothing is extracted from an unverified zip");
        }

        [TestMethod]
        public async Task ApiDigestThatDisagreesWithTheSumsStartsNothing()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(Newer, apiDigest: Sha(Encoding.ASCII.GetBytes("a different digest")));

            Assert.IsFalse(await Boot(fake));

            Assert.AreEqual(0, fake.StartCount);
            Assert.IsFalse(File.Exists(Path.Combine(StagingDirFor(Newer), "LmpAgenciesUpdater.exe")));
            Assert.IsFalse(Directory.EnumerateFiles(_staging, "*.zip", SearchOption.AllDirectories).Any(), "no zip is kept");
        }

        [TestMethod]
        public async Task ReleaseWithoutAnApiDigestVerifiesAgainstTheSumsAlone()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(Newer, apiDigest: "");

            Assert.IsTrue(await Boot(fake));
            Assert.AreEqual(1, fake.StartCount);
        }

        [TestMethod]
        public async Task SameBuildStartsNothing()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(AgenciesBuild.Number);

            Assert.IsFalse(await Boot(fake));

            Assert.AreEqual(0, fake.StartCount);
            CollectionAssert.AreEqual(new[] { AgenciesBuild.LatestReleaseApi }, fake.Requested);
        }

        [TestMethod]
        public async Task OlderReleaseStartsNothing()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(AgenciesBuild.Number - 1);

            Assert.IsFalse(await Boot(fake));
            Assert.AreEqual(0, fake.StartCount);
        }

        [TestMethod]
        public async Task FetchFailureIsLoggedAndBootContinues()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(Newer);
            fake.FailWith = new global::System.Net.Http.HttpRequestException("offline");

            Assert.IsFalse(await Boot(fake));

            Assert.AreEqual(0, fake.StartCount);
            StringAssert.Contains(ConsoleText(_log), "offline");
        }

        [TestMethod]
        public async Task UnparseableLatestReleaseStartsNothing()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = new Fake();
            fake.Files[AgenciesBuild.LatestReleaseApi] = Encoding.UTF8.GetBytes("<html>not json</html>");

            Assert.IsFalse(await Boot(fake));
            Assert.AreEqual(0, fake.StartCount);
        }

        [TestMethod]
        public async Task NonWindowsOnlyLogsAvailability()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(Newer);

            Assert.IsFalse(await Boot(fake, isWindows: false));

            Assert.AreEqual(0, fake.StartCount);
            CollectionAssert.AreEqual(new[] { AgenciesBuild.LatestReleaseApi }, fake.Requested);
            StringAssert.Contains(ConsoleText(_log), "Agencies update available: agencies." + Newer);
        }

        [TestMethod]
        public async Task StartFailureIsLoggedAndBootContinues()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(Newer);

            var started = await AgenciesServerUpdater.TryUpdateAtBootAsync(fake.Fetch, _binaries, new string[0], _binaries,
                _ => throw new InvalidOperationException("cannot launch"), true, 1);

            Assert.IsFalse(started);
            StringAssert.Contains(ConsoleText(_log), "cannot launch");
        }

        [TestMethod]
        public async Task PackageWithoutTheHelperStartsNothing()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var fake = Release(Newer, includeHelper: false);

            Assert.IsFalse(await Boot(fake));

            Assert.AreEqual(0, fake.StartCount);
            Assert.IsFalse(File.Exists(Path.Combine(StagingDirFor(Newer), "LmpAgenciesUpdater.exe")));
        }

        // ---- Failure memory ----

        [TestMethod]
        public async Task FailedLastResultSkipsThatBuildAndPersistsIt()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            Directory.CreateDirectory(_staging);
            WriteResult(false, Newer, "rolled back");
            var fake = Release(Newer);

            Assert.IsFalse(await Boot(fake));

            Assert.AreEqual(0, fake.StartCount);
            CollectionAssert.AreEqual(new[] { AgenciesBuild.LatestReleaseApi }, fake.Requested, "a failed build is never downloaded again");
            Assert.AreEqual(Newer.ToString(), File.ReadAllText(FailedBuildPath).Trim());
            Assert.IsFalse(File.Exists(ResultPath), "the result is consumed");
            var log = ConsoleText(_log);
            StringAssert.Contains(log, "rolled back");
            StringAssert.Contains(log, "skipping agencies." + Newer);
        }

        [TestMethod]
        public async Task PersistedFailureStillSkipsOnLaterBoots()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            Directory.CreateDirectory(_staging);
            WriteResult(false, Newer);
            var first = Release(Newer);
            Assert.IsFalse(await Boot(first));

            // Next boot: the result file is gone, only failed-build.txt remains.
            Assert.IsFalse(File.Exists(ResultPath));
            var second = Release(Newer);
            Assert.IsFalse(await Boot(second));

            Assert.AreEqual(0, second.StartCount);
            CollectionAssert.AreEqual(new[] { AgenciesBuild.LatestReleaseApi }, second.Requested);
            Assert.IsTrue(File.Exists(FailedBuildPath), "the memory is kept while that release is still the latest");
        }

        [TestMethod]
        public async Task NewerReleaseClearsThePersistedFailureAndProceeds()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            Directory.CreateDirectory(_staging);
            File.WriteAllText(FailedBuildPath, Newer.ToString());
            var fake = Release(Newer + 1);

            Assert.IsTrue(await Boot(fake));

            Assert.AreEqual(1, fake.StartCount);
            Assert.AreEqual(Newer + 1, HelperCommandLine.Parse(Split(fake.Started.Arguments).ToArray()).Build);
            Assert.IsFalse(File.Exists(FailedBuildPath));
        }

        [TestMethod]
        public async Task SuccessResultForABuildThatNeverTookEffectSkipsIt()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            Directory.CreateDirectory(_staging);
            // The helper said it installed agencies.Newer, but this process is still on Number.
            WriteResult(true, Newer, "Updated agencies." + AgenciesBuild.Number + " -> agencies." + Newer);
            var fake = Release(Newer);

            Assert.IsFalse(await Boot(fake));

            Assert.AreEqual(0, fake.StartCount);
            Assert.AreEqual(Newer.ToString(), File.ReadAllText(FailedBuildPath).Trim());
            Assert.IsFalse(File.Exists(ResultPath));
        }

        [TestMethod]
        public async Task SuccessResultForTheRunningBuildIsConsumedWithoutRecordingAFailure()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            Directory.CreateDirectory(_staging);
            WriteResult(true, AgenciesBuild.Number, "Updated agencies." + (AgenciesBuild.Number - 1) + " -> agencies." + AgenciesBuild.Number);
            var fake = Release(AgenciesBuild.Number);

            Assert.IsFalse(await Boot(fake));

            Assert.IsFalse(File.Exists(ResultPath));
            Assert.IsFalse(File.Exists(FailedBuildPath));
            StringAssert.Contains(ConsoleText(_log), "Updated agencies.");
        }

        [TestMethod]
        public async Task AFailedResultDoesNotBlockALaterNewerRelease()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            Directory.CreateDirectory(_staging);
            WriteResult(false, Newer);
            var fake = Release(Newer + 1);

            Assert.IsTrue(await Boot(fake));

            Assert.AreEqual(1, fake.StartCount);
            Assert.IsFalse(File.Exists(FailedBuildPath), "the newer release made the old failure irrelevant");
        }

        // ---- Staging cleanup ----

        [TestMethod]
        public async Task StagingCleanupRemovesOldBuildFoldersButKeepsTheBackupAndMarkers()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var old = StagingDirFor(1);
            var current = StagingDirFor(AgenciesBuild.Number);
            var backup = AgenciesUpdatePaths.BackupDir(_staging);
            Directory.CreateDirectory(Path.Combine(old, "extract"));
            File.WriteAllText(Path.Combine(old, "extract", "x.dll"), "x");
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(current, "LmpAgenciesUpdater.exe"), "may still be running");
            Directory.CreateDirectory(Path.Combine(backup, "Config"));
            File.WriteAllText(Path.Combine(backup, "Server.dll"), "previous server dll");
            File.WriteAllText(Path.Combine(backup, "Config", "kept.xml"), "kept");
            var failedFile = FailedBuildPath;
            File.WriteAllText(failedFile, "99");

            Assert.IsFalse(await Boot(Release(AgenciesBuild.Number)));

            Assert.IsFalse(Directory.Exists(old), "an old build's staging folder is deleted");
            Assert.IsTrue(Directory.Exists(current), "the running build's folder holds the helper that may still be exiting");
            Assert.AreEqual("previous server dll", File.ReadAllText(Path.Combine(backup, "Server.dll")));
            Assert.AreEqual("kept", File.ReadAllText(Path.Combine(backup, "Config", "kept.xml")));
            Assert.AreEqual("99", File.ReadAllText(failedFile), "failed-build.txt is memory, not staging");
        }

        [TestMethod]
        public async Task StagingCleanupHappensEvenWhenTheUpdateCheckFails()
        {
            GeneralSettings.SettingsStore.AgencyAutoUpdate = true;
            var old = StagingDirFor(1);
            Directory.CreateDirectory(old);
            var backup = AgenciesUpdatePaths.BackupDir(_staging);
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "Server.exe"), "old");
            var fake = new Fake { FailWith = new InvalidOperationException("no network") };

            Assert.IsFalse(await Boot(fake));

            Assert.IsFalse(Directory.Exists(old));
            Assert.IsTrue(File.Exists(Path.Combine(backup, "Server.exe")));
        }

        // ---- FetchLatestAsync / HttpFetch / setting ----

        [TestMethod]
        public async Task FetchLatestParsesTheLatestReleaseFromTheApiUrl()
        {
            var fake = Release(Newer);

            var info = await AgenciesServerUpdater.FetchLatestAsync(fake.Fetch);

            Assert.IsNotNull(info);
            Assert.AreEqual(Newer, info.Build);
            Assert.AreEqual(fake.Tag, info.Tag);
            CollectionAssert.AreEqual(new[] { AgenciesBuild.LatestReleaseApi }, fake.Requested);
        }

        [TestMethod]
        public async Task FetchLatestReturnsNullForAReleaseThatIsNotAnAgenciesRelease()
        {
            var fake = new Fake();
            fake.Files[AgenciesBuild.LatestReleaseApi] = Encoding.UTF8.GetBytes("{\"tag_name\":\"v1.2.3\",\"assets\":[]}");

            Assert.IsNull(await AgenciesServerUpdater.FetchLatestAsync(fake.Fetch));
        }

        [TestMethod]
        public async Task FetchLatestPropagatesFetchFailures()
        {
            var fake = new Fake { FailWith = new InvalidOperationException("boom") };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => AgenciesServerUpdater.FetchLatestAsync(fake.Fetch));
        }

        [TestMethod]
        public async Task HttpFetchRefusesNonHttpsUrlsBeforeTouchingTheNetwork()
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => AgenciesServerUpdater.HttpFetch("http://github.com/duzos/LunaMP-Agencies/releases/download/x/y.zip"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => AgenciesServerUpdater.HttpFetch("file:///C:/Windows/win.ini"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => AgenciesServerUpdater.HttpFetch("https://example.com/duzos/LunaMP-Agencies/releases/download/x/y.zip"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => AgenciesServerUpdater.HttpFetch("https://api.github.com/repos/someone-else/LunaMP-Agencies/releases/latest"));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => AgenciesServerUpdater.HttpFetch(null));
        }

        [TestMethod]
        public void AgencyAutoUpdateDefaultsOffAndIsDocumentedInTheConfigFile()
        {
            Assert.IsFalse(new GeneralSettingsDefinition().AgencyAutoUpdate);

            var comment = typeof(GeneralSettingsDefinition).GetProperty("AgencyAutoUpdate").GetCustomAttribute<XmlCommentAttribute>();
            Assert.IsNotNull(comment);
            Assert.AreEqual(
                "If true, the server checks duzos/LunaMP-Agencies releases at startup on Windows and installs a newer agencies build before accepting players. " +
                "Only files in the release package are replaced; Universe/Config/logs/Plugins are never touched; replaced files are backed up in update-staging/backup.",
                comment.Value);

            var path = Path.GetTempFileName();
            try
            {
                LunaXmlSerializer.WriteToXmlFile(new GeneralSettingsDefinition(), path);
                var xml = File.ReadAllText(path);
                StringAssert.Contains(xml, "<AgencyAutoUpdate>false</AgencyAutoUpdate>");
                StringAssert.Contains(xml, "update-staging/backup");

                File.WriteAllText(path, xml.Replace("<AgencyAutoUpdate>false</AgencyAutoUpdate>", "<AgencyAutoUpdate>true</AgencyAutoUpdate>"));
                Assert.IsTrue(((GeneralSettingsDefinition)LunaXmlSerializer.ReadXmlFromPath(typeof(GeneralSettingsDefinition), path)).AgencyAutoUpdate);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        public void ConfigWrittenBeforeTheSettingExistedGainsTheDefault()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "<GeneralSettingsDefinition><ServerName>Old server</ServerName></GeneralSettingsDefinition>");
                var loaded = (GeneralSettingsDefinition)LunaXmlSerializer.ReadXmlFromPath(typeof(GeneralSettingsDefinition), path);
                Assert.AreEqual("Old server", loaded.ServerName);
                Assert.IsFalse(loaded.AgencyAutoUpdate);
            }
            finally { File.Delete(path); }
        }

        // ---- Reference CommandLineToArgvW splitter (the helper's real parser sees exactly this) ----

        private static List<string> Split(string commandLine)
        {
            var args = new List<string>();
            var i = 0;
            var n = commandLine.Length;
            while (true)
            {
                while (i < n && (commandLine[i] == ' ' || commandLine[i] == '\t')) i++;
                if (i >= n) break;

                var sb = new StringBuilder();
                var inQuotes = false;
                while (i < n)
                {
                    var c = commandLine[i];
                    if (c == '\\')
                    {
                        var slashes = 0;
                        while (i < n && commandLine[i] == '\\') { slashes++; i++; }
                        if (i < n && commandLine[i] == '"')
                        {
                            sb.Append('\\', slashes / 2);
                            if (slashes % 2 == 1) { sb.Append('"'); i++; }
                        }
                        else sb.Append('\\', slashes);
                    }
                    else if (c == '"')
                    {
                        if (inQuotes && i + 1 < n && commandLine[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { inQuotes = !inQuotes; i++; }
                    }
                    else if (!inQuotes && (c == ' ' || c == '\t')) break;
                    else { sb.Append(c); i++; }
                }
                args.Add(sb.ToString());
            }
            return args;
        }
    }
}
