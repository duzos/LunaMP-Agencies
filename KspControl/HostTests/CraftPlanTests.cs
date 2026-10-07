using KspControl.Contracts;
using KspControl.EditorModel;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
namespace KspControl.HostTests;

/// <summary>Scriptable catalog source standing in for the bridge's parts.construction_catalog.</summary>
internal sealed class FakeCatalog : ICatalogFetcher
{
 public readonly Dictionary<string,JObject> Parts=new(StringComparer.Ordinal);
 public readonly List<string[]> Calls=new();
 public readonly List<string?> Epochs=new();
 public bool ResearchAbsent;
 public bool Sandbox=true;
 /// <summary>Simulates a world change after the first chunk: later calls that carry the first epoch get stale_world.</summary>
 public bool WorldChangesAfterFirstCall;
 /// <summary>Simulates the research state flipping after the first chunk.</summary>
 public bool ResearchFlipsAfterFirstCall;
 /// <summary>Drops this name from every reply, or repeats it when <see cref="RepeatName"/> is set.</summary>
 public string? DropName; public string? RepeatName;
 public Func<BridgeResponse?>? Override;
 public Task<string> FetchAsync(IReadOnlyList<string> partNames,string? expectedWorldEpoch,CancellationToken cancellationToken)
 {
  Calls.Add(partNames.ToArray()); Epochs.Add(expectedWorldEpoch);
  if(Override?.Invoke() is { } forced) return Task.FromResult(JsonConvert.SerializeObject(forced));
  string epoch=WorldChangesAfterFirstCall && Calls.Count>1 ? "epoch-2" : "epoch-1";
  if(expectedWorldEpoch!=null && expectedWorldEpoch!=epoch) return Task.FromResult(JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode="stale_world",WorldEpoch=epoch }));
  var items=partNames.Where(n => n!=DropName).Select(n => Parts.TryGetValue(n,out var p) ? (JToken)p.DeepClone() : new JObject { ["name"]=n,["found"]=false }).ToList();
  if(RepeatName!=null && partNames.Contains(RepeatName)) items.Add(items.First(t => (string?)t["name"]==RepeatName).DeepClone());
  var research=ResearchAbsent ? (Sandbox ? "absent_sandbox_allowed" : "unreadable") : (ResearchFlipsAfterFirstCall && Calls.Count>1 ? "absent_sandbox_allowed" : "available");
  return Task.FromResult(JsonConvert.SerializeObject(new BridgeResponse
  {
   Status="completed",WorldEpoch=epoch,Revision=1,
   Data=new JObject { ["requested"]=partNames.Count,["researchAndDevelopment"]=research,["parts"]=new JArray(items) }
  }));
 }
}

/// <summary>Serves catalog replies produced by the REAL bridge mapper (linked into this test project) with the default verified-support policy.</summary>
internal sealed class MapperBackedCatalog(FakeCatalog source) : ICatalogFetcher
{
 private sealed class Reader(FakeCatalog source) : KspControl.Bridge.ICatalogPartReader
 {
  public bool ResearchAvailable => true; public bool SandboxMode => false;
  public KspControl.Bridge.CatalogPartSource? Read(string name)
  {
   if(!source.Parts.TryGetValue(name,out var p)) return null;
   var cat=(string)p["category"]!;
   var part=new KspControl.Bridge.CatalogPartSource { Name=name,KspCategory="Propulsion",Buildable=true,TechAvailable=true,ModelPurchased=true,
    ModuleNames=cat switch { "command" => new[] { "ModuleCommand" },"engine" => new[] { "ModuleEnginesFX" },"decoupler" => new[] { "ModuleDecouple" },_ => new string[0] },
    ResourceNames=cat=="tank" ? new[] { "LiquidFuel" } : new string[0] };
   foreach(var n in (JArray)p["stackNodes"]!)
    part.StackNodes.Add(new KspControl.Bridge.CatalogNodeSource { Id=(string)n["id"]!,Position=new double[] { 0,(double)n["position"]![1]!,0 },Orientation=new double[] { 0,(double)n["orientation"]![1]!,0 },Size=1 });
   var rules=(JObject)p["attachRules"]!;
   part.AttachRules=new KspControl.Bridge.CatalogAttachRulesSource { Stack=(bool)rules["stack"]!,Srf=(bool)rules["srf"]!,AllowStack=(bool)rules["allowStack"]!,AllowSrf=(bool)rules["allowSrf"]! };
   if(p["surfaceNode"] is JObject) part.SurfaceNode=new KspControl.Bridge.CatalogNodeSource { Id="srfAttach",Position=new double[] { 0,0,0 },Orientation=new double[] { 1,0,0 } };
   return part;
  }
 }
 public Task<string> FetchAsync(IReadOnlyList<string> partNames,string? expectedWorldEpoch,CancellationToken cancellationToken)
 {
  var data=KspControl.Bridge.ConstructionCatalogMapper.Build(partNames.ToList(),new Reader(source),KspControl.Bridge.ConstructionSupportPolicy.Default);
  return Task.FromResult(JsonConvert.SerializeObject(new BridgeResponse { Status="completed",WorldEpoch="mapped-epoch",Data=data }));
 }
}

internal static class CatalogFx
{
 public static JObject Part(string name,string category,bool srf=false,bool buildable=true,string support="verified",bool allowed=true,params (string id,double y,double oy)[] nodes) => new()
 {
  ["name"]=name,["found"]=true,["category"]=category,["kspCategory"]="Propulsion",["buildable"]=buildable,["partsStockAllowed"]=allowed,
  ["stockAllowedBasis"]="tech_and_model_purchased",["constructionSupport"]=support,["nodeSource"]="prefab",
  ["stackNodes"]=new JArray(nodes.Select(n => new JObject { ["id"]=n.id,["position"]=new JArray(0,n.y,0),["orientation"]=new JArray(0,n.oy,0),["size"]=1 })),
  ["surfaceNode"]=srf ? new JObject { ["position"]=new JArray(0,0,0),["orientation"]=new JArray(1,0,0) } : JValue.CreateNull(),
  ["attachRules"]=new JObject { ["stack"]=true,["srf"]=srf,["allowStack"]=true,["allowSrf"]=true,["allowCollision"]=false,["allowDock"]=false }
 };
 /// <summary>Parts whose node values are invented placeholders (their cfgs were not readable). They carry "fixtureValues": true and the real bridge policy never verifies them.</summary>
 public static readonly string[] PlaceholderParts={ "radialDecoupler","solidBooster.sm.v2","pointyNoseConeB" };
 /// <summary>
 /// Stock node values from the S0 twin and PlannerTests. radialDecoupler, solidBooster.sm.v2 and pointyNoseConeB are PLACEHOLDER fixtures.
 /// This catalog marks every part <paramref name="support"/> so planner behaviour can be tested; see ConstructionSupportPolicy.Default tests for what the bridge really reports.
 /// </summary>
 public static FakeCatalog Stock(string support="verified")
 {
  var c=new FakeCatalog();
  void Add(JObject p) { if(PlaceholderParts.Contains((string)p["name"]!)) p["fixtureValues"]=true; c.Parts[(string)p["name"]!]=p; }
  Add(Part("mk1pod.v2","command",support:support,nodes:new[] { ("bottom",-0.4050379,-1.0),("top",0.6423756,1.0) }));
  Add(Part("probeCoreOcto.v2","command",support:support,nodes:new[] { ("bottom",-0.1870818,-1.0),("top",0.1870818,1.0) }));
  Add(Part("fuelTankSmall","tank",support:support,nodes:new[] { ("top",0.55525,1.0),("bottom",-0.55525,-1.0) }));
  Add(Part("liquidEngine.v2","engine",support:support,nodes:new[] { ("top",0.0,1.0),("bottom",-1.63,-1.0) }));
  Add(Part("Decoupler.1","decoupler",support:support,nodes:new[] { ("top",0.05,1.0),("bottom",-0.05,-1.0) }));
  Add(Part("radialDecoupler","decoupler",srf:true,support:support));
  Add(Part("solidBooster.sm.v2","engine",srf:true,support:support,nodes:new[] { ("top",0.9,1.0) }));
  Add(Part("pointyNoseConeB","other",support:support,nodes:new[] { ("bottom01",-0.625,-1.0) }));
  return c;
 }
 static readonly JsonSerializerSettings Camel=new() { ContractResolver=new CamelCasePropertyNamesContractResolver(),NullValueHandling=NullValueHandling.Ignore };
 public static string Json(GraphDto g) => JsonConvert.SerializeObject(g,Camel);
 static GraphPartDto P(string id,string part,string? parent=null,string? pn=null,string? n=null) => new() { Id=id,Part=part,Parent=parent,ParentNode=pn,Node=n };
 public static GraphDto T1() => new() { Name="Probe One",Facility="VAB",Root="core",Parts={ P("core","probeCoreOcto.v2"),P("tank","fuelTankSmall","core","bottom","top"),P("engine","liquidEngine.v2","tank","bottom","top") } };
 public static GraphDto Pod() => new() { Name="Pod One",Facility="VAB",Root="pod",Parts={ P("pod","mk1pod.v2"),P("tank","fuelTankSmall","pod","bottom","top"),P("engine","liquidEngine.v2","tank","bottom","top") } };
 public static GraphDto T2() => new() { Name="Two Stage",Facility="VAB",Root="pod",Parts={
  P("pod","mk1pod.v2"),P("t1","fuelTankSmall","pod","bottom","top"),P("e1","liquidEngine.v2","t1","bottom","top"),P("d","Decoupler.1","e1","bottom","top"),
  P("t2","fuelTankSmall","d","bottom","top"),P("e2","liquidEngine.v2","t2","bottom","top") } };
 public static GraphDto T3(int n=4) => new() { Name="Boosted",Facility="VAB",Root="core",Parts={
  P("core","probeCoreOcto.v2"),P("tank","fuelTankSmall","core","bottom","top"),P("engine","liquidEngine.v2","tank","bottom","top"),
  new() { Id="rd",Part="radialDecoupler",Parent="tank",Symmetry=n,Surface=new() { HeightOffset=0,AngleDegrees=0 } },
  new() { Id="srb",Part="solidBooster.sm.v2",Parent="rd",Surface=new() { HeightOffset=0,AngleDegrees=270 } },
  P("cone","pointyNoseConeB","srb","top","bottom01") } };
}

[TestClass] public class CraftPlanTests
{
 private static async Task<JObject> Plan(GraphDto g,ICatalogFetcher c) => JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(g),c,CancellationToken.None));
 private static JObject Data(JObject reply) { Assert.AreEqual("completed",(string?)reply["Status"]); return (JObject)reply["Data"]!; }
 private static string[] Codes(JObject data) => ((JArray)data["issues"]!).Select(i => (string)i["code"]!).ToArray();

 [TestMethod] public async Task T1PlanHasNoIssuesAndStagingForEveryPart()
 {
  var data=Data(await Plan(CatalogFx.T1(),CatalogFx.Stock()));
  Assert.IsTrue((bool)data["ok"]!,string.Join(",",Codes(data))); Assert.AreEqual("T1",(string?)data["topology"]);
  Assert.AreEqual(64,((string)data["planHash"]!).Length); Assert.AreEqual(64,((string)data["catalogHash"]!).Length);
  Assert.AreEqual("not_included",(string?)data["modVersions"]);
  var parts=(JArray)data["parts"]!; Assert.AreEqual(3,parts.Count);
  foreach(var p in parts) foreach(var f in new[] { "istg","dstg","sidx","sqor","sepI","attm" }) Assert.IsNotNull(p["staging"]![f],f);
  Assert.AreEqual("core",(string?)parts[0]["id"]); Assert.AreEqual("root",(string?)parts[0]["attachment"]); Assert.AreEqual(-1,(int)parts[0]["staging"]!["istg"]!);
  Assert.AreEqual("stack",(string?)parts[1]["attachment"]); Assert.AreEqual("core",(string?)parts[1]["parent"]);
  // Probe core +/-0.1870818 and tank +/-0.55525: tank centre sits 0.1870818+0.55525 below the root.
  Assert.AreEqual(15-(0.1870818+0.55525),(double)parts[1]["position"]![1]!,1e-5);
  Assert.AreEqual(15-(0.1870818+0.55525)-0.55525,(double)parts[2]["position"]![1]!,1e-5);
  Assert.IsFalse(parts.Any(p => (bool)p["calibratedAtApply"]!));
 }

 [TestMethod] public async Task HostPlanEqualsThePurePlannerOnTheSameCatalog()
 {
  var fake=CatalogFx.Stock(); var data=Data(await Plan(CatalogFx.Pod(),fake));
  var fetch=new CatalogFetch(); Assert.IsTrue(CatalogWire.TryParse(JObject.Parse(await fake.FetchAsync(new[] { "mk1pod.v2","fuelTankSmall","liquidEngine.v2" },null,default))["Data"] as JObject,new[] { "mk1pod.v2","fuelTankSmall","liquidEngine.v2" },fetch,out var err),err);
  var catalog=new ConstructionCatalog(); foreach(var e in fetch.Found.Values) catalog.Add(e.Part);
  var direct=CraftPlanner.Plan(CatalogFx.Pod(),catalog); Assert.IsTrue(direct.Ok);
  Assert.AreEqual(catalog.Hash(),(string?)data["catalogHash"]); Assert.AreEqual(PlanHasher.Hash(CatalogFx.Pod(),direct,catalog.Hash()),(string?)data["planHash"]);
  for(int i=0;i<3;i++) Assert.AreEqual(direct.Craft!.Parts[i].Staging.Istg,(int)data["parts"]![i]!["staging"]!["istg"]!);
 }

 [TestMethod] public async Task PlanHashAndCatalogHashAreStableAcrossRunsAndSensitiveToContent()
 {
  var a=Data(await Plan(CatalogFx.T2(),CatalogFx.Stock())); var b=Data(await Plan(CatalogFx.T2(),CatalogFx.Stock()));
  Assert.IsTrue((bool)a["ok"]!,string.Join(",",Codes(a)));
  Assert.AreEqual((string?)a["planHash"],(string?)b["planHash"]); Assert.AreEqual((string?)a["catalogHash"],(string?)b["catalogHash"]);
  Assert.AreEqual(a.ToString(),b.ToString(),"whole reply is byte-stable");
  var moved=CatalogFx.Stock(); moved.Parts["fuelTankSmall"]["stackNodes"]![0]!["position"]![1]=0.56;
  var c=Data(await Plan(CatalogFx.T2(),moved));
  Assert.AreNotEqual((string?)a["catalogHash"],(string?)c["catalogHash"]); Assert.AreNotEqual((string?)a["planHash"],(string?)c["planHash"]);
  var renamed=CatalogFx.T2(); renamed.Name="Other Name";
  Assert.AreNotEqual((string?)a["planHash"],(string?)Data(await Plan(renamed,CatalogFx.Stock()))["planHash"]);
 }

 [TestMethod] public async Task CatalogHashIgnoresUnusedPartsAndRequestOrder()
 {
  var one=Data(await Plan(CatalogFx.Pod(),CatalogFx.Stock()));
  var extra=CatalogFx.Stock(); extra.Parts["Decoupler.1"]["stackNodes"]![0]!["position"]![1]=9;
  Assert.AreEqual((string?)one["catalogHash"],(string?)Data(await Plan(CatalogFx.Pod(),extra))["catalogHash"],"only parts the graph names are hashed");
  var reordered=CatalogFx.Pod(); reordered.Parts.Reverse(); reordered.Root="pod";
  Assert.AreEqual((string?)one["catalogHash"],(string?)Data(await Plan(reordered,CatalogFx.Stock()))["catalogHash"]);
 }

 [TestMethod] public async Task T2PlanDecouplerStaging()
 {
  var data=Data(await Plan(CatalogFx.T2(),CatalogFx.Stock())); Assert.IsTrue((bool)data["ok"]!,string.Join(",",Codes(data)));
  Assert.AreEqual("T2",(string?)data["topology"]);
  var s=((JArray)data["parts"]!).ToDictionary(p => (string)p["id"]!,p => p["staging"]!);
  Assert.AreEqual(2,(int)s["e2"]["istg"]!); Assert.AreEqual(1,(int)s["d"]["istg"]!); Assert.AreEqual(0,(int)s["e1"]["istg"]!);
 }

 [TestMethod] public async Task T3QuadHasSymmetryGroupsAndCalibratedSurfaceParts()
 {
  var data=Data(await Plan(CatalogFx.T3(4),CatalogFx.Stock())); Assert.IsTrue((bool)data["ok"]!,string.Join(",",Codes(data)));
  Assert.AreEqual("T3",(string?)data["topology"]);
  var parts=((JArray)data["parts"]!).ToList();
  Assert.AreEqual(3+4*3,parts.Count);
  var surface=parts.Where(p => (string?)p["attachment"]=="surface").ToList(); Assert.AreEqual(8,surface.Count);
  Assert.IsTrue(surface.All(p => (bool)p["calibratedAtApply"]!)); Assert.IsTrue(parts.Where(p => (string?)p["attachment"]!="surface").All(p => !(bool)p["calibratedAtApply"]!));
  Assert.AreEqual(3,((JArray)data["symmetryGroups"]!).Count); Assert.IsTrue(((JArray)data["symmetryGroups"]!).All(g => ((JArray)g).Count==4));
  Assert.IsTrue(surface.All(p => (int)p["staging"]!["attm"]! == 1));
 }

 [TestMethod] public async Task UnsupportedTopologyIsAnIssueWithoutAHash()
 {
  var g=CatalogFx.Pod(); g.Parts.RemoveAt(2);  // pod + tank only: no engine
  var data=Data(await Plan(g,CatalogFx.Stock()));
  Assert.IsFalse((bool)data["ok"]!); CollectionAssert.Contains(Codes(data),"unsupported_staging_topology");
  Assert.AreEqual(JTokenType.Null,data["planHash"]!.Type); Assert.AreEqual(JTokenType.Null,data["topology"]!.Type);
  Assert.AreEqual(64,((string)data["catalogHash"]!).Length);
 }

 [TestMethod] public async Task UnknownPartIsReportedAndListedAsMissing()
 {
  var g=CatalogFx.Pod(); g.Parts[1].Part="notARealPart";
  var data=Data(await Plan(g,CatalogFx.Stock()));
  CollectionAssert.Contains(Codes(data),"unknown_part"); Assert.IsFalse((bool)data["ok"]!);
  CollectionAssert.AreEqual(new[] { "notARealPart" },((JArray)data["catalog"]!["missing"]!).Select(t => (string)t!).ToArray());
 }

 [TestMethod] public async Task LockedPartIsReportedPerGraphPartId()
 {
  var c=CatalogFx.Stock(); c.Parts["liquidEngine.v2"]["partsStockAllowed"]=false;
  var data=Data(await Plan(CatalogFx.Pod(),c));
  var locked=((JArray)data["issues"]!).Where(i => (string?)i["code"]=="part_locked").ToList();
  Assert.AreEqual(1,locked.Count); Assert.AreEqual("engine",(string?)locked[0]["partId"]);
  Assert.IsFalse((bool)data["ok"]!); Assert.AreEqual(JTokenType.Null,data["planHash"]!.Type);
  Assert.AreEqual(3,((JArray)data["parts"]!).Count,"layout is still reported for diagnosis");
 }

 [TestMethod] public async Task UnbuildableAndUnverifiedPartsAreIssues()
 {
  var c=CatalogFx.Stock(); c.Parts["fuelTankSmall"]["buildable"]=false; c.Parts["liquidEngine.v2"]["constructionSupport"]="unverified";
  var data=Data(await Plan(CatalogFx.Pod(),c));
  var issues=((JArray)data["issues"]!).Select(i => $"{(string)i["code"]!}:{(string?)i["partId"]}").ToArray();
  CollectionAssert.Contains(issues,"part_not_buildable:tank"); CollectionAssert.Contains(issues,"part_construction_unverified:engine");
  CollectionAssert.AreEqual(new[] { "liquidEngine.v2" },((JArray)data["catalog"]!["unverified"]!).Select(t => (string)t!).ToArray());
 }

 [TestMethod] public async Task UnverifiedStockParts()
 {
  var data=Data(await Plan(CatalogFx.T1(),CatalogFx.Stock("unverified")));
  Assert.AreEqual(3,Codes(data).Count(c => c=="part_construction_unverified")); Assert.IsFalse((bool)data["ok"]!);
 }

 [TestMethod] public async Task SandboxWithoutResearchIsReported()
 {
  var c=CatalogFx.Stock(); c.ResearchAbsent=true;
  var data=Data(await Plan(CatalogFx.Pod(),c));
  Assert.IsTrue((bool)data["ok"]!); Assert.AreEqual("absent_sandbox_allowed",(string?)data["catalog"]!["researchAndDevelopment"]); Assert.AreEqual("epoch-1",(string?)data["catalog"]!["worldEpoch"]);
 }

 [TestMethod] public async Task GeometryBoundsAreIssues()
 {
  var g=CatalogFx.T3(); g.Parts[3].Surface!.HeightOffset=51;
  var data=Data(await Plan(g,CatalogFx.Stock()));
  CollectionAssert.Contains(Codes(data),"invalid_surface"); Assert.AreEqual(0,((JArray)data["parts"]!).Count);
  var g2=CatalogFx.Pod(); g2.Parts[1].ParentNode="nope";
  CollectionAssert.Contains(Codes(Data(await Plan(g2,CatalogFx.Stock()))),"missing_parent_node");
  var g3=CatalogFx.Pod(); g3.Parts[2].Symmetry=3;
  CollectionAssert.Contains(Codes(Data(await Plan(g3,CatalogFx.Stock()))),"unsupported_symmetry_attachment");
 }

 [TestMethod] public async Task ShapeIssuesAreReportedWithoutTouchingTheBridge()
 {
  var fake=CatalogFx.Stock(); var g=CatalogFx.Pod(); g.Facility="SPH"; g.Parts[1].Id="bad id!";
  var data=Data(await Plan(g,fake));
  Assert.IsFalse((bool)data["ok"]!); CollectionAssert.Contains(Codes(data),"facility_mismatch"); CollectionAssert.Contains(Codes(data),"invalid_part_id");
  Assert.AreEqual(0,fake.Calls.Count);
 }

 [TestMethod] public async Task CatalogIsFetchedInChunksOfAtMost32DistinctNames()
 {
  var fake=CatalogFx.Stock(); var g=new GraphDto { Name="Long",Facility="VAB",Root="p0" };
  g.Parts.Add(new() { Id="p0",Part="mk1pod.v2" });
  string prev="p0";
  for(int i=1;i<=39;i++)
  {
   fake.Parts["tank"+i]=CatalogFx.Part("tank"+i,"tank",nodes:new[] { ("top",0.5,1.0),("bottom",-0.5,-1.0) });
   g.Parts.Add(new() { Id="p"+i,Part="tank"+i,Parent=prev,ParentNode=i==1 ? "bottom" : "bottom",Node="top" }); prev="p"+i;
  }
  g.Parts.Add(new() { Id="p40",Part="tank1",Parent=prev,ParentNode="bottom",Node="top" });   // duplicate name is fetched once
  var data=Data(await Plan(g,fake));
  CollectionAssert.AreEqual(new[] { 32,8 },fake.Calls.Select(c => c.Length).ToArray());
  Assert.AreEqual(40,fake.Calls.SelectMany(c => c).Distinct().Count());
  Assert.AreEqual(40,((JArray)data["catalog"]!["partsChecked"]!).Count);
 }

 [TestMethod] public async Task Exactly250PartsAreAcceptedAndPlannedThroughEightCalls()
 {
  var fake=CatalogFx.Stock(); var g=new GraphDto { Name="Max",Facility="VAB",Root="p0" };
  g.Parts.Add(new() { Id="p0",Part="mk1pod.v2" }); string prev="p0";
  for(int i=1;i<250;i++) { fake.Parts["t"+i]=CatalogFx.Part("t"+i,"tank",nodes:new[] { ("top",0.5,1.0),("bottom",-0.5,-1.0) }); g.Parts.Add(new() { Id="p"+i,Part="t"+i,Parent=prev,ParentNode="bottom",Node="top" }); prev="p"+i; }
  var reply=JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(g),fake,default));
  Assert.AreEqual("completed",(string?)reply["Status"]); Assert.AreEqual(8,fake.Calls.Count);
  Assert.IsTrue(fake.Calls.All(c => c.Length<=ConstructionLimits.MaxCatalogParts));
  CollectionAssert.Contains(Codes((JObject)reply["Data"]!),"unsupported_staging_topology","a tank-only chain has no engine");
 }

 [DataTestMethod]
 [DataRow(null,"graph")] [DataRow("","graph")] [DataRow("   ","graph")] [DataRow("not json","valid graph JSON")] [DataRow("[]","JSON object")] [DataRow("42","JSON object")] [DataRow("null","JSON object")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[]}","1..250")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\"}","1..250")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[null]}","null")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"pos\":[0,0,0]}]}","valid graph JSON")]
 [DataRow("{\"name\":\"x\",\"name\":\"y\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[]}","valid graph JSON")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"symmetry\":\"two\"}]}","valid graph JSON")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":99999999999}]}","valid graph JSON")]
 public async Task BadGraphArgumentsAreInvalidArgumentWithoutTouchingTheBridge(string? json,string detail)
 {
  var fake=CatalogFx.Stock();
  var reply=JObject.Parse(await CraftPlanService.PlanAsync(json,fake,default));
  Assert.AreEqual("failed",(string?)reply["Status"]); Assert.AreEqual("invalid_argument",(string?)reply["ReasonCode"]);
  StringAssert.Contains((string?)reply["Data"]!["detail"],detail); Assert.AreEqual(0,fake.Calls.Count);
 }

 [TestMethod] public async Task Over250PartsAndOver256KiBAreInvalidArgument()
 {
  var fake=CatalogFx.Stock(); var g=new GraphDto { Name="Big",Facility="VAB",Root="p0" };
  for(int i=0;i<251;i++) g.Parts.Add(new() { Id="p"+i,Part="mk1pod.v2" });
  var r1=JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(g),fake,default));
  Assert.AreEqual("invalid_argument",(string?)r1["ReasonCode"]); StringAssert.Contains((string?)r1["Data"]!["detail"],"1..250");
  var huge="{\"name\":\""+new string('x',ConstructionLimits.MaxGraphBytes)+"\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"mk1pod.v2\"}]}";
  var r2=JObject.Parse(await CraftPlanService.PlanAsync(huge,fake,default));
  Assert.AreEqual("invalid_argument",(string?)r2["ReasonCode"]); StringAssert.Contains((string?)r2["Data"]!["detail"],"262144");
  Assert.AreEqual(0,fake.Calls.Count);
 }

 [TestMethod] public async Task ByteLimitCountsUtf8NotCharacters()
 {
  var s=new string('é',ConstructionLimits.MaxGraphBytes/2+10);   // 2 bytes each
  var json="{\"name\":\""+s+"\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"mk1pod.v2\"}]}";
  Assert.IsTrue(json.Length<ConstructionLimits.MaxGraphBytes);
  Assert.AreEqual("invalid_argument",(string?)JObject.Parse(await CraftPlanService.PlanAsync(json,CatalogFx.Stock(),default))["ReasonCode"]);
 }

 [TestMethod] public async Task BridgeFailureIsPassedThroughWithItsReason()
 {
  var fake=CatalogFx.Stock(); fake.Override=()=>new BridgeResponse { Status="failed",ReasonCode="bridge_unreachable" };
  var reply=JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(CatalogFx.Pod()),fake,default));
  Assert.AreEqual("failed",(string?)reply["Status"]); Assert.AreEqual("bridge_unreachable",(string?)reply["ReasonCode"]);
 }

 [TestMethod] public async Task MalformedCatalogReplyIsProtocolInvalid()
 {
  var fake=CatalogFx.Stock();
  fake.Override=()=>new BridgeResponse { Status="completed",Data=new JObject { ["parts"]=new JArray(new JObject { ["name"]="unrequested",["found"]=true }) } };
  Assert.AreEqual("protocol_invalid",(string?)JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(CatalogFx.Pod()),fake,default))["ReasonCode"]);
  fake.Override=()=>new BridgeResponse { Status="completed",Data=new JObject() };
  Assert.AreEqual("protocol_invalid",(string?)JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(CatalogFx.Pod()),fake,default))["ReasonCode"]);
  var bad=CatalogFx.Stock(); bad.Parts["mk1pod.v2"]["stackNodes"]![0]!["position"]=new JArray(1,2);
  Assert.AreEqual("protocol_invalid",(string?)JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(CatalogFx.Pod()),bad,default))["ReasonCode"]);
 }

 [TestMethod] public async Task UnknownBridgeCategoryBecomesOther()
 {
  var c=CatalogFx.Stock(); c.Parts["pointyNoseConeB"]["category"]="parachute";
  var data=Data(await Plan(CatalogFx.T3(2),c));
  Assert.IsTrue((bool)data["ok"]!,string.Join(",",Codes(data)));
  Assert.AreEqual("other",(string?)((JArray)data["parts"]!).First(p => (string?)p["part"]=="pointyNoseConeB")["category"]);
 }

 [TestMethod] public async Task LaterChunksCarryTheFirstChunksWorldEpoch()
 {
  var fake=CatalogFx.Stock(); var g=new GraphDto { Name="Long",Facility="VAB",Root="p0" };
  g.Parts.Add(new() { Id="p0",Part="mk1pod.v2" }); string prev="p0";
  for(int i=1;i<=40;i++) { fake.Parts["tank"+i]=CatalogFx.Part("tank"+i,"tank",nodes:new[] { ("top",0.5,1.0),("bottom",-0.5,-1.0) }); g.Parts.Add(new() { Id="p"+i,Part="tank"+i,Parent=prev,ParentNode="bottom",Node="top" }); prev="p"+i; }
  var data=Data(await Plan(g,fake));
  CollectionAssert.AreEqual(new string?[] { null,"epoch-1" },fake.Epochs.ToArray());
  Assert.AreEqual("epoch-1",(string?)data["catalog"]!["worldEpoch"]);
 }

 [TestMethod] public async Task WorldChangeBetweenChunksIsStaleWorld()
 {
  var fake=CatalogFx.Stock(); fake.WorldChangesAfterFirstCall=true; var g=new GraphDto { Name="Long",Facility="VAB",Root="p0" };
  g.Parts.Add(new() { Id="p0",Part="mk1pod.v2" }); string prev="p0";
  for(int i=1;i<=40;i++) { fake.Parts["tank"+i]=CatalogFx.Part("tank"+i,"tank",nodes:new[] { ("top",0.5,1.0),("bottom",-0.5,-1.0) }); g.Parts.Add(new() { Id="p"+i,Part="tank"+i,Parent=prev,ParentNode="bottom",Node="top" }); prev="p"+i; }
  var reply=JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(g),fake,default));
  Assert.AreEqual("failed",(string?)reply["Status"]); Assert.AreEqual("stale_world",(string?)reply["ReasonCode"]);
 }

 [TestMethod] public async Task ResearchStateChangingBetweenChunksIsProtocolInvalid()
 {
  var fake=CatalogFx.Stock(); fake.ResearchFlipsAfterFirstCall=true; var g=new GraphDto { Name="Long",Facility="VAB",Root="p0" };
  g.Parts.Add(new() { Id="p0",Part="mk1pod.v2" }); string prev="p0";
  for(int i=1;i<=40;i++) { fake.Parts["tank"+i]=CatalogFx.Part("tank"+i,"tank",nodes:new[] { ("top",0.5,1.0),("bottom",-0.5,-1.0) }); g.Parts.Add(new() { Id="p"+i,Part="tank"+i,Parent=prev,ParentNode="bottom",Node="top" }); prev="p"+i; }
  var reply=JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(g),fake,default));
  Assert.AreEqual("protocol_invalid",(string?)reply["ReasonCode"]); StringAssert.Contains((string?)reply["Data"]!["detail"],"research");
 }

 [TestMethod] public async Task RequestedNameAbsentFromTheReplyIsMissing()
 {
  var fake=CatalogFx.Stock(); fake.DropName="fuelTankSmall";
  var data=Data(await Plan(CatalogFx.Pod(),fake));
  CollectionAssert.Contains(Codes(data),"unknown_part");
  CollectionAssert.AreEqual(new[] { "fuelTankSmall" },((JArray)data["catalog"]!["missing"]!).Select(t => (string)t!).ToArray());
 }

 [TestMethod] public async Task DuplicateReplyEntriesAreProtocolInvalid()
 {
  var fake=CatalogFx.Stock(); fake.RepeatName="mk1pod.v2";
  var reply=JObject.Parse(await CraftPlanService.PlanAsync(CatalogFx.Json(CatalogFx.Pod()),fake,default));
  Assert.AreEqual("protocol_invalid",(string?)reply["ReasonCode"]);
 }

 [TestMethod] public async Task UnreadableResearchStateBlocksPartsAsNotAllowed()
 {
  var fake=CatalogFx.Stock(); fake.ResearchAbsent=true; fake.Sandbox=false;
  foreach(var p in fake.Parts.Values) { p["partsStockAllowed"]=false; p["stockAllowedBasis"]="research_state_unreadable"; }
  var data=Data(await Plan(CatalogFx.Pod(),fake));
  Assert.IsFalse((bool)data["ok"]!); Assert.AreEqual(3,Codes(data).Count(c => c=="part_locked")); Assert.AreEqual("unreadable",(string?)data["catalog"]!["researchAndDevelopment"]);
 }

 [TestMethod] public async Task T3WithTheRealDefaultSupportPolicyIsRefusedAsUnverified()
 {
  var data=Data(await Plan(CatalogFx.T3(2),new MapperBackedCatalog(CatalogFx.Stock())));
  Assert.IsFalse((bool)data["ok"]!);
  var unverified=((JArray)data["issues"]!).Where(i => (string?)i["code"]=="part_construction_unverified").Select(i => (string)i["partId"]!).OrderBy(x => x).ToArray();
  CollectionAssert.AreEqual(new[] { "cone","rd","srb" },unverified);
  CollectionAssert.AreEquivalent(new[] { "radialDecoupler","solidBooster.sm.v2","pointyNoseConeB" },((JArray)data["catalog"]!["unverified"]!).Select(t => (string)t!).ToArray());
 }

 [TestMethod] public async Task T1AndT2WithTheRealDefaultSupportPolicyAreAccepted()
 {
  foreach(var g in new[] { CatalogFx.T1(),CatalogFx.Pod(),CatalogFx.T2() })
  {
   var data=Data(await Plan(g,new MapperBackedCatalog(CatalogFx.Stock())));
   Assert.IsTrue((bool)data["ok"]!,g.Name+": "+string.Join(",",Codes(data)));
  }
 }

 [DataTestMethod]
 [DataRow("{\"name\":\"x\",\"Name\":\"y\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}","unknown member")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"Part\":\"p\"}]}","unknown member")]
 [DataRow("{/*c*/\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}","comments")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]} // tail","comments")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"symmetry\":\"2\"}]}","symmetry must be an integer")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"symmetry\":2.0}]}","symmetry must be an integer")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":true}]}","stage must be an integer")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"surface\":{\"heightOffset\":\"1\"}}]}","heightOffset must be a number")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"surface\":{\"angle\":1}}]}","unknown member")]
 [DataRow("{\"name\":5,\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}","name must be a string")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":{}}","parts must be an array")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"configuration\":\"x\"}]}","configuration must be an array")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]} {}","valid graph JSON")]
 [DataRow("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":100000000000000000000}]}","stage is out of range")]
 public async Task StrictGraphParsingRejectsCoercionCommentsAndCaseVariants(string json,string detail)
 {
  var fake=CatalogFx.Stock();
  var reply=JObject.Parse(await CraftPlanService.PlanAsync(json,fake,default));
  Assert.AreEqual("invalid_argument",(string?)reply["ReasonCode"]); StringAssert.Contains((string?)reply["Data"]!["detail"],detail); Assert.AreEqual(0,fake.Calls.Count);
 }

 [TestMethod] public void StrictParsingStillAcceptsWellFormedGraphsWithIntegralAndFractionalNumbers()
 {
  var g=CraftPlanService.ParseGraph("{\"name\":\"x\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"symmetry\":2,\"stage\":1,\"surface\":{\"heightOffset\":1,\"angleDegrees\":90.5},\"configuration\":[]}]}",out var error);
  Assert.IsNotNull(g,error); Assert.AreEqual(1.0,g!.Parts[0].Surface!.HeightOffset); Assert.AreEqual(90.5,g.Parts[0].Surface!.AngleDegrees); Assert.AreEqual(2,g.Parts[0].Symmetry);
 }

 [TestMethod] public void ConstructionCatalogIsAReadOperationAndNotAControlOperation()
 {
  Assert.IsTrue(BridgeClient.ReadOperations.Contains(ConstructionOperations.Catalog));
  Assert.IsFalse(BridgeClient.ControlOperationSet.Contains(ConstructionOperations.Catalog));
  Assert.AreEqual("parts.construction_catalog",ConstructionOperations.Catalog);
 }

 [TestMethod] [DoNotParallelize] public async Task LoopbackFetchSendsPartNamesAndPlansTheReply()
 {
  var stock=CatalogFx.Stock();
  using var bridge=new FakeBridge(r =>
  {
   Assert.AreEqual(ConstructionOperations.Catalog,r.Operation);
   var names=((JArray)r.Arguments["partNames"]!).Select(t => (string)t!).ToArray();
   var reply=FakeBridge.Ok(new JObject { ["researchAndDevelopment"]="available",["parts"]=new JArray(names.Select(n => (JToken)stock.Parts[n])) }); reply.WorldEpoch="loop-epoch"; return reply;
  });
  var tools=new CraftPlanTools(new BridgeCatalogFetcher(new BridgeClient()));
  var data=Data(JObject.Parse(await tools.CraftPlan(CatalogFx.Json(CatalogFx.Pod()))));
  Assert.IsTrue((bool)data["ok"]!,string.Join(",",Codes(data)));
  Assert.AreEqual(1,bridge.Requests.Count);
  CollectionAssert.AreEquivalent(new[] { "mk1pod.v2","fuelTankSmall","liquidEngine.v2" },((JArray)bridge.Requests[0].Arguments["partNames"]!).Select(t => (string)t!).ToArray());
 }

 [TestMethod] [DoNotParallelize] public async Task ReadAsyncSendsTheExpectedWorldEpoch()
 {
  using var bridge=new FakeBridge(r => FakeBridge.Ok(new JObject()));
  await new BridgeClient().ReadAsync(ConstructionOperations.Catalog,new JObject { ["partNames"]=new JArray("a") },default,"epoch-x");
  await new BridgeClient().ReadAsync(ConstructionOperations.Catalog,new JObject { ["partNames"]=new JArray("a") },default);
  Assert.AreEqual("epoch-x",bridge.Requests[0].ExpectedWorldEpoch); Assert.IsNull(bridge.Requests[1].ExpectedWorldEpoch);
 }

 [TestMethod] [DoNotParallelize] public async Task LoopbackBridgeRefusalReachesTheCaller()
 {
  using var bridge=new FakeBridge(r => FakeBridge.Fail("invalid_argument"));
  var reply=JObject.Parse(await new CraftPlanTools(new BridgeCatalogFetcher(new BridgeClient())).CraftPlan(CatalogFx.Json(CatalogFx.Pod())));
  Assert.AreEqual("invalid_argument",(string?)reply["ReasonCode"]);
 }

 [TestMethod] [DoNotParallelize] public async Task ToolInvalidArgumentDoesNotOpenASocket()
 {
  using var bridge=new FakeBridge(r => FakeBridge.Ok(new JObject()));
  var reply=JObject.Parse(await new CraftPlanTools(new BridgeCatalogFetcher(new BridgeClient())).CraftPlan("{"));
  Assert.AreEqual("invalid_argument",(string?)reply["ReasonCode"]); Assert.AreEqual(0,bridge.Connections);
 }
}
