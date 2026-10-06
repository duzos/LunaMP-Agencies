using LmpCommon.Agency;
using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server → all clients: a single vessel's owning-agency mapping has
    /// been recorded or changed. Used so newly-launched vessels are
    /// immediately associated with their agency on every connected client
    /// without requiring a full <see cref="AgencyVesselMapSyncMsgData"/>.
    ///
    /// AgencyId == Guid.Empty means the vessel was removed and the mapping
    /// should be dropped.
    /// </summary>
    public class AgencyVesselMapEntryMsgData : AgencyBaseMsgData
    {
        internal AgencyVesselMapEntryMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvVesselMapEntry;
        public override string ClassName { get; } = nameof(AgencyVesselMapEntryMsgData);

        public Guid VesselId;
        public Guid AgencyId;

        public bool OwnershipSnapshotPresent;
        public long OwnershipRevision;
        public VesselOwnershipRecord OwnershipRecord;

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(VesselId.ToByteArray());
            lidgrenMsg.Write(AgencyId.ToByteArray());
            if(OwnershipSnapshotPresent) { lidgrenMsg.Write(true); lidgrenMsg.Write(OwnershipRevision); lidgrenMsg.Write(OwnershipRecord!=null); if(OwnershipRecord!=null) VesselOwnershipWire.Write(lidgrenMsg,OwnershipRecord); }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            VesselId = new Guid(lidgrenMsg.ReadBytes(16));
            AgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            OwnershipSnapshotPresent=false; OwnershipRevision=0; OwnershipRecord=null;
            if(lidgrenMsg.LengthBits-lidgrenMsg.Position>=8 && lidgrenMsg.ReadBoolean()) { VesselOwnershipWire.Require(lidgrenMsg,65); OwnershipRevision=lidgrenMsg.ReadInt64(); if(lidgrenMsg.ReadBoolean()) OwnershipRecord=VesselOwnershipWire.Read(lidgrenMsg); OwnershipSnapshotPresent=true; }
        }

        internal override int InternalGetMessageSize() => 32 + (OwnershipSnapshotPresent ? 10 + (OwnershipRecord==null ? 0 : VesselOwnershipWire.Size(OwnershipRecord)) : 0);
    }
}
