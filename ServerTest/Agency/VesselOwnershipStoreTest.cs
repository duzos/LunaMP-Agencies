using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.System;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class VesselOwnershipStoreTest
    {
        private static string Proto(Guid id,int root,params uint[] parts)
            => "pid = "+id.ToString("N")+"\nname = Test\nroot = "+root+"\n"+string.Concat(parts.Select(p=>"PART\n{\nuid = "+p+"\nname = probe\n}\n"))+"ORBIT\n{\n}\nACTIONGROUPS\n{\n}\nDISCOVERY\n{\n}\nFLIGHTPLAN\n{\n}\nCTRLSTATE\n{\n}\nVESSELMODULES\n{\n}\n";

        [TestMethod]
        public void LegacyMigrationKeepsOwnerAndCorruptJsonNeverFallsBack()
        {
            using(var scope=new AgencyTestScope())
            {
                var id=Guid.NewGuid();var owner=Guid.NewGuid();Directory.CreateDirectory(Path.GetDirectoryName(AgencyVesselMap.MapFilePath));
                File.WriteAllText(AgencyVesselMap.MapFilePath,id.ToString("N")+" = "+owner.ToString("N"));AgencyVesselMap.Load();
                Assert.AreEqual(owner,AgencyVesselMap.Get(id).OwnerAgencyId);Assert.IsTrue(File.Exists(AgencyVesselMap.OwnershipFilePath));
                Assert.IsNull(AgencyVesselMap.Get(Guid.NewGuid()));
                File.WriteAllText(AgencyVesselMap.OwnershipFilePath,"{broken");AgencyVesselMap.Load();Assert.IsFalse(AgencyVesselMap.Ready);
                Assert.IsFalse(AgencyVesselMap.Mutate(id,owner,true,VesselOwnershipOperation.Claim,Guid.Empty,VesselDockingPolicy.Nobody).Success);
                Assert.AreEqual("{broken",File.ReadAllText(AgencyVesselMap.OwnershipFilePath));
            }
        }
        [TestMethod]
        public async Task ClaimIsAtomicAndPersistenceFailurePublishesNothing()
        {
            using(var scope=new AgencyTestScope())
            {
                var id=Guid.NewGuid();var a=Guid.NewGuid();var b=Guid.NewGuid();
                var outcomes=await Task.WhenAll(Task.Run(()=>AgencyVesselMap.Mutate(id,a,false,VesselOwnershipOperation.Claim,Guid.Empty,VesselDockingPolicy.Nobody)),Task.Run(()=>AgencyVesselMap.Mutate(id,b,false,VesselOwnershipOperation.Claim,Guid.Empty,VesselDockingPolicy.Nobody)));
                Assert.AreEqual(1,outcomes.Count(x=>x.Success));var before=AgencyVesselMap.GetOwnershipSnapshot();var original=File.ReadAllText(AgencyVesselMap.OwnershipFilePath);
                try {AgencyVesselMap.PersistenceCheckpoint=stage=>throw new IOException("injected");Assert.IsFalse(AgencyVesselMap.Mutate(id,AgencyVesselMap.Get(id).OwnerAgencyId,true,VesselOwnershipOperation.Transfer,Guid.NewGuid(),VesselDockingPolicy.Nobody).Success);}
                finally {AgencyVesselMap.PersistenceCheckpoint=null;}
                Assert.AreEqual(before.Revision,AgencyVesselMap.GetOwnershipSnapshot().Revision);Assert.AreEqual(original,File.ReadAllText(AgencyVesselMap.OwnershipFilePath));
            }
        }
        [DataTestMethod]
        [DataRow("journal-committed")]
        [DataRow("survivor-written")]
        [DataRow("weak-deleted")]
        [DataRow("before-journal-clear")]
        public void CouplingRecoversEveryPostCommitFailureAndRestoresNewGuidConstituent(string failure)
        {
            using(var scope=new AgencyTestScope())
            {
                var oldPath=VesselStoreSystem.VesselsPath;var saved=VesselStoreSystem.CurrentVessels.ToArray();
                try
                {
                    VesselStoreSystem.CurrentVessels.Clear();VesselStoreSystem.VesselsPath=Path.Combine(scope.Root,"Universe","Vessels");Directory.CreateDirectory(VesselStoreSystem.VesselsPath);
                    var a=Guid.NewGuid();var b=Guid.NewGuid();var ownerA=Guid.NewGuid();var ownerB=Guid.NewGuid();var op=Guid.NewGuid();
                    var aa=Proto(a,0,11);var bb=Proto(b,1,21,22);var merged=Proto(a,0,11,21,22);
                    VesselStoreSystem.CurrentVessels[a]=new Server.System.Vessel.Classes.Vessel(aa);VesselStoreSystem.CurrentVessels[b]=new Server.System.Vessel.Classes.Vessel(bb);
                    File.WriteAllText(Path.Combine(VesselStoreSystem.VesselsPath,a+".txt"),aa);File.WriteAllText(Path.Combine(VesselStoreSystem.VesselsPath,b+".txt"),bb);
                    AgencyVesselMap.Set(a,ownerA);AgencyVesselMap.Set(b,ownerB);
                    AgencyVesselMap.PersistenceCheckpoint=stage=>{if(stage==failure) throw new IOException("injected "+stage);};
                    Assert.ThrowsException<IOException>(()=>AgencyVesselMap.CommitCouple(op,"pilot",a,b,merged,new Server.System.Vessel.Classes.Vessel(merged),11,21));
                    Assert.IsTrue(AgencyVesselMap.HasPendingJournal);AgencyVesselMap.PersistenceCheckpoint=null;
                    AgencyVesselMap.Load();AgencyVesselMap.RecoverJournal();Assert.IsTrue(AgencyVesselMap.Ready);Assert.IsFalse(File.Exists(Path.Combine(VesselStoreSystem.VesselsPath,b+".txt")));Assert.IsFalse(VesselStoreSystem.VesselExists(b));
                    CollectionAssert.AreEquivalent(new uint[]{11,21,22},AgencyVesselMap.PartIds(VesselStoreSystem.CurrentVessels[a]));Assert.IsNotNull(AgencyVesselMap.GetReceipt(op,"pilot"));
                    Assert.IsFalse(AgencyVesselMap.Mutate(a,ownerA,true,VesselOwnershipOperation.Transfer,Guid.NewGuid(),VesselDockingPolicy.Nobody).Success);
                    var split=Guid.NewGuid();Assert.IsTrue(AgencyVesselMap.RestoreSplit(a,split,22,21));Assert.IsTrue(AgencyVesselMap.ResolveSplit(split,new uint[]{21,22}));Assert.AreEqual(ownerB,AgencyVesselMap.Get(split).OwnerAgencyId);
                    AgencyVesselMap.Load();Assert.AreEqual(ownerB,AgencyVesselMap.Get(split).OwnerAgencyId);
                }
                finally {AgencyVesselMap.PersistenceCheckpoint=null;VesselStoreSystem.VesselsPath=oldPath;VesselStoreSystem.CurrentVessels.Clear();foreach(var p in saved)VesselStoreSystem.CurrentVessels[p.Key]=p.Value;}
            }
        }
        [TestMethod]
        public async Task QueuedDiskWriterCannotRestorePreCouplingProto()
        {
            using(var scope=new AgencyTestScope())
            using(var entered=new global::System.Threading.ManualResetEventSlim())
            using(var proceed=new global::System.Threading.ManualResetEventSlim())
            {
                var oldPath=VesselStoreSystem.VesselsPath;var saved=VesselStoreSystem.CurrentVessels.ToArray();Task writer=null;
                try
                {
                    VesselStoreSystem.CurrentVessels.Clear();VesselStoreSystem.VesselsPath=Path.Combine(scope.Root,"Vessels");
                    var a=Guid.NewGuid();var b=Guid.NewGuid();var merged=Proto(a,0,1,2);
                    VesselStoreSystem.CurrentVessels[a]=new Server.System.Vessel.Classes.Vessel(Proto(a,0,1));VesselStoreSystem.CurrentVessels[b]=new Server.System.Vessel.Classes.Vessel(Proto(b,0,2));
                    AgencyVesselMap.PersistenceCheckpoint=stage=>{if(stage=="before-vessel-persist") {entered.Set();if(!proceed.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();}};
                    writer=Task.Run(()=>{VesselStoreSystem.PersistVesselToFile(a);VesselStoreSystem.PersistVesselToFile(b);});Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
                    AgencyVesselMap.CommitCouple(Guid.NewGuid(),"writer",a,b,merged,new Server.System.Vessel.Classes.Vessel(merged),1,2);
                    proceed.Set();await writer;
                    CollectionAssert.AreEquivalent(new uint[]{1,2},AgencyVesselMap.PartIds(new Server.System.Vessel.Classes.Vessel(File.ReadAllText(Path.Combine(VesselStoreSystem.VesselsPath,a+".txt")))));
                    Assert.IsFalse(File.Exists(Path.Combine(VesselStoreSystem.VesselsPath,b+".txt")));
                }
                finally {proceed.Set();if(writer!=null)await writer;AgencyVesselMap.PersistenceCheckpoint=null;VesselStoreSystem.VesselsPath=oldPath;VesselStoreSystem.CurrentVessels.Clear();foreach(var p in saved)VesselStoreSystem.CurrentVessels[p.Key]=p.Value;}
            }
        }

        [TestMethod]
        public void TransferClearsCoownersAndDoesNotExposeMutableSnapshots()
        {
            using(var scope=new AgencyTestScope())
            {
                var id=Guid.NewGuid();var owner=Guid.NewGuid();var co=Guid.NewGuid();var next=Guid.NewGuid();AgencyVesselMap.Set(id,owner);
                Assert.IsTrue(AgencyVesselMap.Mutate(id,owner,true,VesselOwnershipOperation.AddCoOwner,co,0).Success);
                var copy=AgencyVesselMap.Get(id);copy.CoOwnerAgencyIds[0]=next;Assert.AreEqual(co,AgencyVesselMap.Get(id).CoOwnerAgencyIds[0]);
                Assert.IsTrue(AgencyVesselMap.Mutate(id,owner,true,VesselOwnershipOperation.SetDockingPolicy,Guid.Empty,VesselDockingPolicy.Anyone).Success);
                Assert.IsTrue(AgencyVesselMap.Mutate(id,owner,true,VesselOwnershipOperation.Transfer,next,0).Success);
                Assert.AreEqual(next,AgencyVesselMap.Get(id).OwnerAgencyId);Assert.AreEqual(0,AgencyVesselMap.Get(id).CoOwnerAgencyIds.Length);Assert.AreEqual(VesselDockingPolicy.Nobody,AgencyVesselMap.Get(id).DockingPolicy);
            }
        }
    }
}
