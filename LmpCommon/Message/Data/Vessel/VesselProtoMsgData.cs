using System;
using LmpCommon.Agency;
using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;

namespace LmpCommon.Message.Data.Vessel
{
    public class VesselProtoMsgData : VesselBaseMsgData
    {
        internal VesselProtoMsgData() { }

        public int NumBytes;
        public byte[] Data = new byte[0];
        public bool ForceReload;

        /// <summary>
        /// Human-readable description of why this vessel update is being sent
        /// (e.g. "Flight ready (launch)", "Part decoupled", "Science transmission").
        /// Purely informational; consumed by the server for the craft-create/remove audit log.
        /// Written at the END of the payload so older peers (who stop reading earlier) stay compatible.
        /// </summary>
        public string Reason;
        public Guid EconomyLaunchId;
        public Guid EconomyLaunchToken;
        public Guid EconomyParentVesselId;
        public string EconomyEvaCrew;
        public ToolingCargo[] EconomyCargo;
        public Guid EconomySplitOperationId;
        public byte[] EconomySplitParentData = Array.Empty<byte>();
        public int[] EconomyManifestIndices = Array.Empty<int>();

        public override VesselMessageType VesselMessageType => VesselMessageType.Proto;

        public override string ClassName { get; } = nameof(VesselProtoMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            lidgrenMsg.Write(ForceReload);
            Common.ThreadSafeCompress(this, ref Data, ref NumBytes);

            lidgrenMsg.Write(NumBytes);
            lidgrenMsg.Write(Data, 0, NumBytes);

            // Backwards-compatible field: must be written LAST so older peers that don't know
            // about it simply stop reading before this byte range.
            lidgrenMsg.Write(Reason ?? string.Empty);
            if (EconomyLaunchId != Guid.Empty || EconomyParentVesselId != Guid.Empty || EconomyCargo != null || EconomySplitParentData.Length > 0)
            {
                if (EconomyManifestIndices.Length > ToolingPolicy.MaxParts) throw new System.IO.InvalidDataException();
                GuidUtil.Serialize(EconomyLaunchId, lidgrenMsg);
                GuidUtil.Serialize(EconomyLaunchToken, lidgrenMsg);
                lidgrenMsg.Write(EconomyManifestIndices.Length);
                foreach (var index in EconomyManifestIndices) lidgrenMsg.Write(index);
                GuidUtil.Serialize(EconomyParentVesselId, lidgrenMsg);
                lidgrenMsg.Write(EconomyEvaCrew ?? string.Empty);
                AgencyEconomyWire.Write(lidgrenMsg, EconomyCargo ?? Array.Empty<ToolingCargo>());
                if (EconomySplitParentData.Length > VesselOwnershipPolicy.MaxMergedVesselBytes) throw new System.IO.InvalidDataException();
                lidgrenMsg.Write(EconomySplitParentData.Length);
                lidgrenMsg.Write(EconomySplitParentData);
                GuidUtil.Serialize(EconomySplitOperationId, lidgrenMsg);
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            ForceReload = lidgrenMsg.ReadBoolean();

            NumBytes = lidgrenMsg.ReadInt32();
            if (Data.Length < NumBytes)
                Data = new byte[NumBytes];

            lidgrenMsg.ReadBytes(Data, 0, NumBytes);

            Common.ThreadSafeDecompress(this, ref Data, NumBytes, out NumBytes);

            // Backwards-compatible read: older peers don't send this trailing field.
            Reason = lidgrenMsg.Position < lidgrenMsg.LengthBits ? lidgrenMsg.ReadString() : null;
            EconomyLaunchId = EconomyLaunchToken = EconomyParentVesselId = Guid.Empty;
            EconomyEvaCrew = null;
            EconomyCargo = null;
            EconomySplitParentData = Array.Empty<byte>();
            EconomySplitOperationId = Guid.Empty;
            EconomyManifestIndices = Array.Empty<int>();
            if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
            {
                VesselOwnershipWire.Require(lidgrenMsg, 288);
                EconomyLaunchId = GuidUtil.Deserialize(lidgrenMsg);
                EconomyLaunchToken = GuidUtil.Deserialize(lidgrenMsg);
                var count = lidgrenMsg.ReadInt32();
                if (count < 0 || count > ToolingPolicy.MaxParts) throw new System.IO.InvalidDataException();
                VesselOwnershipWire.Require(lidgrenMsg, count * 32);
                EconomyManifestIndices = new int[count];
                for (var i = 0; i < count; i++) EconomyManifestIndices[i] = lidgrenMsg.ReadInt32();
                if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
                {
                    VesselOwnershipWire.Require(lidgrenMsg, 128);
                    EconomyParentVesselId = GuidUtil.Deserialize(lidgrenMsg);
                    EconomyEvaCrew = lidgrenMsg.ReadString();
                    if (EconomyEvaCrew.Length > 256) throw new System.IO.InvalidDataException();
                    if (lidgrenMsg.Position < lidgrenMsg.LengthBits) EconomyCargo = AgencyEconomyWire.Read<ToolingCargo[]>(lidgrenMsg);
                    if (lidgrenMsg.Position < lidgrenMsg.LengthBits)
                    {
                        VesselOwnershipWire.Require(lidgrenMsg, 32);
                        var parentSize = lidgrenMsg.ReadInt32();
                        if (parentSize < 0 || parentSize > VesselOwnershipPolicy.MaxMergedVesselBytes) throw new System.IO.InvalidDataException();
                        VesselOwnershipWire.Require(lidgrenMsg, checked(parentSize * 8));
                        EconomySplitParentData = lidgrenMsg.ReadBytes(parentSize);
                        if (lidgrenMsg.Position < lidgrenMsg.LengthBits) { VesselOwnershipWire.Require(lidgrenMsg, 128); EconomySplitOperationId = GuidUtil.Deserialize(lidgrenMsg); }
                    }
                }
            }
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + sizeof(bool) + sizeof(int) + sizeof(byte) * NumBytes + Reason.GetByteCount() + (EconomyLaunchId == Guid.Empty && EconomyParentVesselId == Guid.Empty && EconomyCargo == null && EconomySplitParentData.Length == 0 ? 0 : 52 + EconomyManifestIndices.Length * 4 + EconomyEvaCrew.GetByteCount() + AgencyEconomyWire.Size(EconomyCargo ?? Array.Empty<ToolingCargo>()) + 20 + EconomySplitParentData.Length);
        }
    }
}
