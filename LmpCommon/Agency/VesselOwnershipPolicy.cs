using System;
using System.Linq;

namespace LmpCommon.Agency
{
    public static class VesselOwnershipPolicy
    {
        public const int MaxCoOwners = 64;
        public const int MaxRecords = 10000;
        public const int MaxMergedVesselBytes = 10 * 1024 * 1000;
        public static bool CanControl(VesselOwnershipRecord record, Guid actorAgency)
            => actorAgency != Guid.Empty && (record == null || record.OwnerAgencyId == Guid.Empty || record.OwnerAgencyId == actorAgency || record.CoOwnerAgencyIds.Contains(actorAgency));
        public static bool CanManage(VesselOwnershipRecord record, Guid actorAgency, bool isAgencyOwner)
            => isAgencyOwner && actorAgency != Guid.Empty && record != null && record.OwnerAgencyId == actorAgency;
        public static DockingDecision EvaluateDock(VesselOwnershipRecord record, Guid actorAgency, bool ownerOnline)
        {
            if (actorAgency == Guid.Empty) return DockingDecision.Deny;
            if (record == null || record.OwnerAgencyId == Guid.Empty || record.OwnerAgencyId == actorAgency) return DockingDecision.Allow;
            if (ownerOnline) return DockingDecision.RequestConsent;
            return record.DockingPolicy == VesselDockingPolicy.Anyone || (record.DockingPolicy == VesselDockingPolicy.CoOwners && record.CoOwnerAgencyIds.Contains(actorAgency)) ? DockingDecision.Allow : DockingDecision.Deny;
        }
    }
}
