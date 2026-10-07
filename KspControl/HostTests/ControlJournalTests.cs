using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.HostTests;
[TestClass] public class ControlJournalTests
{
 private string directory=null!; private readonly DateTimeOffset now=DateTimeOffset.UtcNow;
 [TestInitialize] public void Init()=>directory=Path.Combine(Path.GetTempPath(),"ksp-control-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ if(Directory.Exists(directory)) Directory.Delete(directory,true); }
 private MissionGrant Grant(long generation=1,string id="grant")=>new(id,generation,"bridge_reported",now.AddHours(1),100,new[]{"launch"},new[]{"test-craft"});
 private (ControlJournal journal,ControlLease lease) Setup()
 {
  var journal=new ControlJournal(directory); journal.ProvisionGrant(Grant());
  return(journal,journal.Acquire("bridge-lease-1","tester","world",now,TimeSpan.FromMinutes(2)));
 }
 private Job Admit(ControlJournal j,ControlLease l,string id="req",decimal cost=60,long generation=1)=>j.Admit(id,"grant",generation,l.Id,"world","launch","test-craft","{}",cost,now);
 [TestMethod] public void DuplicateDoesNotReserveTwiceAndConflictsFail()
 { var(j,l)=Setup(); using(j){ Assert.AreEqual(Admit(j,l),Admit(j,l)); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,cost:50)); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"two")); } }
 [TestMethod] public void JournalMirrorsTheBridgeLeaseIdInsteadOfInventingOne()
 { var(j,l)=Setup(); using(j){ Assert.AreEqual("bridge-lease-1",l.Id); Assert.AreEqual("world",l.WorldEpoch); Assert.AreEqual(l,j.CurrentLease); } }
 [TestMethod] public void WorldEpochLivesOnTheLeaseNotTheGrant()
 { var(j,l)=Setup(); using(j){ Assert.AreEqual("world",j.CurrentLease!.WorldEpoch); Assert.ThrowsException<InvalidOperationException>(()=>j.Admit("x","grant",1,l.Id,"other-epoch","launch","test-craft","{}",1,now)); j.Revoke(); var next=j.Acquire("bridge-lease-2","tester","other-epoch",now,TimeSpan.FromMinutes(1)); j.Admit("y","grant",1,next.Id,"other-epoch","launch","test-craft","{}",1,now); } }
 [TestMethod] public void RevocationBetweenAdmissionAndDispatchDenies()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.Revoke(); Assert.ThrowsException<InvalidOperationException>(()=>j.Begin("req",now)); Assert.AreEqual("accepted",j.Get("req").Status); } }
 [TestMethod] public void GrantRevocationDeniesDispatch()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.RevokeGrant("grant"); Assert.ThrowsException<InvalidOperationException>(()=>j.Begin("req",now)); } }
 [TestMethod] public void IdAndGenerationAreUniqueAndARevokedPairIsNeverRevived()
 {
  var(j,l)=Setup(); using(j){
   Assert.ThrowsException<InvalidOperationException>(()=>j.ProvisionGrant(Grant()),"same pair twice");
   j.RevokeGrant("grant"); Assert.ThrowsException<InvalidOperationException>(()=>j.ProvisionGrant(Grant()),"revoked pair");
   j.ProvisionGrant(Grant(2)); Admit(j,l,"gen2",generation:2);
   Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"stale",generation:1),"an older generation no longer authorises");
  }
  using var restarted=new ControlJournal(directory); Assert.ThrowsException<InvalidOperationException>(()=>restarted.ProvisionGrant(Grant()),"revocation survives restart");
 }
 [TestMethod] public void AGrantPairRecordedByAnEarlierRunCanBeReestablishedFromTheBridge()
 {
  var(j,_)=Setup(); j.Dispose();
  using var restarted=new ControlJournal(directory); restarted.ProvisionGrant(Grant());
  var lease=restarted.Acquire("bridge-lease-9","tester","world",now,TimeSpan.FromMinutes(1)); Admit(restarted,lease);
 }
 [TestMethod] public void MissingBindingOrGenerationIsRejected()
 {
  using var j=new ControlJournal(directory);
  Assert.ThrowsException<ArgumentException>(()=>j.ProvisionGrant(Grant(0))); Assert.ThrowsException<ArgumentException>(()=>j.ProvisionGrant(Grant() with { Binding="" }));
 }
 [TestMethod] public void RestartNeverReplaysAndRetainsReservation()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.Begin("req",now); } using var restarted=new ControlJournal(directory); Assert.AreEqual("indeterminate",restarted.Get("req").Status); Assert.ThrowsException<InvalidOperationException>(()=>restarted.Begin("req",now)); }
 [TestMethod] public void CompletedSpendPersistsAndCannotBeRefundedByCancellation()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.Begin("req",now); j.Complete("req",55); Assert.ThrowsException<InvalidOperationException>(()=>j.CancelBeforeDispatch("req")); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"two",46)); Admit(j,l,"three",45); } }
 [TestMethod] public void SecondControllerAndWrongWorldDenied()
 { var(j,l)=Setup(); using(j){ Assert.ThrowsException<InvalidOperationException>(()=>j.Acquire("other-lease","other","world",now,TimeSpan.FromMinutes(1))); Assert.ThrowsException<InvalidOperationException>(()=>j.Admit("x","grant",1,l.Id,"other","launch","test-craft","{}",1,now)); } }
 [TestMethod] public void ExpiredLeaseAndUnlistedEntityDenied()
 { var(j,l)=Setup(); using(j){ Assert.ThrowsException<InvalidOperationException>(()=>j.Admit("x","grant",1,l.Id,"world","launch","foreign","{}",1,now)); Admit(j,l); Assert.ThrowsException<InvalidOperationException>(()=>j.Begin("req",now.AddMinutes(3))); } }
 [TestMethod] public void RenewExtendsOnlyTheNamedLiveLease()
 {
  var(j,l)=Setup(); using(j){
   var renewed=j.Renew(l.Id,now.AddMinutes(1),TimeSpan.FromMinutes(3)); Assert.AreEqual(now.AddMinutes(4),renewed.ExpiresAt);
   Assert.ThrowsException<InvalidOperationException>(()=>j.Renew("other",now,TimeSpan.FromMinutes(1))); Assert.ThrowsException<InvalidOperationException>(()=>j.Renew(l.Id,now.AddMinutes(10),TimeSpan.FromMinutes(1)));
   Assert.ThrowsException<ArgumentException>(()=>j.Renew(l.Id,now,TimeSpan.FromMinutes(6)));
  }
 }
 [TestMethod] public void ExclusiveJournalPreventsSecondHost()
 { var(j,l)=Setup(); using(j){ Assert.ThrowsException<IOException>(()=>new ControlJournal(directory)); } }
 [TestMethod] public void CancellationReleasesOnlyUndispatchedReservation()
 { var(j,l)=Setup(); using(j){ Admit(j,l); j.CancelBeforeDispatch("req"); Admit(j,l,"second",100); j.Begin("second",now); j.MarkIndeterminate("second","socket_lost"); Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"third",1)); } }
 [TestMethod] public void JobsRecordTheGrantGenerationAndFingerprintIncludesIt()
 {
  var(j,l)=Setup(); using(j){ var one=Admit(j,l,"a",1,1); Assert.AreEqual(1,one.GrantGeneration); j.ProvisionGrant(Grant(2)); var two=Admit(j,l,"b",1,2); Assert.AreEqual(2,two.GrantGeneration); Assert.AreNotEqual(one.Fingerprint,two.Fingerprint); }
 }
}
