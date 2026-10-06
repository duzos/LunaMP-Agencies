using LmpCommon.Agency;
using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Vessel
{
    public class VesselCoupleMsgData : VesselBaseMsgData
    {
        /// <inheritdoc />
        internal VesselCoupleMsgData() { }
        public override VesselMessageType VesselMessageType => VesselMessageType.Couple;

        public uint PartFlightId;
        public Guid CoupledVesselId;
        public uint CoupledPartFlightId;
        public int SubspaceId;
        public int Trigger;
        public Guid OperationId;
        public Guid GrantId;
        public byte[] MergedVesselData = Array.Empty<byte>();

        public override string ClassName { get; } = nameof(VesselCoupleMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(PartFlightId);
            GuidUtil.Serialize(CoupledVesselId, lidgrenMsg);
            lidgrenMsg.Write(CoupledPartFlightId);
            lidgrenMsg.Write(SubspaceId);
            lidgrenMsg.Write(Trigger);
            if(OperationId!=Guid.Empty) {
                if(MergedVesselData.Length==0 || MergedVesselData.Length>VesselOwnershipPolicy.MaxMergedVesselBytes) throw new System.IO.InvalidDataException();
                GuidUtil.Serialize(OperationId,lidgrenMsg); GuidUtil.Serialize(GrantId,lidgrenMsg); lidgrenMsg.Write(MergedVesselData.Length); lidgrenMsg.Write(MergedVesselData);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            PartFlightId = lidgrenMsg.ReadUInt32();
            CoupledVesselId = GuidUtil.Deserialize(lidgrenMsg);
            CoupledPartFlightId = lidgrenMsg.ReadUInt32();
            SubspaceId = lidgrenMsg.ReadInt32();
            Trigger = lidgrenMsg.ReadInt32();
            OperationId=Guid.Empty; GrantId=Guid.Empty; MergedVesselData=Array.Empty<byte>();
            if(lidgrenMsg.Position<lidgrenMsg.LengthBits) {
                VesselOwnershipWire.Require(lidgrenMsg,288); OperationId=GuidUtil.Deserialize(lidgrenMsg); GrantId=GuidUtil.Deserialize(lidgrenMsg);
                int count=lidgrenMsg.ReadInt32(); if(OperationId==Guid.Empty || count<1 || count>VesselOwnershipPolicy.MaxMergedVesselBytes) throw new System.IO.InvalidDataException();
                VesselOwnershipWire.Require(lidgrenMsg,count*8); MergedVesselData=lidgrenMsg.ReadBytes(count);
            }
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(uint) * 2 + GuidUtil.ByteSize + sizeof(int) * 2 + (OperationId==Guid.Empty ? 0 : 36+MergedVesselData.Length);
        }
    }
}
