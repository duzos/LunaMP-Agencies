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
    /// Authoritative mapping of vessel id → owning agency id, used by the
    /// per-agency CommNet filter on the client. Persisted to
    /// <c>Universe/AgencyVesselMap.txt</c> as a flat list of
    /// <c>vesselId = agencyId</c> lines so it survives restarts independently
    /// of vessel files (each vessel doesn't need a custom field added).
    ///
    /// Recorded on every first-time vessel proto sync (see
    /// <see cref="Server.Message.VesselMsgReader"/>); cleared when the vessel
    /// is removed (recovery, debris-clean, etc.).
    /// </summary>
    public static class AgencyVesselMap
    {
        public static string MapFilePath = Path.Combine(ServerContext.UniverseDirectory, "AgencyVesselMap.txt");

        private static readonly ConcurrentDictionary<Guid, Guid> _map = new ConcurrentDictionary<Guid, Guid>();
        private static readonly object _diskLock = new object();

        public static IReadOnlyDictionary<Guid, Guid> Snapshot => _map;

        public static bool TryGetAgency(Guid vesselId, out Guid agencyId) =>
            _map.TryGetValue(vesselId, out agencyId);

        public static void Set(Guid vesselId, Guid agencyId)
        {
            if (vesselId == Guid.Empty || agencyId == Guid.Empty) return;
            _map[vesselId] = agencyId;
            FlushAsync();
        }

        public static void Remove(Guid vesselId)
        {
            if (_map.TryRemove(vesselId, out _))
                FlushAsync();
        }

        public static void Load()
        {
            lock (_diskLock)
            {
                _map.Clear();
                if (!FileHandler.FileExists(MapFilePath)) return;

                foreach (var raw in FileHandler.ReadFileLines(MapFilePath))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var parts = raw.Split('=');
                    if (parts.Length != 2) continue;
                    if (Guid.TryParseExact(parts[0].Trim(), "N", out var vesselId) &&
                        Guid.TryParseExact(parts[1].Trim(), "N", out var agencyId))
                    {
                        _map[vesselId] = agencyId;
                    }
                }
                LunaLog.Info($"[Agency] Loaded {_map.Count} vessel→agency mappings.");
            }
        }

        private static void FlushAsync()
        {
            // Cheap to flush — small file, infrequent updates. Synchronous is
            // fine here; offload to a Task only if profiling shows otherwise.
            global::System.Threading.Tasks.Task.Run(() =>
            {
                lock (_diskLock)
                {
                    try
                    {
                        var sb = new global::System.Text.StringBuilder();
                        foreach (var kv in _map.OrderBy(p => p.Key))
                        {
                            sb.Append(kv.Key.ToString("N"));
                            sb.Append(" = ");
                            sb.Append(kv.Value.ToString("N"));
                            sb.Append('\n');
                        }
                        FileHandler.WriteToFile(MapFilePath, sb.ToString());
                    }
                    catch (Exception e)
                    {
                        LunaLog.Error($"[Agency] Failed to flush vessel-agency map: {e}");
                    }
                }
            });
        }
    }
}
