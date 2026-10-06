using System;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
namespace LmpCommon.Message.Data.Agency
{
    public sealed class AgencyVisibilitySnapshotMsgData : AgencyBaseMsgData
    {
        internal AgencyVisibilitySnapshotMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvVisibilitySnapshot;
        public override string ClassName => nameof(AgencyVisibilitySnapshotMsgData);
        public bool Ready;
        public long Revision;
        public VisibilityEndpoint[] Endpoints = Array.Empty<VisibilityEndpoint>();
        public VisibilityAgencyGrant[] AgencyGrants = Array.Empty<VisibilityAgencyGrant>();
        public VisibilityCraftOverride[] CraftOverrides = Array.Empty<VisibilityCraftOverride>();
        private VisibilitySnapshot Capture() => new VisibilitySnapshot { Ready = Ready, Revision = Revision, Endpoints = Endpoints, AgencyGrants = AgencyGrants, CraftOverrides = CraftOverrides };
        internal override void InternalSerialize(NetOutgoingMessage message) { var value = Capture(); VisibilityLimits.Validate(value); AgencyEconomyWire.Write(message, value); }
        internal override void InternalDeserialize(NetIncomingMessage message)
        {
            Ready = false; Revision = 0; Endpoints = Array.Empty<VisibilityEndpoint>(); AgencyGrants = Array.Empty<VisibilityAgencyGrant>(); CraftOverrides = Array.Empty<VisibilityCraftOverride>();
            var value = AgencyEconomyWire.Read<VisibilitySnapshot>(message); VisibilityLimits.Validate(value);
            Ready = value.Ready; Revision = value.Revision; Endpoints = value.Endpoints; AgencyGrants = value.AgencyGrants; CraftOverrides = value.CraftOverrides;
        }
        internal override int InternalGetMessageSize() => AgencyEconomyWire.Size(Capture());
    }
}
