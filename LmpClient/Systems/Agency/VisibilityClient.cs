using System;
using System.Collections.Generic;
using System.Linq;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpClient.Systems.SettingsSys;

namespace LmpClient.Systems.Agency
{
    public static class VisibilityClient
    {
        private static readonly object gate = new object();
        private static Dictionary<Guid,VisibilityEndpoint> endpoints=new Dictionary<Guid,VisibilityEndpoint>();
        private static VisibilityAgencyGrant[] grants=Array.Empty<VisibilityAgencyGrant>();
        private static VisibilityCraftOverride[] overrides=Array.Empty<VisibilityCraftOverride>();
        private static readonly HashSet<Guid> sharedOwners=new HashSet<Guid>();
        private static readonly Dictionary<Guid,VisibilityOverride> localOverrides=new Dictionary<Guid,VisibilityOverride>();
        private static readonly Dictionary<Guid,DateTime> physicsUntil=new Dictionary<Guid,DateTime>();
        private static readonly Dictionary<Guid,DateTime> visibleUntil=new Dictionary<Guid,DateTime>();
        private static readonly Dictionary<Guid,SensorCache> sensors=new Dictionary<Guid,SensorCache>();
        private static bool ready, refreshPresentation, wasEnabled;
        private static long revision=-1;
        private static int targetCursor,sensorCursor;
        private static DateTime nextIndex,nextPresentation;
        private static VisibilitySensorIndex index=new VisibilitySensorIndex(Array.Empty<VisibilitySensor>());
        private sealed class SensorCache {internal double Power;internal DateTime Expires;}
        public static bool Enabled=>MainSystem.NetworkState>=ClientState.Handshaking && SettingsSystem.ServerSettings.AgencyHideCraft;
        public static bool Ready {get{lock(gate)return ready;}}
        public static string LatestStatus {get;private set;}
        public static string DiagnosticReason=>Harmony.AgencyVisibility.DiagnosticReason;
        public static IReadOnlyList<VisibilityEndpoint> GetEndpointsSnapshot(){lock(gate)return endpoints.Values.Select(e=>e.Copy()).ToArray();}
        public static IReadOnlyList<VisibilityAgencyGrant> GetAgencyGrantsSnapshot(){lock(gate)return grants.Select(g=>new VisibilityAgencyGrant{OwnerAgencyId=g.OwnerAgencyId,TargetAgencyId=g.TargetAgencyId}).ToArray();}
        public static IReadOnlyList<VisibilityCraftOverride> GetCraftOverridesSnapshot(){lock(gate)return overrides.Select(o=>new VisibilityCraftOverride{Source=o.Source.Copy(),TargetAgencyId=o.TargetAgencyId,Rule=o.Rule}).ToArray();}
        internal static void Apply(AgencyVisibilitySnapshotMsgData data)
        {
            lock(gate)
            {
                if(data.Revision<revision) return;
                try
                {
                    if(!data.Ready || data.Endpoints==null || data.AgencyGrants==null || data.CraftOverrides==null) throw new InvalidOperationException();
                    var next=data.Endpoints.ToDictionary(e=>e.VesselId,e=>e.Copy());
                    if(next.Count>10000 || next.Values.Any(e=>e.VesselId==Guid.Empty || e.OwnershipRevision<0) || data.AgencyGrants.Length>10000 || data.CraftOverrides.Length>32768) throw new InvalidOperationException();
                    if(data.CraftOverrides.Any(o=>o.Source==null || !Enum.IsDefined(typeof(VisibilityOverride),o.Rule))) throw new InvalidOperationException();
                    endpoints=next;grants=data.AgencyGrants.Select(g=>new VisibilityAgencyGrant{OwnerAgencyId=g.OwnerAgencyId,TargetAgencyId=g.TargetAgencyId}).ToArray();
                    overrides=data.CraftOverrides.Select(o=>new VisibilityCraftOverride{Source=o.Source.Copy(),TargetAgencyId=o.TargetAgencyId,Rule=o.Rule}).ToArray();
                    sharedOwners.Clear();localOverrides.Clear();
                    var mine=AgencySystem.Singleton.MyAgencyId;
                    foreach(var grant in grants) if(grant.TargetAgencyId==mine) sharedOwners.Add(grant.OwnerAgencyId);
                    foreach(var item in overrides)
                        if(item.TargetAgencyId==mine && endpoints.TryGetValue(item.Source.VesselId,out var current) && current.OwnerAgencyId==item.Source.OwnerAgencyId && current.OwnershipRevision==item.Source.OwnershipRevision)
                            localOverrides[item.Source.VesselId]=item.Rule;
                    ready=true;revision=data.Revision;
                }
                catch {ready=false;endpoints.Clear();grants=Array.Empty<VisibilityAgencyGrant>();overrides=Array.Empty<VisibilityCraftOverride>();sharedOwners.Clear();localOverrides.Clear();}
                visibleUntil.Clear();physicsUntil.Clear();sensors.Clear();nextIndex=default(DateTime);refreshPresentation=true;
            }
        }
        internal static void ApplyResult(AgencyVisibilityResultMsgData result){LatestStatus=result.Reason;}
        internal static void Invalidate(){lock(gate){ready=false;endpoints.Clear();visibleUntil.Clear();physicsUntil.Clear();sensors.Clear();index=new VisibilitySensorIndex(Array.Empty<VisibilitySensor>());nextIndex=default(DateTime);refreshPresentation=true;}}
        public static Guid SetAgencyShare(Guid target,bool enabled)=>Send(Guid.Empty,target,VisibilityOperation.SetAgencyShare,enabled,VisibilityOverride.Inherit);
        public static Guid SetVesselShare(Guid vessel,Guid target,VisibilityOverride rule)=>Send(vessel,target,VisibilityOperation.SetCraftOverride,false,rule);
        private static Guid Send(Guid vessel,Guid target,VisibilityOperation operation,bool enabled,VisibilityOverride rule)
        {
            if(!Enabled || !Ready){LatestStatus="Waiting for visibility settings.";return Guid.Empty;}
            var data=global::LmpClient.Network.NetworkMain.CliMsgFactory.CreateNewMessageData<AgencyVisibilityCommandMsgData>();
            data.RequestId=Guid.NewGuid();data.VesselId=vessel;data.TargetAgencyId=target;data.Operation=operation;data.Enabled=enabled;data.Rule=rule;
            lock(gate)data.ExpectedOwnershipRevision=endpoints.TryGetValue(vessel,out var endpoint)?endpoint.OwnershipRevision:0;
            global::LmpClient.Network.NetworkSender.QueueOutgoingMessage(global::LmpClient.Network.NetworkMain.CliMsgFactory.CreateNew<LmpCommon.Message.Client.AgencyCliMsg>(data));
            LatestStatus="Saving visibility settings...";return data.RequestId;
        }
        private static Guid Owner(Guid id)=>endpoints.TryGetValue(id,out var entry)?entry.OwnerAgencyId:AgencySystem.Singleton.GetVesselAgency(id);
        public static bool CanSee(Guid id)
        {
            if(!Enabled || id==Guid.Empty)return true;
            lock(gate)
            {
                var mine=AgencySystem.Singleton.MyAgencyId;var owner=Owner(id);
                return VisibilityPolicy.CanSee(mine,owner,ready,sharedOwners.Contains(owner),localOverrides.TryGetValue(id,out var rule)?rule:VisibilityOverride.Inherit,physicsUntil.TryGetValue(id,out var physicalUntil)&&physicalUntil>DateTime.UtcNow,
                    visibleUntil.TryGetValue(id,out var until)&&until>DateTime.UtcNow);
            }
        }
        public static bool CanSee(Vessel vessel)
        {
            if(!vessel || !Enabled)return true;
            if(CanSee(vessel.id))return true;
            var active=FlightGlobals.ActiveVessel;
            if(!active)return false;
            lock(gate)if(Owner(active.id)!=AgencySystem.Singleton.MyAgencyId || AgencySystem.Singleton.MyAgencyId==Guid.Empty)return false;
            var radius=Math.Max(PhysicsRadius(active),PhysicsRadius(vessel));
            return (active.GetWorldPos3D()-vessel.GetWorldPos3D()).sqrMagnitude<=radius*radius;
        }
        private static double PhysicsRadius(Vessel vessel)
        {
            var ranges=vessel.vesselRanges?.GetSituationRanges(vessel.situation);
            var radius=2500d;
            if(ranges!=null){if(!float.IsNaN(ranges.pack)&&!float.IsInfinity(ranges.pack))radius=Math.Max(radius,ranges.pack);if(!float.IsNaN(ranges.unpack)&&!float.IsInfinity(ranges.unpack))radius=Math.Max(radius,ranges.unpack);}
            return radius;
        }
        private static VisibilityPoint Point(Vessel vessel){var p=vessel.GetWorldPos3D();return new VisibilityPoint(p.x,p.y,p.z);}
        internal static void Tick()
        {
            var enabled=Enabled;
            if(!enabled)
            {
                if(wasEnabled){Clear();Harmony.AgencyVisibility.RestorePresentation();}
                wasEnabled=false;return;
            }
            wasEnabled=true;
            var all=FlightGlobals.Vessels;
            if(all==null || all.Count==0)return;
            var now=DateTime.UtcNow;
            lock(gate)
            {
                for(var count=0;count<Math.Min(32,all.Count);count++)
                {
                    if(sensorCursor>=all.Count)sensorCursor=0;
                    var vessel=all[sensorCursor++];
                    if(!vessel || Owner(vessel.id)!=AgencySystem.Singleton.MyAgencyId || AgencySystem.Singleton.MyAgencyId==Guid.Empty)continue;
                    if(!sensors.TryGetValue(vessel.id,out var cached)||cached.Expires<=now)
                        sensors[vessel.id]=new SensorCache{Power=VisibilitySensors.StrongestPower(vessel),Expires=now.AddSeconds(2)};
                }
                if(now>=nextIndex)
                {
                    nextIndex=now.AddMilliseconds(500);
                    var alive=new HashSet<Guid>(all.Where(v=>v).Select(v=>v.id));
                    foreach(var id in visibleUntil.Where(p=>p.Value<=now || !alive.Contains(p.Key)).Select(p=>p.Key).ToArray()){visibleUntil.Remove(id);refreshPresentation=true;}
                    foreach(var id in physicsUntil.Where(p=>p.Value<=now || !alive.Contains(p.Key)).Select(p=>p.Key).ToArray()){physicsUntil.Remove(id);refreshPresentation=true;}
                    foreach(var id in sensors.Keys.Where(id=>!alive.Contains(id)).ToArray())sensors.Remove(id);
                    var samples=new List<VisibilitySensor>();
                    foreach(var vessel in all)
                    {
                        if(!vessel || Owner(vessel.id)!=AgencySystem.Singleton.MyAgencyId || AgencySystem.Singleton.MyAgencyId==Guid.Empty)continue;
                        var floor=PhysicsRadius(vessel);var power=sensors.TryGetValue(vessel.id,out var cache)&&cache.Expires>now?cache.Power:0;
                        samples.Add(new VisibilitySensor{Position=Point(vessel),SensorRadius=Math.Max(power,floor),PhysicsRadius=floor});
                    }
                    index=new VisibilitySensorIndex(samples);
                }
                for(var count=0;count<Math.Min(128,all.Count);count++)
                {
                    if(targetCursor>=all.Count)targetCursor=0;
                    var vessel=all[targetCursor++];if(!vessel)continue;
                    var before=CanSee(vessel.id);var point=Point(vessel);
                    var inRange=index.InSensorRange(point);
                    if(index.InPhysicsRange(point,PhysicsRadius(vessel)))physicsUntil[vessel.id]=now.AddSeconds(1);else physicsUntil.Remove(vessel.id);
                    if(inRange)visibleUntil[vessel.id]=now.AddSeconds(1);else visibleUntil.Remove(vessel.id);
                    var after=CanSee(vessel.id);
                    if(before!=after)
                    {
                        refreshPresentation=true;
                        Diagnostics.PlaytestDiagnostics.Write("client.visibility.changed",()=> $"vessel={vessel.id} visible={after}");
                    }
                }
            }
            Harmony.AgencyVisibility.ClearHiddenSelection();
            if(refreshPresentation && now>=nextPresentation){refreshPresentation=false;nextPresentation=now.AddMilliseconds(250);Harmony.AgencyVisibility.RefreshPresentation();}
        }
        internal static void Clear()
        {
            lock(gate){endpoints.Clear();grants=Array.Empty<VisibilityAgencyGrant>();overrides=Array.Empty<VisibilityCraftOverride>();sharedOwners.Clear();localOverrides.Clear();visibleUntil.Clear();physicsUntil.Clear();sensors.Clear();ready=false;revision=-1;nextIndex=default(DateTime);index=new VisibilitySensorIndex(Array.Empty<VisibilitySensor>());refreshPresentation=true;}
            LatestStatus=null;
        }
    }
}
