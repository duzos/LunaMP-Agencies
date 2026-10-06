using System;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
using LmpCommon.Message.Base;
namespace LmpCommon.Message.Data.Agency
{
 public sealed class AgencyDockResponseMsgData: AgencyBaseMsgData
 {
 internal AgencyDockResponseMsgData() {}
 public override AgencyMessageType AgencyMessageType => AgencyMessageType.CliDockResponse;
 public override string ClassName => nameof(AgencyDockResponseMsgData);
 public Guid RequestId;
 public bool Accept;
 internal override void InternalSerialize(NetOutgoingMessage m) { GuidUtil.Serialize(RequestId, m); m.Write(Accept); }
 internal override void InternalDeserialize(NetIncomingMessage m) { RequestId = GuidUtil.Deserialize(m); Accept = m.ReadBoolean(); }
 internal override int InternalGetMessageSize() => 16 + 1;
 }
}
