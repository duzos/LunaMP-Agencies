using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Server.Base;
using LmpCommon.Message.Types;
using System;
using System.Collections.Generic;

namespace LmpCommon.Message.Server
{
    public class AgencySrvMsg : SrvMsgBase<AgencyBaseMsgData>
    {
        internal AgencySrvMsg() { }

        public override string ClassName { get; } = nameof(AgencySrvMsg);

        protected override Dictionary<ushort, Type> SubTypeDictionary { get; } = new Dictionary<ushort, Type>
        {
            [(ushort)AgencyMessageType.SrvSyncAll] = typeof(AgencySyncAllMsgData),
            [(ushort)AgencyMessageType.SrvUpsert] = typeof(AgencyUpsertMsgData),
            [(ushort)AgencyMessageType.SrvDelete] = typeof(AgencyDeleteMsgData),
            [(ushort)AgencyMessageType.SrvJoinRequestPosted] = typeof(AgencyJoinRequestPostedMsgData),
            [(ushort)AgencyMessageType.SrvJoinRequestResolved] = typeof(AgencyJoinRequestResolvedMsgData),
            [(ushort)AgencyMessageType.SrvReply] = typeof(AgencyReplyMsgData),
            [(ushort)AgencyMessageType.SrvVesselMapSync] = typeof(AgencyVesselMapSyncMsgData),
            [(ushort)AgencyMessageType.SrvVesselMapEntry] = typeof(AgencyVesselMapEntryMsgData),
        };

        public override ServerMessageType MessageType => ServerMessageType.Agency;

        protected override int DefaultChannel => 23;

        public override NetDeliveryMethod NetDeliveryMethod => NetDeliveryMethod.ReliableOrdered;
    }
}
