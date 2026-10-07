using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.HostTests;
[TestClass] public class ControlJournalTests
{
 private string directory=null!; private readonly DateTimeOffset now=DateTimeOffset.UtcNow;
 [TestInitialize] public void Init()=>directory=Path.Combine(Path.GetTempPath(),"ksp-control-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ if(Directory.Exists(directory)) Directory.Delete(directory,true); }
 private (ControlJournal journal,ControlLease lease) Setup()
 {
  var journal=new ControlJournal(directory); journal.ProvisionGrant(new("grant","world",now.AddHours(1),100,new[]{"launch"},new[]{"test-craft"}));
  return(journal,journal.Acquire("tester","world",now,TimeSpan.FromMinutes(2)));
 }
 private Job Admit(ControlJournal j,ControlLease l,string id="req",decimal cost=60)=>j.Admit(id,"grant",l.Id,"world","launch","test-craft","{}",cost,now);
 [TestMethod] public void DuplicateDoesNotReserveTwiceAndConflictsFail()
 { var(j,l)=Setup(); using(j){ Assert.AreEqual(Admit(j,l),Admit(j,l)); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,cost:50)); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"two")); } }
 [TestMethod] public void RevocationBetweenAdmissionAndDispatchDenies()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.Revoke(); Assert.ThrowsException<InvalidOperationException>(()=>j.Begin("req",now)); Assert.AreEqual("accepted",j.Get("req").Status); } }
 [TestMethod] public void GrantRevocationDeniesDispatch()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.RevokeGrant("grant"); Assert.ThrowsException<InvalidOperationException>(()=>j.Begin("req",now)); } }
 [TestMethod] public void RevokedGrantIdCannotBeReissued()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.RevokeGrant("grant"); Assert.ThrowsException<InvalidOperationException>(()=>j.ProvisionGrant(new("grant","world",now.AddHours(1),100,new[]{"launch"},new[]{"test-craft"}))); Assert.ThrowsException<InvalidOperationException>(()=>j.Begin("req",now)); } using var restarted=new ControlJournal(directory); Assert.ThrowsException<InvalidOperationException>(()=>restarted.ProvisionGrant(new("grant","world",now.AddHours(1),100,new[]{"launch"},new[]{"test-craft"}))); }
 [TestMethod] public void RestartNeverReplaysAndRetainsReservation()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.Begin("req",now); } using var restarted=new ControlJournal(directory); Assert.AreEqual("indeterminate",restarted.Get("req").Status); Assert.ThrowsException<InvalidOperationException>(()=>restarted.Begin("req",now)); }
 [TestMethod] public void CompletedSpendPersistsAndCannotBeRefundedByCancellation()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.Begin("req",now); j.Complete("req",55); Assert.ThrowsException<InvalidOperationException>(()=>j.CancelBeforeDispatch("req")); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"two",46)); Admit(j,l,"three",45); } }
 [TestMethod] public void SecondControllerAndWrongWorldDenied()
 { var(j,l)=Setup(); using(j){ Assert.ThrowsException<InvalidOperationException>(()=>j.Acquire("other","world",now,TimeSpan.FromMinutes(1))); Assert.ThrowsException<InvalidOperationException>(()=>j.Admit("x","grant",l.Id,"other","launch","test-craft","{}",1,now)); } }
 [TestMethod] public void ExpiredLeaseAndUnlistedEntityDenied()
 { var(j,l)=Setup(); using(j){ Assert.ThrowsException<InvalidOperationException>(()=>j.Admit("x","grant",l.Id,"world","launch","foreign","{}",1,now)); Admit(j,l); Assert.ThrowsException<InvalidOperationException>(()=>j.Begin("req",now.AddMinutes(3))); } }
 [TestMethod] public void ExclusiveJournalPreventsSecondHost()
 { var(j,l)=Setup(); using(j){ Assert.ThrowsException<IOException>(()=>new ControlJournal(directory)); } }
 [TestMethod] public void CancellationReleasesOnlyUndispatchedReservation()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.CancelBeforeDispatch("req"); Admit(j,l,"second",100); j.Begin("second",now); j.MarkIndeterminate("second","socket_lost"); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"third",1)); } }
}

