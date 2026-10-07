using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] public class LaunchGrantCliTests
{
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-grant-launch-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ if(Directory.Exists(dir)) Directory.Delete(dir,true); }
 private (int Code,string Out,string Err) Run(params string[] args)
 {
  var output=new StringWriter(); var error=new StringWriter();
  int code=GrantCli.Run(args,output,error,_=>null,()=>new DateTime(2026,3,1,12,0,0,DateTimeKind.Utc));
  return (code,output.ToString(),error.ToString());
 }
 private string[] Issue(params string[] extra)=>new[]{"issue","--trust-dir",dir,"--ksp-root",@"C:\Games\KSP","--save","Sandbox"}.Concat(extra).ToArray();
 private GrantPayload Read()
 { var v=GrantCodec.Verify(File.ReadAllText(Path.Combine(dir,"grant.json")),File.ReadAllBytes(Path.Combine(dir,"grant.key"))); Assert.IsTrue(v.Ok,v.State); return v.Payload; }

 [TestMethod] public void ASpendLimitIsZeroUnlessTheHumanSetsOne()
 {
  Assert.AreEqual(0,Run(Issue()).Code); Assert.AreEqual(0L,Read().SpendLimitFunds);
  Assert.AreEqual(0,Run(Issue("--spend","25000")).Code); Assert.AreEqual(25000L,Read().SpendLimitFunds);
 }

 [TestMethod] public void TheLaunchFamilyExpandsToTheLaunchEffectAndStaysAlongsideOthers()
 {
  Assert.AreEqual(0,Run(Issue("--ops","editor.replace_craft,launch,launch","--spend","100")).Code);
  CollectionAssert.AreEqual(new[]{"editor.replace_craft","editor.launch"},Read().Operations);
  Assert.AreEqual(0,Run(Issue("--ops","editor.launch")).Code); CollectionAssert.AreEqual(new[]{"editor.launch"},Read().Operations);
 }

 [TestMethod] public void ALaunchIsNeverInTheDefaultOperations()
 {
  Assert.AreEqual(0,Run(Issue()).Code); CollectionAssert.DoesNotContain(Read().Operations,"editor.launch"); CollectionAssert.DoesNotContain(GrantCli.DefaultOperations,"editor.launch");
 }

 [DataTestMethod] [DataRow("-1")] [DataRow("abc")] [DataRow("12.5")] [DataRow("")] [DataRow("99999999999999")]
 public void ABadSpendLimitIsUsageAndWritesNothing(string spend)
 {
  var result=Run(Issue("--spend",spend));
  Assert.AreEqual(GrantCli.Usage,result.Code,result.Err); StringAssert.Contains(result.Err,"--spend"); Assert.IsFalse(File.Exists(Path.Combine(dir,"grant.json")));
 }

 [TestMethod] public void RearmAndRevokeKeepTheSpendLimitFixed()
 {
  Run(Issue("--spend","40000","--ops","launch"));
  Assert.AreEqual(0,Run("revoke","--trust-dir",dir).Code); Assert.AreEqual(40000L,Read().SpendLimitFunds);
  Assert.AreEqual(0,Run("rearm","--trust-dir",dir,"--hours","4").Code); Assert.AreEqual(40000L,Read().SpendLimitFunds);
 }

 [TestMethod] public void ShowReportsTheSpendLimit()
 {
  Run(Issue("--spend","1234")); var shown=JObject.Parse(Run("show","--trust-dir",dir).Out);
  Assert.AreEqual(1234L,(long?)shown["spendLimitFunds"]);
 }

 [TestMethod] public void TheSpendLimitIsPartOfTheSignedPayload()
 {
  Run(Issue("--spend","500")); var text=File.ReadAllText(Path.Combine(dir,"grant.json")); var key=File.ReadAllBytes(Path.Combine(dir,"grant.key"));
  var envelope=JObject.Parse(text); var payload=System.Text.Encoding.UTF8.GetString(Convert.FromBase64String((string)envelope["payload"]!));
  var tampered=payload.Replace("\"spendLimitFunds\":500","\"spendLimitFunds\":900000");
  Assert.AreNotEqual(payload,tampered);
  envelope["payload"]=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tampered));
  Assert.AreEqual(GrantStates.InvalidMac,GrantCodec.Verify(envelope.ToString(),key).State,"raising the cap by editing the file breaks the signature");
 }
}
[TestClass] public class LaunchJournalTests
{
 private string directory=null!; private readonly DateTimeOffset now=DateTimeOffset.UtcNow;
 [TestInitialize] public void Init()=>directory=Path.Combine(Path.GetTempPath(),"ksp-launch-journal-tests",Guid.NewGuid().ToString("N"));
 [TestCleanup] public void Cleanup(){ if(Directory.Exists(directory)) Directory.Delete(directory,true); }
 private MissionGrant Grant(string id="grant",long generation=1,decimal limit=100)=>new(id,generation,"bridge_reported",now.AddHours(1),limit,new[]{"editor.launch"},new[]{"editor:VAB"});
 private (ControlJournal journal,ControlLease lease) Setup(params MissionGrant[] grants)
 {
  var journal=new ControlJournal(directory); foreach(var g in grants.Length==0 ? new[]{ Grant() } : grants) journal.ProvisionGrant(g);
  return(journal,journal.Acquire("lease-1","tester","world",now,TimeSpan.FromMinutes(2)));
 }
 private Job Admit(ControlJournal j,ControlLease l,string id,decimal cost,string grant="grant",long generation=1)=>j.Admit(id,grant,generation,l.Id,"world","editor.launch","editor:VAB","{}",cost,now);

 [TestMethod] public void SettleRecordsTheChargeGrossAndReleasesTheRest()
 {
  var(j,l)=Setup(); using(j){
   Admit(j,l,"a",60); j.Begin("a",now); var done=j.Settle("a","completed","observed","{}",25);
   Assert.AreEqual("completed",done.Status); Assert.AreEqual((25m,0m),j.SpendFor("grant"));
   Admit(j,l,"b",75);
   Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"c",1),"25 spent plus 75 reserved fills the 100 cap");
  }
 }

 [TestMethod] public void ACancelledOrFailedSettleReleasesEverythingAndAnIndeterminateOneKeepsItsHold()
 {
  var(j,l)=Setup(); using(j){
   Admit(j,l,"a",60); j.Begin("a",now); j.Settle("a","cancelled","not_dispatched","{}",0); Assert.AreEqual((0m,0m),j.SpendFor("grant"));
   Admit(j,l,"b",60); j.Begin("b",now); j.Settle("b","failed","launch_rejected","{}",0); Assert.AreEqual((0m,0m),j.SpendFor("grant"));
   Admit(j,l,"c",60); j.Begin("c",now); j.Settle("c","indeterminate","disconnected","{}",0); Assert.AreEqual((0m,60m),j.SpendFor("grant"));
   Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"d",41));
  }
 }

 [TestMethod] public void AnIndeterminateSettleNeverRecordsACharge()
 {
  var(j,l)=Setup(); using(j){ Admit(j,l,"a",60); j.Begin("a",now); j.Settle("a","indeterminate","disconnected","{}",30); Assert.AreEqual((0m,60m),j.SpendFor("grant")); }
 }

 [TestMethod] public void SettleOnlyAppliesToAJobInFlightAndRejectsBadInput()
 {
  var(j,l)=Setup(); using(j){
   Admit(j,l,"a",60); j.Begin("a",now); j.Settle("a","completed","observed","{}",10);
   Assert.ThrowsException<InvalidOperationException>(()=>j.Settle("a","completed","observed","{}",10),"a job settles once");
   Admit(j,l,"b",10); Assert.ThrowsException<ArgumentException>(()=>j.Settle("b","completed","observed","{}",-1));
   Assert.ThrowsException<ArgumentException>(()=>j.Settle("b","running","observed","{}",0));
   Assert.AreEqual((10m,10m),j.SpendFor("grant"));
  }
 }

 [TestMethod] public void TheCapBelongsToTheGrantNotTheJournal()
 {
  var first=Grant("grant-a",1,100); var second=Grant("grant-b",1,100);
  var(j,l)=Setup(first,second); using(j){
   Admit(j,l,"a",100,"grant-a"); j.Begin("a",now); j.Settle("a","completed","observed","{}",100);
   Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"b",1,"grant-a"),"grant-a is spent");
   // The lease is bound to the grant that was admitted with it, so a second grant needs its own admission path; the cap arithmetic is what matters here.
   Assert.AreEqual((100m,0m),j.SpendFor("grant-a")); Assert.AreEqual((0m,0m),j.SpendFor("grant-b"));
  }
 }

 [TestMethod] public void ARearmedGrantKeepsWhatItAlreadySpent()
 {
  var(j,l)=Setup(); using(j){
   Admit(j,l,"a",70); j.Begin("a",now); j.Settle("a","completed","observed","{}",70);
   j.ProvisionGrant(Grant("grant",2,100));
   Assert.ThrowsException<InvalidOperationException>(()=>Admit(j,l,"b",31,generation:2),"the new generation keeps the id's gross spend");
   Admit(j,l,"c",30,generation:2);
  }
 }

 [TestMethod] public void SpendSurvivesARestartAndInFlightJobsBecomeIndeterminateAndStayHeld()
 {
  var(j,l)=Setup(); Admit(j,l,"a",40); j.Begin("a",now); j.Settle("a","completed","observed","{}",40); Admit(j,l,"b",50); j.Begin("b",now); j.Dispose();
  using var again=new ControlJournal(directory); again.ProvisionGrant(Grant());
  Assert.AreEqual("indeterminate",again.Get("b").Status); Assert.AreEqual((40m,50m),again.SpendFor("grant"));
 }

 [TestMethod] public void RebaseLeaseAdoptsTheNewEpochForTheSameLeaseOnly()
 {
  var(j,l)=Setup(); using(j){
   j.RebaseLease("lease-1","epoch-2"); Assert.AreEqual("epoch-2",j.CurrentLease!.WorldEpoch); Assert.AreEqual("lease-1",j.CurrentLease.Id);
   Assert.ThrowsException<InvalidOperationException>(()=>j.RebaseLease("other","epoch-3"));
   Assert.ThrowsException<ArgumentException>(()=>j.RebaseLease("lease-1"," "));
   j.Admit("x","grant",1,"lease-1","epoch-2","editor.launch","editor:VAB","{}",1,now);
  }
 }
}
