using Lidgren.Network;
using LmpCommon.Message.Base;
using System;

namespace LmpCommon.Message.Data.ShareProgress
{
    /// <summary>
    /// Wrapper for transmitting the ksp Contract objects.
    /// </summary>
    public class ContractInfo
    {
        public Guid ContractGuid;
        public int NumBytes;
        public byte[] Data = new byte[0];

        /// <summary>
        /// Agency that owns this contract. Guid.Empty means unowned
        /// (visible to all agencies, acceptable by the first agency to try).
        /// Set on server when a contract is first accepted; relayed to all
        /// clients so listings show ownership consistently.
        /// </summary>
        public Guid OwningAgencyId = Guid.Empty;

        public ContractInfo() { }

        /// <summary>
        /// Copy constructor.
        /// </summary>
        public ContractInfo(ContractInfo copyFrom)
        {
            ContractGuid = copyFrom.ContractGuid;
            OwningAgencyId = copyFrom.OwningAgencyId;
            NumBytes = copyFrom.NumBytes;
            if (Data.Length < NumBytes)
                Data = new byte[NumBytes];

            Array.Copy(copyFrom.Data, Data, NumBytes);
        }

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            GuidUtil.Serialize(ContractGuid, lidgrenMsg);
            GuidUtil.Serialize(OwningAgencyId, lidgrenMsg);

            Common.ThreadSafeCompress(this, ref Data, ref NumBytes);

            lidgrenMsg.Write(NumBytes);
            lidgrenMsg.Write(Data, 0, NumBytes);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            ContractGuid = GuidUtil.Deserialize(lidgrenMsg);
            OwningAgencyId = GuidUtil.Deserialize(lidgrenMsg);

            NumBytes = lidgrenMsg.ReadInt32();
            if (Data.Length < NumBytes)
                Data = new byte[NumBytes];

            lidgrenMsg.ReadBytes(Data, 0, NumBytes);

            Common.ThreadSafeDecompress(this, ref Data, NumBytes, out NumBytes);
        }

        public int GetByteCount()
        {
            return GuidUtil.ByteSize * 2 + sizeof(int) + sizeof(byte) * NumBytes;
        }
    }
}
