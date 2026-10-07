using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace KspControl.Host;
// Internal foundation only: no MCP tool can create grants or dispatch mutations yet.
public sealed record MissionGrant(string Id,string WorldEpoch,DateTimeOffset ExpiresAt,decimal SpendingLimit,string[] Operations,string[] Entities);
public sealed record ControlLease(string Id,string Owner,string WorldEpoch,DateTimeOffset ExpiresAt);
public sealed record Job(string RequestId,string Fingerprint,string GrantId,string LeaseId,string WorldEpoch,string Operation,string EntityId,decimal ReservedCost,string Status,string Reason);
public sealed class ControlJournal : IDisposable
{
 private sealed class State { public Dictionary<string,Job> Jobs {get;set;}=new(); public decimal Charged {get;set;} }
 private bool faulted; private readonly object gate=new(); private readonly string path; private readonly FileStream exclusive; private State state;
 private readonly Dictionary<string,MissionGrant> grants=new(StringComparer.Ordinal); private ControlLease? lease;
 public ControlJournal(string directory)
 {
  Directory.CreateDirectory(directory); path=Path.Combine(directory,"jobs.json");
  exclusive=new FileStream(Path.Combine(directory,"host.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
  try {
   state=File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InvalidDataException("invalid_journal") : new State();
   // Never replay an in-flight effect after restart, including an accepted request.
   foreach(var item in state.Jobs.Values.ToArray()) if(item.Status is "accepted" or "running") state.Jobs[item.RequestId]=item with { Status="indeterminate",Reason="host_restart_reconcile_required" };
   Save();
  } catch { exclusive.Dispose(); throw; }
 }
 public void ProvisionGrant(MissionGrant grant)
 {
  if(string.IsNullOrWhiteSpace(grant.Id)||string.IsNullOrWhiteSpace(grant.WorldEpoch)||grant.SpendingLimit<0||grant.Operations==null||grant.Entities==null) throw new ArgumentException("invalid_grant");
  lock(gate) { if(grants.ContainsKey(grant.Id)) throw new InvalidOperationException("grant_already_provisioned"); grants.Add(grant.Id,grant with { Operations=grant.Operations.ToArray(),Entities=grant.Entities.ToArray() }); }
 }
 public ControlLease Acquire(string owner,string world,DateTimeOffset now,TimeSpan duration)
 {
  if(string.IsNullOrWhiteSpace(owner)||string.IsNullOrWhiteSpace(world)||duration<=TimeSpan.Zero||duration>TimeSpan.FromMinutes(5)) throw new ArgumentException("invalid_lease");
  lock(gate) { if(lease!=null&&lease.ExpiresAt>now) throw new InvalidOperationException("control_busy"); return lease=new ControlLease(Guid.NewGuid().ToString("N"),owner,world,now+duration); }
 }
 public void Revoke() { lock(gate) lease=null; }
 public void RevokeGrant(string id) { lock(gate) grants.Remove(id); }
 public Job Admit(string requestId,string grantId,string leaseId,string world,string operation,string entity,string canonicalArguments,decimal maximumCost,DateTimeOffset now)
 {
  if(string.IsNullOrWhiteSpace(requestId)||requestId.Length>128||canonicalArguments.Length>1048576||maximumCost<0) throw new ArgumentException("invalid_request");
  var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {grantId,leaseId,world,operation,entity,canonicalArguments,maximumCost}))));
  lock(gate) {
   if(faulted) throw new IOException("journal_unavailable");
   if(state.Jobs.TryGetValue(requestId,out var previous)) { if(previous.Fingerprint!=fingerprint) throw new InvalidOperationException("request_id_conflict"); return previous; }
   var grant=Authorize(grantId,leaseId,world,operation,entity,now);
   // Gross spend is never replenished by income. Indeterminate reservations remain held.
   decimal reserved=state.Jobs.Values.Where(j=>j.Status is "accepted" or "running" or "indeterminate").Sum(j=>j.ReservedCost);
   if(state.Charged+reserved+maximumCost>grant.SpendingLimit) throw new InvalidOperationException("budget_exceeded");
   var job=new Job(requestId,fingerprint,grantId,leaseId,world,operation,entity,maximumCost,"accepted",""); state.Jobs.Add(requestId,job); Save(); return job;
  }
 }
 public Job Begin(string id,DateTimeOffset now)
 {
  lock(gate) { var job=Get(id); if(job.Status!="accepted") throw new InvalidOperationException("not_dispatchable"); Authorize(job.GrantId,job.LeaseId,job.WorldEpoch,job.Operation,job.EntityId,now); return Set(job with { Status="running" }); }
 }
 public Job Complete(string id,decimal actualCost)
 {
  lock(gate) { var job=Get(id); if(job.Status!="running"||actualCost<0||actualCost>job.ReservedCost) throw new InvalidOperationException("invalid_completion"); state.Charged+=actualCost; return Set(job with { Status="completed",Reason="observed" }); }
 }
 public Job CancelBeforeDispatch(string id)
 {
  lock(gate) { var job=Get(id); if(job.Status!="accepted") throw new InvalidOperationException("reconciliation_required"); return Set(job with {Status="cancelled",Reason="not_dispatched"}); }
 }
 public Job MarkIndeterminate(string id,string reason)
 {
  lock(gate) { var job=Get(id); if(job.Status!="running") throw new InvalidOperationException("not_running"); return Set(job with {Status="indeterminate",Reason=reason}); }
 }
 public Job Get(string id) { lock(gate) { if(faulted) throw new IOException("journal_unavailable"); return state.Jobs.TryGetValue(id,out var job)?job:throw new KeyNotFoundException("unknown_job"); } }
 private MissionGrant Authorize(string grantId,string leaseId,string world,string operation,string entity,DateTimeOffset now)
 {
  if(lease==null||lease.Id!=leaseId||lease.WorldEpoch!=world||lease.ExpiresAt<=now) throw new InvalidOperationException("lease_revoked_or_expired");
  if(!grants.TryGetValue(grantId,out var grant)||grant.WorldEpoch!=world||grant.ExpiresAt<=now||!grant.Operations.Contains(operation,StringComparer.Ordinal)||!grant.Entities.Contains(entity,StringComparer.Ordinal)) throw new InvalidOperationException("grant_denied");
  return grant;
 }
 private Job Set(Job job) { state.Jobs[job.RequestId]=job; Save(); return job; }
 private void Save()
 {
  try { string temporary=path+".tmp"; var bytes=JsonSerializer.SerializeToUtf8Bytes(state);
  using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
  File.Move(temporary,path,true); } catch { faulted=true; throw; }
 }
 public void Dispose() => exclusive.Dispose();
}

