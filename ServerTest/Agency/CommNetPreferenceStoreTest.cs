using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ServerTest.Agency
{
 [TestClass,DoNotParallelize]
 public class CommNetPreferenceStoreTest
 {
  private static ClientStructure AddClient(Guid agency,int port)
  {
   var connection=(NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));typeof(NetConnection).GetField("m_remoteEndPoint",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(connection,new IPEndPoint(IPAddress.Loopback,port));
   var client=(ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));typeof(ClientStructure).GetField("<Connection>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(client,connection);
   client.AgencyId=agency;client.UniqueIdentifier="pilot"+port;client.PlayerName=client.UniqueIdentifier;client.Authenticated=true;client.ConnectionStatus=ConnectionStatus.Connected;ServerContext.Clients[client.Endpoint]=client;
   var a=new Server.Agency.Agency {Id=agency,OwnerUniqueId=client.UniqueIdentifier};a.Members.Add(new Server.Agency.Agency.Member{UniqueId=client.UniqueIdentifier,DisplayName=client.PlayerName});AgencyStore.Agencies[agency]=a;return client;
  }
  [TestMethod]
  public void F3OffIndependentStampsPersistAndTargetTransferReturnCannotReviveConsent()
  {
   using(var scope=new AgencyTestScope())
   {
    var saved=ServerContext.Clients.ToArray();var vessels=VesselStoreSystem.CurrentVessels.ToArray();
    try
    {
     ServerContext.Clients.Clear();GeneralSettings.SettingsStore.AgencyCommNetOptIn=true;GeneralSettings.SettingsStore.AgencyCommNetPerAgency=true;Assert.IsFalse(GeneralSettings.SettingsStore.AgencyVesselOwnership);
     var a=AddClient(Guid.NewGuid(),31001);var b=AddClient(Guid.NewGuid(),31002);var va=Guid.NewGuid();var vb=Guid.NewGuid();
     var placeholder=(Server.System.Vessel.Classes.Vessel)RuntimeHelpers.GetUninitializedObject(typeof(Server.System.Vessel.Classes.Vessel));VesselStoreSystem.CurrentVessels[va]=placeholder;VesselStoreSystem.CurrentVessels[vb]=placeholder;
     AgencyVesselMap.Set(va,a.AgencyId);AgencyVesselMap.Set(vb,b.AgencyId);AgencyCommNetStore.Load();
     Assert.IsFalse(AgencyCommNetStore.Mutate(a,vb,va,CommNetOperation.SetAcceptAll,true).Success);
     Assert.IsTrue(AgencyCommNetStore.Mutate(a,va,vb,CommNetOperation.SetTarget,true).Success);
     Assert.IsTrue(AgencyCommNetStore.Mutate(b,vb,Guid.Empty,CommNetOperation.SetAcceptAll,true).Success);
     var snapshot=AgencyCommNetStore.GetSnapshot();Assert.IsTrue(snapshot.Ready);Assert.AreEqual(2,snapshot.Endpoints.Length);
     Assert.IsTrue(CommNetOptInPolicy.CanLink(true,true,false,false,snapshot.Endpoints.Single(x=>x.VesselId==va),snapshot.Endpoints.Single(x=>x.VesselId==vb),snapshot.Preferences));
     AgencyCommNetStore.Load();Assert.AreEqual(2,AgencyCommNetStore.GetSnapshot().Preferences.Length);
     Assert.IsTrue(AgencyVesselMap.Mutate(vb,b.AgencyId,true,VesselOwnershipOperation.Transfer,a.AgencyId,0).Success);
     Assert.IsTrue(AgencyVesselMap.Mutate(vb,a.AgencyId,true,VesselOwnershipOperation.Transfer,b.AgencyId,0).Success);
     AgencyCommNetStore.Load();snapshot=AgencyCommNetStore.GetSnapshot();Assert.AreEqual(0,snapshot.Preferences.Single(p=>p.Source.VesselId==va).Targets.Length);Assert.IsFalse(snapshot.Preferences.Any(p=>p.Source.VesselId==vb));
     Assert.IsFalse(CommNetOptInPolicy.CanLink(true,true,false,false,snapshot.Endpoints.Single(x=>x.VesselId==va),snapshot.Endpoints.Single(x=>x.VesselId==vb),snapshot.Preferences));
     // Failed write leaves the authoritative document and revision unchanged.
     var before=AgencyCommNetStore.GetSnapshot().Revision;File.Delete(AgencyCommNetStore.FilePath);Directory.CreateDirectory(AgencyCommNetStore.FilePath);
     Assert.IsFalse(AgencyCommNetStore.Mutate(a,va,Guid.Empty,CommNetOperation.SetAcceptAll,true).Success);Assert.AreEqual(before,AgencyCommNetStore.GetSnapshot().Revision);
     Directory.Delete(AgencyCommNetStore.FilePath);File.WriteAllText(AgencyCommNetStore.FilePath,"{broken");AgencyCommNetStore.Load();Assert.IsFalse(AgencyCommNetStore.GetSnapshot().Ready);Assert.IsFalse(AgencyCommNetStore.Mutate(a,va,Guid.Empty,CommNetOperation.SetAcceptAll,true).Success);Assert.AreEqual("{broken",File.ReadAllText(AgencyCommNetStore.FilePath));
    }
    finally{ServerContext.Clients.Clear();foreach(var p in saved)ServerContext.Clients[p.Key]=p.Value;VesselStoreSystem.CurrentVessels.Clear();foreach(var p in vessels)VesselStoreSystem.CurrentVessels[p.Key]=p.Value;}
   }
  }
 }
}
