using Server.Log;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Server.Agency
{
    /// <summary>
    /// Server-wide registry of which agency was the first to claim each
    /// "first to X" milestone. Each achievement key (e.g.
    /// <c>RecoverFromOrbit:Mun</c>) is awarded to the first agency that
    /// reports it; subsequent agencies do not displace the holder.
    ///
    /// The registry is reconstructed from each agency's
    /// <see cref="Agency.FirstAchievements"/> dictionary at startup, and is
    /// kept in sync via <see cref="ClaimIfFirst"/> on every achievement-
    /// reporting client message.
    /// </summary>
    public static class AgencyAchievementRegistry
    {
        // achievementKey -> agencyId that holds it.
        private static readonly ConcurrentDictionary<string, Guid> Holders =
            new ConcurrentDictionary<string, Guid>();

        /// <summary>
        /// Build the in-memory holders dictionary from the agencies loaded at
        /// startup. Idempotent — safe to call after a partial load.
        /// </summary>
        public static void RebuildFromAgencies()
        {
            Holders.Clear();
            foreach (var agency in AgencyStore.Agencies.Values)
            {
                foreach (var kv in agency.FirstAchievements)
                {
                    // If two agencies somehow hold the same key (shouldn't
                    // happen, but be defensive), pick the earlier-claimed one.
                    Holders.AddOrUpdate(kv.Key, agency.Id, (_, current) =>
                    {
                        var currentTicks = AgencyStore.Agencies.TryGetValue(current, out var cur) &&
                                           cur.FirstAchievements.TryGetValue(kv.Key, out var ct) ? ct : long.MaxValue;
                        return kv.Value < currentTicks ? agency.Id : current;
                    });
                }
            }
            LunaLog.Debug($"[Agency] Achievement registry rebuilt: {Holders.Count} milestones claimed.");
        }

        /// <summary>
        /// Try to claim <paramref name="key"/> for <paramref name="agency"/>.
        /// Returns true and records the claim if no agency held it before;
        /// returns false if another agency already holds it (no-op).
        /// </summary>
        public static bool ClaimIfFirst(Agency agency, string key)
        {
            if (agency == null || string.IsNullOrEmpty(key)) return false;

            if (Holders.TryAdd(key, agency.Id))
            {
                lock (agency.Lock)
                {
                    agency.FirstAchievements[key] = DateTime.UtcNow.Ticks;
                }
                LunaLog.Info($"[Agency] First achievement '{key}' claimed by '{agency.Name}'.");
                AgencyStore.PersistAgency(agency);
                AgencyNetwork.BroadcastUpsert(agency);
                return true;
            }
            return false;
        }

        public static int CountFor(Guid agencyId) =>
            Holders.Count(kv => kv.Value == agencyId);
    }
}
