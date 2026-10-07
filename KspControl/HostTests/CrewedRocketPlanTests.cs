using KspControl.Bridge;
using KspControl.Contracts;
using KspControl.Host;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.HostTests;

/// <summary>
/// craft_plan end to end through the REAL bridge mapper, fed what the live modpack reports: B9/CryoTanks stock tanks with no prefab resources (KSP category FuelTank),
/// stock attach rules (attachRules cfg values) and stock node positions, the production construction-support table.
/// </summary>
[TestClass]
public class CrewedRocketPlanTests
{
 /// <summary>The crewed two-stage rocket for the P3 flight, as the craft_plan graph argument.</summary>
 public const string CrewedTwoStageGraph = """
 {"name":"Crewed Orbiter","facility":"VAB","root":"pod","parts":[
  {"id":"pod","part":"mk1pod.v2"},
  {"id":"chute","part":"parachuteSingle","parent":"pod","parentNode":"top","node":"bottom"},
  {"id":"shield","part":"HeatShield1","parent":"pod","parentNode":"bottom","node":"top"},
  {"id":"dec1","part":"Decoupler.1","parent":"shield","parentNode":"direct","node":"top"},
  {"id":"upperTankA","part":"fuelTank","parent":"dec1","parentNode":"bottom","node":"top"},
  {"id":"upperTankB","part":"fuelTankSmall","parent":"upperTankA","parentNode":"bottom","node":"top"},
  {"id":"terrier","part":"liquidEngine3.v2","parent":"upperTankB","parentNode":"bottom","node":"top"},
  {"id":"dec2","part":"Decoupler.1","parent":"terrier","parentNode":"bottom","node":"top"},
  {"id":"lowerTankA","part":"fuelTank.long","parent":"dec2","parentNode":"bottom","node":"top"},
  {"id":"lowerTankB","part":"fuelTank.long","parent":"lowerTankA","parentNode":"bottom","node":"top"},
  {"id":"swivel","part":"liquidEngine2.v2","parent":"lowerTankB","parentNode":"bottom","node":"top"}
 ]}
 """;

 static readonly string[] B9Tank={ "ModuleCargoPart","TweakScale","ModuleB9PartSwitch","ModuleCryoTank","ModuleFreeIva","ModuleB9PartSwitch","ModuleB9PartInfo","AttachedOnEditor" };

 sealed class LiveShapedReader : ICatalogPartReader
 {
  public bool ResearchAvailable => false; public bool SandboxMode => true;
  static CatalogNodeSource N(string id,double y,double oy) => new() { Id=id,Position=new[] { 0,y,0 },Orientation=new[] { 0,oy,0 },Size=1 };
  /// <summary>attachRules = stack, srfAttach, allowStack, allowSrfAttach, allowCollision.</summary>
  static CatalogAttachRulesSource R(int stack,int srf,int allowStack,int allowSrf) => new() { Stack=stack==1,Srf=srf==1,AllowStack=allowStack==1,AllowSrf=allowSrf==1 };
  static CatalogPartSource P(string name,string ksp,string[] modules,string[] resources,CatalogAttachRulesSource rules,params CatalogNodeSource[] nodes) =>
   new() { Name=name,KspCategory=ksp,Buildable=true,ModuleNames=modules,ResourceNames=resources,AttachRules=rules,StackNodes=nodes };
  public CatalogPartSource? Read(string name) => name switch
  {
   "mk1pod.v2" => P(name,"Pods",new[] { "ModuleCommand","ModuleReactionWheel","ModuleB9PartSwitch" },new[] { "ElectricCharge","MonoPropellant" },R(1,0,1,1),N("bottom",-0.4050379,-1),N("top",0.6423756,1)),
   "parachuteSingle" => P(name,"Utility",new[] { "ModuleParachute","ModuleDragModifier" },new string[0],R(1,0,0,1),N("bottom",-0.0120649,-1)),
   "HeatShield1" => P(name,"Thermal",new[] { "ModuleJettison","ModuleAblator","ModuleDecouple" },new[] { "Ablator" },R(1,0,1,0),N("direct",0,-1),N("bottom",-0.17,-1),N("top",0.022,1)),
   "Decoupler.1" => P(name,"Coupling",new[] { "ModuleDecouple","ModuleToggleCrossfeed" },new string[0],R(1,0,1,1),N("top",0.05,1),N("bottom",-0.05,-1)),
   "fuelTankSmall" => P(name,"FuelTank",B9Tank,new string[0],R(1,1,1,1),N("top",0.55525,1),N("bottom",-0.55525,-1)),
   "fuelTank" => P(name,"FuelTank",B9Tank,new string[0],R(1,1,1,1),N("top",0.981725,1),N("bottom",-0.9125,-1)),
   "fuelTank.long" => P(name,"FuelTank",B9Tank,new string[0],R(1,1,1,1),N("top",1.875,1),N("bottom",-1.8875,-1)),
   "liquidEngine3.v2" => P(name,"Engine",new[] { "ModuleEnginesFX","ModuleGimbal","ModuleJettison" },new string[0],R(1,0,1,0),N("top",0,1),N("bottom",-0.83,-1)),
   "liquidEngine2.v2" => P(name,"Engine",new[] { "ModuleEnginesFX","ModuleJettison","ModuleGimbal" },new string[0],R(1,0,1,0),N("top",0,1),N("bottom",-1.63,-1)),
   "liquidEngine.v2" => P(name,"Engine",new[] { "ModuleEnginesFX","ModuleJettison" },new string[0],R(1,0,1,0),N("top",0,1),N("bottom",-1.63,-1)),
   _ => null
  };
 }

 sealed class Fetcher : ICatalogFetcher
 {
  public Task<string> FetchAsync(IReadOnlyList<string> partNames,string? expectedWorldEpoch,CancellationToken cancellationToken)
  {
   var data=ConstructionCatalogMapper.Build(partNames.ToList(),new LiveShapedReader(),ConstructionSupportPolicy.Default);
   return Task.FromResult(JsonConvert.SerializeObject(new BridgeResponse { Status="completed",WorldEpoch="live-epoch",Data=data }));
  }
 }

 static async Task<JObject> Plan(string graph) => (JObject)JObject.Parse(await CraftPlanService.PlanAsync(graph,new Fetcher(),CancellationToken.None))["Data"]!;

 [TestMethod] public async Task TheCrewedTwoStageRocketPlansAgainstTheLiveShapedCatalog()
 {
  var data=await Plan(CrewedTwoStageGraph);
  Assert.IsTrue((bool)data["ok"]!,data["issues"]!.ToString());
  Assert.AreEqual("T2",(string?)data["topology"]);
  var parts=((JArray)data["parts"]!).ToDictionary(p => (string)p["id"]!,p => (JObject)p);
  Assert.AreEqual("tank",(string?)parts["upperTankB"]["category"],"a B9-switched FL-T200 is a tank");
  Assert.AreEqual("heatshield",(string?)parts["shield"]["category"]);
  int Istg(string id) => (int)parts[id]["staging"]!["istg"]!;
  Assert.AreEqual(0,Istg("chute")); Assert.AreEqual(1,Istg("dec1")); Assert.AreEqual(2,Istg("terrier")); Assert.AreEqual(3,Istg("dec2")); Assert.AreEqual(4,Istg("swivel"));
  Assert.AreEqual(-1,Istg("shield")); Assert.AreEqual(-1,(int)parts["shield"]["staging"]!["sidx"]!);
  // Construction support is reported, never refused.
  CollectionAssert.Contains(((JArray)data["catalog"]!["unverified"]!).Select(t => (string)t!).ToArray(),"liquidEngine3.v2");
 }

 [TestMethod] public async Task TheVerifiedOnlyStackFromTheLiveReproPlans()
 {
  // mk1pod.v2 -> Decoupler.1 -> fuelTankSmall x3 -> liquidEngine.v2, and the same without the decoupler (both refused live before the category fix).
  var withDecoupler="""
  {"name":"Repro","facility":"VAB","root":"pod","parts":[{"id":"pod","part":"mk1pod.v2"},
   {"id":"d","part":"Decoupler.1","parent":"pod","parentNode":"bottom","node":"top"},
   {"id":"t1","part":"fuelTankSmall","parent":"d","parentNode":"bottom","node":"top"},{"id":"t2","part":"fuelTankSmall","parent":"t1","parentNode":"bottom","node":"top"},
   {"id":"t3","part":"fuelTankSmall","parent":"t2","parentNode":"bottom","node":"top"},{"id":"e","part":"liquidEngine.v2","parent":"t3","parentNode":"bottom","node":"top"}]}
  """;
  var a=await Plan(withDecoupler);
  Assert.IsTrue((bool)a["ok"]!,a["issues"]!.ToString()); Assert.AreEqual("T2",(string?)a["topology"]);
  var without=withDecoupler.Replace("""{"id":"d","part":"Decoupler.1","parent":"pod","parentNode":"bottom","node":"top"},""","").Replace("\"parent\":\"d\"","\"parent\":\"pod\"");
  var b=await Plan(without);
  Assert.IsTrue((bool)b["ok"]!,b["issues"]!.ToString()); Assert.AreEqual("T1",(string?)b["topology"]);
 }
}
