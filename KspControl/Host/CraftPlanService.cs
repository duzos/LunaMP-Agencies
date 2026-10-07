using System.Globalization;
using KspControl.Contracts;
using KspControl.EditorModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;

/// <summary>Fetches construction catalog data from the bridge, one call per chunk of at most <see cref="ConstructionLimits.MaxCatalogParts"/> names.</summary>
public interface ICatalogFetcher
{
 /// <summary>Returns the raw serialized <see cref="BridgeResponse"/> for one chunk.</summary>
 Task<string> FetchAsync(IReadOnlyList<string> partNames,CancellationToken cancellationToken);
}

public sealed class BridgeCatalogFetcher(BridgeClient bridge) : ICatalogFetcher
{
 public Task<string> FetchAsync(IReadOnlyList<string> partNames,CancellationToken cancellationToken) =>
  bridge.ReadAsync(ConstructionOperations.Catalog,new JObject { ["partNames"]=new JArray(partNames) },cancellationToken);
}

/// <summary>Per-part catalog data as served by the bridge, beyond what the planner needs.</summary>
public sealed record CatalogEntry(ConstructionPart Part,bool StockAllowed,string Basis);

public sealed class CatalogFetch
{
 public Dictionary<string,CatalogEntry> Found { get; } = new(StringComparer.Ordinal);
 public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);
 public string? WorldEpoch { get; set; }
 public string? ResearchAndDevelopment { get; set; }
}

/// <summary>
/// craft_plan core. The planner runs here, on the host, on catalog data fetched from the bridge; the bridge only serves catalog data.
/// Everything below is pure except the injected fetcher, so plans are deterministic and testable against a fake catalog.
/// </summary>
public static class CraftPlanService
{
 private static readonly JsonSerializerSettings GraphSettings=new()
 {
  MissingMemberHandling=MissingMemberHandling.Error, MaxDepth=16, DateParseHandling=DateParseHandling.None,
  NullValueHandling=NullValueHandling.Include, Culture=CultureInfo.InvariantCulture, TypeNameHandling=TypeNameHandling.None
 };

 public static string Invalid(string detail) => JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode=ControlReasons.InvalidArgument,Data=new JObject { ["detail"]=detail } });

 /// <summary>Argument-level parse. Returns null and an invalid_argument detail when the JSON is unusable; shape and catalog problems are plan issues, not argument errors.</summary>
 public static GraphDto? ParseGraph(string? json,out string? error)
 {
  error=null;
  if(string.IsNullOrWhiteSpace(json)) { error="graph must be a non-empty JSON object"; return null; }
  if(System.Text.Encoding.UTF8.GetByteCount(json)>ConstructionLimits.MaxGraphBytes) { error=$"graph must be at most {ConstructionLimits.MaxGraphBytes} bytes of JSON"; return null; }
  GraphDto? graph;
  try
  {
   if(JToken.Parse(json,new JsonLoadSettings { DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error }) is not JObject) { error="graph must be a JSON object"; return null; }
   graph=JsonConvert.DeserializeObject<GraphDto>(json,GraphSettings);
  }
  catch(JsonException e) { error="graph is not valid graph JSON: "+Brief(e.Message); return null; }
  if(graph==null) { error="graph must be a JSON object"; return null; }
  if(graph.Parts==null || graph.Parts.Count<1 || graph.Parts.Count>ConstructionLimits.MaxGraphParts) { error=$"graph.parts must contain 1..{ConstructionLimits.MaxGraphParts} parts"; return null; }
  if(graph.Parts.Any(p=>p==null)) { error="graph.parts must not contain null entries"; return null; }
  return graph;
 }

 private static string Brief(string message) { var line=message.Split('\n')[0].Trim(); return line.Length>200 ? line[..200] : line; }

 public static async Task<string> PlanAsync(string? graphJson,ICatalogFetcher fetcher,CancellationToken cancellationToken)
 {
  var graph=ParseGraph(graphJson,out var error);
  if(graph==null) return Invalid(error!);
  var shape=GraphDtoValidator.Validate(graph);
  if(shape.Count!=0) return Render(graph,shape,null,null,new CatalogFetch(),null);
  var names=new List<string>(); var seen=new HashSet<string>(StringComparer.Ordinal);
  foreach(var p in graph.Parts) if(seen.Add(p.Part)) names.Add(p.Part);
  var fetch=new CatalogFetch();
  for(int i=0;i<names.Count;i+=ConstructionLimits.MaxCatalogParts)
  {
   var chunk=names.GetRange(i,Math.Min(ConstructionLimits.MaxCatalogParts,names.Count-i));
   var raw=await fetcher.FetchAsync(chunk,cancellationToken);
   BridgeResponse? reply;
   try { reply=JsonConvert.DeserializeObject<BridgeResponse>(raw); } catch(JsonException) { reply=null; }
   if(reply==null) return JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode="protocol_invalid" });
   if(reply.Status!="completed") return JsonConvert.SerializeObject(reply);
   if(!CatalogWire.TryParse(reply.Data,chunk,fetch,out var wireError))
    return JsonConvert.SerializeObject(new BridgeResponse { Status="failed",ReasonCode="protocol_invalid",Data=new JObject { ["detail"]=wireError } });
   fetch.WorldEpoch??=reply.WorldEpoch;
  }
  var catalog=new ConstructionCatalog();
  foreach(var entry in fetch.Found.Values) catalog.Add(entry.Part);
  var plan=CraftPlanner.Plan(graph,catalog);
  var issues=new List<PlanIssue>(plan.Issues);
  foreach(var p in graph.Parts) if(fetch.Found.TryGetValue(p.Part,out var e) && !e.StockAllowed) issues.Add(new PlanIssue("part_locked",p.Id,"not unlocked or purchased for this save ("+e.Basis+")"));
  return Render(graph,issues,plan,catalog,fetch,names);
 }

 /// <summary>Builds the tool reply from a finished plan. Exposed for tests that supply their own catalog and skip the bridge.</summary>
 public static string Render(GraphDto graph,IReadOnlyList<PlanIssue> issues,PlanResult? plan,ConstructionCatalog? catalog,CatalogFetch fetch,IReadOnlyList<string>? requested)
 {
  bool ok=issues.Count==0 && plan is { Ok: true };
  string? catalogHash=catalog is { Parts.Count: >0 } ? catalog.Hash() : null;
  var data=new JObject
  {
   ["ok"]=ok,
   ["issues"]=new JArray(issues.Select(i => new JObject { ["code"]=i.Code,["partId"]=i.PartId,["reason"]=i.Reason })),
   ["planHash"]=ok ? PlanHasher.Hash(graph,plan!,catalogHash!) : null,
   ["planHashVersion"]=PlanHasher.Version,
   ["catalogHash"]=catalogHash,
   ["topology"]=ok ? plan!.Topology : null,
   ["name"]=graph.Name,
   ["facility"]=graph.Facility,
   ["partCount"]=plan?.Layout?.Parts.Count,
   ["parts"]=plan?.Layout==null ? new JArray() : Parts(plan,ok),
   ["symmetryGroups"]=plan?.Layout==null ? new JArray() : new JArray(plan.Layout.SymmetryGroups.Select(g => new JArray(g))),
   ["catalog"]=new JObject
   {
    ["worldEpoch"]=fetch.WorldEpoch,["researchAndDevelopment"]=fetch.ResearchAndDevelopment,
    ["partsChecked"]=requested==null ? null : new JArray(requested),
    ["unverified"]=new JArray(fetch.Found.Values.Where(e => !e.Part.IsVerified).Select(e => e.Part.Name).OrderBy(n => n,StringComparer.Ordinal)),
    ["missing"]=new JArray(fetch.Missing.OrderBy(n => n,StringComparer.Ordinal))
   },
   ["modVersions"]="not_included",
   ["notes"]=new JArray("Transforms are part-local metres relative to the root part. Surface placements are provisional and marked calibratedAtApply; the apply step measures and recalibrates them (R1-section 6.5).",
    "_modVersions is copied from the live SaveShip header at apply time and is not part of planHash.")
  };
  return JsonConvert.SerializeObject(new BridgeResponse { Status="completed",Data=data });
 }

 private static JArray Parts(PlanResult plan,bool withStaging)
 {
  var result=new JArray();
  foreach(var p in plan.Layout!.Parts)
  {
   var o=new JObject
   {
    ["index"]=p.Index,["id"]=p.Source.Id,["part"]=p.Source.Part,["parent"]=p.ParentIndex<0 ? null : plan.Layout.Parts[p.ParentIndex].Source.Id,["parentIndex"]=p.ParentIndex<0 ? null : p.ParentIndex,
    ["attachment"]=p.Kind.ToString().ToLowerInvariant(),["parentNode"]=p.ParentNodeId,["node"]=p.NodeId,
    ["instance"]=p.InstanceIndex,["instanceCount"]=p.InstanceCount,
    ["position"]=Vec(p.Position),["rotation"]=Quat(p.Rotation),["attPos0"]=Vec(p.AttPos0),["attRot0"]=Quat(p.AttRot0),
    ["category"]=p.Definition.Category,["calibratedAtApply"]=p.Kind==AttachKind.Surface
   };
   if(withStaging && plan.Craft!=null)
   {
    var s=plan.Craft.Parts[p.Index].Staging;
    o["staging"]=new JObject { ["istg"]=s.Istg,["dstg"]=s.Dstg,["sidx"]=s.Sidx,["sqor"]=s.Sqor,["sepI"]=s.SepI,["attm"]=s.Attm };
   }
   result.Add(o);
  }
  return result;
 }
 private static JToken Num(double v) => double.Parse(RotationMath.Number(v),CultureInfo.InvariantCulture);
 private static JArray Vec(Vector v) => new(Num(v.X),Num(v.Y),Num(v.Z));
 private static JArray Quat(Rotation q) => new(Num(q.X),Num(q.Y),Num(q.Z),Num(q.W));
}

/// <summary>Wire to model translation for the bridge's parts.construction_catalog reply.</summary>
public static class CatalogWire
{
 public static bool TryParse(JObject? data,IReadOnlyList<string> requested,CatalogFetch into,out string error)
 {
  error="";
  if(data?["parts"] is not JArray parts) { error="catalog reply has no parts array"; return false; }
  into.ResearchAndDevelopment??=(string?)data["researchAndDevelopment"];
  var wanted=new HashSet<string>(requested,StringComparer.Ordinal);
  try
  {
   foreach(var item in parts)
   {
    if(item is not JObject o || (string?)o["name"] is not { } name || !wanted.Contains(name)) { error="catalog reply contains an unrequested or malformed part"; return false; }
    if(!((bool?)o["found"]??false)) { into.Missing.Add(name); continue; }
    var rules=o["attachRules"] as JObject ?? new JObject();
    var part=new ConstructionPart
    {
     Name=name,Category=Category((string?)o["category"]),Buildable=(bool?)o["buildable"]??false,
     ConstructionSupport=(string?)o["constructionSupport"]=="verified" ? "verified" : "unverified",
     AttachRules=new AttachRulesDefinition
     {
      Stack=(bool?)rules["stack"]??false,Srf=(bool?)rules["srf"]??false,AllowStack=(bool?)rules["allowStack"]??false,
      AllowSrf=(bool?)rules["allowSrf"]??false,AllowCollision=(bool?)rules["allowCollision"]??false,AllowDock=(bool?)rules["allowDock"]??false
     }
    };
    foreach(var n in (o["stackNodes"] as JArray) ?? new JArray())
    {
     if(n is not JObject node || (string?)node["id"] is not { } id) { error="malformed node in "+name; return false; }
     part.StackNodes.Add(new ConstructionNode { Id=id,Position=Vec(node["position"]),Orientation=Vec(node["orientation"]),Size=(int?)node["size"]??0 });
    }
    if(o["surfaceNode"] is JObject srf) part.SurfaceNode=new SurfaceNodeDefinition { Position=Vec(srf["position"]),Orientation=Vec(srf["orientation"]) };
    var allowed=(bool?)o["partsStockAllowed"]??false;
    into.Found[name]=new CatalogEntry(part,allowed,(string?)o["stockAllowedBasis"]??"unknown");
   }
  }
  catch(Exception e) when(e is ArgumentException or InvalidCastException or FormatException or JsonException) { error="malformed catalog data: "+e.GetType().Name; return false; }
  return true;
 }

 private static string Category(string? c) => c switch
 {
  PartCategories.Command or PartCategories.Tank or PartCategories.Engine or PartCategories.Decoupler => c,
  _ => PartCategories.Other
 };

 private static Vector Vec(JToken? token)
 {
  if(token is not JArray a || a.Count!=3) throw new ArgumentException("vector");
  var v=new Vector((double)a[0],(double)a[1],(double)a[2]);
  if(!v.IsFinite) throw new ArgumentException("vector");
  return v;
 }
}
