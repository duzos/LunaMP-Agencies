using LmpCommon.Agency;
using Newtonsoft.Json;
using Server.Context;
using Server.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Server.Agency
{
    public sealed class VesselConstituent
    {
        public Guid OriginalVesselId;
        public uint RootPartUid;
        public uint[] PartUids = Array.Empty<uint>();
        public VesselOwnershipRecord Ownership;
    }
    public sealed class CouplingReceipt
    {
        public Guid OperationId, DominantId, WeakId;
        public string Requester;
        public long CreatedUtcTicks;
    }
    public sealed class CouplingJournal
    {
        public CouplingReceipt Receipt;
        public string MergedProto;
    }
    public sealed class PendingVesselSplit
    {
        public Guid ParentId;
        public string Requester;
        public long ConnectionTicks;
        public uint Root, Boundary;
        public uint[] AllowedParts;
        public List<VesselConstituent> Constituents;
        public VesselOwnershipRecord ParentOwnership;
    }
    public sealed class OwnershipDocument
    {
        public int Version = 1;
        public long Revision;
        public Dictionary<Guid, VesselOwnershipRecord> Records = new Dictionary<Guid, VesselOwnershipRecord>();
        public Dictionary<Guid, List<VesselConstituent>> Constituents = new Dictionary<Guid, List<VesselConstituent>>();
        public HashSet<Guid> Absorbed = new HashSet<Guid>();
        public List<CouplingReceipt> Receipts = new List<CouplingReceipt>();
        public CouplingJournal Journal;
        public Dictionary<Guid,PendingVesselSplit> PendingSplits = new Dictionary<Guid,PendingVesselSplit>();
    }
    public sealed class OwnershipSnapshot
    {
        public long Revision { get; }
        public VesselOwnershipRecord[] Records { get; }
        public OwnershipSnapshot(long revision, VesselOwnershipRecord[] records) { Revision = revision; Records = records; }
    }
    /// <summary>One persisted authority, also projecting the legacy CommNet map.</summary>
    public static class AgencyVesselMap
    {
        public static readonly object TransactionGate = new object();
        private static OwnershipDocument _document = new OwnershipDocument();
        private static string _loadError;
        // Fault injection is called at durable transaction boundaries. Tests restore it.
        public static Action<string> PersistenceCheckpoint;
        public static string MapFilePath => Path.Combine(ServerContext.UniverseDirectory, "AgencyVesselMap.txt");
        public static string OwnershipFilePath => Path.Combine(ServerContext.UniverseDirectory, "AgencyVesselOwnership.json");
        public static bool Ready { get { lock(TransactionGate) return _loadError == null && _document.Journal == null; } }
        public static IReadOnlyDictionary<Guid, Guid> Snapshot { get { lock(TransactionGate) return _document.Records.Values.Select(Effective).Where(x=>x.OwnerAgencyId!=Guid.Empty).ToDictionary(x=>x.VesselId,x=>x.OwnerAgencyId); } }
        public static OwnershipSnapshot GetOwnershipSnapshot() { lock(TransactionGate) return new OwnershipSnapshot(_document.Revision,_document.Records.Values.Select(Effective).ToArray()); }
        public static VesselOwnershipRecord Get(Guid id) { lock(TransactionGate) return _document.Records.TryGetValue(id,out var r)?Effective(r):null; }
        public static bool TryGetAgency(Guid id,out Guid agency) { lock(TransactionGate) { agency=Get(id)?.OwnerAgencyId??Guid.Empty; return agency!=Guid.Empty; } }
        public static bool IsAbsorbed(Guid id) { lock(TransactionGate) return _document.Absorbed.Contains(id); }
        public static Task WaitForPendingWritesAsync() => Task.CompletedTask;
        public static void Load()
        {
            lock(TransactionGate)
            {
                _document = new OwnershipDocument(); _loadError=null; AgencyEconomyStore.Initialized=false;
                try
                {
                    if(File.Exists(OwnershipFilePath))
                    {
                        if(new FileInfo(OwnershipFilePath).Length>64L*1024*1024) throw new InvalidDataException("Ownership file too large.");
                        _document=JsonConvert.DeserializeObject<OwnershipDocument>(File.ReadAllText(OwnershipFilePath)) ?? throw new InvalidDataException();
                        Validate(_document);
                    }
                    else if(File.Exists(MapFilePath))
                    {
                        foreach(var line in File.ReadLines(MapFilePath))
                        {
                            var parts=line.Split('=');
                            if(parts.Length==2 && Guid.TryParse(parts[0].Trim(),out var id) && Guid.TryParse(parts[1].Trim(),out var agency) && id!=Guid.Empty && agency!=Guid.Empty)
                                _document.Records[id]=new VesselOwnershipRecord { VesselId=id,OwnerAgencyId=agency };
                        }
                        Validate(_document); Persist(_document);
                    }
                }
                catch(Exception e) { _loadError="Ownership data unavailable: "+e.GetType().Name; _document=new OwnershipDocument(); }
            }
        }
        private static void Validate(OwnershipDocument d)
        {
            if(d.Version!=1 || d.Revision<0 || d.Records==null || d.Records.Count>VesselOwnershipPolicy.MaxRecords || d.Constituents==null || d.Absorbed==null || d.Receipts==null || d.Receipts.Count>256) throw new InvalidDataException();
            foreach(var p in d.Records) ValidateRecord(p.Key,p.Value);
            if(d.Constituents.Count>VesselOwnershipPolicy.MaxRecords) throw new InvalidDataException();
            foreach(var list in d.Constituents.Values)
            {
                if(list==null || list.Count>256) throw new InvalidDataException();
                foreach(var c in list) { if(c==null || c.PartUids==null || c.PartUids.Length>100000 || c.PartUids.Distinct().Count()!=c.PartUids.Length) throw new InvalidDataException(); ValidateRecord(c.OriginalVesselId,c.Ownership); }
            }
            if(d.Journal!=null && (d.Journal.Receipt==null || Encoding.UTF8.GetByteCount(d.Journal.MergedProto ?? "")>VesselOwnershipPolicy.MaxMergedVesselBytes)) throw new InvalidDataException();
        }
        private static void ValidateRecord(Guid id,VesselOwnershipRecord r)
        {
            if(r==null || id==Guid.Empty || r.VesselId!=id || r.Revision<0 || r.CoOwnerAgencyIds==null || r.CoOwnerAgencyIds.Length>VesselOwnershipPolicy.MaxCoOwners || r.CoOwnerAgencyIds.Any(x=>x==Guid.Empty || x==r.OwnerAgencyId) || r.CoOwnerAgencyIds.Distinct().Count()!=r.CoOwnerAgencyIds.Length || !Enum.IsDefined(typeof(VesselDockingPolicy),r.DockingPolicy)) throw new InvalidDataException();
        }
        private static VesselOwnershipRecord Effective(VesselOwnershipRecord raw)
        {
            var r=raw.Copy();
            if(VesselOwnershipSystem.Enabled)
            {
                if(r.OwnerAgencyId!=Guid.Empty && !AgencyStore.Agencies.ContainsKey(r.OwnerAgencyId)) {r.OwnerAgencyId=Guid.Empty;r.DockingPolicy=VesselDockingPolicy.Nobody;}
                r.CoOwnerAgencyIds=r.CoOwnerAgencyIds.Where(AgencyStore.Agencies.ContainsKey).ToArray();
            }
            return r;
        }
        private static OwnershipDocument Copy()
        {
            var d=JsonConvert.DeserializeObject<OwnershipDocument>(JsonConvert.SerializeObject(_document));
            foreach(var key in d.Records.Keys.ToArray()) d.Records[key]=Effective(d.Records[key]);
            foreach(var c in d.Constituents.Values.SelectMany(x=>x)) c.Ownership=Effective(c.Ownership);
            foreach(var split in d.PendingSplits.Values) {split.ParentOwnership=Effective(split.ParentOwnership);foreach(var c in split.Constituents)c.Ownership=Effective(c.Ownership);}
            return d;
        }
        private static void CheckReady() { if(!Ready) throw new InvalidOperationException(_loadError ?? "Ownership recovery is pending."); }
        private static void Persist(OwnershipDocument candidate)
        {
            Validate(candidate); PersistenceCheckpoint?.Invoke("before-document");
            AtomicWrite(OwnershipFilePath,JsonConvert.SerializeObject(candidate));
        }
        public static void AtomicWrite(string path,string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var writer=new StreamWriter(stream,new UTF8Encoding(false))) { writer.Write(contents); writer.Flush(); stream.Flush(true); }
                File.Move(temporary,path,true);
            }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void Commit(OwnershipDocument candidate)
        {
            candidate.Revision=checked(_document.Revision+1);
            if (AgencyEconomyStore.Enabled && AgencyEconomyStore.Initialized) AgencyEconomyStore.CommitOwnership(candidate);
            else { Persist(candidate); _document=candidate; }
        }
        internal static OwnershipDocument ExportDocument() { lock (TransactionGate) return Copy(); }
        internal static void ApplyEconomyProjection(OwnershipDocument candidate)
        {
            lock (TransactionGate) { Persist(candidate); _document = candidate; }
        }
        public static void Set(Guid id,Guid agency)
        {
            if(id==Guid.Empty || agency==Guid.Empty) return;
            lock(TransactionGate) { CheckReady(); var next=Copy(); next.Records[id]=new VesselOwnershipRecord {VesselId=id,OwnerAgencyId=agency,Revision=next.Revision+1}; Commit(next); }
        }
        public static void RegisterNew(Guid id,Guid agency)
        {
            lock(TransactionGate) { CheckReady(); if(!_document.Records.ContainsKey(id)) Set(id,agency); }
        }
        public static void Remove(Guid id)
        {
            lock(TransactionGate) { CheckReady(); if(!_document.Records.ContainsKey(id) && !_document.Constituents.ContainsKey(id)) return; var next=Copy(); next.Records.Remove(id); next.Constituents.Remove(id); next.Absorbed.Remove(id); Commit(next); }
        }
        public static (bool Success,string Reason) Mutate(Guid id,Guid actor,bool isOwner,VesselOwnershipOperation op,Guid target,VesselDockingPolicy policy)
        {
            lock(TransactionGate)
            {
                try
                {
                    CheckReady(); var old=Get(id);
                    if(op==VesselOwnershipOperation.Claim)
                    { if(actor==Guid.Empty || old?.OwnerAgencyId!=null && old.OwnerAgencyId!=Guid.Empty) return (false,"Craft already has an owner."); }
                    else if(!VesselOwnershipPolicy.CanManage(old,actor,isOwner)) return(false,"Only the owning agency owner can manage this craft.");
                    var next=Copy(); var record=old ?? new VesselOwnershipRecord {VesselId=id};
                    if(op==VesselOwnershipOperation.Transfer && next.Constituents.TryGetValue(id,out var parts) && parts.Any(c=>c.Ownership.OwnerAgencyId!=record.OwnerAgencyId)) return(false,"Undock visiting craft before handing over this combined vessel.");
                    var previousOwner=record.OwnerAgencyId;
                    Apply(record,op,actor,target,policy); record.Revision=next.Revision+1; next.Records[id]=record;
                    if(next.Constituents.TryGetValue(id,out var constituents)) foreach(var c in constituents.Where(c=>c.Ownership.OwnerAgencyId==previousOwner)) { Apply(c.Ownership,op,actor,target,policy); c.Ownership.Revision=record.Revision; }
                    Commit(next); return(true,"Craft permissions updated.");
                }
                catch(Exception e) { return(false,"Craft permissions unchanged: "+e.GetType().Name); }
            }
        }
        private static void Apply(VesselOwnershipRecord r,VesselOwnershipOperation op,Guid actor,Guid target,VesselDockingPolicy policy)
        {
            switch(op)
            {
                case VesselOwnershipOperation.Claim: r.OwnerAgencyId=actor; break;
                case VesselOwnershipOperation.Transfer: if(target==Guid.Empty) throw new ArgumentException(); r.OwnerAgencyId=target; r.CoOwnerAgencyIds=Array.Empty<Guid>(); r.DockingPolicy=VesselDockingPolicy.Nobody; break;
                case VesselOwnershipOperation.AddCoOwner: if(target==Guid.Empty || target==r.OwnerAgencyId) throw new ArgumentException(); r.CoOwnerAgencyIds=r.CoOwnerAgencyIds.Concat(new[]{target}).Distinct().ToArray(); break;
                case VesselOwnershipOperation.RemoveCoOwner: r.CoOwnerAgencyIds=r.CoOwnerAgencyIds.Where(x=>x!=target).ToArray(); break;
                case VesselOwnershipOperation.SetDockingPolicy: r.DockingPolicy=policy; break;
                default: throw new ArgumentException();
            }
        }
        public static void RemoveAgency(Guid agency)
        {
            lock(TransactionGate)
            {
                CheckReady(); var next=Copy();
                foreach(var r in next.Records.Values.Concat(next.Constituents.Values.SelectMany(x=>x).Select(x=>x.Ownership)))
                { if(r.OwnerAgencyId==agency) {r.OwnerAgencyId=Guid.Empty;r.DockingPolicy=VesselDockingPolicy.Nobody;} r.CoOwnerAgencyIds=r.CoOwnerAgencyIds.Where(x=>x!=agency).ToArray(); r.Revision=next.Revision+1; }
                Commit(next);
            }
        }
        private static List<VesselConstituent> Constituents(OwnershipDocument d,Guid id,global::Server.System.Vessel.Classes.Vessel vessel)
        {
            if(d.Constituents.TryGetValue(id,out var stored)) return stored;
            var ids=PartIds(vessel); var root=ids.FirstOrDefault();
            if(int.TryParse(vessel.Fields.GetSingle("root")?.Value,out var index) && index>=0 && index<ids.Length) root=ids[index];
            return new List<VesselConstituent> { new VesselConstituent {OriginalVesselId=id,RootPartUid=root,PartUids=ids,Ownership=d.Records.TryGetValue(id,out var r)?r.Copy():new VesselOwnershipRecord {VesselId=id}} };
        }
        public static uint[] PartIds(global::Server.System.Vessel.Classes.Vessel vessel) => vessel.Parts.GetAll().Select(x=>x.Key).ToArray();
        public static CouplingReceipt GetReceipt(Guid op,string requester)
        { lock(TransactionGate) return _document.Receipts.FirstOrDefault(r=>r.OperationId==op && r.Requester==requester); }
        public static void CommitCouple(Guid operation,string requester,Guid dominant,Guid weak,string mergedProto,global::Server.System.Vessel.Classes.Vessel merged,uint dominantBoundary,uint weakBoundary)
        {
            lock(TransactionGate)
            {
                CheckReady();
                if(!VesselStoreSystem.CurrentVessels.TryGetValue(dominant,out var a) || !VesselStoreSystem.CurrentVessels.TryGetValue(weak,out var b)) throw new InvalidOperationException("Missing vessel.");
                var aa=PartIds(a);var bb=PartIds(b);var mm=PartIds(merged);
                if(!aa.Contains(dominantBoundary) || !bb.Contains(weakBoundary) || aa.Intersect(bb).Any() || mm.Distinct().Count()!=mm.Length || !new HashSet<uint>(aa.Concat(bb)).SetEquals(mm)) throw new InvalidDataException("Merged parts do not match participants.");
                var next=Copy(); next.Constituents[dominant]=Constituents(next,dominant,a).Concat(Constituents(next,weak,b)).ToList(); next.Constituents.Remove(weak); next.Absorbed.Add(weak);
                var receipt=new CouplingReceipt {OperationId=operation,Requester=requester,DominantId=dominant,WeakId=weak,CreatedUtcTicks=DateTime.UtcNow.Ticks};
                next.Journal=new CouplingJournal {Receipt=receipt,MergedProto=mergedProto}; Commit(next);
                // From this point failure requires recovery, never rollback or ordinary rejection.
                PersistenceCheckpoint?.Invoke("journal-committed"); RecoverJournal();
            }
        }
        public static void RecoverJournal()
        {
            lock(TransactionGate)
            {
                if(_loadError!=null) throw new InvalidOperationException(_loadError);
                var journal=_document.Journal; if(journal==null) return;
                var r=journal.Receipt; var merged=new global::Server.System.Vessel.Classes.Vessel(journal.MergedProto);
                AtomicWrite(Path.Combine(VesselStoreSystem.VesselsPath,r.DominantId+VesselStoreSystem.VesselFileFormat),journal.MergedProto);
                PersistenceCheckpoint?.Invoke("survivor-written");
                File.Delete(Path.Combine(VesselStoreSystem.VesselsPath,r.WeakId+VesselStoreSystem.VesselFileFormat));
                PersistenceCheckpoint?.Invoke("weak-deleted");
                VesselStoreSystem.CurrentVessels[r.DominantId]=merged; VesselStoreSystem.CurrentVessels.TryRemove(r.WeakId,out _);
                var next=Copy(); next.Journal=null; next.Receipts.Add(r); next.Receipts=next.Receipts.OrderByDescending(x=>x.CreatedUtcTicks).Take(256).ToList();
                PersistenceCheckpoint?.Invoke("before-journal-clear"); Commit(next);
            }
        }
        public static bool HasPendingJournal { get { lock(TransactionGate) return _document.Journal!=null; } }
        public static long CaptureEpoch() { lock(TransactionGate) return _document.Revision; }
        public static bool CanApplyEpoch(Guid id,long epoch) { lock(TransactionGate) return (!AgencyEconomyStore.Enabled || AgencyEconomyStore.Ready) && (!(VesselOwnershipSystem.Enabled || AgencyEconomyStore.Enabled) || (Ready && !_document.Absorbed.Contains(id) && !_document.PendingSplits.ContainsKey(id) && !IsSplitParent(id) && _document.Revision==epoch && !global::Server.System.VesselContext.RemovedVessels.ContainsKey(id))); }
        public static bool IsSplitParent(Guid id) { lock(TransactionGate) return _document.PendingSplits.Values.Any(s => s.ParentId == id); }
        public static Guid PendingSplitParent(Guid id) { lock(TransactionGate) return _document.PendingSplits.TryGetValue(id, out var split) ? split.ParentId : Guid.Empty; }
        public static bool IsPendingSplit(Guid id) { lock(TransactionGate) return _document.PendingSplits.ContainsKey(id); }
        public static bool RestoreSplit(Guid parent,Guid child,uint root,uint boundary,uint[] actualParts=null,string requester=null,long connectionTicks=0)
        {
            lock(TransactionGate)
            {
                CheckReady(); if(child==Guid.Empty || parent==child || _document.Records.ContainsKey(child) || _document.PendingSplits.ContainsKey(child)) return false;
                if(!VesselStoreSystem.CurrentVessels.TryGetValue(parent,out var vessel) || !PartIds(vessel).Contains(boundary)) return false;
                var next=Copy();
                if(next.PendingSplits.Count>=256) return false;
                next.PendingSplits[child]=new PendingVesselSplit {ParentId=parent,Requester=requester,ConnectionTicks=connectionTicks,Root=root,Boundary=boundary,AllowedParts=PartIds(vessel),Constituents=next.Constituents.TryGetValue(parent,out var c)?c:new List<VesselConstituent>(),ParentOwnership=Get(parent)??new VesselOwnershipRecord {VesselId=parent}};
                Commit(next);
                return actualParts==null || ResolveSplit(child,actualParts);
            }
        }
        public static bool SplitBelongsTo(Guid child, string requester, long connectionTicks)
        {
            lock (TransactionGate) return !_document.PendingSplits.TryGetValue(child, out var pending) || pending.Requester == requester && pending.ConnectionTicks == connectionTicks;
        }

        public static void CancelPendingSplits(string requester = null, long connectionTicks = 0, Guid? child = null)
        {
            lock (TransactionGate)
            {
                // An irreversible topology journal must replay before pending cleanup.
                if (!Ready || AgencyEconomyStore.Enabled && !AgencyEconomyStore.Ready) return;
                var cancelled = _document.PendingSplits.Where(p => (!child.HasValue || p.Key == child.Value) && (requester == null || p.Value.Requester == requester && p.Value.ConnectionTicks == connectionTicks)).Select(p => p.Key).ToArray();
                if (cancelled.Length == 0) return;
                var next = Copy();
                foreach (var id in cancelled) next.PendingSplits.Remove(id);
                Commit(next);
            }
        }

        public static bool ResolveSplit(Guid child,uint[] actualParts,string childProto=null,string parentProto=null)
        {
            lock(TransactionGate)
            {
                if(!_document.PendingSplits.TryGetValue(child,out var pending)) return true;
                if(actualParts.Length==0 || !actualParts.Contains(pending.Boundary) || actualParts.Distinct().Count()!=actualParts.Length || actualParts.Any(p=>!pending.AllowedParts.Contains(p))) return false;
                if (AgencyEconomyStore.ToolingEnabled && childProto != null)
                {
                    if (string.IsNullOrEmpty(parentProto)) return false;
                    var parentVessel = new global::Server.System.Vessel.Classes.Vessel(parentProto);
                    var parentIds = PartIds(parentVessel);
                    if (!Guid.TryParse(parentVessel.Fields.GetSingle("pid")?.Value, out var parentId) || parentId != pending.ParentId || parentIds.Intersect(actualParts).Any() || !new HashSet<uint>(parentIds.Concat(actualParts)).SetEquals(pending.AllowedParts)) return false;
                }
                var candidates=pending.Constituents.Where(c=>c.PartUids.Contains(pending.Boundary) && (pending.Root==0 || c.RootPartUid==pending.Root)).ToArray();
                if(pending.Constituents.Count>0 && candidates.Length!=1) return false;
                var owner=Effective(candidates.Length==1?candidates[0].Ownership:pending.ParentOwnership);
                var next=Copy(); var selected=new HashSet<uint>(actualParts);
                if(next.Constituents.TryGetValue(pending.ParentId,out var parentParts))
                {
                    var childParts=new List<VesselConstituent>();
                    foreach(var c in parentParts.ToArray())
                    {
                        var moved=c.PartUids.Where(selected.Contains).ToArray(); if(moved.Length==0) continue;
                        childParts.Add(new VesselConstituent {OriginalVesselId=c.OriginalVesselId,RootPartUid=c.RootPartUid,PartUids=moved,Ownership=c.Ownership.Copy()});
                        c.PartUids=c.PartUids.Where(p=>!selected.Contains(p)).ToArray(); if(c.PartUids.Length==0) parentParts.Remove(c);
                    }
                    if(childParts.Count>0) next.Constituents[child]=childParts;
                }
                owner.VesselId=child; owner.Revision=next.Revision+1; next.Records[child]=owner; next.PendingSplits.Remove(child); next.Absorbed.Remove(child);
                if (AgencyEconomyStore.ToolingEnabled) { next.Revision = checked(_document.Revision + 1); AgencyEconomyStore.CommitSplit(next, pending.ParentId, child, actualParts, childProto, parentProto); }
                else Commit(next);
                return true;
            }
        }
    }
}
