using Newtonsoft.Json.Linq;
namespace KspControl.Host;
/// <summary>
/// Lazily opens the control journal and mirrors what the bridge reports (grant id and generation, lease id).
/// A second MCP host cannot take the exclusive lock and stays without a journal: <c>locked_by_other_host</c>.
/// Journal trouble never fails a control tool; it is reported in control_status.
/// </summary>
public sealed class JournalAccess : IDisposable
{
 private readonly object gate=new(); private readonly string directory; private ControlJournal? journal; private string state="not_opened";
 public JournalAccess() : this(Environment.GetEnvironmentVariable("KSP_CONTROL_JOURNAL_DIR") is { Length: >0 } configured ? configured : DefaultDirectory()) { }
 /// <summary>%LOCALAPPDATA%\KspControl\journal\installId. The id is the bridge's install hash when KSP_CONTROL_KSP_ROOT names the KSP root; otherwise "default" (the host is not told the root).</summary>
 public static string DefaultDirectory()
 {
  string? root=Environment.GetEnvironmentVariable("KSP_CONTROL_KSP_ROOT"); string id="default";
  if(!string.IsNullOrWhiteSpace(root)) { try { id=KspControl.Contracts.GrantBindingKey.InstallId(root); } catch(Exception) { } }
  return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KspControl","journal",id);
 }
 public JournalAccess(string directory) { this.directory=directory; }
 /// <summary>available, locked_by_other_host or unavailable.</summary>
 public string State { get { TryGet(); lock(gate) return state; } }
 public ControlJournal? TryGet()
 {
  lock(gate)
  {
   if(journal!=null) return journal;
   try { journal=new ControlJournal(directory); state="available"; }
   catch(IOException) { state="locked_by_other_host"; }
   catch(Exception) { state="unavailable"; }
   return journal;
  }
 }
 /// <summary>Mirrors a bridge-granted lease. The bridge is authoritative, so any older mirrored lease is replaced.</summary>
 public void MirrorAcquire(JObject data,JObject? status)
 {
  var j=TryGet(); if(j==null) return;
  try
  {
   var leaseId=(string?)data["leaseId"]; var grantId=(string?)data["grantId"]; var epoch=(string?)data["epoch"];
   if(leaseId==null||grantId==null||epoch==null||data["generation"]==null) return;
   long generation=(long)data["generation"]!; int seconds=(int?)data["expiresInSeconds"]??30;
   var grantInfo=status?["grant"] as JObject;
   var operations=(grantInfo?["operations"] as JArray)?.Select(t=>(string)t!).ToArray() ?? Array.Empty<string>();
   var entities=(grantInfo?["facilities"] as JArray)?.Select(t=>"editor:"+(string)t!).ToArray() ?? Array.Empty<string>();
   DateTimeOffset expires=DateTimeOffset.TryParse((string?)grantInfo?["expiresUtc"],out var parsed) ? parsed : DateTimeOffset.UtcNow.AddMinutes(5);
   try { j.ProvisionGrant(new MissionGrant(grantId,generation,"bridge_reported",expires,0,operations,entities)); } catch(InvalidOperationException) { /* already mirrored in this run, or revoked here */ }
   j.Revoke();
   j.Acquire(leaseId,(string?)data["purpose"]??"unspecified",epoch,DateTimeOffset.UtcNow,TimeSpan.FromSeconds(Math.Clamp(seconds,1,300)));
  } catch(Exception) { /* audit mirror only */ }
 }
 public void MirrorRenew(string leaseId,int seconds)
 { try { TryGet()?.Renew(leaseId,DateTimeOffset.UtcNow,TimeSpan.FromSeconds(Math.Clamp(seconds,1,300))); } catch(Exception) { } }
 public void MirrorEnd(string leaseId)
 { try { var j=TryGet(); if(j?.CurrentLease?.Id==leaseId) j.Revoke(); } catch(Exception) { } }
 public void Dispose() { lock(gate) { journal?.Dispose(); journal=null; } }
}
