using KspControl.Bridge;
using KspControl.Contracts;
using KspControl.EditorModel;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;
/// <summary>
/// The bridge re-plans at apply admission from the same graph text and the same catalog reply the host used for craft_plan. These tests
/// feed the REAL host code and the REAL bridge code (linked into this project) the same inputs, so any drift between the two
/// implementations of the graph binder and the catalog wire parser fails here instead of surfacing as a live plan_changed.
/// </summary>
[TestClass] public class BridgeParityTests
{
 private const string Two="{\"name\":\"Probe One\",\"facility\":\"VAB\",\"root\":\"pod\",\"parts\":[{\"id\":\"pod\",\"part\":\"mk1pod.v2\"},{\"id\":\"tank\",\"part\":\"fuelTankSmall\",\"parent\":\"pod\",\"parentNode\":\"bottom\",\"node\":\"top\"},{\"id\":\"engine\",\"part\":\"liquidEngine.v2\",\"parent\":\"tank\",\"parentNode\":\"bottom\",\"node\":\"top\"}]}";

 private static readonly string[] Graphs=
 {
  Two, "", "   ", "[]", "{", "null", "5", "\"x\"",
  "{\"name\":\"a\",\"parts\":[]}", "{\"name\":\"a\",\"name\":\"b\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}", "{\"Name\":\"a\"}", "{\"name\":\"a\",\"facility\":\"VAB\",\"root\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}],\"extra\":1}",
  "{\"name\":5,\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}", "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"symmetry\":2.5}]}", "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"symmetry\":\"2\"}]}",
  "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":99999999999}]}", "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":123456789012345678901234567890}]}",
  "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"surface\":{\"heightOffset\":\"x\"}}]}", "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"surface\":{\"heightOffset\":1.5,\"angleDegrees\":45,\"z\":1}}]}",
  "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"surface\":{\"heightOffset\":1.5,\"angleDegrees\":45},\"symmetry\":3,\"stage\":4}]}", "{\"name\":\"a\",\"parts\":[null]}", "{\"name\":\"a\",\"parts\":[5]}", "{\"name\":\"a\",\"parts\":{}}",
  "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"configuration\":[\"x\",1]}]}", "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"configuration\":\"x\"}]}", "{\"name\":\"a\",// c\n\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}",
  "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}", "{\"name\":null,\"parts\":[{\"id\":\"a\",\"part\":null}]}", "{\"name\":\"\\u00e9\",\"parts\":[{\"id\":\"a\",\"part\":\"p\"}]}",
  new string('[',40)+new string(']',40), "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":1e2}]}", "{\"name\":\"a\",\"parts\":[{\"id\":\"a\",\"part\":\"p\",\"stage\":-1}]}"
 };

 [TestMethod] public void BothBindersAcceptAndRefuseTheSameGraphsWithTheSameErrors()
 {
  foreach(var json in Graphs)
  {
   var host=CraftPlanService.ParseGraph(json,out var hostError); var bridge=GraphJson.Parse(json,out var bridgeError);
   Assert.AreEqual(host==null,bridge==null,"accept or refuse differs for: "+json);
   Assert.AreEqual(hostError,bridgeError,"error text differs for: "+json);
   if(host!=null) Assert.AreEqual(JsonConvert.SerializeObject(host),JsonConvert.SerializeObject(bridge),"bound graph differs for: "+json);
  }
 }

 [TestMethod] public void BothBindersRefuseTooManyPartsAndOversizeText()
 {
  var parts=string.Join(",",Enumerable.Range(0,ConstructionLimits.MaxGraphParts+1).Select(i => "{\"id\":\"p"+i+"\",\"part\":\"x\"}"));
  foreach(var json in new[]{ "{\"name\":\"a\",\"facility\":\"VAB\",\"root\":\"p0\",\"parts\":["+parts+"]}","{\"name\":\""+new string('x',ConstructionLimits.MaxGraphBytes)+"\"}" })
  {
   var host=CraftPlanService.ParseGraph(json,out var hostError); var bridge=GraphJson.Parse(json,out var bridgeError);
   Assert.IsNull(host); Assert.IsNull(bridge); Assert.AreEqual(hostError,bridgeError);
  }
 }

 // ---- the catalog wire ----

 private sealed class Reader : ICatalogPartReader
 {
  public bool ResearchAvailable { get; set; } = true; public bool SandboxMode { get; set; }
  public readonly Dictionary<string,CatalogPartSource> Parts=new(StringComparer.Ordinal);
  public CatalogPartSource? Read(string name)=>Parts.TryGetValue(name,out var p) ? p : null;
  private static CatalogNodeSource N(string id,double y,double oy)=>new(){ Id=id,Position=new[]{0.0,y,0.0},Orientation=new[]{0.0,oy,0.0},Size=1 };
  public Reader()
  {
   Parts["mk1pod.v2"]=Make("mk1pod.v2",new[]{"ModuleCommand"},new[]{"ElectricCharge"},N("bottom",-0.4050379,-1),N("top",0.6423756,1));
   Parts["fuelTankSmall"]=Make("fuelTankSmall",Array.Empty<string>(),new[]{"LiquidFuel","Oxidizer"},N("top",0.55525,1),N("bottom",-0.55525,-1));
   Parts["liquidEngine.v2"]=Make("liquidEngine.v2",new[]{"ModuleEnginesFX"},new[]{"LiquidFuel"},N("top",0,1),N("bottom",-1.63,-1));
   var fin=Make("basicFin",Array.Empty<string>(),Array.Empty<string>()); fin.AttachRules=new(){ Srf=true,AllowSrf=true }; fin.SurfaceNode=N("srfAttach",0,0); Parts["basicFin"]=fin;
   var variant=Make("variantTank",Array.Empty<string>(),new[]{"LiquidFuel"},N("top",1,1),N("bottom",-1,-1)); variant.VariantName="Orange"; variant.VariantStackNodes=new List<CatalogNodeSource>{ N("bottom",-1.25,-1) }; Parts["variantTank"]=variant;
   Parts["locked"]=Make("locked",Array.Empty<string>(),Array.Empty<string>()); Parts["locked"].TechAvailable=false;
  }
  private static CatalogPartSource Make(string name,string[] modules,string[] resources,params CatalogNodeSource[] nodes)=>new()
  {
   Name=name,KspCategory="Propulsion",Buildable=true,TechAvailable=true,ModelPurchased=true,ModuleNames=modules,ResourceNames=resources,StackNodes=nodes,
   AttachRules=new CatalogAttachRulesSource { Stack=true,AllowStack=true,Srf=false,AllowSrf=true }
  };
 }

 private static (CatalogFetch Host,ReplanCatalog Bridge,bool HostOk,bool BridgeOk,string HostError,string BridgeError) Both(JObject data,IReadOnlyList<string> names)
 {
  var host=new CatalogFetch(); var bridge=new ReplanCatalog();
  var hostOk=CatalogWire.TryParse(data,names,host,out var hostError); var bridgeOk=ReplanCatalog.TryParse(data,names.ToList(),bridge,out var bridgeError);
  return (host,bridge,hostOk,bridgeOk,hostError,bridgeError);
 }

 private static readonly string[] AllNames={ "mk1pod.v2","fuelTankSmall","liquidEngine.v2","basicFin","variantTank","locked","nope" };

 [TestMethod] public void BothParsersBuildTheSameCatalogFromTheRealMapperOutput()
 {
  foreach(var (research,sandbox) in new[]{ (true,false),(false,true),(false,false) })
  {
   var reader=new Reader { ResearchAvailable=research,SandboxMode=sandbox };
   var data=ConstructionCatalogMapper.Build(AllNames.ToList(),reader,ConstructionSupportPolicy.Default);
   var both=Both(data,AllNames);
   Assert.IsTrue(both.HostOk && both.BridgeOk,both.HostError+both.BridgeError);
   Assert.AreEqual(both.Host.ResearchAndDevelopment,both.Bridge.ResearchAndDevelopment);
   CollectionAssert.AreEquivalent(both.Host.Missing.ToArray(),both.Bridge.Missing.ToArray());
   CollectionAssert.AreEquivalent(both.Host.Found.Keys.ToArray(),both.Bridge.Found.Keys.ToArray());
   foreach(var name in both.Host.Found.Keys)
   {
    Assert.AreEqual(JsonConvert.SerializeObject(both.Host.Found[name].Part),JsonConvert.SerializeObject(both.Bridge.Found[name].Part),name);
    Assert.AreEqual(both.Host.Found[name].StockAllowed,both.Bridge.Found[name].StockAllowed,name); Assert.AreEqual(both.Host.Found[name].Basis,both.Bridge.Found[name].Basis,name);
   }
   var hostCatalog=new ConstructionCatalog(); foreach(var e in both.Host.Found.Values) hostCatalog.Add(e.Part);
   var bridgeCatalog=new ConstructionCatalog(); foreach(var e in both.Bridge.Found.Values) bridgeCatalog.Add(e.Part);
   Assert.AreEqual(hostCatalog.Hash(),bridgeCatalog.Hash(),"catalogHash differs");
  }
 }

 [TestMethod] public void BothParsersRefuseTheSameMalformedReplies()
 {
  var good=ConstructionCatalogMapper.Build(new List<string>{ "mk1pod.v2","fuelTankSmall" },new Reader(),ConstructionSupportPolicy.Default);
  var names=new[]{ "mk1pod.v2","fuelTankSmall" };
  var cases=new List<JObject?>();
  JObject Mutate(Action<JObject> edit){ var copy=(JObject)good.DeepClone(); edit(copy); return copy; }
  cases.Add(Mutate(d => d["parts"]!.Parent!.Remove()));
  cases.Add(Mutate(d => d["parts"]=new JObject()));
  cases.Add(Mutate(d => ((JArray)d["parts"]!).Add(new JObject { ["name"]="unrequested",["found"]=false })));
  cases.Add(Mutate(d => ((JArray)d["parts"]!).Add(((JArray)d["parts"]!)[0].DeepClone())));
  cases.Add(Mutate(d => ((JArray)d["parts"]![0]!["stackNodes"]!)[0]!["position"]=new JArray(1,2)));
  cases.Add(Mutate(d => ((JArray)d["parts"]![0]!["stackNodes"]!)[0]!["position"]=new JArray("a","b","c")));
  cases.Add(Mutate(d => ((JObject)((JArray)d["parts"]![0]!["stackNodes"]!)[0]!).Remove("id")));
  cases.Add(Mutate(d => d["parts"]![0]!["attachRules"]="x"));
  cases.Add(Mutate(d => ((JArray)d["parts"]!).RemoveAt(1)));
  cases.Add(Mutate(d => d["parts"]![0]!["constructionSupport"]="surely"));
  cases.Add(Mutate(d => d["parts"]![0]!["category"]="weird"));
  cases.Add(Mutate(d => ((JArray)d["parts"]!)[0]=new JObject { ["name"]="mk1pod.v2",["found"]=false }));
  cases.Add(null);
  foreach(var data in cases)
  {
   var both=Both(data!,names);
   Assert.AreEqual(both.HostOk,both.BridgeOk,"accept or refuse differs: "+data);
   if(!both.HostOk) Assert.AreEqual(both.HostError,both.BridgeError,"error differs: "+data);
   else
   {
    CollectionAssert.AreEquivalent(both.Host.Missing.ToArray(),both.Bridge.Missing.ToArray());
    foreach(var name in both.Host.Found.Keys) Assert.AreEqual(JsonConvert.SerializeObject(both.Host.Found[name].Part),JsonConvert.SerializeObject(both.Bridge.Found[name].Part),name);
   }
  }
 }

 [TestMethod] public async Task HostCraftPlanAndBridgeApplyPlannerProduceTheSamePlanHash()
 {
  var reader=new Reader();
  var fetcher=new MapperFetcher(reader);
  var hostReply=JObject.Parse(await CraftPlanService.PlanAsync(Two,fetcher,CancellationToken.None));
  Assert.AreEqual(true,(bool)hostReply["Data"]!["ok"]!,hostReply.ToString());
  var bridge=ApplyPlanner.Prepare(Two,reader,new EditorHeader("1.12.5","mods:probe-1"),new EditorUi("Probe","","Squad/Flags/default"),()=>1,"VAB");
  Assert.IsTrue(bridge.Ok,bridge.Reason+bridge.Detail);
  Assert.AreEqual((string?)hostReply["Data"]!["planHash"],bridge.PlanHash,"the planHash a model passes as expectedPlanHash is what the bridge recomputes");
  Assert.AreEqual((string?)hostReply["Data"]!["catalogHash"],bridge.CatalogHash);
  Assert.AreEqual((string?)hostReply["Data"]!["topology"],bridge.Plan!.Topology);
  // A locked part is refused on both sides with the same code.
  var locked=Two.Replace("liquidEngine.v2","locked");
  var hostLocked=JObject.Parse(await CraftPlanService.PlanAsync(locked,fetcher,CancellationToken.None));
  var bridgeLocked=ApplyPlanner.Prepare(locked,reader,new EditorHeader("1.12.5","mods:probe-1"),new EditorUi("Probe","","f"),()=>1,"VAB");
  Assert.IsFalse(bridgeLocked.Ok);
  CollectionAssert.AreEquivalent(((JArray)hostLocked["Data"]!["issues"]!).Select(i => (string)i["code"]!).Distinct().ToArray(),bridgeLocked.Issues.Select(i => i.Code).Distinct().ToArray());
 }

 private sealed class MapperFetcher(Reader reader) : ICatalogFetcher
 {
  public Task<string> FetchAsync(IReadOnlyList<string> partNames,string? expectedWorldEpoch,CancellationToken cancellationToken)
   => Task.FromResult(JsonConvert.SerializeObject(new BridgeResponse { Status="completed",WorldEpoch="epoch-1",Revision=1,Data=ConstructionCatalogMapper.Build(partNames.ToList(),reader,ConstructionSupportPolicy.Default) }));
 }
}
