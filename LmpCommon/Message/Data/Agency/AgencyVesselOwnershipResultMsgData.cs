using System;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using LmpCommon.Message.Base;
namespace LmpCommon.Message.Data.Agency
{
 public sealed class AgencyVesselOwnershipResultMsgData: AgencyBaseMsgData
 {
 internal AgencyVesselOwnershipResultMsgData() {}
 public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvVesselOwnershipResult;
 public override string ClassName => nameof(AgencyVesselOwnershipResultMsgData);
 public Guid RequestId;
 public Guid VesselId;
 public bool Success;
 public string Reason;
 internal override void InternalSerialize(NetOutgoingMessage m) { GuidUtil.Serialize(RequestId, m); GuidUtil.Serialize(VesselId, m); m.Write(Success); m.Write(Reason ?? string.Empty); }
 internal override void InternalDeserialize(NetIncomingMessage m) { RequestId = GuidUtil.Deserialize(m); VesselId = GuidUtil.Deserialize(m); Success = m.ReadBoolean(); Reason = m.ReadString(); if (Reason.Length > 512) throw new System.IO.InvalidDataException("Text too long."); }
 internal override int InternalGetMessageSize() => 16 + 16 + 1 + Reason.GetByteCount();
 }
}
