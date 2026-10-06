using System;
using System.IO;
using System.Linq;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Message.Types;
namespace LmpCommon.Message.Data.Agency
{
    public sealed class AgencyCommNetSnapshotMsgData : AgencyBaseMsgData
    {
        internal AgencyCommNetSnapshotMsgData() {}
        public override AgencyMessageType AgencyMessageType => AgencyMessageType.SrvCommNetSnapshot;
        public override string ClassName => nameof(AgencyCommNetSnapshotMsgData);
        public bool Ready;
        public long Revision;
        public CommNetEndpoint[] Endpoints=Array.Empty<CommNetEndpoint>();
        public CommNetPreference[] Preferences=Array.Empty<CommNetPreference>();
        internal override void InternalSerialize(NetOutgoingMessage m)
        {
            if(Revision<0 || Endpoints.Length>CommNetOptInPolicy.MaxEndpoints || Preferences.Length>CommNetOptInPolicy.MaxPreferences || Preferences.Sum(p=>p.Targets.Length)>CommNetOptInPolicy.MaxTotalTargets)throw new InvalidDataException();
            m.Write(Ready);m.Write(Revision);m.Write(Endpoints.Length);foreach(var e in Endpoints)CommNetWire.Write(m,e);
            m.Write(Preferences.Length);foreach(var p in Preferences)CommNetWire.Write(m,p);
        }
        internal override void InternalDeserialize(NetIncomingMessage m)
        {
            Ready=false;Revision=0;Endpoints=Array.Empty<CommNetEndpoint>();Preferences=Array.Empty<CommNetPreference>();
            VesselOwnershipWire.Require(m,97);var ready=m.ReadBoolean();var revision=m.ReadInt64();int count=m.ReadInt32();
            if(revision<0 || count<0 || count>CommNetOptInPolicy.MaxEndpoints)throw new InvalidDataException();VesselOwnershipWire.Require(m,count*320+32);
            var endpoints=new CommNetEndpoint[count];for(int i=0;i<count;i++)endpoints[i]=CommNetWire.Read(m);
            if(endpoints.Select(e=>e.VesselId).Distinct().Count()!=count)throw new InvalidDataException();
            count=m.ReadInt32();if(count<0 || count>CommNetOptInPolicy.MaxPreferences)throw new InvalidDataException();
            var preferences=new CommNetPreference[count];var totalTargets=0;for(int i=0;i<count;i++){preferences[i]=CommNetWire.ReadPreference(m);totalTargets+=preferences[i].Targets.Length;if(totalTargets>CommNetOptInPolicy.MaxTotalTargets)throw new InvalidDataException();}
            if(preferences.Select(p=>p.Source.VesselId).Distinct().Count()!=count)throw new InvalidDataException();
            Ready=ready;Revision=revision;Endpoints=endpoints;Preferences=preferences;
        }
        internal override int InternalGetMessageSize()=>17+40*Endpoints.Length+Preferences.Sum(CommNetWire.Size);
    }
}
