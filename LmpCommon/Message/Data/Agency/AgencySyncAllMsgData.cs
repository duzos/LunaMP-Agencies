using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Agency
{
    public class AgencySyncAllMsgData : AgencyBaseMsgData
    {
        internal AgencySyncAllMsgData() { }

        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvSyncAll;
        public override string ClassName { get; } = nameof(AgencySyncAllMsgData);

        /// <summary>
        /// The recipient's current agency id (Guid.Empty if none).
        /// Allows clients to know "this is me" without a second roundtrip.
        /// </summary>
        public Guid MyAgencyId;

        public AgencyInfo[] Agencies = Array.Empty<AgencyInfo>();

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            lidgrenMsg.Write(MyAgencyId.ToByteArray());
            var arr = Agencies ?? Array.Empty<AgencyInfo>();
            lidgrenMsg.Write(arr.Length);
            foreach (var a in arr)
                AgencyWireHelpers.WriteAgencyInfo(lidgrenMsg, a);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            MyAgencyId = new Guid(lidgrenMsg.ReadBytes(16));
            var count = lidgrenMsg.ReadInt32();
            if (count < 0) count = 0;
            Agencies = new AgencyInfo[count];
            for (int i = 0; i < count; i++)
                Agencies[i] = AgencyWireHelpers.ReadAgencyInfo(lidgrenMsg);
        }

        internal override int InternalGetMessageSize() => 16 + sizeof(int);
    }
}
