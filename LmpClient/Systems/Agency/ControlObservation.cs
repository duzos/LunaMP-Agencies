using System;
using LmpCommon.Enums;

namespace LmpClient.Systems.Agency
{
    /// <summary>Fail-closed disclosure boundary for the optional mission-control bridge.</summary>
    public static class ControlObservation
    {
        public const int ApiVersion = 1;
        public static Guid AgencyId => MainSystem.NetworkState >= ClientState.Connected
            ? AgencySystem.Singleton.MyAgencyId : Guid.Empty;

        public static bool MayInspectActiveVessel(Guid vesselId)
        {
            var agency = AgencySystem.Singleton;
            if (AgencyId == Guid.Empty || !agency.OwnershipReady) return false;
            var records = agency.GetOwnershipSnapshot();
            return records.TryGetValue(vesselId, out var record) && record.OwnerAgencyId == AgencyId;
        }
    }
}
