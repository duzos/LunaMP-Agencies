using System;
using System.IO;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
namespace LmpCommon.Message.Data.Agency
{
    public sealed class AgencyCommNetCommandMsgData : AgencyBaseMsgData
    {
        internal AgencyCommNetCommandMsgData() {}
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliCommNetCommand;
        public override string ClassName => nameof(AgencyCommNetCommandMsgData);
        public Guid RequestId,VesselId,TargetVesselId;
        public CommNetOperation Operation;
        public bool Enabled;
        internal override void InternalSerialize(NetOutgoingMessage m) {GuidUtil.Serialize(RequestId,m);GuidUtil.Serialize(VesselId,m);GuidUtil.Serialize(TargetVesselId,m);m.Write((byte)Operation);m.Write(Enabled);}
        internal override void InternalDeserialize(NetIncomingMessage m) {VesselOwnershipWire.Require(m,393);RequestId=GuidUtil.Deserialize(m);VesselId=GuidUtil.Deserialize(m);TargetVesselId=GuidUtil.Deserialize(m);Operation=(CommNetOperation)m.ReadByte();Enabled=m.ReadBoolean();if(!Enum.IsDefined(typeof(CommNetOperation),Operation))throw new InvalidDataException();}
        internal override int InternalGetMessageSize()=>50;
    }
    public sealed class AgencyCommNetResultMsgData : AgencyBaseMsgData
    {
        internal AgencyCommNetResultMsgData() {}
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvCommNetResult;
        public override string ClassName => nameof(AgencyCommNetResultMsgData);
        public Guid RequestId;
        public bool Success;
        public string Reason;
        internal override void InternalSerialize(NetOutgoingMessage m) {GuidUtil.Serialize(RequestId,m);m.Write(Success);m.Write(Reason??string.Empty);}
        internal override void InternalDeserialize(NetIncomingMessage m) {VesselOwnershipWire.Require(m,129);RequestId=GuidUtil.Deserialize(m);Success=m.ReadBoolean();Reason=m.ReadString();if(Reason.Length>512)throw new InvalidDataException();}
        internal override int InternalGetMessageSize()=>17+Reason.GetByteCount();
    }
}
