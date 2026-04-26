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

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(VesselId.ToByteArray());
            lidgrenMsg.Write(AgencyId.ToByteArray());
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            VesselId = new Guid(lidgrenMsg.ReadBytes(16));
            AgencyId = new Guid(lidgrenMsg.ReadBytes(16));
        }

        internal override int InternalGetMessageSize() => 32;
    }
}
