using System;
using System.IO;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using LmpCommon.Message.Base;
namespace LmpCommon.Message.Data.Agency
{
    public sealed class AgencyVisibilityCommandMsgData : AgencyBaseMsgData
    {
        internal AgencyVisibilityCommandMsgData() { }
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliVisibilityCommand;
        public override string ClassName => nameof(AgencyVisibilityCommandMsgData);
        public Guid RequestId, VesselId, TargetAgencyId;
        public VisibilityOperation Operation;
        public bool Enabled;
        public VisibilityOverride Rule;
        public long ExpectedOwnershipRevision;
        internal override void InternalSerialize(NetOutgoingMessage message)
        {
            GuidUtil.Serialize(RequestId, message); GuidUtil.Serialize(VesselId, message); GuidUtil.Serialize(TargetAgencyId, message);
            message.Write((byte)Operation); message.Write(Enabled); message.Write((byte)Rule); message.Write(ExpectedOwnershipRevision);
        }
        internal override void InternalDeserialize(NetIncomingMessage message)
        {
            VesselOwnershipWire.Require(message, 465);
            RequestId = GuidUtil.Deserialize(message); VesselId = GuidUtil.Deserialize(message); TargetAgencyId = GuidUtil.Deserialize(message);
            Operation = (VisibilityOperation)message.ReadByte(); Enabled = message.ReadBoolean(); Rule = (VisibilityOverride)message.ReadByte(); ExpectedOwnershipRevision = message.ReadInt64();
            if (!Enum.IsDefined(typeof(VisibilityOperation), Operation) || !Enum.IsDefined(typeof(VisibilityOverride), Rule) || ExpectedOwnershipRevision < 0) throw new InvalidDataException();
        }
        internal override int InternalGetMessageSize() => 59;
    }
}
