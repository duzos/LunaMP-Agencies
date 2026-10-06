using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Server;
using Newtonsoft.Json;
using Server.Client;
using Server.Context;
using Server.Diagnostics;
using Server.Server;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.Agency
{
    public sealed class CommNetPreferenceDocument
    {
        public int Version=1;
        public long Revision;
        public CommNetPreference[] Preferences=Array.Empty<CommNetPreference>();
    }
    public static class AgencyCommNetStore
    {
        private static CommNetPreferenceDocument _document=new CommNetPreferenceDocument();
        private static string _loadError;
        private static long _snapshotRevision;
        public static string FilePath=>Path.Combine(ServerContext.UniverseDirectory,"AgencyCommNet.json");
        public static bool Enabled=>GeneralSettings.SettingsStore.AgencyCommNetOptIn && GeneralSettings.SettingsStore.AgencyCommNetPerAgency;
        public static bool Ready { get { lock(AgencyVesselMap.TransactionGate) return _loadError==null && AgencyVesselMap.Ready; } }
        public static void Load()
        {
            lock(AgencyVesselMap.TransactionGate)
            {
                _document=new CommNetPreferenceDocument();_loadError=null;_snapshotRevision=0;
                try
                {
                    if(File.Exists(FilePath))
                    {
                        if(new FileInfo(FilePath).Length>32L*1024*1024)throw new InvalidDataException("CommNet preferences exceed size limit.");
                        var candidate=JsonConvert.DeserializeObject<CommNetPreferenceDocument>(File.ReadAllText(FilePath));Validate(candidate);_document=candidate;
                    }
                    _snapshotRevision=_document.Revision;
                }
                catch(Exception e) {_loadError="CommNet preferences could not be loaded ("+e.GetType().Name+"). Repair the saved file before editing.";}
            }
        }
        private static void ValidateEndpoint(CommNetEndpoint e)
        {
            if(e==null || e.VesselId==Guid.Empty || e.OwnerAgencyId==Guid.Empty || e.OwnershipRevision<0)throw new InvalidDataException("Invalid endpoint stamp.");
        }
        private static void Validate(CommNetPreferenceDocument d)
        {
            if(d==null || d.Version!=1 || d.Revision<0 || d.Preferences==null || d.Preferences.Length>CommNetOptInPolicy.MaxPreferences)throw new InvalidDataException();
            if(d.Preferences.Any(p=>p==null) || d.Preferences.Select(p=>p.Source?.VesselId).Distinct().Count()!=d.Preferences.Length)throw new InvalidDataException();
            if(d.Preferences.Sum(p=>p.Targets?.Length??0)>CommNetOptInPolicy.MaxTotalTargets)throw new InvalidDataException("Too many CommNet selections.");
            foreach(var p in d.Preferences)
            {
                ValidateEndpoint(p.Source);
                if(p.Targets==null || p.Targets.Length>CommNetOptInPolicy.MaxTargets || p.Targets.Any(t=>t==null) || p.Targets.Select(t=>t.VesselId).Distinct().Count()!=p.Targets.Length)throw new InvalidDataException();
                foreach(var t in p.Targets){ValidateEndpoint(t);if(t.VesselId==p.Source.VesselId)throw new InvalidDataException();}
            }
        }
        private static CommNetEndpoint[] Endpoints()
        {
            return VesselStoreSystem.CurrentVessels.Keys.Where(id=>!AgencyVesselMap.IsAbsorbed(id) && !AgencyVesselMap.IsPendingSplit(id) && !VesselContext.RemovedVessels.ContainsKey(id)).OrderBy(id=>id).Take(CommNetOptInPolicy.MaxEndpoints).Select(id=>
            {
                var r=AgencyVesselMap.Get(id);var owner=r?.OwnerAgencyId??Guid.Empty;
                if(!AgencyStore.Agencies.ContainsKey(owner))owner=Guid.Empty;
                return new CommNetEndpoint{VesselId=id,OwnerAgencyId=owner,OwnershipRevision=r?.Revision??0};
            }).ToArray();
        }
        private static CommNetPreference[] CurrentPreferences(CommNetEndpoint[] endpoints)
        {
            var current=endpoints.ToDictionary(e=>e.VesselId);
            return _document.Preferences.Where(p=>current.TryGetValue(p.Source.VesselId,out var source) && source.OwnerAgencyId!=Guid.Empty && CommNetOptInPolicy.SameStamp(source,p.Source)).Select(p=>new CommNetPreference
            {
                Source=p.Source.Copy(),AcceptAll=p.AcceptAll,
                Targets=p.Targets.Where(t=>current.TryGetValue(t.VesselId,out var target) && target.OwnerAgencyId!=Guid.Empty && CommNetOptInPolicy.SameStamp(target,t)).Select(t=>t.Copy()).ToArray()
            }).ToArray();
        }
        public static AgencyCommNetSnapshotMsgData GetSnapshot()
        {
            lock(AgencyVesselMap.TransactionGate)
            {
                var result=ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyCommNetSnapshotMsgData>();result.Ready=Ready;result.Revision=_snapshotRevision;
                result.Endpoints=AgencyVesselMap.Ready?Endpoints():Array.Empty<CommNetEndpoint>();result.Preferences=Ready?CurrentPreferences(result.Endpoints):Array.Empty<CommNetPreference>();return result;
            }
        }
        public static (bool Success,string Reason) Mutate(ClientStructure client,Guid vesselId,Guid targetId,CommNetOperation operation,bool enabled)
        {
            lock(AgencyVesselMap.TransactionGate)
            {
                if(!Enabled)return(false,"CommNet opt-in is disabled.");
                if(!Ready)return(false,_loadError??"Vessel ownership is unavailable.");
                if(client==null || !client.Authenticated || client.ConnectionStatus!=ConnectionStatus.Connected || !ServerContext.Clients.Values.Any(c=>ReferenceEquals(c,client)) || !AgencyStore.Agencies.TryGetValue(client.AgencyId,out var agency) || !agency.HasMember(client.UniqueIdentifier))return(false,"Active agency membership is required.");
                var endpoints=Endpoints();var source=endpoints.FirstOrDefault(e=>e.VesselId==vesselId);
                if(source==null || source.OwnerAgencyId==Guid.Empty || source.OwnerAgencyId!=client.AgencyId)return(false,"Only members of the owning agency may edit this craft.");
                var rows=CurrentPreferences(endpoints).ToDictionary(p=>p.Source.VesselId);rows.TryGetValue(vesselId,out var preference);preference=preference??new CommNetPreference{Source=source.Copy()};
                switch(operation)
                {
                    case CommNetOperation.SetAcceptAll:preference.AcceptAll=enabled;break;
                    case CommNetOperation.SetTarget:
                        if(targetId==vesselId)return(false,"Select a different craft.");
                        var target=endpoints.FirstOrDefault(e=>e.VesselId==targetId);
                        if(enabled && (target==null || target.OwnerAgencyId==Guid.Empty))return(false,"Target craft must exist and have an owning agency.");
                        preference.Targets=preference.Targets.Where(t=>t.VesselId!=targetId).Concat(enabled?new[]{target.Copy()}:Array.Empty<CommNetEndpoint>()).ToArray();break;
                    default:return(false,"Unknown CommNet operation.");
                }
                rows[vesselId]=preference;
                var candidate=new CommNetPreferenceDocument{Revision=checked(Math.Max(_document.Revision,_snapshotRevision)+1),Preferences=rows.Values.ToArray()};
                try
                {
                    Validate(candidate);var json=JsonConvert.SerializeObject(candidate);
                    if(Encoding.UTF8.GetByteCount(json)>32*1024*1024)throw new InvalidDataException("CommNet preferences exceed size limit.");
                    AgencyVesselMap.AtomicWrite(FilePath,json);
                }
                catch(Exception e) {return(false,"CommNet preferences unchanged: "+e.GetType().Name);}
                _document=candidate;_snapshotRevision=candidate.Revision;return(true,"CommNet preferences saved.");
            }
        }
        public static void HandleCommand(ClientStructure client,AgencyCommNetCommandMsgData command)
        {
            var result=Mutate(client,command.VesselId,command.TargetVesselId,command.Operation,command.Enabled);
            PlaytestDiagnostics.Write("commnet.command",()=> $"vessel={command.VesselId} target={command.TargetVesselId} operation={command.Operation} enabled={command.Enabled} success={result.Success}");
            if(result.Success)Broadcast();
            var reply=ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyCommNetResultMsgData>();reply.RequestId=command.RequestId;reply.Success=result.Success;reply.Reason=result.Reason;MessageQueuer.SendToClient<AgencySrvMsg>(client,reply);
        }
        public static void SendTo(ClientStructure client)
        {
            if(Enabled)MessageQueuer.SendToClient<AgencySrvMsg>(client,GetSnapshot());
        }
        public static void Broadcast()
        {
            if(!Enabled)return;
            AgencyCommNetSnapshotMsgData snapshot;
            lock(AgencyVesselMap.TransactionGate){_snapshotRevision=checked(_snapshotRevision+1);snapshot=GetSnapshot();}
            MessageQueuer.SendToAllClients<AgencySrvMsg>(snapshot);
        }
    }
}
