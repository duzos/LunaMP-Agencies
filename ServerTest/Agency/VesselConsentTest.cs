using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Locks;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Interface;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ServerTest.Agency
{
 [TestClass,DoNotParallelize]
 public class VesselConsentTest
 {
  private static ClientStructure Client(int port,Guid agency,string name)
  {
   var connection=(NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));
   typeof(NetConnection).GetField("m_remoteEndPoint",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(connection,new IPEndPoint(IPAddress.Loopback,port));
   var client=(ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
   typeof(ClientStructure).GetField("<Connection>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(client,connection);
   typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(client,new ConcurrentQueue<IServerMessageBase>());
   client.AgencyId=agency;client.UniqueIdentifier=name;client.PlayerName=name;client.Authenticated=true;client.ConnectionStatus=ConnectionStatus.Connected;ServerContext.Clients[client.Endpoint]=client;
   var a=new Server.Agency.Agency {Id=agency,OwnerUniqueId=name};a.Members.Add(new Server.Agency.Agency.Member{UniqueId=name,DisplayName=name});AgencyStore.Agencies[agency]=a;return client;
  }
  [TestMethod]
  public void ConsentWrongResponderTimeoutRetryRevocationAndOfflinePolicy()
  {
   using(var scope=new AgencyTestScope())
   {
    var clients=ServerContext.Clients.ToArray();var vessels=VesselStoreSystem.CurrentVessels.ToArray();var oldClock=VesselOwnershipSystem.UtcNow;var oldRequestTimeout=GeneralSettings.SettingsStore.AgencyDockRequestTimeoutSeconds;var now=DateTime.UtcNow;
    ClientStructure a=null,b=null;
    try
    {
     ServerContext.Clients.Clear();GeneralSettings.SettingsStore.AgencyVesselOwnership=true;GeneralSettings.SettingsStore.AgencyDockRequestTimeoutSeconds=3;VesselOwnershipSystem.UtcNow=()=>now;
     a=Client(32111,Guid.NewGuid(),"Requester");b=Client(32112,Guid.NewGuid(),"Owner");var source=Guid.NewGuid();var target=Guid.NewGuid();
     VesselStoreSystem.CurrentVessels[source]=(Server.System.Vessel.Classes.Vessel)RuntimeHelpers.GetUninitializedObject(typeof(Server.System.Vessel.Classes.Vessel));VesselStoreSystem.CurrentVessels[target]=VesselStoreSystem.CurrentVessels[source];
     AgencyVesselMap.Set(source,a.AgencyId);AgencyVesselMap.Set(target,b.AgencyId);Assert.IsTrue(LockSystem.AcquireLock(new LockDefinition(LockType.Control,a.PlayerName,source),false,out _));
     var factory=new ClientMessageFactory();
     AgencyDockRequestMsgData Ask() {var q=factory.CreateNewMessageData<AgencyDockRequestMsgData>();q.RequestId=Guid.NewGuid();q.SourceVesselId=source;q.TargetVesselId=target;VesselOwnershipSystem.RequestDock(a,q);return q;}
     AgencyDockStatusMsgData Last(ClientStructure c)=>c.SendMessageQueue.Select(m=>m.Data).OfType<AgencyDockStatusMsgData>().Last();
     var first=Ask();Assert.AreEqual(DockConsentStatus.Pending,Last(a).Status);var reply=factory.CreateNewMessageData<AgencyDockResponseMsgData>();reply.RequestId=first.RequestId;reply.Accept=true;
     VesselOwnershipSystem.RespondDock(a,reply);Assert.AreEqual(DockConsentStatus.Pending,Last(a).Status);
     now=now.AddSeconds(4);VesselOwnershipSystem.SweepExpired();Assert.AreEqual(DockConsentStatus.Expired,Last(a).Status);Assert.AreEqual(DockConsentStatus.Expired,Last(b).Status);
     var second=Ask();Assert.AreNotEqual(first.RequestId,second.RequestId);Assert.AreEqual(second.RequestId,Last(b).RequestId);reply.RequestId=second.RequestId;VesselOwnershipSystem.RespondDock(b,reply);Assert.AreEqual(DockConsentStatus.Granted,Last(a).Status);Assert.AreNotEqual(Guid.Empty,Last(a).GrantId);
     AgencyVesselMap.Mutate(target,b.AgencyId,true,VesselOwnershipOperation.SetDockingPolicy,Guid.Empty,VesselDockingPolicy.Anyone);VesselOwnershipSystem.Changed();Assert.AreEqual(DockConsentStatus.Cancelled,Last(a).Status);Assert.AreEqual(DockConsentStatus.Cancelled,Last(b).Status);
     ServerContext.Clients.TryRemove(b.Endpoint,out _);Ask();Assert.AreEqual(DockConsentStatus.Granted,Last(a).Status);
     var child=Guid.NewGuid();var proto="pid = "+source.ToString("N")+"\nroot = 0\nPART\n{\nuid = 101\nname = probe\n}\n"+string.Concat(new[]{"ORBIT","ACTIONGROUPS","DISCOVERY","FLIGHTPLAN","CTRLSTATE","VESSELMODULES"}.Select(x=>x+"\n{\n}\n"));
     VesselStoreSystem.CurrentVessels[source]=new Server.System.Vessel.Classes.Vessel(proto);
     Assert.IsTrue(AgencyVesselMap.RestoreSplit(source,child,0,101));Assert.IsTrue(VesselOwnershipSystem.CanControl(a,source));Assert.IsFalse(VesselOwnershipSystem.CanControl(a,child));
     Assert.IsFalse(LockSystem.AcquireLock(new LockDefinition(LockType.Control,a.PlayerName,child),true,out _));
     Assert.IsTrue(AgencyVesselMap.ResolveSplit(child,new uint[]{101}));VesselOwnershipSystem.Changed();Assert.IsTrue(VesselOwnershipSystem.CanControl(a,child));
    }
    finally
    {
     if(a!=null){VesselOwnershipSystem.Disconnect(a);foreach(var l in LockSystem.LockQuery.GetAllPlayerLocks(a.PlayerName).ToArray())LockSystem.ReleaseLock(l);}if(b!=null)VesselOwnershipSystem.Disconnect(b);
     ServerContext.Clients.Clear();foreach(var p in clients)ServerContext.Clients[p.Key]=p.Value;VesselStoreSystem.CurrentVessels.Clear();foreach(var p in vessels)VesselStoreSystem.CurrentVessels[p.Key]=p.Value;VesselOwnershipSystem.UtcNow=oldClock;GeneralSettings.SettingsStore.AgencyDockRequestTimeoutSeconds=oldRequestTimeout;
    }
   }
  }
 }
}
