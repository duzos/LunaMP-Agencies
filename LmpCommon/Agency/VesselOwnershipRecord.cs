using System;
namespace LmpCommon.Agency
{
    public enum VesselDockingPolicy : byte { Nobody, CoOwners, Anyone }
    public enum VesselOwnershipOperation : byte { Claim, AddCoOwner, RemoveCoOwner, Transfer, SetDockingPolicy, Delete }
    public enum DockConsentStatus : byte { Pending, Granted, Denied, Expired, Cancelled, Consumed, Completed, Rejected, RecoveryRequired }
    public enum DockingDecision { Allow, Deny, RequestConsent }
    public sealed class VesselOwnershipRecord
    {
        public Guid VesselId;
        public Guid OwnerAgencyId;
        public Guid[] CoOwnerAgencyIds = Array.Empty<Guid>();
        public VesselDockingPolicy DockingPolicy;
        public long Revision;
        public VesselOwnershipRecord Copy() => new VesselOwnershipRecord { VesselId = VesselId, OwnerAgencyId = OwnerAgencyId, CoOwnerAgencyIds = (Guid[])CoOwnerAgencyIds.Clone(), DockingPolicy = DockingPolicy, Revision = Revision };
    }
}
