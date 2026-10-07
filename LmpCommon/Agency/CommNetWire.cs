using System;
using System.IO;
using System.Linq;
using Lidgren.Network;
namespace LmpCommon.Agency
{
    public static class CommNetWire
    {
        public static void Write(NetOutgoingMessage m,CommNetEndpoint endpoint)
        {
            if(endpoint==null || endpoint.VesselId==Guid.Empty || endpoint.OwnershipRevision<0) throw new InvalidDataException("Invalid CommNet endpoint.");
            m.Write(endpoint.VesselId.ToByteArray());m.Write(endpoint.OwnerAgencyId.ToByteArray());m.Write(endpoint.OwnershipRevision);
        }
        public static CommNetEndpoint Read(NetIncomingMessage m)
        {
            VesselOwnershipWire.Require(m,320);
            var e=new CommNetEndpoint{VesselId=new Guid(m.ReadBytes(16)),OwnerAgencyId=new Guid(m.ReadBytes(16)),OwnershipRevision=m.ReadInt64()};
            if(e.VesselId==Guid.Empty || e.OwnershipRevision<0) throw new InvalidDataException("Invalid CommNet endpoint.");return e;
        }
        public static void Write(NetOutgoingMessage m,CommNetPreference p)
        {
            if(p==null || p.Targets==null || p.Targets.Length>CommNetOptInPolicy.MaxTargets) throw new InvalidDataException();
            Write(m,p.Source);m.Write(p.AcceptAll);m.Write(p.ActiveScanning);m.Write(p.Targets.Length);foreach(var t in p.Targets)Write(m,t);
        }
        public static CommNetPreference ReadPreference(NetIncomingMessage m)
        {
            var p=new CommNetPreference{Source=Read(m)};VesselOwnershipWire.Require(m,34);p.AcceptAll=m.ReadBoolean();p.ActiveScanning=m.ReadBoolean();int count=m.ReadInt32();
            if(count<0 || count>CommNetOptInPolicy.MaxTargets)throw new InvalidDataException();VesselOwnershipWire.Require(m,count*320);
            p.Targets=new CommNetEndpoint[count];for(int i=0;i<count;i++)p.Targets[i]=Read(m);
            if(p.Targets.Select(x=>x.VesselId).Distinct().Count()!=count)throw new InvalidDataException();return p;
        }
        public static int Size(CommNetPreference p)=>45+40*p.Targets.Length;
    }
}
