using LmpCommon.Agency;
using Lidgren.Network;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    /// <summary>
    /// Server → client snapshot of the entire vessel→agency mapping. Sent on
    /// connect so clients can immediately filter the per-agency CommNet
    /// graph. Subsequent updates arrive as <see cref="AgencyVesselMapEntryMsgData"/>.
    /// </summary>
    public class AgencyVesselMapSyncMsgData : AgencyBaseMsgData
    {
        internal AgencyVesselMapSyncMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvVesselMapSync;
        public override string ClassName { get; } = nameof(AgencyVesselMapSyncMsgData);

        public Guid[] VesselIds = Array.Empty<Guid>();
        public Guid[] AgencyIds = Array.Empty<Guid>();

        public bool OwnershipSnapshotPresent;
        public long OwnershipRevision;
        public VesselOwnershipRecord[] OwnershipRecords = Array.Empty<VesselOwnershipRecord>();

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            var n = VesselIds?.Length ?? 0;
            if(n>VesselOwnershipPolicy.MaxRecords || AgencyIds == null || AgencyIds.Length!=n) throw new System.IO.InvalidDataException();
            lidgrenMsg.Write(n);
            for (int i = 0; i < n; i++)
            {
                lidgrenMsg.Write(VesselIds[i].ToByteArray());
                lidgrenMsg.Write(AgencyIds[i].ToByteArray());
            }
            if(OwnershipSnapshotPresent) {
                lidgrenMsg.Write(true); lidgrenMsg.Write(OwnershipRevision);
                if(OwnershipRecords.Length>VesselOwnershipPolicy.MaxRecords) throw new System.IO.InvalidDataException();
                lidgrenMsg.Write(OwnershipRecords.Length);
                foreach(var record in OwnershipRecords) VesselOwnershipWire.Write(lidgrenMsg,record);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            var n = lidgrenMsg.ReadInt32();
            if(n<0 || n>VesselOwnershipPolicy.MaxRecords) throw new System.IO.InvalidDataException();
            VesselOwnershipWire.Require(lidgrenMsg,n*256);
            VesselIds = new Guid[n];
            AgencyIds = new Guid[n];
            for (int i = 0; i < n; i++)
            {
                VesselIds[i] = new Guid(lidgrenMsg.ReadBytes(16));
                AgencyIds[i] = new Guid(lidgrenMsg.ReadBytes(16));
            }
            OwnershipSnapshotPresent=false; OwnershipRevision=0; OwnershipRecords=Array.Empty<VesselOwnershipRecord>();
            if(lidgrenMsg.LengthBits-lidgrenMsg.Position>=8 && lidgrenMsg.ReadBoolean()) {
                VesselOwnershipWire.Require(lidgrenMsg,96); OwnershipRevision=lidgrenMsg.ReadInt64(); var count=lidgrenMsg.ReadInt32();
                if(count<0 || count>VesselOwnershipPolicy.MaxRecords || OwnershipRevision<0) throw new System.IO.InvalidDataException();
                OwnershipRecords=new VesselOwnershipRecord[count]; for(int i=0;i<count;i++) OwnershipRecords[i]=VesselOwnershipWire.Read(lidgrenMsg);
                OwnershipSnapshotPresent=true;
            }
        }

        internal override int InternalGetMessageSize()
            => sizeof(int) + (VesselIds?.Length ?? 0) * 32 + (OwnershipSnapshotPresent ? 13 + global::System.Linq.Enumerable.Sum(OwnershipRecords, VesselOwnershipWire.Size) : 0);
    }
}
