using LmpCommon.Agency;
using LmpCommon.Locks;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using Server.Client;
using Server.Context;
using Server.Server;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Server.Agency
{
    public static class VesselOwnershipSystem
    {
        private sealed class Request
        {
            public Guid Id,Grant,Source,Target,Agency,TargetOwner;
            public ClientStructure Client;
            public long SourceRevision,TargetRevision;
            public DateTime Expires;
            public DockConsentStatus Status;
        }
        private static readonly Dictionary<Guid,Request> Requests=new Dictionary<Guid,Request>();
        private static readonly HashSet<ClientStructure> Rejected=new HashSet<ClientStructure>();
        public static Func<DateTime> UtcNow=()=>DateTime.UtcNow;
        public static bool Enabled=>GeneralSettings.SettingsStore.AgencyVesselOwnership;
        public static bool IsRejected(ClientStructure c) { lock(AgencyVesselMap.TransactionGate) return Rejected.Contains(c); }
        private static bool Actor(ClientStructure c)=>c!=null && c.Authenticated && c.ConnectionStatus==LmpCommon.Enums.ConnectionStatus.Connected && ServerContext.Clients.Values.Any(x=>ReferenceEquals(x,c)) && c.AgencyId!=Guid.Empty && AgencyStore.Agencies.TryGetValue(c.AgencyId,out var a) && a.HasMember(c.UniqueIdentifier);
        // Deleting is a management act: the agency owner of the owning agency, an active member.
        internal static bool CanManageCraft(ClientStructure c,VesselOwnershipRecord record)=>Actor(c) && VesselOwnershipPolicy.CanManage(record,c.AgencyId,AgencyStore.Agencies[c.AgencyId].OwnerUniqueId==c.UniqueIdentifier);
        public static bool CanControl(ClientStructure c,Guid vessel)=>!Enabled || (AgencyVesselMap.Ready && Actor(c) && !IsRejected(c) && !AgencyVesselMap.IsDeleted(vessel) && !AgencyVesselMap.IsAbsorbed(vessel) && !AgencyVesselMap.IsPendingSplit(vessel) && VesselOwnershipPolicy.CanControl(AgencyVesselMap.Get(vessel),c.AgencyId));
        private static long Revision(Guid id)=>AgencyVesselMap.Get(id)?.Revision??0;
        private static int Timeout(bool grant)=>Math.Max(1,Math.Min(120,grant?GeneralSettings.SettingsStore.AgencyDockGrantTimeoutSeconds:GeneralSettings.SettingsStore.AgencyDockRequestTimeoutSeconds));
        private static bool Valid(Request r)=>Actor(r.Client) && !Rejected.Contains(r.Client) && r.Client.AgencyId==r.Agency && Revision(r.Source)==r.SourceRevision && Revision(r.Target)==r.TargetRevision && (AgencyVesselMap.Get(r.Target)?.OwnerAgencyId??Guid.Empty)==r.TargetOwner && VesselStoreSystem.VesselExists(r.Source) && VesselStoreSystem.VesselExists(r.Target) && LockSystem.LockQuery.ControlLockBelongsToPlayer(r.Source,r.Client.PlayerName) && CanControl(r.Client,r.Source);
        private static void SendStatus(Request r,ClientStructure to,string reason="",Guid operation=default)
        {
            global::Server.Diagnostics.PlaytestDiagnostics.Write("ownership.docking",()=> $"request={r.Id} operation={operation} source={r.Source} target={r.Target} status={r.Status} agency={r.Agency}");
            var m=ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyDockStatusMsgData>();
            m.RequestId=r.Id;m.GrantId=r.Grant;m.SourceVesselId=r.Source;m.TargetVesselId=r.Target;m.RequesterAgencyId=r.Agency;m.RequesterName=r.Client.PlayerName;m.Status=r.Status;m.ExpiresUtcTicks=r.Expires.Ticks;m.Reason=reason;m.OperationId=operation;
            MessageQueuer.SendToClient<AgencySrvMsg>(to,m);
        }
        public static void RequestDock(ClientStructure client,AgencyDockRequestMsgData data)
        {
            if(!Enabled) return;
            SweepExpired();
            Request request; ClientStructure[] recipients=Array.Empty<ClientStructure>(); string reason="";
            lock(AgencyVesselMap.TransactionGate)
            {
                Prune();
                foreach(var stale in Requests.Values.Where(x=>(x.Status==DockConsentStatus.Pending || x.Status==DockConsentStatus.Granted) && !Valid(x))) stale.Status=DockConsentStatus.Cancelled;
                request=Requests.Values.FirstOrDefault(r=>ReferenceEquals(r.Client,client) && r.Source==data.SourceVesselId && r.Target==data.TargetVesselId && (r.Status==DockConsentStatus.Pending || r.Status==DockConsentStatus.Granted));
                if(request==null)
                {
                    request=new Request {Id=data.RequestId,Source=data.SourceVesselId,Target=data.TargetVesselId,Client=client,Agency=client.AgencyId,SourceRevision=Revision(data.SourceVesselId),TargetRevision=Revision(data.TargetVesselId),TargetOwner=AgencyVesselMap.Get(data.TargetVesselId)?.OwnerAgencyId??Guid.Empty,Expires=UtcNow().AddSeconds(Timeout(false)),Status=DockConsentStatus.Denied};
                    if(data.RequestId==Guid.Empty || Requests.ContainsKey(data.RequestId) || Requests.Count>=256 || data.SourceVesselId==data.TargetVesselId || !Valid(request)) reason="Docking request is not valid for your controlled craft.";
                    else
                    {
                        var record=AgencyVesselMap.Get(request.Target);
                        recipients=record==null?Array.Empty<ClientStructure>():AgencyNetwork.GetOnlineAgencyMembers(record.OwnerAgencyId).ToArray();
                        var decision=VesselOwnershipPolicy.EvaluateDock(record,client.AgencyId,recipients.Length>0);
                        request.Status=decision==DockingDecision.Allow?DockConsentStatus.Granted:decision==DockingDecision.RequestConsent?DockConsentStatus.Pending:DockConsentStatus.Denied;
                        if(request.Status==DockConsentStatus.Granted) {request.Grant=Guid.NewGuid();request.Expires=UtcNow().AddSeconds(Timeout(true));}
                        Requests[request.Id]=request;
                    }
                }
            }
            SendStatus(request,client,reason);
            if(request.Status==DockConsentStatus.Pending) foreach(var recipient in recipients) SendStatus(request,recipient);
        }
        public static void SweepExpired()
        {
            Request[] expired;
            lock(AgencyVesselMap.TransactionGate)
            {
                expired=Requests.Values.Where(r=>(r.Status==DockConsentStatus.Pending || r.Status==DockConsentStatus.Granted) && r.Expires<=UtcNow()).ToArray();
                foreach(var r in expired) r.Status=DockConsentStatus.Expired;
            }
            foreach(var r in expired) Notify(r,"Docking permission expired.");
        }
        private static void Notify(Request r,string reason)
        {
            SendStatus(r,r.Client,reason);
            var targetOwner=AgencyVesselMap.Get(r.Target)?.OwnerAgencyId??Guid.Empty;
            foreach(var c in AgencyNetwork.GetOnlineAgencyMembers(targetOwner)) if(!ReferenceEquals(c,r.Client)) SendStatus(r,c,reason);
        }
        private static void Prune()
        {
            foreach(var id in Requests.Where(p=>p.Value.Expires<UtcNow().AddMinutes(-2)).Select(p=>p.Key).ToArray()) Requests.Remove(id);
        }
        public static void RespondDock(ClientStructure client,AgencyDockResponseMsgData data)
        {
            Request r;
            lock(AgencyVesselMap.TransactionGate)
            {
                if(!Requests.TryGetValue(data.RequestId,out r) || r.Status!=DockConsentStatus.Pending || !Actor(client) || AgencyVesselMap.Get(r.Target)?.OwnerAgencyId!=client.AgencyId) return;
                r.Status=r.Expires<=UtcNow()?DockConsentStatus.Expired:!Valid(r)?DockConsentStatus.Cancelled:data.Accept?DockConsentStatus.Granted:DockConsentStatus.Denied;
                if(r.Status==DockConsentStatus.Granted) {r.Grant=Guid.NewGuid();r.Expires=UtcNow().AddSeconds(Timeout(true));}
            }
            SendStatus(r,r.Client);
            foreach(var c in AgencyNetwork.GetOnlineAgencyMembers(client.AgencyId)) SendStatus(r,c);
        }
        public static void Command(ClientStructure client,AgencyVesselOwnershipCommandMsgData data)
        {
            if(!Enabled) return;
            (bool Success,string Reason) result;
            // Deleting runs through the central removal service, which owns the gate, locks and broadcasts.
            if(data.Operation==VesselOwnershipOperation.Delete) result=Actor(client)?AgencyVesselMap.DeleteCraft(data.VesselId,client):(false,"Craft or agency is unavailable.");
            else lock(AgencyVesselMap.TransactionGate)
            {
                if(!Actor(client) || AgencyVesselMap.IsPendingSplit(data.VesselId) || !VesselStoreSystem.VesselExists(data.VesselId)) result=(false,"Craft or agency is unavailable.");
                else if((data.Operation==VesselOwnershipOperation.Transfer || data.Operation==VesselOwnershipOperation.AddCoOwner) && !AgencyStore.Agencies.ContainsKey(data.TargetAgencyId)) result=(false,"Target agency not found.");
                else result=AgencyVesselMap.Mutate(data.VesselId,client.AgencyId,AgencyStore.Agencies[client.AgencyId].OwnerUniqueId==client.UniqueIdentifier,data.Operation,data.TargetAgencyId,data.DockingPolicy);
            }
            global::Server.Diagnostics.PlaytestDiagnostics.Write("ownership.command",()=> $"vessel={data.VesselId} operation={data.Operation} actorAgency={client.AgencyId} success={result.Success}");
            if(result.Success && data.Operation!=VesselOwnershipOperation.Delete) Changed();
            var m=ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyVesselOwnershipResultMsgData>();m.RequestId=data.RequestId;m.VesselId=data.VesselId;m.Success=result.Success;m.Reason=result.Reason;MessageQueuer.SendToClient<AgencySrvMsg>(client,m);
        }
        public static void Changed()
        {
            List<Request> cancelled=new List<Request>(); List<LockDefinition> revoked=new List<LockDefinition>();
            lock(AgencyVesselMap.TransactionGate)
            {
                foreach(var r in Requests.Values.Where(r=>r.Status==DockConsentStatus.Pending || r.Status==DockConsentStatus.Granted)) if(!Valid(r)) {r.Status=DockConsentStatus.Cancelled;cancelled.Add(r);}
                foreach(var client in ClientRetriever.GetAuthenticatedClients()) foreach(var l in LockSystem.LockQuery.GetAllPlayerLocks(client.PlayerName).Where(l=>l.Type==LockType.Control).ToArray()) if(!CanControl(client,l.VesselId) && LockSystem.ReleaseLock(l)) revoked.Add(l);
            }
            foreach(var r in cancelled) Notify(r,"Craft permissions changed.");
            foreach(var l in revoked) {var m=ServerContext.ServerMessageFactory.CreateNewMessageData<LockReleaseMsgData>();m.Lock=l;m.LockResult=true;MessageQueuer.SendToAllClients<LockSrvMsg>(m);}
            foreach(var client in ClientRetriever.GetAuthenticatedClients()) AgencyNetwork.SendVesselMapSyncTo(client);
            AgencyCommNetStore.Broadcast();
            AgencyVisibilityStore.Broadcast();
        }
        public static void Disconnect(ClientStructure client)
        {
            lock(AgencyVesselMap.TransactionGate) { foreach(var id in Requests.Where(p=>ReferenceEquals(p.Value.Client,client)).Select(p=>p.Key).ToArray()) Requests.Remove(id); Rejected.Remove(client); }
        }
        public static bool Couple(ClientStructure client,VesselCoupleMsgData data)
            => Couple(client, data, out _);

        public static bool Couple(ClientStructure client,VesselCoupleMsgData data,out bool newlyApplied)
        {
            newlyApplied = false;
            Request r=null; string error=null; bool completed=false; bool recovery=false;
            lock(AgencyVesselMap.TransactionGate)
            {
                try
                {
                    var receipt=AgencyVesselMap.GetReceipt(data.OperationId,client.UniqueIdentifier);
                    if(receipt!=null) {if(receipt.DominantId!=data.VesselId || receipt.WeakId!=data.CoupledVesselId) throw new InvalidOperationException("Operation was already used.");completed=true;}
                    else
                    {
                        if(data.OperationId==Guid.Empty || data.VesselId==data.CoupledVesselId || data.MergedVesselData.Length==0 || data.MergedVesselData.Length>VesselOwnershipPolicy.MaxMergedVesselBytes || !Actor(client)) throw new InvalidOperationException("Missing coupling operation or merged vessel.");
                        var source=LockSystem.LockQuery.ControlLockBelongsToPlayer(data.VesselId,client.PlayerName)?data.VesselId:data.CoupledVesselId;
                        var target=source==data.VesselId?data.CoupledVesselId:data.VesselId;
                        if(!LockSystem.LockQuery.ControlLockBelongsToPlayer(source,client.PlayerName) || !CanControl(client,source)) throw new InvalidOperationException("Control permission changed.");
                        var record=AgencyVesselMap.Get(target);
                        if(!Enum.IsDefined(typeof(LmpCommon.Enums.CoupleTrigger), data.Trigger)) throw new InvalidOperationException("Unknown coupling trigger.");
                        var generic=data.Trigger==(int)LmpCommon.Enums.CoupleTrigger.Kerbal || data.Trigger==(int)LmpCommon.Enums.CoupleTrigger.Other;
                        if(generic && !CanControl(client,target)) throw new InvalidOperationException("Both craft must permit control for boarding or construction.");
                        var implicitAllow=!Enabled || generic || record==null || record.OwnerAgencyId==Guid.Empty || record.OwnerAgencyId==client.AgencyId;
                        if(Enabled && (!implicitAllow || data.GrantId!=Guid.Empty))
                        {
                            r=Requests.Values.FirstOrDefault(x=>x.Grant==data.GrantId && ReferenceEquals(x.Client,client) && x.Source==source && x.Target==target);
                            if(r==null || r.Status!=DockConsentStatus.Granted || r.Expires<=UtcNow() || !Valid(r)) throw new InvalidOperationException("Docking grant expired or was revoked.");
                        }
                        var text=new UTF8Encoding(false,true).GetString(data.MergedVesselData);var merged=new global::Server.System.Vessel.Classes.Vessel(text);
                        if(!Guid.TryParse(merged.Fields.GetSingle("pid")?.Value,out var parsed) || parsed!=data.VesselId) throw new InvalidOperationException("Merged vessel identity mismatch.");
                        AgencyVesselMap.CommitCouple(data.OperationId,client.UniqueIdentifier,data.VesselId,data.CoupledVesselId,text,merged,data.PartFlightId,data.CoupledPartFlightId);
                        newlyApplied = true;
                        if(r!=null) r.Status=DockConsentStatus.Consumed; completed=true;
                    }
                }
                catch(Exception e) {recovery=AgencyVesselMap.HasPendingJournal || AgencyEconomyStore.Enabled && !AgencyEconomyStore.Ready;error=recovery?"Coupling committed; server recovery required.":e.Message;Rejected.Add(client);}
            }
            var status=r??new Request {Client=client,Agency=client.AgencyId,Source=data.VesselId,Target=data.CoupledVesselId,Grant=data.GrantId};
            status.Status=completed?DockConsentStatus.Completed:recovery?DockConsentStatus.RecoveryRequired:DockConsentStatus.Rejected;SendStatus(status,client,error??"",data.OperationId);
            if(!completed) {client.DisconnectClient=true;return false;}
            Changed();return true;
        }
    }
}
