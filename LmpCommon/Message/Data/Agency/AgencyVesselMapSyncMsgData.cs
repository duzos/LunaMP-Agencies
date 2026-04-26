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

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            var n = VesselIds?.Length ?? 0;
            lidgrenMsg.Write(n);
            for (int i = 0; i < n; i++)
            {
                lidgrenMsg.Write(VesselIds[i].ToByteArray());
                lidgrenMsg.Write(AgencyIds[i].ToByteArray());
            }
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            var n = lidgrenMsg.ReadInt32();
            if (n < 0) n = 0;
            VesselIds = new Guid[n];
            AgencyIds = new Guid[n];
            for (int i = 0; i < n; i++)
            {
                VesselIds[i] = new Guid(lidgrenMsg.ReadBytes(16));
                AgencyIds[i] = new Guid(lidgrenMsg.ReadBytes(16));
            }
        }

        internal override int InternalGetMessageSize()
            => sizeof(int) + (VesselIds?.Length ?? 0) * 32;
    }
}
