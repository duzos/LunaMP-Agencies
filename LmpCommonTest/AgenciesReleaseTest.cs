using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace LmpCommonTest
{
    [TestClass]
    public class AgenciesReleaseTest
    {
        private const string H1 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string H2 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        private const string Tag3 = "v0.30.0-agencies.3";

        private static string ClientName(string tag = Tag3) => AgenciesBuild.AssetName(AgenciesBuild.ClientAssetPrefix, tag);
        private static string ServerName(string tag = Tag3) => AgenciesBuild.AssetName(AgenciesBuild.ServerAssetPrefix, tag);
        private static string UrlOf(string name, string tag = Tag3) => AgenciesBuild.DownloadPrefix + tag + "/" + name;

        private static JObject Asset(string name, string digest, string url = null)
        {
            var asset = new JObject { ["name"] = name, ["browser_download_url"] = url ?? UrlOf(name) };
            if (digest != null) asset["digest"] = digest;
            return asset;
        }

        /// <summary>A release with the three assets for agencies.3; zips carry digests (the client one upper-cased on purpose).</summary>
        private static JObject Release()
        {
            return new JObject
            {
                ["tag_name"] = Tag3,
                ["draft"] = false,
                ["prerelease"] = false,
                ["body"] = "Changelog line",
                ["assets"] = new JArray(
                    Asset(ClientName(), "sha256:" + H1.ToUpperInvariant()),
                    Asset(ServerName(), "sha256:" + H2),
                    Asset(AgenciesBuild.SumsAsset, null))
            };
        }

        private static JObject AssetNamed(JObject release, string name)
        {
            foreach (var a in (JArray)release["assets"])
                if ((string)a["name"] == name) return (JObject)a;
            throw new InvalidOperationException("no asset " + name);
        }

        private static AgenciesReleaseInfo Info() => AgenciesRelease.Parse(Release().ToString());

        // ---- AgenciesBuild ----

        [TestMethod]
        public void BuildTagMatchesNumber()
        {
            Assert.AreEqual(3, AgenciesBuild.Number);
            Assert.AreEqual("v0.30.0-agencies.3", AgenciesBuild.Tag);
            Assert.AreEqual(AgenciesBuild.Number, AgenciesRelease.ParseBuild(AgenciesBuild.Tag));
        }

        [TestMethod]
        public void BuildConstantsAreDerivedFromOwnerAndRepo()
        {
            Assert.AreEqual("https://api.github.com/repos/duzos/LunaMP-Agencies/releases/latest", AgenciesBuild.LatestReleaseApi);
            Assert.AreEqual("https://github.com/duzos/LunaMP-Agencies/releases/latest", AgenciesBuild.ReleasesPage);
            Assert.AreEqual("https://github.com/duzos/LunaMP-Agencies/releases/download/", AgenciesBuild.DownloadPrefix);
            Assert.AreEqual("LunaMultiplayer-Agencies-Client-v0.30.0-agencies.3.zip", ClientName());
            Assert.AreEqual("LunaMultiplayer-Agencies-Server-v0.30.0-agencies.3.zip", ServerName());
            Assert.AreEqual("SHA256SUMS.txt", AgenciesBuild.SumsAsset);
        }

        [TestMethod]
        public void UpdatePathsAreDerivedFromTheRoot()
        {
            var root = Path.Combine("some", "root");
            Assert.AreEqual(Path.Combine(root, "3"), AgenciesUpdatePaths.StagingDir(root, 3));
            Assert.AreEqual(Path.Combine(root, "3", "extract"), AgenciesUpdatePaths.ExtractDir(root, 3));
            Assert.AreEqual(Path.Combine(root, "backup"), AgenciesUpdatePaths.BackupDir(root));
            Assert.AreEqual(Path.Combine(root, "last-result.txt"), AgenciesUpdatePaths.ResultPath(root));
        }

        // ---- ParseBuild ----

        [DataTestMethod]
        [DataRow("v0.30.0-agencies.3", 3)]
        [DataRow("v0.31.2-agencies.12", 12)]
        [DataRow("v10.0.0-agencies.2147483647", int.MaxValue)]
        public void ParseBuildReadsTheCounter(string tag, int expected)
        {
            Assert.AreEqual(expected, AgenciesRelease.ParseBuild(tag));
        }

        [DataTestMethod]
        [DataRow("0.30.0")]
        [DataRow("v0.30.0")]
        [DataRow("v0.30.0-agencies.")]
        [DataRow("v0.30.0-agencies.x")]
        [DataRow("v0.30.0-agencies.-1")]
        [DataRow("v0.30.0-agencies.0")]
        [DataRow("v0.30.0-agencies.2147483648")]
        [DataRow("v0.30.0-agencies.3\n")]
        [DataRow(" v0.30.0-agencies.3")]
        [DataRow("v0.30-agencies.3")]
        [DataRow("v0.30.0-agencies.3-rc1")]
        [DataRow("v0.30.0-agencies.٣")] // an Arabic-indic digit must not count
        [DataRow("")]
        [DataRow((string)null)]
        public void ParseBuildRejectsEverythingElse(string tag)
        {
            Assert.IsNull(AgenciesRelease.ParseBuild(tag));
        }

        // ---- Parse ----

        [TestMethod]
        public void ParseReadsAGoodRelease()
        {
            var info = Info();
            Assert.IsNotNull(info);
            Assert.AreEqual(3, info.Build);
            Assert.AreEqual(Tag3, info.Tag);
            Assert.AreEqual("Changelog line", info.Changelog);
            Assert.AreEqual(ClientName(), info.ClientZipName);
            Assert.AreEqual(UrlOf(ClientName()), info.ClientZipUrl);
            Assert.AreEqual(ServerName(), info.ServerZipName);
            Assert.AreEqual(UrlOf(ServerName()), info.ServerZipUrl);
            Assert.AreEqual(UrlOf(AgenciesBuild.SumsAsset), info.SumsUrl);
            Assert.AreEqual(H1, info.ClientDigest, "digests are lowercased");
            Assert.AreEqual(H2, info.ServerDigest);
        }

        [TestMethod]
        public void ParseWithoutDigestsLeavesThemNull()
        {
            var release = Release();
            ((JObject)AssetNamed(release, ClientName())).Remove("digest");
            AssetNamed(release, ServerName())["digest"] = JValue.CreateNull();
            var info = AgenciesRelease.Parse(release.ToString());
            Assert.IsNotNull(info);
            Assert.IsNull(info.ClientDigest);
            Assert.IsNull(info.ServerDigest);
        }

        [DataTestMethod]
        [DataRow("sha512:" + H1)]
        [DataRow("sha256:abc")]
        [DataRow("sha256:" + H1 + "00")]
        [DataRow("md5:" + H1)]
        [DataRow(H1)]
        [DataRow("")]
        public void ParseIgnoresDigestsThatAreNotSha256Hex(string digest)
        {
            var release = Release();
            AssetNamed(release, ClientName())["digest"] = digest;
            var info = AgenciesRelease.Parse(release.ToString());
            Assert.IsNotNull(info);
            Assert.IsNull(info.ClientDigest);
        }

        [TestMethod]
        public void ParseAcceptsAMissingDraftAndPrereleaseFlag()
        {
            var release = Release();
            release.Remove("draft");
            release.Remove("prerelease");
            Assert.IsNotNull(AgenciesRelease.Parse(release.ToString()), "/releases/latest already excludes both; only an explicit true is rejected");
        }

        [TestMethod]
        public void ParseRejectsPrereleasesAndDrafts()
        {
            var pre = Release(); pre["prerelease"] = true;
            Assert.IsNull(AgenciesRelease.Parse(pre.ToString()));
            var draft = Release(); draft["draft"] = true;
            Assert.IsNull(AgenciesRelease.Parse(draft.ToString()));
            var weird = Release(); weird["draft"] = "no";
            Assert.IsNull(AgenciesRelease.Parse(weird.ToString()), "a non-boolean flag fails closed");
        }

        [TestMethod]
        public void ParseRejectsAMissingAsset()
        {
            foreach (var name in new[] { ClientName(), ServerName(), AgenciesBuild.SumsAsset })
            {
                var release = Release();
                AssetNamed(release, name).Remove();
                Assert.IsNull(AgenciesRelease.Parse(release.ToString()), "missing " + name);
            }
        }

        [TestMethod]
        public void ParseRejectsAssetsNamedForADifferentTag()
        {
            var release = Release();
            release["tag_name"] = "v0.30.0-agencies.4";
            Assert.IsNull(AgenciesRelease.Parse(release.ToString()));
        }

        [DataTestMethod]
        [DataRow("https://evil.example/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/{0}", "wrong host")]
        [DataRow("https://github.com/evil/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/{0}", "wrong owner")]
        [DataRow("https://github.com/duzos/Other/releases/download/v0.30.0-agencies.3/{0}", "wrong repo")]
        [DataRow("http://github.com/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/{0}", "http")]
        [DataRow("https://github.com/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/{0}?token=1", "query")]
        [DataRow("https://github.com/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/{0}#x", "fragment")]
        [DataRow("https://github.com/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/../v0.30.0-agencies.3/{0}", "dot dot")]
        [DataRow("https://github.com/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.2/{0}", "other tag folder")]
        [DataRow("https://github.com/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3//{0}", "double slash")]
        [DataRow("HTTPS://GITHUB.COM/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/{0}", "host case")]
        [DataRow("https://github.com/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/{0} ", "trailing space")]
        [DataRow("https://github.com/duzos/LunaMP-Agencies/releases/download/v0.30.0-agencies.3/{0}/", "trailing slash")]
        [DataRow("", "empty")]
        public void ParseRejectsUntrustedAssetUrls(string urlTemplate, string why)
        {
            foreach (var victim in new[] { ClientName(), ServerName(), AgenciesBuild.SumsAsset })
            {
                var release = Release();
                AssetNamed(release, victim)["browser_download_url"] = string.Format(urlTemplate, victim);
                Assert.IsNull(AgenciesRelease.Parse(release.ToString()), why + " for " + victim);
            }
        }

        [TestMethod]
        public void ParseRejectsAnAssetWithoutAUrl()
        {
            var release = Release();
            AssetNamed(release, ServerName()).Remove("browser_download_url");
            Assert.IsNull(AgenciesRelease.Parse(release.ToString()));
        }

        [TestMethod]
        public void ParseRejectsABadTag()
        {
            foreach (var tag in new[] { "v0.30.0", "agencies.3", "v0.30.0-agencies.0", "v0.30.0-agencies.3/../x" })
            {
                var release = Release();
                release["tag_name"] = tag;
                Assert.IsNull(AgenciesRelease.Parse(release.ToString()), tag);
            }
            var noTag = Release(); noTag.Remove("tag_name");
            Assert.IsNull(AgenciesRelease.Parse(noTag.ToString()));
            var numberTag = Release(); numberTag["tag_name"] = 3;
            Assert.IsNull(AgenciesRelease.Parse(numberTag.ToString()));
        }

        [TestMethod]
        public void ParseRejectsGarbage()
        {
            Assert.IsNull(AgenciesRelease.Parse(null));
            Assert.IsNull(AgenciesRelease.Parse(""));
            Assert.IsNull(AgenciesRelease.Parse("   "));
            Assert.IsNull(AgenciesRelease.Parse("not json"));
            Assert.IsNull(AgenciesRelease.Parse("[]"));
            Assert.IsNull(AgenciesRelease.Parse("{}"));
            Assert.IsNull(AgenciesRelease.Parse("{\"tag_name\":\"v0.30.0-agencies.3\",\"assets\":{}}"));
            Assert.IsNull(AgenciesRelease.Parse("{\"tag_name\":\"v0.30.0-agencies.3\",\"assets\":[1,\"x\",null]}"));
        }

        [TestMethod]
        public void ParseKeepsABodyThatLooksLikeADate()
        {
            var release = Release();
            release["body"] = "2026-10-07T00:00:00Z";
            var info = AgenciesRelease.Parse(release.ToString());
            Assert.IsNotNull(info);
            Assert.AreEqual("2026-10-07T00:00:00Z", info.Changelog);
        }

        [TestMethod]
        public void ParseTurnsANullBodyIntoAnEmptyChangelog()
        {
            var release = Release();
            release["body"] = JValue.CreateNull();
            Assert.AreEqual("", AgenciesRelease.Parse(release.ToString()).Changelog);
            release.Remove("body");
            Assert.AreEqual("", AgenciesRelease.Parse(release.ToString()).Changelog);
        }

        // ---- IsTrustedAssetUrl ----

        [TestMethod]
        public void IsTrustedAssetUrlAcceptsOnlyTheExactForm()
        {
            var name = ClientName();
            Assert.IsTrue(AgenciesRelease.IsTrustedAssetUrl(UrlOf(name), Tag3, name));

            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(UrlOf(name) + "?x=1", Tag3, name));
            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(UrlOf(name).Replace("https:", "http:"), Tag3, name));
            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(UrlOf(name).ToUpperInvariant(), Tag3, name));
            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(UrlOf(name, "v0.30.0-agencies.2"), Tag3, name));
            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(UrlOf(name), Tag3, ServerName()));
            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(null, Tag3, name));
            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(UrlOf(name), null, name));
            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(UrlOf(name), Tag3, null));
            Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl("", "", ""));
        }

        [TestMethod]
        public void IsTrustedAssetUrlRejectsTagsAndNamesThatCanEscapeTheirSegment()
        {
            // Even when the URL equals prefix + tag + "/" + name literally, a segment that
            // carries path or query syntax is never trusted.
            foreach (var tag in new[] { "x/../y", "v1?x", "v1#x", "v1\\x", "v1%2e%2e", "a/b", ".." })
                Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(AgenciesBuild.DownloadPrefix + tag + "/n.zip", tag, "n.zip"), tag);
            foreach (var name in new[] { "../n.zip", "a/n.zip", "n.zip?x", "n.zip#x", "n\\.zip", "n%2e.zip", ".." })
                Assert.IsFalse(AgenciesRelease.IsTrustedAssetUrl(AgenciesBuild.DownloadPrefix + Tag3 + "/" + name, Tag3, name), name);
        }

        // ---- ParseSums ----

        [TestMethod]
        public void ParseSumsReadsMixedCaseLinesAndIgnoresTheRest()
        {
            var text = "﻿" + H1.ToUpperInvariant() + "  a.zip\r\n" +
                       "\r\n" +
                       "abc123  short.zip\r\n" +
                       H2 + "  b.zip\r\n" +
                       "not a hash line\r\n";
            var sums = AgenciesRelease.ParseSums(text);
            Assert.AreEqual(2, sums.Count);
            Assert.AreEqual(H1, sums["a.zip"]);
            Assert.AreEqual(H2, sums["b.zip"]);
        }

        [TestMethod]
        public void ParseSumsAcceptsUnixLineEndingsAndTheBinaryMarker()
        {
            var sums = AgenciesRelease.ParseSums(H1 + " *a.zip\n" + H2 + "\tb.zip\n");
            Assert.AreEqual(H1, sums["a.zip"]);
            Assert.AreEqual(H2, sums["b.zip"]);
        }

        [TestMethod]
        public void ParseSumsDoesNotTruncateALongerHash()
        {
            var sums = AgenciesRelease.ParseSums(H1 + H2 + "  long.zip\n");
            Assert.AreEqual(0, sums.Count);
        }

        [TestMethod]
        public void ParseSumsDropsAnEntryThatIsListedWithTwoDifferentHashes()
        {
            var sums = AgenciesRelease.ParseSums(H1 + "  a.zip\n" + H2 + "  a.zip\n" + H1 + "  b.zip\n" + H1 + "  b.zip\n");
            Assert.IsFalse(sums.ContainsKey("a.zip"));
            Assert.AreEqual(H1, sums["b.zip"], "the same hash twice is not a conflict");
        }

        [TestMethod]
        public void ParseSumsHandlesNullAndEmpty()
        {
            Assert.AreEqual(0, AgenciesRelease.ParseSums(null).Count);
            Assert.AreEqual(0, AgenciesRelease.ParseSums("").Count);
        }

        // ---- ExpectedSha256 ----

        private static AgenciesReleaseInfo InfoWithDigests(string client, string server) =>
            new AgenciesReleaseInfo { Build = 3, Tag = Tag3, ClientZipName = ClientName(), ServerZipName = ServerName(), ClientDigest = client, ServerDigest = server };

        [TestMethod]
        public void ExpectedSha256ReturnsTheSumsEntryWhenThereIsNoDigest()
        {
            var sums = H1 + "  " + ClientName() + "\n";
            Assert.AreEqual(H1, AgenciesRelease.ExpectedSha256(InfoWithDigests(null, null), ClientName(), sums));
        }

        [TestMethod]
        public void ExpectedSha256ReturnsTheSumsEntryWhenTheDigestAgrees()
        {
            var sums = H1 + "  " + ClientName() + "\n" + H2 + "  " + ServerName() + "\n";
            var info = InfoWithDigests(H1, H2);
            Assert.AreEqual(H1, AgenciesRelease.ExpectedSha256(info, ClientName(), sums));
            Assert.AreEqual(H2, AgenciesRelease.ExpectedSha256(info, ServerName(), sums));
        }

        [TestMethod]
        public void ExpectedSha256IsNullWhenTheDigestDisagrees()
        {
            var sums = H1 + "  " + ClientName() + "\n" + H2 + "  " + ServerName() + "\n";
            Assert.IsNull(AgenciesRelease.ExpectedSha256(InfoWithDigests(H2, null), ClientName(), sums));
            Assert.IsNull(AgenciesRelease.ExpectedSha256(InfoWithDigests(null, H1), ServerName(), sums));
        }

        [TestMethod]
        public void ExpectedSha256IsNullWithoutASumsEntry()
        {
            var sums = H1 + "  " + ClientName() + "\n";
            Assert.IsNull(AgenciesRelease.ExpectedSha256(InfoWithDigests(H1, H2), ServerName(), sums));
            Assert.IsNull(AgenciesRelease.ExpectedSha256(InfoWithDigests(H1, H2), "unknown.zip", sums));
            Assert.IsNull(AgenciesRelease.ExpectedSha256(InfoWithDigests(H1, H2), ClientName(), ""));
            Assert.IsNull(AgenciesRelease.ExpectedSha256(InfoWithDigests(H1, H2), ClientName(), null));
            Assert.IsNull(AgenciesRelease.ExpectedSha256(null, ClientName(), sums));
        }

        [TestMethod]
        public void ExpectedSha256ComparesTheDigestWithoutCase()
        {
            var sums = H1 + "  " + ClientName() + "\n";
            Assert.AreEqual(H1, AgenciesRelease.ExpectedSha256(InfoWithDigests(H1.ToUpperInvariant(), null), ClientName(), sums));
        }

        [TestMethod]
        public void ExpectedSha256WorksOnAParsedRelease()
        {
            var info = Info();
            var sums = H1 + "  " + ClientName() + "\n" + H2 + "  " + ServerName() + "\n";
            Assert.AreEqual(H1, AgenciesRelease.ExpectedSha256(info, info.ClientZipName, sums));
            Assert.AreEqual(H2, AgenciesRelease.ExpectedSha256(info, info.ServerZipName, sums));
        }

        // ---- Sha256Hex ----

        [TestMethod]
        public void Sha256HexMatchesTheKnownVectors()
        {
            using (var abc = new MemoryStream(Encoding.ASCII.GetBytes("abc")))
                Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AgenciesRelease.Sha256Hex(abc));
            using (var empty = new MemoryStream())
                Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", AgenciesRelease.Sha256Hex(empty));
        }

        // ---- Decide ----

        private static AgenciesLastResult Last(string spec)
        {
            switch (spec)
            {
                case "none": return null;
                case "fail3": return new AgenciesLastResult { Success = false, Build = 3, PreviousBuild = 2, Message = "boom" };
                case "ok3": return new AgenciesLastResult { Success = true, Build = 3, PreviousBuild = 2, Message = "Updated" };
                default: throw new ArgumentException(spec);
            }
        }

        [DataTestMethod]
        // current, latest, skipped, auto, lastResult, failedBuild, forced, expected
        [DataRow(2, 2, 0, false, "none", 0, false, AgenciesUpdateAction.None)]
        [DataRow(2, 3, 0, false, "none", 0, false, AgenciesUpdateAction.Prompt)]
        [DataRow(2, 3, 3, false, "none", 0, false, AgenciesUpdateAction.None)]
        [DataRow(2, 3, 3, false, "none", 0, true, AgenciesUpdateAction.Prompt)]
        [DataRow(2, 3, 0, true, "none", 0, false, AgenciesUpdateAction.AutoDownload)]
        [DataRow(2, 3, 0, true, "fail3", 0, false, AgenciesUpdateAction.ShowFailure)]
        [DataRow(2, 3, 0, true, "ok3", 0, false, AgenciesUpdateAction.ShowFailure)]
        [DataRow(2, 3, 0, true, "none", 3, false, AgenciesUpdateAction.ShowFailure)]
        [DataRow(2, 4, 0, true, "none", 3, false, AgenciesUpdateAction.AutoDownload)]
        [DataRow(3, 3, 0, true, "ok3", 0, false, AgenciesUpdateAction.None)]
        [DataRow(3, 2, 0, true, "none", 0, true, AgenciesUpdateAction.None)]
        // beyond the plan's rows
        [DataRow(2, 4, 0, true, "fail3", 0, false, AgenciesUpdateAction.AutoDownload)]
        [DataRow(2, 4, 0, false, "ok3", 0, false, AgenciesUpdateAction.Prompt)]
        [DataRow(2, 3, 0, false, "fail3", 0, true, AgenciesUpdateAction.ShowFailure)]
        [DataRow(2, 3, 0, true, "none", 0, true, AgenciesUpdateAction.AutoDownload)]
        [DataRow(2, 3, 3, true, "none", 0, false, AgenciesUpdateAction.None)]
        [DataRow(2, 3, 3, true, "fail3", 3, false, AgenciesUpdateAction.None)]
        [DataRow(3, 3, 0, false, "fail3", 3, true, AgenciesUpdateAction.None)]
        [DataRow(3, 2, 0, false, "none", 0, false, AgenciesUpdateAction.None)]
        public void DecideFollowsThePlanTable(int current, int latest, int skipped, bool auto, string last, int failedBuild, bool forced, AgenciesUpdateAction expected)
        {
            Assert.AreEqual(expected, AgenciesUpdateDecision.Decide(current, latest, skipped, auto, Last(last), failedBuild, forced));
        }
    }
}
