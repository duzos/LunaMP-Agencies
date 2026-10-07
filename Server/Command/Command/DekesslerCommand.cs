using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using Server.Command.Command.Base;
using Server.Context;
using Server.Log;
using Server.Server;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Linq;
using Server.Agency;

namespace Server.Command.Command
{
    public class DekesslerCommand : SimpleCommand
    {
        private static long _lastDekesslerTime;

        public static void CheckTimer()
        {
            //0 or less is disabled.
            if (GeneralSettings.SettingsStore.AutoDekessler > 0 &&
                ServerContext.ServerClock.ElapsedMilliseconds - _lastDekesslerTime >
                TimeSpan.FromMinutes(GeneralSettings.SettingsStore.AutoDekessler).TotalMilliseconds)
            {
                _lastDekesslerTime = ServerContext.ServerClock.ElapsedMilliseconds;
                RunDekessler();
            }
        }

        public override bool Execute(string commandArgs)
        {
            RunDekessler();
            return true;
        }

        private static bool IsDebris(global::Server.System.Vessel.Classes.Vessel vessel)
            => string.Equals(vessel.Fields.GetSingle("type")?.Value, "debris", StringComparison.OrdinalIgnoreCase);

        // Pending splits, split parents and control locks are already spared by the removal service's admin filters (single source of truth).
        // An Update lock means a player has the debris loaded and may be about to decouple from it, so it is kept too: loaded debris near any player is never cleared.
        private static bool IsInUse(Guid id) => LockSystem.LockQuery.UpdateLockExists(id);

        private static void RunDekessler()
        {
            var ids = VesselStoreSystem.CurrentVessels.ToArray().Where(p => IsDebris(p.Value)).Select(p => p.Key).ToArray();
            // One pass, one ownership commit. Filters run under the removal gate: with agency rules active crewed debris is kept.
            var result = VesselRemovalService.Remove(ids, "Debris cleanup", VesselRemovalMode.Ordinary, null, VesselRemovalOptions.AdminCleanup(id =>
                !VesselStoreSystem.CurrentVessels.TryGetValue(id, out var current) || !IsDebris(current) ? "Not debris." :
                AgencyVesselMap.AgencyRulesActive && AgencyVesselMap.IsCrewed(current) ? "Crewed." : IsInUse(id) ? "In use." : null));
            foreach (var id in result.Removed) LunaLog.Normal($"Removed debris vessel: {id}");
            var removalCount = result.Removed.Length;

            if (removalCount > 0)
                LunaLog.Normal($"Removed {removalCount} debris");
        }
    }
}
