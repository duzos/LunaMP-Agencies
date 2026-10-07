using System.Text;
using KspControl.Contracts;
using KspControl.Contracts.Fixtures;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
[TestClass] public class GrantFixtureTests
{
 [TestMethod] public void ReEncodingFixedInputsReproducesTheCommittedFixtureByteForByte()
 {
  string again=GrantCodec.Encode(GrantFixture.Payload(),GrantFixture.Key);
  if(Environment.GetEnvironmentVariable(GrantFixture.RegenerateVariable)=="1")
  {
   string source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../Contracts/Fixtures/grant-fixture.json"));
   File.WriteAllText(source,again+"\n",new UTF8Encoding(false)); Assert.Inconclusive("fixture regenerated at "+source);
  }
  var committed=JObject.Parse(GrantFixture.EnvelopeText); var fresh=JObject.Parse(again);
  Assert.AreEqual((string?)committed["payload"],(string?)fresh["payload"],"payload bytes differ");
  Assert.AreEqual((string?)committed["mac"],(string?)fresh["mac"],"mac differs");
  Assert.AreEqual(Convert.ToBase64String(GrantCodec.SerializePayload(GrantFixture.Payload())),(string?)committed["payload"]);
 }
 [TestMethod] public void FixtureVerifiesOnNet10()
 { var v=GrantCodec.Verify(GrantFixture.EnvelopeText,GrantFixture.Key); Assert.IsTrue(v.Ok,v.State); Assert.AreEqual("fixture-grant-0001",v.Payload.GrantId); }
}
[TestClass] public class GrantCliTests
{
 private string dir=null!;
 [TestInitialize] public void Init()=>dir=Path.Combine(Path.GetTempPath(),"ksp-grant-tests",Guid.NewGuid().ToString("N"));
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

 [TestMethod] public void IssueCreatesKeyOnceAndAVerifiableGrant()
 {
  var first=Run(Issue()); Assert.AreEqual(0,first.Code,first.Err);
  byte[] key=File.ReadAllBytes(Path.Combine(dir,"grant.key")); Assert.AreEqual(32,key.Length);
  var p=Read();
  Assert.AreEqual(1,p.Generation); Assert.IsFalse(p.Revoked); Assert.AreEqual("refuse",p.UnsavedCraftPolicy); Assert.AreEqual(0,p.SpendLimitFunds);
  CollectionAssert.AreEqual(new[]{"VAB"},p.Facilities); CollectionAssert.AreEqual(GrantCli.DefaultOperations,p.Operations);
  Assert.AreEqual("offline:Sandbox",p.Binding.Agency); Assert.AreEqual("Sandbox",p.Binding.SaveFolder); Assert.AreEqual(GrantBindingKey.InstallId(@"C:\Games\KSP"),p.Binding.InstallId);
  Assert.AreEqual("2026-03-01T12:00:00.000Z",p.IssuedUtc); Assert.AreEqual("2026-03-01T20:00:00.000Z",p.ExpiresUtc);
  string firstId=p.GrantId;
  var second=Run(Issue("--hours","2")); Assert.AreEqual(0,second.Code,second.Err);
  CollectionAssert.AreEqual(key,File.ReadAllBytes(Path.Combine(dir,"grant.key")),"key is created once");
  p=Read(); Assert.AreEqual(2,p.Generation); Assert.AreNotEqual(firstId,p.GrantId); Assert.AreEqual("2026-03-01T14:00:00.000Z",p.ExpiresUtc);
 }
 [TestMethod] public void IssueBindsAgencyGuidWhenGiven()
 {
  var guid=Guid.NewGuid(); Assert.AreEqual(0,Run(Issue("--agency",guid.ToString("N"),"--policy","snapshot_then_replace","--max-parts","40")).Code);
  var p=Read(); Assert.AreEqual(guid.ToString("D"),p.Binding.Agency); Assert.AreEqual("snapshot_then_replace",p.UnsavedCraftPolicy); Assert.AreEqual(40,p.MaxParts);
 }
 [TestMethod] public void RevokeAndRearmEachWriteGenerationPlusOneWithTheSameId()
 {
  Run(Issue()); var issued=Read();
  Assert.AreEqual(0,Run("revoke","--trust-dir",dir).Code); var revoked=Read();
  Assert.AreEqual(2,revoked.Generation); Assert.IsTrue(revoked.Revoked); Assert.AreEqual(issued.GrantId,revoked.GrantId);
  Assert.AreEqual(0,Run("rearm","--trust-dir",dir,"--hours","3").Code); var rearmed=Read();
  Assert.AreEqual(3,rearmed.Generation); Assert.IsFalse(rearmed.Revoked); Assert.AreEqual(issued.GrantId,rearmed.GrantId); Assert.AreEqual("2026-03-01T15:00:00.000Z",rearmed.ExpiresUtc);
  Assert.AreEqual(issued.Binding.InstallId,rearmed.Binding.InstallId);
  Assert.AreEqual(0,Directory.GetFiles(dir,"*.tmp").Length);
 }
 [TestMethod] public void ShowNeverPrintsKeyPayloadOrMac()
 {
  Run(Issue()); var shown=Run("show","--trust-dir",dir); Assert.AreEqual(0,shown.Code);
  var envelope=JObject.Parse(File.ReadAllText(Path.Combine(dir,"grant.json")));
  string key=Convert.ToBase64String(File.ReadAllBytes(Path.Combine(dir,"grant.key")));
  foreach(var secret in new[]{key,(string)envelope["payload"]!,(string)envelope["mac"]!,Convert.ToHexString(File.ReadAllBytes(Path.Combine(dir,"grant.key")))})
   Assert.IsFalse(shown.Out.Contains(secret,StringComparison.OrdinalIgnoreCase),"show leaked secret material");
  var json=JObject.Parse(shown.Out); Assert.AreEqual("signature_valid",(string?)json["state"]); Assert.AreEqual(1,(int?)json["generation"]);
 }
 [TestMethod] public void ShowReportsMissingAndTampered()
 {
  Assert.AreEqual("missing",(string?)JObject.Parse(Run("show","--trust-dir",dir).Out)["state"]);
  Run(Issue()); string path=Path.Combine(dir,"grant.json"); var envelope=JObject.Parse(File.ReadAllText(path));
  var bytes=Convert.FromBase64String((string)envelope["payload"]!); bytes[5]^=1; envelope["payload"]=Convert.ToBase64String(bytes); File.WriteAllText(path,envelope.ToString());
  Assert.AreEqual("invalid_mac",(string?)JObject.Parse(Run("show","--trust-dir",dir).Out)["state"]);
 }
 [TestMethod] public void RevokeAndRearmRefuseWithoutAUsableGrantAndIssueStillWorksAfterTamper()
 {
  Assert.AreEqual(GrantCli.Failed,Run("revoke","--trust-dir",dir).Code); Assert.AreEqual(GrantCli.Failed,Run("rearm","--trust-dir",dir).Code);
  Run(Issue()); File.WriteAllText(Path.Combine(dir,"grant.json"),"{\"payload\":\"AAAA\",\"mac\":\"AAAA\"}");
  Assert.AreEqual(GrantCli.Failed,Run("revoke","--trust-dir",dir).Code);
  Assert.AreEqual(0,Run(Issue()).Code); Assert.IsTrue(Read().Generation>=1);
 }
 [TestMethod] public void IssueGenerationNeverGoesBackwards()
 {
  Run(Issue()); Run("revoke","--trust-dir",dir); Run("revoke","--trust-dir",dir); // generation 3
  File.Delete(Path.Combine(dir,"grant.key")); // a lost key must not let generation restart
  Assert.AreEqual(0,Run(Issue()).Code); Assert.AreEqual(4,Read().Generation);
 }
 [TestMethod] public void MissingSubcommandOrRequiredOptionsExitOne()
 {
  Assert.AreEqual(GrantCli.Usage,Run().Code); Assert.AreEqual(GrantCli.Usage,Run("frobnicate").Code);
  Assert.AreEqual(GrantCli.Usage,Run("issue","--trust-dir",dir).Code); Assert.AreEqual(GrantCli.Usage,Run("issue","--trust-dir",dir,"--save","S").Code);
  Assert.IsFalse(File.Exists(Path.Combine(dir,"grant.key")),"a refused issue creates no key");
 }
 [TestMethod] public void BadOptionsAreUsageErrors()
 {
  Assert.AreEqual(GrantCli.Usage,Run(Issue("--hours","0")).Code); Assert.AreEqual(GrantCli.Usage,Run(Issue("--hours","169")).Code);
  Assert.AreEqual(GrantCli.Usage,Run(Issue("--agency","not-a-guid")).Code); Assert.AreEqual(GrantCli.Usage,Run(Issue("--agency",Guid.Empty.ToString())).Code);
  Assert.AreEqual(GrantCli.Usage,Run(Issue("--policy","always")).Code); Assert.AreEqual(GrantCli.Usage,Run(Issue("--facilities","LAUNCHPAD")).Code);
  Assert.AreEqual(GrantCli.Usage,Run(Issue("--unknown","x")).Code); Assert.AreEqual(GrantCli.Usage,Run(Issue("--save","again")).Code);
  Assert.AreEqual(GrantCli.Usage,Run(Issue("--max-parts")).Code);
 }
 [TestMethod] public void TrustDirectoryCanComeFromTheEnvironmentAndExplicitPaths()
 {
  var output=new StringWriter(); var error=new StringWriter();
  int code=GrantCli.Run(new[]{"issue","--ksp-root",@"C:\Games\KSP","--save","Env"},output,error,name=>name=="KSP_CONTROL_TRUST_DIR"?dir:null);
  Assert.AreEqual(0,code,error.ToString()); Assert.IsTrue(File.Exists(Path.Combine(dir,"grant.json")));
  string custom=Path.Combine(dir,"elsewhere","g.json"),key=Path.Combine(dir,"elsewhere","k.bin");
  Assert.AreEqual(0,Run("issue","--grant-file",custom,"--key-file",key,"--ksp-root",@"C:\Games\KSP","--save","X").Code); Assert.IsTrue(File.Exists(custom)); Assert.AreEqual(32,File.ReadAllBytes(key).Length);
 }
}
