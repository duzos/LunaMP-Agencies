using LmpCommon.Agency;
using LunaConfigNode.CfgNode;
using Server.Context;
using Server.Log;
using Server.System;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Server.Agency
{
    /// <summary>
    /// Persistence for <see cref="Agency"/> metadata (name, owner, members,
    /// pending join requests, resource headline values). Each agency lives in
    /// its own directory under <see cref="AgenciesPath"/>. Scenario state
    /// (tech tree, contracts, etc.) is stored under the same directory by
    /// <see cref="AgencyScenarioStore"/>.
    ///
    /// Layout:
    ///   Universe/Agencies/&lt;guid&gt;/meta.txt
    ///   Universe/Agencies/&lt;guid&gt;/Scenarios/&lt;Module&gt;.txt
    ///
    /// Loading is defensive: a corrupt or malformed meta file is skipped with
    /// a warning rather than aborting startup.
    /// </summary>
    public static class AgencyStore
    {
        public static string AgenciesPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies");
        public const string AgencyFileFormat = ".txt";
        public const string MetaFileName = "meta.txt";

        private static readonly object BackupLock = new object();

        public static readonly ConcurrentDictionary<Guid, Agency> Agencies = new ConcurrentDictionary<Guid, Agency>();

        public static string AgencyDirectory(Guid id) => Path.Combine(AgenciesPath, id.ToString("N"));
        public static string MetaPath(Guid id) => Path.Combine(AgencyDirectory(id), MetaFileName);

        /// <summary>
        /// Scans Universe/Agencies for subdirectories and loads each agency's meta.
        /// Safe to call on an empty directory.
        /// </summary>
        public static void LoadExistingAgencies()
        {
            if (!FileHandler.FolderExists(AgenciesPath))
                FileHandler.FolderCreate(AgenciesPath);

            foreach (var dir in Directory.GetDirectories(AgenciesPath))
            {
                var metaFile = Path.Combine(dir, MetaFileName);
                if (!File.Exists(metaFile))
                {
                    LunaLog.Warning($"[Agency] Skipping directory without meta.txt: {dir}");
                    continue;
                }

                try
                {
                    var node = new ConfigNode(FileHandler.ReadFileText(metaFile));
                    var agency = Deserialize(node);
                    if (agency == null)
                    {
                        LunaLog.Warning($"[Agency] Meta file produced null agency: {metaFile}");
                        continue;
                    }
                    Agencies[agency.Id] = agency;
                    LunaLog.Info($"[Agency] Loaded agency='{agency.Name}' id={agency.Id} members={agency.Members.Count} owner={agency.OwnerDisplayName}");
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[Agency] Failed to load {metaFile}: {e}");
                }
            }
        }

        /// <summary>
        /// Writes the given agency's meta.txt to disk. Creates the agency
        /// directory if needed. Called whenever mutating state.
        /// </summary>
        public static void PersistAgency(Agency agency)
        {
            if (agency == null) return;

            var dir = AgencyDirectory(agency.Id);
            if (!FileHandler.FolderExists(dir))
                FileHandler.FolderCreate(dir);

            var node = Serialize(agency);
            lock (BackupLock)
            {
                FileHandler.WriteToFile(MetaPath(agency.Id), node.ToString());
            }
        }

        public static void PersistAll()
        {
            foreach (var a in Agencies.Values)
                PersistAgency(a);
        }

        public static void DeleteAgencyFiles(Guid id)
        {
            var dir = AgencyDirectory(id);
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                    LunaLog.Info($"[Agency] Deleted agency directory id={id}");
                }
            }
            catch (Exception e)
            {
                LunaLog.Error($"[Agency] Failed to delete agency directory id={id}: {e}");
            }
        }

        #region Serialization

        private static ConfigNode Serialize(Agency agency)
        {
            // The ConfigNode(string) constructor parses *inner* field/node
            // content — it does not parse the outermost "Name { ... }"
            // wrapper. So build the body only and set Name via the property.
            var inv = global::System.Globalization.CultureInfo.InvariantCulture;
            var sb = new global::System.Text.StringBuilder();
            sb.Append($"id = {agency.Id.ToString("N")}\n");
            sb.Append($"name = {EscapeText(agency.Name)}\n");
            sb.Append($"ownerUniqueId = {EscapeText(agency.OwnerUniqueId)}\n");
            sb.Append($"ownerDisplayName = {EscapeText(agency.OwnerDisplayName)}\n");
            sb.Append($"funds = {agency.Funds.ToString(inv)}\n");
            sb.Append($"science = {agency.Science.ToString(inv)}\n");
            sb.Append($"reputation = {agency.Reputation.ToString(inv)}\n");
            sb.Append($"createdUtcTicks = {agency.CreatedUtcTicks.ToString(inv)}\n");
            sb.Append($"isSolo = {(agency.IsSolo ? "true" : "false")}\n");
            sb.Append($"unlockedTechCount = {agency.UnlockedTechCount.ToString(inv)}\n");

            sb.Append("MEMBERS\n{\n");
            foreach (var m in agency.Members)
            {
                sb.Append("\tMEMBER\n\t{\n");
                sb.Append($"\t\tuniqueId = {EscapeText(m.UniqueId)}\n");
                sb.Append($"\t\tdisplayName = {EscapeText(m.DisplayName)}\n");
                sb.Append("\t}\n");
            }
            sb.Append("}\n");

            sb.Append("JOIN_REQUESTS\n{\n");
            foreach (var r in agency.PendingJoinRequests)
            {
                sb.Append("\tREQUEST\n\t{\n");
                sb.Append($"\t\tplayerUniqueId = {EscapeText(r.PlayerUniqueId)}\n");
                sb.Append($"\t\tplayerDisplayName = {EscapeText(r.PlayerDisplayName)}\n");
                sb.Append($"\t\trequestedUtcTicks = {r.RequestedUtcTicks.ToString(inv)}\n");
                sb.Append("\t}\n");
            }
            sb.Append("}\n");

            return new ConfigNode(sb.ToString()) { Name = "AGENCY" };
        }

        private static string EscapeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // ConfigNode entries are \n separated, so strip newlines. Quotes
            // are not special in KSP's ConfigNode format but we collapse
            // whitespace to keep persisted files readable.
            return s.Replace("\n", " ").Replace("\r", " ");
        }

        private static Agency Deserialize(ConfigNode node)
        {
            var idStr = GetValueOrDefault(node, "id", null);
            if (string.IsNullOrEmpty(idStr) || !Guid.TryParseExact(idStr, "N", out var id))
                return null;

            var agency = new Agency
            {
                Id = id,
                Name = GetValueOrDefault(node, "name", string.Empty),
                OwnerUniqueId = GetValueOrDefault(node, "ownerUniqueId", string.Empty),
                OwnerDisplayName = GetValueOrDefault(node, "ownerDisplayName", string.Empty),
                Funds = ParseDoubleSafe(GetValueOrDefault(node, "funds", "0")),
                Science = ParseFloatSafe(GetValueOrDefault(node, "science", "0")),
                Reputation = ParseFloatSafe(GetValueOrDefault(node, "reputation", "0")),
                CreatedUtcTicks = ParseLongSafe(GetValueOrDefault(node, "createdUtcTicks", "0")),
                IsSolo = string.Equals(GetValueOrDefault(node, "isSolo", "false"), "true", StringComparison.OrdinalIgnoreCase),
                UnlockedTechCount = ParseIntSafe(GetValueOrDefault(node, "unlockedTechCount", "0")),
            };

            var membersNode = FindChildNode(node, "MEMBERS");
            if (membersNode != null)
            {
                foreach (var mn in EnumerateChildNodes(membersNode, "MEMBER"))
                {
                    var member = new Agency.Member
                    {
                        UniqueId = GetValueOrDefault(mn, "uniqueId", string.Empty),
                        DisplayName = GetValueOrDefault(mn, "displayName", string.Empty),
                    };
                    if (!string.IsNullOrEmpty(member.UniqueId))
                        agency.Members.Add(member);
                }
            }

            var requestsNode = FindChildNode(node, "JOIN_REQUESTS");
            if (requestsNode != null)
            {
                foreach (var rn in EnumerateChildNodes(requestsNode, "REQUEST"))
                {
                    var req = new JoinRequestInfo
                    {
                        AgencyId = agency.Id,
                        PlayerUniqueId = GetValueOrDefault(rn, "playerUniqueId", string.Empty),
                        PlayerDisplayName = GetValueOrDefault(rn, "playerDisplayName", string.Empty),
                        RequestedUtcTicks = ParseLongSafe(GetValueOrDefault(rn, "requestedUtcTicks", "0")),
                    };
                    if (!string.IsNullOrEmpty(req.PlayerUniqueId))
                        agency.PendingJoinRequests.Add(req);
                }
            }

            return agency;
        }

        #endregion

        #region ConfigNode helpers

        private static string GetValueOrDefault(ConfigNode node, string key, string defaultValue)
        {
            try
            {
                var entry = node.GetValue(key);
                var v = entry?.Value;
                return string.IsNullOrEmpty(v) ? defaultValue : v;
            }
            catch { return defaultValue; }
        }

        private static ConfigNode FindChildNode(ConfigNode parent, string name)
        {
            try
            {
                var entry = parent.GetNode(name);
                return entry?.Value;
            }
            catch { return null; }
        }

        private static IEnumerable<ConfigNode> EnumerateChildNodes(ConfigNode parent, string name)
        {
            IEnumerable<ConfigNode> Inner()
            {
                foreach (var n in parent.GetNodes(name).Select(e => e.Value))
                    if (n != null) yield return n;
            }
            try { return Inner(); }
            catch { return Array.Empty<ConfigNode>(); }
        }

        private static double ParseDoubleSafe(string s) =>
            double.TryParse(s, global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.0;

        private static float ParseFloatSafe(string s) =>
            float.TryParse(s, global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;

        private static long ParseLongSafe(string s) =>
            long.TryParse(s, out var v) ? v : 0L;

        private static int ParseIntSafe(string s) =>
            int.TryParse(s, out var v) ? v : 0;

        #endregion
    }
}
