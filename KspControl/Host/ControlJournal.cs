using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace KspControl.Host;
// Audit mirror of what the bridge reports. The journal never verifies grants (only the bridge holds the key) and no MCP tool can create one.
// Binding is an opaque label of what the bridge said the grant is bound to; the world epoch lives on the lease, not the grant.
public sealed record MissionGrant(string Id,long Generation,string Binding,DateTimeOffset ExpiresAt,decimal SpendingLimit,string[] Operations,string[] Entities);
public sealed record ControlLease(string Id,string Owner,string WorldEpoch,DateTimeOffset ExpiresAt);
public sealed record Job(string RequestId,string Fingerprint,string GrantId,long GrantGeneration,string LeaseId,string WorldEpoch,string Operation,string EntityId,decimal ReservedCost,string Status,string Reason)
{
 /// <summary>The terminal job envelope exactly as the bridge reported it, so a retried request or job_status answers from the journal without asking the game again.</summary>
 public string Result { get; init; } = "";
}
public sealed class ControlJournal : IDisposable
{
 private sealed class State { public Dictionary<string,Job> Jobs {get;set;}=new(); public decimal Charged {get;set;} public Dictionary<string,decimal> ChargedByGrant {get;set;}=new(StringComparer.Ordinal); public HashSet<string> GrantKeys {get;set;}=new(StringComparer.Ordinal); public HashSet<string> RevokedGrantKeys {get;set;}=new(StringComparer.Ordinal); }
 private bool faulted; private readonly object gate=new(); private readonly string path; private readonly FileStream exclusive; private State state;
 private readonly Dictionary<string,MissionGrant> grants=new(StringComparer.Ordinal); private static string Key(string id,long generation)=>id+"#"+generation; private ControlLease? lease;
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
  if(string.IsNullOrWhiteSpace(grant.Id)||grant.Generation<=0||string.IsNullOrWhiteSpace(grant.Binding)||grant.SpendingLimit<0||grant.Operations==null||grant.Entities==null) throw new ArgumentException("invalid_grant");
  // (id, generation) is unique: a revoked pair is never revived and a pair already held cannot be added twice.
  // A pair recorded by an earlier host run may be re-established, because the bridge (not this journal) is authoritative.
  string key=Key(grant.Id,grant.Generation);
  lock(gate) {
   if(state.RevokedGrantKeys.Contains(key)||grants.Values.Any(g=>Key(g.Id,g.Generation)==key)) throw new InvalidOperationException("grant_already_provisioned");
   state.GrantKeys.Add(key); Save(); grants[grant.Id]=grant with { Operations=grant.Operations.ToArray(),Entities=grant.Entities.ToArray() };
  }
 }
 // The lease id is the bridge's id, mirrored here; the journal no longer invents its own.
 public ControlLease Acquire(string leaseId,string owner,string world,DateTimeOffset now,TimeSpan duration)
 {
  if(string.IsNullOrWhiteSpace(leaseId)||string.IsNullOrWhiteSpace(owner)||string.IsNullOrWhiteSpace(world)||duration<=TimeSpan.Zero||duration>TimeSpan.FromMinutes(5)) throw new ArgumentException("invalid_lease");
  lock(gate) { if(lease!=null&&lease.ExpiresAt>now) throw new InvalidOperationException("control_busy"); return lease=new ControlLease(leaseId,owner,world,now+duration); }
 }
 public ControlLease? CurrentLease { get { lock(gate) return lease; } }
 public ControlLease Renew(string leaseId,DateTimeOffset now,TimeSpan duration)
 {
  if(duration<=TimeSpan.Zero||duration>TimeSpan.FromMinutes(5)) throw new ArgumentException("invalid_lease");
  lock(gate) { if(lease==null||lease.Id!=leaseId||lease.ExpiresAt<=now) throw new InvalidOperationException("lease_revoked_or_expired"); return lease=lease with { ExpiresAt=now+duration }; }
 }
 public void Revoke() { lock(gate) lease=null; }
 public void RevokeGrant(string id) { lock(gate) { if(grants.TryGetValue(id,out var held)) { state.RevokedGrantKeys.Add(Key(held.Id,held.Generation)); Save(); grants.Remove(id); } } }
 public Job Admit(string requestId,string grantId,long grantGeneration,string leaseId,string world,string operation,string entity,string canonicalArguments,decimal maximumCost,DateTimeOffset now)
 {
  if(string.IsNullOrWhiteSpace(requestId)||requestId.Length>128||canonicalArguments.Length>1048576||maximumCost<0) throw new ArgumentException("invalid_request");
  string Fingerprint(string epoch)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {grantId,grantGeneration,leaseId,world=epoch,operation,entity,canonicalArguments,maximumCost}))));
  var fingerprint=Fingerprint(world);
  lock(gate) {
   if(faulted) throw new IOException("journal_unavailable");
   // A retry is the same request even if a launch moved the same lease to a new world epoch since: compare against the epoch it was admitted under.
   if(state.Jobs.TryGetValue(requestId,out var previous)) { var comparable=previous.LeaseId==leaseId && previous.WorldEpoch!=world ? Fingerprint(previous.WorldEpoch) : fingerprint; if(previous.Fingerprint!=comparable) throw new InvalidOperationException("request_id_conflict"); return previous; }
   var grant=Authorize(grantId,grantGeneration,leaseId,world,operation,entity,now);
   // Gross spend is never replenished by income. Indeterminate reservations remain held. The cap belongs to the grant (all its generations):
   // a freshly issued grant starts a fresh cap, a re-armed one keeps what it already spent.
   decimal reserved=state.Jobs.Values.Where(j=>j.GrantId==grantId && j.Status is "accepted" or "running" or "indeterminate").Sum(j=>j.ReservedCost);
   if(ChargedFor(grantId)+reserved+maximumCost>grant.SpendingLimit) throw new InvalidOperationException("budget_exceeded");
   var job=new Job(requestId,fingerprint,grantId,grantGeneration,leaseId,world,operation,entity,maximumCost,"accepted",""); state.Jobs.Add(requestId,job); Save(); return job;
  }
 }
 public Job Begin(string id,DateTimeOffset now)
 {
  lock(gate) { var job=Get(id); if(job.Status!="accepted") throw new InvalidOperationException("not_dispatchable"); Authorize(job.GrantId,job.GrantGeneration,job.LeaseId,job.WorldEpoch,job.Operation,job.EntityId,now); return Set(job with { Status="running" }); }
 }
 public Job Complete(string id,decimal actualCost)
 {
  lock(gate) { var job=Get(id); if(job.Status!="running"||actualCost<0||actualCost>job.ReservedCost) throw new InvalidOperationException("invalid_completion"); AddCharge(job.GrantId,actualCost); return Set(job with { Status="completed",Reason="observed" }); }
 }
 private decimal ChargedFor(string grantId) => state.ChargedByGrant.TryGetValue(grantId,out var spent) ? spent : 0m;
 private void AddCharge(string grantId,decimal amount) { state.Charged+=amount; state.ChargedByGrant[grantId]=ChargedFor(grantId)+amount; }
 /// <summary>The gross funds this grant has spent so far (completed charges only), and what its in-flight and indeterminate jobs still hold.</summary>
 public (decimal Charged,decimal Reserved) SpendFor(string grantId)
 {
  lock(gate) return (ChargedFor(grantId),state.Jobs.Values.Where(j=>j.GrantId==grantId && j.Status is "accepted" or "running" or "indeterminate").Sum(j=>j.ReservedCost));
 }
 /// <summary>
 /// Ends a job that was accepted or running and records what it actually cost. A completed job adds <paramref name="charged"/> to the grant's gross
 /// spend whatever its reservation was (the server's charge is the truth) and releases the rest of the reservation; every other status releases the
 /// whole reservation except "indeterminate", which keeps it. Never call it with a charge for a status other than completed unless the funds really left.
 /// </summary>
 public Job Settle(string id,string status,string reason,string result,decimal charged)
 {
  if(status is not ("completed" or "failed" or "cancelled" or "indeterminate") || charged<0) throw new ArgumentException("invalid_status");
  lock(gate)
  {
   var job=Get(id); if(job.Status is not ("accepted" or "running")) throw new InvalidOperationException("not_in_flight");
   if(charged>0 && status!="indeterminate") AddCharge(job.GrantId,charged);
   return Set(job with { Status=status,Reason=reason,Result=result.Length>MaxResult ? "" : result });
  }
 }
 /// <summary>After a launch moved the bridge's lease to a new scene: adopt the bridge's new world epoch for the same lease so later requests stay admissible.</summary>
 public void RebaseLease(string leaseId,string newWorldEpoch)
 {
  if(string.IsNullOrWhiteSpace(newWorldEpoch)) throw new ArgumentException("invalid_epoch");
  lock(gate) { if(lease==null||lease.Id!=leaseId) throw new InvalidOperationException("lease_revoked_or_expired"); lease=lease with { WorldEpoch=newWorldEpoch }; }
 }
 /// <summary>Records the terminal outcome of a job that was accepted or running. Cancelling a running job is only valid when the bridge proved it never dispatched.</summary>
 public Job Finish(string id,string status,string reason,string result)
 {
  if(status is not ("completed" or "failed" or "cancelled" or "indeterminate")) throw new ArgumentException("invalid_status");
  lock(gate) { var job=Get(id); if(job.Status is not ("accepted" or "running")) throw new InvalidOperationException("not_in_flight"); return Set(job with { Status=status,Reason=reason,Result=result.Length>MaxResult ? "" : result }); }
 }
 public bool TryGetJob(string id,out Job? job) { lock(gate) { if(faulted) throw new IOException("journal_unavailable"); return state.Jobs.TryGetValue(id,out job); } }
 private const int MaxResult=262144;
 public Job CancelBeforeDispatch(string id)
 {
  lock(gate) { var job=Get(id); if(job.Status!="accepted") throw new InvalidOperationException("reconciliation_required"); return Set(job with {Status="cancelled",Reason="not_dispatched"}); }
 }
 public Job MarkIndeterminate(string id,string reason)
 {
  lock(gate) { var job=Get(id); if(job.Status!="running") throw new InvalidOperationException("not_running"); return Set(job with {Status="indeterminate",Reason=reason}); }
 }
 public Job Get(string id) { lock(gate) { if(faulted) throw new IOException("journal_unavailable"); return state.Jobs.TryGetValue(id,out var job)?job:throw new KeyNotFoundException("unknown_job"); } }
 private MissionGrant Authorize(string grantId,long grantGeneration,string leaseId,string world,string operation,string entity,DateTimeOffset now)
 {
  if(lease==null||lease.Id!=leaseId||lease.WorldEpoch!=world||lease.ExpiresAt<=now) throw new InvalidOperationException("lease_revoked_or_expired");
  if(!grants.TryGetValue(grantId,out var grant)||grant.Generation!=grantGeneration||grant.ExpiresAt<=now||!grant.Operations.Contains(operation,StringComparer.Ordinal)||!grant.Entities.Contains(entity,StringComparer.Ordinal)) throw new InvalidOperationException("grant_denied");
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


