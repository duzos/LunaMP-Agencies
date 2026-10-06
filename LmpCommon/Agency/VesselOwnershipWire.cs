using System;
using System.IO;
using System.Linq;
using Lidgren.Network;
namespace LmpCommon.Agency
{
 public static class VesselOwnershipWire
 {
  public static void Write(NetOutgoingMessage m, VesselOwnershipRecord r)
  {
   if (r == null || r.CoOwnerAgencyIds == null || r.CoOwnerAgencyIds.Length > VesselOwnershipPolicy.MaxCoOwners) throw new InvalidDataException();
   m.Write(r.VesselId.ToByteArray()); m.Write(r.OwnerAgencyId.ToByteArray()); m.Write(r.Revision); m.Write((byte)r.DockingPolicy); m.Write(r.CoOwnerAgencyIds.Length);
   foreach(var id in r.CoOwnerAgencyIds) m.Write(id.ToByteArray());
  }
  public static VesselOwnershipRecord Read(NetIncomingMessage m)
  {
   Require(m, 45*8);
   var r=new VesselOwnershipRecord {VesselId=new Guid(m.ReadBytes(16)),OwnerAgencyId=new Guid(m.ReadBytes(16)),Revision=m.ReadInt64(),DockingPolicy=(VesselDockingPolicy)m.ReadByte()};
   var count=m.ReadInt32();
   if(r.VesselId==Guid.Empty || r.Revision<0 || !Enum.IsDefined(typeof(VesselDockingPolicy),r.DockingPolicy) || count<0 || count>VesselOwnershipPolicy.MaxCoOwners) throw new InvalidDataException();
   Require(m,count*128); r.CoOwnerAgencyIds=new Guid[count];
   for(var i=0;i<count;i++) r.CoOwnerAgencyIds[i]=new Guid(m.ReadBytes(16));
   if(r.CoOwnerAgencyIds.Any(x=>x==Guid.Empty || x==r.OwnerAgencyId) || r.CoOwnerAgencyIds.Distinct().Count()!=count) throw new InvalidDataException();
   return r;
  }
  public static int Size(VesselOwnershipRecord r) => 45+(r?.CoOwnerAgencyIds?.Length??0)*16;
  public static void Require(NetIncomingMessage m,int bits) { if(bits<0 || m.LengthBits-m.Position<bits) throw new EndOfStreamException(); }
 }
}
