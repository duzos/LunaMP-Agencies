using LmpCommon.Agency;
using Server.Client;
using Server.Context;
using Server.Diagnostics;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Server.Agency
{
    public sealed class LaunchSiteSnapshot
    {
        public long Revision { get; }
        public IReadOnlyDictionary<string, Guid> Assignments { get; }
        internal LaunchSiteSnapshot(long revision, Dictionary<string, Guid> assignments)
        {
            Revision = revision;
            Assignments = new ReadOnlyDictionary<string, Guid>(assignments);
        }
    }

    /// <summary>Exclusive exact site IDs; persistence succeeds before committed state is published.</summary>
    public static class AgencyLaunchSiteStore
    {
        private static readonly object MutationLock = new object();
        private static Dictionary<string, Guid> _assignments = new Dictionary<string, Guid>(StringComparer.Ordinal);
        private static long _revision;
        private static string _loadError;
        public static string MapFilePath => Path.Combine(ServerContext.UniverseDirectory, "AgencyLaunchSites.json");
        public static long Revision { get { lock (MutationLock) return _revision; } }

        public static LaunchSiteSnapshot GetSnapshot()
        {
            lock (MutationLock) return new LaunchSiteSnapshot(_revision, new Dictionary<string, Guid>(_assignments, StringComparer.Ordinal));
        }

        public static void Load()
        {
            string warning = null;
            lock (MutationLock)
            {
                _assignments = new Dictionary<string, Guid>(StringComparer.Ordinal);
                _loadError = null;
                _revision++;
                try
                {
                    if (!File.Exists(MapFilePath)) return;
                    // Bound file allocation as well as decoded entry count.
                    if (new FileInfo(MapFilePath).Length > 4L * 1024 * 1024) throw new InvalidDataException("Launch-site file exceeds the size limit.");
                    var entries = JsonSerializer.Deserialize<LaunchSiteAssignment[]>(File.ReadAllText(MapFilePath));
                    if (entries == null || entries.Length > AgencyLaunchSitePolicy.MaxAssignments) throw new InvalidDataException("Invalid launch-site entry count.");
                    var ignored = 0;
                    foreach (var entry in entries)
                    {
                        if (entry == null || !AgencyLaunchSitePolicy.IsValidSiteId(entry.SiteId) || entry.AgencyId == Guid.Empty || !AgencyStore.Agencies.ContainsKey(entry.AgencyId) || _assignments.ContainsKey(entry.SiteId))
                        {
                            ignored++;
                            continue;
                        }
                        _assignments.Add(entry.SiteId, entry.AgencyId);
                    }
                    if (ignored > 0) warning = $"[Agency] Ignored {ignored} invalid, duplicate or orphan launch-site assignments in {MapFilePath}.";
                }
                catch (Exception e)
                {
                    _assignments.Clear();
                    _loadError = $"Launch-site assignments could not be loaded ({e.GetType().Name}). Correct {MapFilePath} and reload before editing assignments.";
                    warning = _loadError;
                }
            }
            if (warning != null) LunaLog.Warning(warning);
        }

        public static (bool Success, string Message) Assign(Guid agencyId, string siteId)
            => Mutate(agencyId, siteId, remove: false, deleteAgency: false);

        public static (bool Success, string Message) Unassign(Guid agencyId, string siteId)
            => Mutate(agencyId, siteId, remove: true, deleteAgency: false);

        // Removal of agency membership and its assignments shares the mutation lock so a
        // concurrent assignment cannot re-add ownership in the gap before agency deletion.
        public static (bool Success, string Message) RemoveAgencyAndAssignments(Guid agencyId)
            => Mutate(agencyId, null, remove: true, deleteAgency: true);

        private static (bool Success, string Message) Mutate(Guid agencyId, string siteId, bool remove, bool deleteAgency)
        {
            var changed = false;
            string result;
            lock (MutationLock)
            {
                if (_loadError != null) return (false, _loadError);
                if (!AgencyStore.Agencies.ContainsKey(agencyId)) return (false, "Agency not found.");
                if (!deleteAgency && !AgencyLaunchSitePolicy.IsValidSiteId(siteId))
                    return (false, $"Site ID must contain 1-{AgencyLaunchSitePolicy.MaxSiteIdLength} characters, with no control characters.");
                var candidate = new Dictionary<string, Guid>(_assignments, StringComparer.Ordinal);
                if (deleteAgency)
                {
                    foreach (var key in candidate.Where(p => p.Value == agencyId).Select(p => p.Key).ToArray()) candidate.Remove(key);
                    changed = candidate.Count != _assignments.Count;
                    result = "Agency launch-site assignments removed.";
                }
                else if (remove)
                {
                    if (!candidate.TryGetValue(siteId, out var owner) || owner != agencyId)
                        return (false, "Site is not assigned to that agency. Refresh assignments before retrying.");
                    candidate.Remove(siteId);
                    changed = true;
                    result = $"Unassigned '{siteId}'.";
                }
                else
                {
                    if (candidate.TryGetValue(siteId, out var owner) && owner == agencyId) return (true, "Site is already assigned to that agency.");
                    if (!candidate.ContainsKey(siteId) && candidate.Count >= AgencyLaunchSitePolicy.MaxAssignments)
                        return (false, "Launch-site assignment limit reached.");
                    candidate[siteId] = agencyId;
                    changed = true;
                    result = $"Assigned '{siteId}' to agency {agencyId}.";
                }
                if (changed)
                {
                    try { Persist(candidate); }
                    catch (Exception e) { return (false, $"Launch-site assignments were not changed: persistence failed ({e.GetType().Name})."); }
                }

                if (changed)
                {
                    _assignments = candidate;
                    _revision++;
                }
                if (deleteAgency) AgencyStore.Agencies.TryRemove(agencyId, out _);
            }
            // Clients reject older revisions; concurrent publication cannot restore an old map.
            if (changed)
            {
                foreach (var client in ClientRetriever.GetAuthenticatedClients()) AgencyNetwork.SendSyncAllTo(client);
                WarnUnassignedAgencies();
                PlaytestDiagnostics.Write("launch-sites.changed", () => $"agency={agencyId} site={siteId} remove={remove} deleteAgency={deleteAgency} revision={Revision}");
            }
            return (true, result);
        }

        private static void Persist(Dictionary<string, Guid> candidate)
        {
            var path = MapFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var entries = candidate.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new LaunchSiteAssignment { SiteId = p.Key, AgencyId = p.Value }).ToArray();
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static void WarnUnassignedAgencies()
        {
            if (!GeneralSettings.SettingsStore.AgencyLaunchSitesPerAgency) return;
            var owners = new HashSet<Guid>(GetSnapshot().Assignments.Values);
            foreach (var agency in AgencyStore.Agencies.Values)
                if (!owners.Contains(agency.Id)) LunaLog.Warning($"[Agency] '{agency.Name}' ({agency.Id}) has no assigned launch sites; launches are blocked, including KSC LaunchPad and Runway.");
        }
    }
}
