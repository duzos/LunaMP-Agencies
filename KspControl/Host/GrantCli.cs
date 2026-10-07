using System.Security.Cryptography;
using System.Text;
using KspControl.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;
/// <summary>
/// <c>KspControl.Host.exe grant issue|revoke|rearm|show</c>. A human-run CLI branch of the host executable; it is never
/// registered as an MCP tool and the MCP path never calls it. Every write is a new envelope with generation + 1.
/// The key is created on first issue, never printed, and never read anywhere else in the host.
/// </summary>
public static class GrantCli
{
 public const int Ok=0,Usage=1,Failed=2;
 public static readonly string[] DefaultOperations={ "editor.replace_craft","editor.restore_snapshot","craft.write" };
 private const int DefaultHours=8,MaxHours=168;
 private static readonly HashSet<string> Flags=new(StringComparer.Ordinal) { "--trust-dir","--grant-file","--key-file","--ksp-root","--save","--agency","--ops","--facilities","--policy","--max-parts","--hours" };

 public static int Run(string[] args,TextWriter output,TextWriter error,Func<string,string?>? environment=null,Func<DateTime>? utcNow=null)
 {
  environment??=Environment.GetEnvironmentVariable; utcNow??=()=>DateTime.UtcNow;
  if(args.Length==0 || args[0] is not ("issue" or "revoke" or "rearm" or "show")) { error.WriteLine("usage: KspControl.Host grant issue|revoke|rearm|show [--trust-dir D] [--grant-file F] [--key-file F] (issue: --ksp-root R --save S [--agency GUID] [--ops a,b] [--facilities VAB] [--policy refuse|snapshot_then_replace] [--max-parts N] [--hours H]) (rearm: [--hours H])"); return Usage; }
  var options=new Dictionary<string,string>(StringComparer.Ordinal);
  for(int i=1;i<args.Length;i+=2)
  {
   if(!Flags.Contains(args[i]) || i+1>=args.Length || options.ContainsKey(args[i])) { error.WriteLine("invalid or repeated option: "+args[i]); return Usage; }
   options[args[i]]=args[i+1];
  }
  try
  {
   string trust=options.GetValueOrDefault("--trust-dir") ?? environment("KSP_CONTROL_TRUST_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KspControl","trust");
   string grantPath=options.GetValueOrDefault("--grant-file") ?? Path.Combine(trust,"grant.json");
   string keyPath=options.GetValueOrDefault("--key-file") ?? Path.Combine(trust,"grant.key");
   var now=utcNow();
   return args[0] switch
   {
    "issue" => Issue(options,grantPath,keyPath,now,output,error),
    "revoke" => Rewrite(options,grantPath,keyPath,now,output,error,revoke:true),
    "rearm" => Rewrite(options,grantPath,keyPath,now,output,error,revoke:false),
    _ => Show(grantPath,keyPath,output,error)
   };
  }
  catch(Exception failure) when(failure is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
  { error.WriteLine("grant command failed: "+failure.GetType().Name); return Failed; }
 }

 private static int Issue(Dictionary<string,string> o,string grantPath,string keyPath,DateTime now,TextWriter output,TextWriter error)
 {
  if(!o.TryGetValue("--ksp-root",out var root)||!o.TryGetValue("--save",out var save)) { error.WriteLine("issue requires --ksp-root and --save"); return Usage; }
  if(!TryHours(o,out var hours)) { error.WriteLine($"--hours must be 1..{MaxHours}"); return Usage; }
  string agency=GrantBindingKey.OfflinePrefix+save;
  if(o.TryGetValue("--agency",out var supplied)) { if(!Guid.TryParse(supplied,out var guid)||guid==Guid.Empty) { error.WriteLine("--agency must be a non-empty GUID"); return Usage; } agency=guid.ToString("D"); }
  int maxParts=250; if(o.TryGetValue("--max-parts",out var parts)&&!int.TryParse(parts,out maxParts)) { error.WriteLine("--max-parts must be an integer"); return Usage; }
  var payload=new GrantPayload
  {
   GrantId=Guid.NewGuid().ToString("N"),Generation=NextGeneration(grantPath,keyPath),IssuedUtc=GrantPayload.FormatUtc(now),ExpiresUtc=GrantPayload.FormatUtc(now.AddHours(hours)),
   Binding=new GrantBindingInfo { InstallId=GrantBindingKey.InstallId(root),SaveFolder=save,Agency=agency },
   Operations=Split(o.GetValueOrDefault("--ops"),DefaultOperations),Facilities=Split(o.GetValueOrDefault("--facilities"),new[]{"VAB"}),
   UnsavedCraftPolicy=o.GetValueOrDefault("--policy") ?? "refuse",MaxParts=maxParts,SpendLimitFunds=0,Revoked=false
  };
  return Write(payload,grantPath,keyPath,create:true,output,error,"issued");
 }

 private static int Rewrite(Dictionary<string,string> o,string grantPath,string keyPath,DateTime now,TextWriter output,TextWriter error,bool revoke)
 {
  var current=ReadVerified(grantPath,keyPath,error); if(current==null) return Failed;
  if(!TryHours(o,out var hours)) { error.WriteLine($"--hours must be 1..{MaxHours}"); return Usage; }
  var next=current;
  next.Generation=checked(current.Generation+1); next.IssuedUtc=GrantPayload.FormatUtc(now);
  if(revoke)
  {
   next.Revoked=true; var keep=current.ExpiresAt; var floor=now.AddHours(1);
   next.ExpiresUtc=GrantPayload.FormatUtc(keep>floor?keep:floor);
  }
  else { next.Revoked=false; next.ExpiresUtc=GrantPayload.FormatUtc(now.AddHours(hours)); }
  return Write(next,grantPath,keyPath,create:false,output,error,revoke?"revoked":"rearmed");
 }

 private static int Show(string grantPath,string keyPath,TextWriter output,TextWriter error)
 {
  if(!File.Exists(grantPath)) { output.WriteLine(JsonConvert.SerializeObject(new { present=false,state="missing" })); return Ok; }
  if(!File.Exists(keyPath)) { output.WriteLine(JsonConvert.SerializeObject(new { present=true,state="key_missing" })); return Ok; }
  var check=GrantCodec.Verify(File.ReadAllText(grantPath,Encoding.UTF8),File.ReadAllBytes(keyPath));
  if(!check.Ok) { output.WriteLine(JsonConvert.SerializeObject(new { present=true,state=check.State,detail=check.Detail })); return Ok; }
  var p=check.Payload;
  // Deliberately omits the payload bytes, the MAC and the key.
  output.WriteLine(JsonConvert.SerializeObject(new { present=true,state="signature_valid",grantId=p.GrantId,generation=p.Generation,issuedUtc=p.IssuedUtc,expiresUtc=p.ExpiresUtc,revoked=p.Revoked,binding=p.Binding,operations=p.Operations,facilities=p.Facilities,unsavedCraftPolicy=p.UnsavedCraftPolicy,maxParts=p.MaxParts,spendLimitFunds=p.SpendLimitFunds },Formatting.None));
  return Ok;
 }

 private static int Write(GrantPayload payload,string grantPath,string keyPath,bool create,TextWriter output,TextWriter error,string verb)
 {
  var problem=GrantCodec.Validate(payload); if(problem!=null) { error.WriteLine("grant refused: "+problem); return Usage; }
  var key=create ? LoadOrCreateKey(keyPath) : LoadKey(keyPath);
  AtomicWrite(grantPath,GrantCodec.Encode(payload,key));
  output.WriteLine($"{verb} grantId={payload.GrantId} generation={payload.Generation} expiresUtc={payload.ExpiresUtc}");
  return Ok;
 }

 private static GrantPayload? ReadVerified(string grantPath,string keyPath,TextWriter error)
 {
  if(!File.Exists(grantPath)||!File.Exists(keyPath)) { error.WriteLine("no existing grant or key; use issue"); return null; }
  var check=GrantCodec.Verify(File.ReadAllText(grantPath,Encoding.UTF8),File.ReadAllBytes(keyPath));
  if(!check.Ok) { error.WriteLine("existing grant is not usable ("+check.State+"); use issue"); return null; }
  return check.Payload;
 }

 /// <summary>Generations only ever increase, so a stale file can be detected. Reads the old one without trusting it.</summary>
 private static long NextGeneration(string grantPath,string keyPath)
 {
  try
  {
   if(!File.Exists(grantPath)) return 1;
   var envelope=JObject.Parse(File.ReadAllText(grantPath,Encoding.UTF8));
   var bytes=Convert.FromBase64String((string)envelope["payload"]!);
   var generation=(long?)JObject.Parse(Encoding.UTF8.GetString(bytes))["generation"] ?? 0;
   return checked(Math.Max(generation,0)+1);
  }
  catch(Exception) { return 1; }
 }

 private static bool TryHours(Dictionary<string,string> o,out int hours)
 {
  hours=DefaultHours;
  return !o.TryGetValue("--hours",out var text) || (int.TryParse(text,out hours) && hours>=1 && hours<=MaxHours);
 }
 private static string[] Split(string? text,string[] fallback) =>
  string.IsNullOrWhiteSpace(text) ? fallback : text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);

 private static byte[] LoadKey(string keyPath)
 {
  var key=File.ReadAllBytes(keyPath);
  if(key.Length!=GrantCodec.KeyLength) throw new ArgumentException("invalid_key_length");
  return key;
 }
 private static byte[] LoadOrCreateKey(string keyPath)
 {
  if(File.Exists(keyPath)) return LoadKey(keyPath);
  var directory=Path.GetDirectoryName(Path.GetFullPath(keyPath)); if(!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
  var key=RandomNumberGenerator.GetBytes(GrantCodec.KeyLength);
  try { using var stream=new FileStream(keyPath,FileMode.CreateNew,FileAccess.Write,FileShare.None); stream.Write(key); stream.Flush(true); return key; }
  catch(IOException) when(File.Exists(keyPath)) { return LoadKey(keyPath); }
 }
 private static void AtomicWrite(string path,string text)
 {
  var directory=Path.GetDirectoryName(Path.GetFullPath(path)); if(!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
  var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(new UTF8Encoding(false).GetBytes(text)); stream.Flush(true); }
  File.Move(temporary,path,true);
 }
}
