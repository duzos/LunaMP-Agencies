using Lidgren.Network;
using LmpCommon.Locks;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
namespace LmpCommon.Message.Data.Lock
{
 public sealed class LockAcquireDeniedMsgData : LockBaseMsgData
 {
  internal LockAcquireDeniedMsgData() {}
  public override LockMessageType LockMessageType => LockMessageType.AcquireDenied;
  public override string ClassName => nameof(LockAcquireDeniedMsgData);
  public LockDefinition Lock = new LockDefinition();
  public string Reason;
  internal override void InternalSerialize(NetOutgoingMessage m) { Lock.Serialize(m); m.Write(Reason ?? string.Empty); }
  internal override void InternalDeserialize(NetIncomingMessage m) { Lock.Deserialize(m); Reason=m.ReadString(); if(Reason.Length>512) throw new System.IO.InvalidDataException(); }
  internal override int InternalGetMessageSize() => Lock.GetByteCount()+Reason.GetByteCount();
 }
}
