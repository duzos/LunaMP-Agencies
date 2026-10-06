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
    public class NukeCommand : SimpleCommand
    {
        private static long _lastNukeTime;

        public static void CheckTimer()
        {
            //0 or less is disabled.
            if (GeneralSettings.SettingsStore.AutoNuke > 0 &&
                 ServerContext.ServerClock.ElapsedMilliseconds - _lastNukeTime >
                 TimeSpan.FromMinutes(GeneralSettings.SettingsStore.AutoNuke).TotalMilliseconds)
            {
                _lastNukeTime = ServerContext.ServerClock.ElapsedMilliseconds;
                RunNuke();
            }
        }

        public override bool Execute(string commandArgs)
        {
            RunNuke();
            return true;
        }

        private static bool IsAtKsc(global::Server.System.Vessel.Classes.Vessel vessel)
        {
            var landed = (vessel.Fields.GetSingle("landed")?.Value ?? string.Empty).ToLower();
            var landedAt = (vessel.Fields.GetSingle("landedAt")?.Value ?? string.Empty).ToLower();
            return landed == "true" && landedAt.Contains("ksc") || landedAt.Contains("runway") || landedAt.Contains("launchpad");
        }

        private static void RunNuke()
        {
            var ids = VesselStoreSystem.CurrentVessels.ToArray().Where(p => IsAtKsc(p.Value)).Select(p => p.Key).ToArray();
            var result = VesselRemovalService.Remove(ids, "KSC cleanup", VesselRemovalMode.Ordinary, null, VesselRemovalOptions.AdminCleanup(id =>
                !VesselStoreSystem.CurrentVessels.TryGetValue(id, out var current) || !IsAtKsc(current) ? "Not at the KSC." : null));
            foreach (var id in result.Removed) LunaLog.Normal($"Removed vessel: {id} from KSC");
            var removalCount = result.Removed.Length;

            if (removalCount > 0)
                LunaLog.Normal($"Nuked {removalCount} vessels around the KSC");
        }
    }
}
