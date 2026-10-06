using System;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using LmpCommon.Message.Base;
namespace LmpCommon.Message.Data.Agency
{
 public sealed class AgencyVesselOwnershipCommandMsgData: AgencyBaseMsgData
 {
 internal AgencyVesselOwnershipCommandMsgData() {}
 public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliVesselOwnershipCommand;
 public override string ClassName => nameof(AgencyVesselOwnershipCommandMsgData);
 public Guid RequestId;
 public Guid VesselId;
 public Guid TargetAgencyId;
 public VesselOwnershipOperation Operation;
 public VesselDockingPolicy DockingPolicy;
 internal override void InternalSerialize(NetOutgoingMessage m) { GuidUtil.Serialize(RequestId, m); GuidUtil.Serialize(VesselId, m); GuidUtil.Serialize(TargetAgencyId, m); m.Write((byte)Operation); m.Write((byte)DockingPolicy); }
 internal override void InternalDeserialize(NetIncomingMessage m) { RequestId = GuidUtil.Deserialize(m); VesselId = GuidUtil.Deserialize(m); TargetAgencyId = GuidUtil.Deserialize(m); Operation = (VesselOwnershipOperation)m.ReadByte(); if (!Enum.IsDefined(typeof(VesselOwnershipOperation), Operation)) throw new System.IO.InvalidDataException("Invalid enum."); DockingPolicy = (VesselDockingPolicy)m.ReadByte(); if (!Enum.IsDefined(typeof(VesselDockingPolicy), DockingPolicy)) throw new System.IO.InvalidDataException("Invalid enum."); }
 internal override int InternalGetMessageSize() => 16 + 16 + 16 + 1 + 1;
 }
}
