using System;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using LmpCommon.Message.Base;
namespace LmpCommon.Message.Data.Agency
{
 public sealed class AgencyDockStatusMsgData: AgencyBaseMsgData
 {
 internal AgencyDockStatusMsgData() {}
 public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvDockStatus;
 public override string ClassName => nameof(AgencyDockStatusMsgData);
 public Guid RequestId;
 public Guid SourceVesselId;
 public Guid TargetVesselId;
 public Guid RequesterAgencyId;
 public string RequesterName;
 public DockConsentStatus Status;
 public long ExpiresUtcTicks;
 public string Reason;
 public Guid OperationId;
 public Guid GrantId;
 internal override void InternalSerialize(NetOutgoingMessage m) { GuidUtil.Serialize(RequestId, m); GuidUtil.Serialize(SourceVesselId, m); GuidUtil.Serialize(TargetVesselId, m); GuidUtil.Serialize(RequesterAgencyId, m); m.Write(RequesterName ?? string.Empty); m.Write((byte)Status); m.Write(ExpiresUtcTicks); m.Write(Reason ?? string.Empty); GuidUtil.Serialize(OperationId, m); GuidUtil.Serialize(GrantId, m); }
 internal override void InternalDeserialize(NetIncomingMessage m) { RequestId = GuidUtil.Deserialize(m); SourceVesselId = GuidUtil.Deserialize(m); TargetVesselId = GuidUtil.Deserialize(m); RequesterAgencyId = GuidUtil.Deserialize(m); RequesterName = m.ReadString(); if (RequesterName.Length > 512) throw new System.IO.InvalidDataException("Text too long."); Status = (DockConsentStatus)m.ReadByte(); if (!Enum.IsDefined(typeof(DockConsentStatus), Status)) throw new System.IO.InvalidDataException("Invalid enum."); ExpiresUtcTicks = m.ReadInt64(); Reason = m.ReadString(); if (Reason.Length > 512) throw new System.IO.InvalidDataException("Text too long."); OperationId = GuidUtil.Deserialize(m); GrantId = GuidUtil.Deserialize(m); }
 internal override int InternalGetMessageSize() => 16 + 16 + 16 + 16 + RequesterName.GetByteCount() + 1 + 8 + Reason.GetByteCount() + 16 + 16;
 }
}
