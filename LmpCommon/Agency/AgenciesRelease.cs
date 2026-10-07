using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LmpCommon.Agency
{
    /// <summary>A parsed, trusted fork release: every URL has been checked against <see cref="AgenciesBuild.DownloadPrefix"/>.</summary>
    public sealed class AgenciesReleaseInfo
    {
        public int Build;
        public string Tag, Changelog;
        public string ClientZipUrl, ClientZipName, ClientDigest;
        public string ServerZipUrl, ServerZipName, ServerDigest;
        public string SumsUrl;
    }

    /// <summary>Pure release logic: tag parsing, asset selection, URL trust, SHA-256 sums and digest cross-checks.</summary>
    public static class AgenciesRelease
    {
        // [0-9] and \z on purpose: \d also matches non-ASCII digits and $ also matches before a trailing newline.
        private static readonly Regex TagPattern = new Regex(@"^v[0-9]+\.[0-9]+\.[0-9]+-agencies\.([0-9]+)\z", RegexOptions.CultureInvariant);
        private static readonly Regex SumsLine = new Regex(@"^([0-9a-fA-F]{64})[ \t]+\*?(\S.*?)[ \t]*\z", RegexOptions.CultureInvariant);

        /// <summary>The build counter of a fork tag such as v0.30.0-agencies.3, or null when the tag is not one or the counter is not a positive int.</summary>
        public static int? ParseBuild(string tag)
        {
            if (tag == null) return null;
            var match = TagPattern.Match(tag);
            if (!match.Success) return null;
            int build;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out build) || build <= 0) return null;
            return build;
        }

        /// <summary>
        /// Parses a GitHub release JSON. Null unless it is a published stable release with a fork tag and with exactly the
        /// client zip, server zip and sums assets for that tag, each at a trusted URL. Digests are lowercase hex or null.
        /// </summary>
        public static AgenciesReleaseInfo Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JObject root;
            try
            {
                // DateParseHandling.None keeps a body (or tag) that looks like a date a plain string.
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                    root = JObject.Load(reader);
            }
            catch (JsonException)
            {
                return null;
            }

            // /releases/latest already excludes both, but they are checked again: only an absent or false flag passes.
            if (IsSetOrMalformed(root["draft"]) || IsSetOrMalformed(root["prerelease"])) return null;

            var tag = AsString(root["tag_name"]);
            var build = ParseBuild(tag);
            if (build == null) return null;

            var assets = root["assets"] as JArray;
            if (assets == null) return null;

            var clientName = AgenciesBuild.AssetName(AgenciesBuild.ClientAssetPrefix, tag);
            var serverName = AgenciesBuild.AssetName(AgenciesBuild.ServerAssetPrefix, tag);
            var sumsName = AgenciesBuild.SumsAsset;

            JObject client = null, server = null, sums = null;
            foreach (var token in assets)
            {
                var asset = token as JObject;
                var name = asset == null ? null : AsString(asset["name"]);
                if (name == null) continue;

                // GitHub never lists one name twice; if it somehow does, nothing is trusted.
                if (name == clientName) { if (client != null) return null; client = asset; }
                else if (name == serverName) { if (server != null) return null; server = asset; }
                else if (name == sumsName) { if (sums != null) return null; sums = asset; }
            }

            var clientUrl = TrustedUrl(client, tag, clientName);
            var serverUrl = TrustedUrl(server, tag, serverName);
            var sumsUrl = TrustedUrl(sums, tag, sumsName);
            if (clientUrl == null || serverUrl == null || sumsUrl == null) return null;

            return new AgenciesReleaseInfo
            {
                Build = build.Value,
                Tag = tag,
                Changelog = AsString(root["body"]) ?? "",
                ClientZipName = clientName,
                ClientZipUrl = clientUrl,
                ClientDigest = ParseDigest(client),
                ServerZipName = serverName,
                ServerZipUrl = serverUrl,
                ServerDigest = ParseDigest(server),
                SumsUrl = sumsUrl
            };
        }

        /// <summary>
        /// True only when <paramref name="url"/> is exactly DownloadPrefix + tag + "/" + name (ordinal, so https, host,
        /// owner, repo and tag folder all have to match) and neither tag nor name can carry path or query syntax.
        /// </summary>
        public static bool IsTrustedAssetUrl(string url, string tag, string name)
        {
            if (url == null || !IsSafeSegment(tag) || !IsSafeSegment(name)) return false;
            return string.Equals(url, AgenciesBuild.DownloadPrefix + tag + "/" + name, StringComparison.Ordinal);
        }

        /// <summary>
        /// Reads a SHA256SUMS.txt (leading BOM allowed): lines of "&lt;64 hex&gt;  &lt;name&gt;", with "*name" accepted for
        /// binary mode. Hashes are lowercased and every other line is ignored. A name listed with two different hashes is
        /// ambiguous and dropped, so it can never verify.
        /// </summary>
        public static Dictionary<string, string> ParseSums(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return result;
            if (text[0] == '﻿') text = text.Substring(1);

            var conflicted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawLine in text.Split('\n'))
            {
                var match = SumsLine.Match(rawLine.TrimEnd('\r'));
                if (!match.Success) continue;

                var hash = match.Groups[1].Value.ToLowerInvariant();
                var name = match.Groups[2].Value;
                if (conflicted.Contains(name)) continue;

                string existing;
                if (result.TryGetValue(name, out existing))
                {
                    if (existing != hash)
                    {
                        result.Remove(name);
                        conflicted.Add(name);
                    }
                    continue;
                }
                result[name] = hash;
            }
            return result;
        }

        /// <summary>
        /// The hash a download of <paramref name="name"/> must have: the sums entry, unless the release's own API digest
        /// for that asset exists and disagrees. Null when there is no entry or the two sources conflict.
        /// </summary>
        public static string ExpectedSha256(AgenciesReleaseInfo info, string name, string sumsText)
        {
            if (info == null || name == null) return null;

            string expected;
            if (!ParseSums(sumsText).TryGetValue(name, out expected)) return null;

            string digest = null;
            if (name == info.ClientZipName) digest = info.ClientDigest;
            else if (name == info.ServerZipName) digest = info.ServerDigest;

            if (!string.IsNullOrEmpty(digest) && !string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase)) return null;
            return expected;
        }

        /// <summary>Lowercase hex SHA-256 of the stream from its current position to the end.</summary>
        public static string Sha256Hex(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        private static string TrustedUrl(JObject asset, string tag, string name)
        {
            if (asset == null) return null;
            var url = AsString(asset["browser_download_url"]);
            return IsTrustedAssetUrl(url, tag, name) ? url : null;
        }

        /// <summary>The lowercase hex after "sha256:", or null when the digest is absent or not a sha256 of 64 hex characters.</summary>
        private static string ParseDigest(JObject asset)
        {
            var digest = AsString(asset["digest"]);
            const string prefix = "sha256:";
            if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            var hex = digest.Substring(prefix.Length);
            if (hex.Length != 64) return null;
            foreach (var c in hex)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return null;
            return hex.ToLowerInvariant();
        }

        /// <summary>A missing or null flag is "not set"; a boolean is its value; any other JSON type is treated as set.</summary>
        private static bool IsSetOrMalformed(JToken flag)
        {
            if (flag == null || flag.Type == JTokenType.Null) return false;
            return flag.Type != JTokenType.Boolean || (bool)flag;
        }

        private static string AsString(JToken token) =>
            token != null && token.Type == JTokenType.String ? (string)token : null;

        /// <summary>A tag or asset name that is a single plain path segment: no separators, query, fragment, percent-escapes, dot-dot or whitespace.</summary>
        private static bool IsSafeSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            foreach (var c in segment)
                if (c == '/' || c == '\\' || c == '?' || c == '#' || c == '%' || c <= ' ' || c == '\u007f') return false;
            return true;
        }
    }
}
