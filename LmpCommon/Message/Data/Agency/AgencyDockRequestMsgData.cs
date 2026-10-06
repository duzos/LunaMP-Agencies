using System;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using LmpCommon.Message.Base;
namespace LmpCommon.Message.Data.Agency
{
 public sealed class AgencyDockRequestMsgData: AgencyBaseMsgData
 {
 internal AgencyDockRequestMsgData() {}
 public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliDockRequest;
 public override string ClassName => nameof(AgencyDockRequestMsgData);
 public Guid RequestId;
 public Guid SourceVesselId;
 public Guid TargetVesselId;
 internal override void InternalSerialize(NetOutgoingMessage m) { GuidUtil.Serialize(RequestId, m); GuidUtil.Serialize(SourceVesselId, m); GuidUtil.Serialize(TargetVesselId, m); }
 internal override void InternalDeserialize(NetIncomingMessage m) { RequestId = GuidUtil.Deserialize(m); SourceVesselId = GuidUtil.Deserialize(m); TargetVesselId = GuidUtil.Deserialize(m); }
 internal override int InternalGetMessageSize() => 16 + 16 + 16;
 }
}
