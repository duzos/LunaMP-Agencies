using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using Newtonsoft.Json;
using Server.Client;
using Server.Context;
using Server.Diagnostics;
using Server.Server;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Server.Agency
{
    public sealed class EconomyAgency
    {
        public double Funds, Science;
        public List<ToolingDesign> Designs = new List<ToolingDesign>();
    }

    public sealed class EconomyLaunch
    {
        public Guid LaunchId, AgencyId, Token, VesselId;
        public string ActorId;
        public long SessionTicks, ExpiresUtcTicks, CreatedSequence;
        public Guid SessionId;
        public LaunchState State;
        public double Charge, Multiplier;
        public ToolingManifest Manifest;
        public string OriginalProto;
        public PaidVesselRecord OriginalParts;
        public long OwnershipRevision;
        public bool ExternallySettled;
    }

    public sealed class EconomyOperationReceipt
    {
        public Guid AgencyId, SessionId;
        public long Sequence;
        public string ActorId, RequestHash;
        public EconomyResult Result;
    }

    public sealed class EconomyVesselJournal
    {
        public OwnershipDocument OwnershipAfter;
        public bool PermanentRemovals = true;
        public Dictionary<Guid, string> Upserts = new Dictionary<Guid, string>();
        public Guid[] Removals = Array.Empty<Guid>();
    }

    public sealed class EconomyDocument
    {
        public Dictionary<Guid, StoredTradeOffer> TradeOffers = new Dictionary<Guid, StoredTradeOffer>();
        public Dictionary<Guid, List<TradeEntitlement>> Entitlements = new Dictionary<Guid, List<TradeEntitlement>>();
        public int Version = 1;
        public long Revision;
        public Dictionary<Guid, EconomyAgency> Agencies = new Dictionary<Guid, EconomyAgency>();
        public Dictionary<Guid, EconomyLaunch> Launches = new Dictionary<Guid, EconomyLaunch>();
        public Dictionary<Guid, PaidVesselRecord> Vessels = new Dictionary<Guid, PaidVesselRecord>();
        public Dictionary<Guid, EconomyOperationReceipt> Operations = new Dictionary<Guid, EconomyOperationReceipt>();
        public EconomyVesselJournal Journal;
        public Dictionary<Guid, long> SessionSequences = new Dictionary<Guid, long>();
    }

    /// <summary>Durable balance and tooling authority; all projections are replayable.</summary>
    public static partial class AgencyEconomyStore
    {
        internal static bool Initialized;
        private static EconomyDocument _document = new EconomyDocument();
        private static string _error;
        public static Func<DateTime> UtcNow = () => DateTime.UtcNow;
        public static Action<string> PersistenceCheckpoint;
        public static bool ToolingEnabled => GeneralSettings.SettingsStore.AgencyTooling;
        public static bool TradeEnabled => GeneralSettings.SettingsStore.AgencyTrade;
        public static bool Enabled => ToolingEnabled || TradeEnabled;
        public static string FilePath => Path.Combine(ServerContext.UniverseDirectory, "AgencyEconomy.json");
        public static bool Ready { get { lock (AgencyVesselMap.TransactionGate) return _error == null && _document.Journal == null; } }
        private const int MaxOperations = 512;
        private static readonly HashSet<ClientStructure> PublicationBlocked = new HashSet<ClientStructure>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);
        public static bool MayPublish(ClientStructure client) { lock (AgencyVesselMap.TransactionGate) return !Enabled || Ready && !PublicationBlocked.Contains(client); }
        private static readonly Dictionary<ClientStructure, Guid> Sessions = new Dictionary<ClientStructure, Guid>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private const int MaxLaunches = 4096;
        private const int MaxFileBytes = 128 * 1024 * 1024;

        private static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
        private static bool ValidAmount(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= ToolingPolicy.MaxCost;
        private static bool UsesFunds => GeneralSettings.SettingsStore.GameMode == GameMode.Career;

        public static void Load()
        {
            Initialized = false;
            if (!Enabled) return;
            lock (AgencyVesselMap.TransactionGate)
            {
                _error = null;
                try
                {
                    var settings = GeneralSettings.SettingsStore;
                    if (!ToolingPolicy.FiniteNonNegative(settings.ToolingCostMultiplier) || !ToolingPolicy.FiniteNonNegative(settings.TooledLaunchMultiplier) || !ToolingPolicy.FiniteNonNegative(settings.ToolingCombineMultiplier))
                        throw new InvalidDataException("Invalid tooling multipliers.");
                    if (File.Exists(FilePath))
                    {
                        if (new FileInfo(FilePath).Length > MaxFileBytes) throw new InvalidDataException("Economy file is too large.");
                        _document = JsonConvert.DeserializeObject<EconomyDocument>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException();
                        Validate(_document);
                    }
                    else
                    {
                        _document = new EconomyDocument();
                        foreach (var agency in AgencyStore.Agencies.Values)
                            _document.Agencies[agency.Id] = new EconomyAgency { Funds = agency.Funds, Science = agency.Science };
                        foreach (var vessel in VesselStoreSystem.CurrentVessels)
                            _document.Vessels[vessel.Key] = new PaidVesselRecord
                            {
                                VesselId = vessel.Key,
                                Parts = vessel.Value.Parts.GetAll().Select(p => new PaidPart
                                {
                                    FlightId = p.Key, Name = p.Value.Fields.GetSingle("name")?.Value,
                                    Legacy = true, Multiplier = 1, MaximumRefund = ToolingPolicy.MaxCost
                                }).ToArray()
                            };
                        Persist(_document);
                    }
                    Sessions.Clear();
                    PublicationBlocked.Clear();
                    _document.SessionSequences.Clear();
                    _document.Operations.Clear();
                    RecoverProjection();
                    var migrated = false;
                    if (ToolingEnabled)
                    {
                        // Trade-only universes admit ordinary launches. When tooling is later
                        // enabled those already-existing craft receive legacy full-price value.
                        foreach (var vessel in VesselStoreSystem.CurrentVessels)
                        {
                            var ids = AgencyVesselMap.PartIds(vessel.Value);
                            if (_document.Vessels.TryGetValue(vessel.Key, out var known) && (!known.Parts.All(p => p.Legacy) || ids.All(id => known.Parts.Any(p => p.FlightId == id)))) continue;
                            _document.Vessels[vessel.Key] = new PaidVesselRecord { VesselId = vessel.Key, Parts = vessel.Value.Parts.GetAll().Select(p => new PaidPart { FlightId = p.Key, Name = p.Value.Fields.GetSingle("name")?.Value, Legacy = true, Multiplier = 1, MaximumRefund = ToolingPolicy.MaxCost }).ToArray() };
                            migrated = true;
                        }
                    }
                    var candidate = Copy(_document);
                    var changed = migrated;
                    foreach (var launch in candidate.Launches.Values.Where(l => l.State == LaunchState.Prepared))
                    {
                        RefundPrepared(candidate, launch);
                        changed = true;
                    }
                    RetireTerminalLaunches(candidate);
                    if (TradeNeedsMaintenance(candidate)) { PruneTrade(candidate); changed = true; }
                    if (changed) Commit(candidate);
                    Initialized = true;
                }
                catch (Exception e) { _error = "Economy recovery required: " + e.GetType().Name; }
            }
        }

        private static void Validate(EconomyDocument document)
        {
            if (document.Version != 1 || document.Revision < 0 || document.Agencies == null || document.Launches == null || document.Vessels == null || document.Operations == null || document.Operations.Count > MaxOperations || document.Launches.Count > MaxLaunches)
                throw new InvalidDataException("Invalid economy document.");
            foreach (var agency in document.Agencies.Values)
            {
                if (agency == null || !ValidAmount(agency.Funds) || !ToolingPolicy.FiniteNonNegative(agency.Science) || agency.Designs == null || agency.Designs.Count > ToolingPolicy.MaxDesigns)
                    throw new InvalidDataException("Invalid economy balance or designs.");
                foreach (var design in agency.Designs)
                    if (ToolingPolicy.Fingerprint(design.Manifest) != design.Fingerprint || !ToolingPolicy.FiniteNonNegative(design.ToolingBasis)) throw new InvalidDataException("Invalid saved tooling.");
            }
            ValidateTrade(document);
            foreach (var launch in document.Launches.Values)
            {
                if (launch.LaunchId == Guid.Empty || !Enum.IsDefined(typeof(LaunchState), launch.State) || !ToolingPolicy.FiniteNonNegative(launch.Charge)) throw new InvalidDataException("Invalid launch receipt.");
                if (launch.Manifest != null) ToolingPolicy.Validate(launch.Manifest);
            }
            foreach (var record in document.Vessels.Values)
            {
                if (record == null || record.VesselId == Guid.Empty || record.Parts == null || record.Cargo == null || record.Parts.Length > ToolingPolicy.MaxParts || record.Cargo.Length > ToolingPolicy.MaxCargo) throw new InvalidDataException("Invalid vessel payment record.");
                if (record.Parts.Select(p => p.FlightId).Distinct().Count() != record.Parts.Length) throw new InvalidDataException("Duplicate paid part.");
                foreach (var part in record.Parts)
                    if (part == null || !ToolingPolicy.FiniteNonNegative(part.Multiplier) || !ToolingPolicy.FiniteNonNegative(part.MaximumRefund)) throw new InvalidDataException("Invalid paid part value.");
            }
        }

        private static void Persist(EconomyDocument document)
        {
            Validate(document);
            // The wire payload has a stricter bound than the ledger. Validate the actual
            // aggregate projection before committing money or assets, including shared vessels.
            foreach (var agencyId in document.Agencies.Keys.Concat(AgencyStore.Agencies.Keys).Distinct())
            {
                if (AgencyEconomyWire.Size(BuildSnapshot(document, agencyId)) > AgencyEconomyWire.MaximumPayloadBytes - 4096)
                    throw new InvalidDataException("Agency economy snapshot storage limit reached; close offers or remove unused assets before retrying.");
            }
            var json = JsonConvert.SerializeObject(document);
            if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new InvalidDataException("Economy capacity reached.");
            PersistenceCheckpoint?.Invoke("before-document");
            AgencyVesselMap.AtomicWrite(FilePath, json);
        }

        private static void Commit(EconomyDocument candidate)
        {
            candidate.Revision = checked(_document.Revision + 1);
            Persist(candidate);
            _document = candidate;
            try
            {
                PersistenceCheckpoint?.Invoke("committed");
                RecoverProjection();
            }
            catch (Exception e)
            {
                _error = "Economy committed; recovery required: " + e.GetType().Name;
                throw;
            }
        }

        public static void RecoverProjection()
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                if (_document.Journal != null)
                {
                    foreach (var vessel in _document.Journal.Upserts)
                    {
                        AgencyVesselMap.AtomicWrite(Path.Combine(VesselStoreSystem.VesselsPath, vessel.Key + VesselStoreSystem.VesselFileFormat), vessel.Value);
                        VesselStoreSystem.CurrentVessels[vessel.Key] = new global::Server.System.Vessel.Classes.Vessel(vessel.Value);
                        VesselContext.RemovedVessels.TryRemove(vessel.Key, out _);
                        PersistenceCheckpoint?.Invoke("vessel-written");
                    }
                    PersistenceCheckpoint?.Invoke("vessels-written");
                    foreach (var vessel in _document.Journal.Removals)
                    {
                        File.Delete(Path.Combine(VesselStoreSystem.VesselsPath, vessel + VesselStoreSystem.VesselFileFormat));
                        VesselStoreSystem.CurrentVessels.TryRemove(vessel, out _);
                        if (_document.Journal.PermanentRemovals) VesselContext.RemovedVessels.TryAdd(vessel, 0);
                    }
                }
                if (_document.Journal?.OwnershipAfter != null)
                    AgencyVesselMap.ApplyEconomyProjection(_document.Journal.OwnershipAfter);
                foreach (var entry in _document.Agencies)
                {
                    if (!AgencyStore.Agencies.TryGetValue(entry.Key, out var agency)) continue;
                    lock (agency.Lock) { agency.Funds = entry.Value.Funds; agency.Science = (float)entry.Value.Science; }
                    AgencyStore.PersistAgency(agency);
                    AgencyScenarioUpdater.WriteFunds(agency.Id, agency.Funds);
                    AgencyScenarioUpdater.WriteScience(agency.Id, agency.Science);
                    AgencyScenarioStore.BackupAgency(agency.Id);
                }
                PersistenceCheckpoint?.Invoke("projections-written");
                if (_document.Journal != null)
                {
                    var complete = Copy(_document);
                    complete.Journal = null;
                    Persist(complete);
                    _document = complete;
                }
                _error = null;
            }
        }

        internal static void CommitOwnership(OwnershipDocument ownership)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                if (!Ready) throw new InvalidOperationException("Economy recovery required.");
                var next = Copy(_document);
                var old = AgencyVesselMap.ExportDocument();
                foreach (var entry in ownership.Records)
                {
                    if (!old.Records.TryGetValue(entry.Key, out var previous) || previous.OwnerAgencyId == entry.Value.OwnerAgencyId) continue;
                    if (!next.Vessels.TryGetValue(entry.Key, out var vessel)) continue;
                    foreach (var part in vessel.Parts)
                        if (next.Launches.TryGetValue(part.LaunchId, out var launch)) launch.ExternallySettled = true;
                }
                var removedIds = old.Records.Keys.Except(ownership.Records.Keys).ToArray();
                foreach (var id in removedIds) next.Vessels.Remove(id);
                RetireTerminalLaunches(next);
                var journal = new EconomyVesselJournal { OwnershipAfter = ownership, Removals = removedIds };
                InvalidateChangedOffers(next, ownership);
                if (ownership.Journal != null)
                {
                    var coupling = ownership.Journal;
                    var receipt = coupling.Receipt;
                    if (ToolingEnabled)
                    {
                        if (!next.Vessels.TryGetValue(receipt.DominantId, out var dominant) || !next.Vessels.TryGetValue(receipt.WeakId, out var weak))
                            throw new InvalidOperationException("Missing paid vessel provenance.");
                        dominant.Parts = dominant.Parts.Concat(weak.Parts).ToArray();
                        dominant.Cargo = dominant.Cargo.Concat(weak.Cargo).ToArray();
                        next.Vessels.Remove(receipt.WeakId);
                    }
                    journal.Upserts[receipt.DominantId] = coupling.MergedProto;
                    journal.Removals = new[] { receipt.WeakId };
                    journal.PermanentRemovals = false;
                    ownership.Receipts.Add(receipt);
                    ownership.Receipts = ownership.Receipts.OrderByDescending(r => r.CreatedUtcTicks).Take(256).ToList();
                    ownership.Journal = null;
                }
                next.Journal = journal;
                Commit(next);
            }
        }

        internal static void CommitSplit(OwnershipDocument ownership, Guid parent, Guid child, uint[] actualParts, string childProto = null, string parentProto = null, HashSet<uint> survivingParent = null)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                if (!Ready) throw new InvalidOperationException("Economy recovery required.");
                var next = Copy(_document);
                if (!next.Vessels.TryGetValue(parent, out var source)) throw new InvalidOperationException("Missing split provenance.");
                var selected = new HashSet<uint>(actualParts);
                if (source.Cargo.Any(c => c.ContainerFlightId == 0)) throw new InvalidOperationException("Cargo container cannot be resolved safely.");
                var moved = source.Parts.Where(p => selected.Contains(p.FlightId)).ToArray();
                if (moved.Length != selected.Count) throw new InvalidOperationException("Split contains unknown paid parts.");
                next.Vessels[child] = new PaidVesselRecord
                {
                    VesselId = child,
                    Parts = moved,
                    Cargo = source.Cargo.Where(c => selected.Contains(c.ContainerFlightId)).ToArray()
                };
                source.Parts = source.Parts.Where(p => !selected.Contains(p.FlightId) && (survivingParent == null || survivingParent.Contains(p.FlightId))).ToArray();
                source.Cargo = source.Cargo.Where(c => !selected.Contains(c.ContainerFlightId) && (survivingParent == null || survivingParent.Contains(c.ContainerFlightId))).ToArray();
                next.Journal = new EconomyVesselJournal { OwnershipAfter = ownership };
                if (childProto != null) next.Journal.Upserts[child] = childProto;
                if (parentProto != null) next.Journal.Upserts[parent] = parentProto;
                Commit(next);
            }
        }

        public static bool TryBalance(Guid agency, out double funds, out double science)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                if (_document.Agencies.TryGetValue(agency, out var balance)) { funds = balance.Funds; science = balance.Science; return true; }
                funds = science = 0;
                return false;
            }
        }

        private static EconomyAgency Agency(EconomyDocument document, Guid id)
        {
            if (document.Agencies.TryGetValue(id, out var existing)) return existing;
            if (!AgencyStore.Agencies.TryGetValue(id, out var source)) throw new InvalidOperationException("Agency not found.");
            var created = new EconomyAgency { Funds = source.Funds, Science = source.Science };
            document.Agencies[id] = created;
            return created;
        }

        private static void RequireActor(ClientStructure client)
        {
            if (client == null || !client.Authenticated || client.ConnectionStatus != ConnectionStatus.Connected || !ServerContext.Clients.Values.Any(c => ReferenceEquals(c, client)) || !AgencyStore.Agencies.TryGetValue(client.AgencyId, out var agency) || !agency.HasMember(client.UniqueIdentifier))
                throw new InvalidOperationException("Active agency membership required.");
            if (!Ready) throw new InvalidOperationException(_error ?? "Economy recovery required.");
        }

        private static void Charge(EconomyAgency agency, double amount)
        {
            if (!ToolingPolicy.FiniteNonNegative(amount)) throw new ArgumentException("Invalid charge.");
            if (UsesFunds && agency.Funds < amount) throw new InvalidOperationException("Insufficient funds.");
            if (UsesFunds) agency.Funds -= amount;
        }

        private static ToolingQuote Quote(EconomyAgency agency, ToolingManifest manifest, string manifestHash)
        {
            if (ToolingPolicy.ManifestHash(manifest) != manifestHash) throw new InvalidOperationException("Craft manifest changed. Request a fresh quote.");
            var settings = GeneralSettings.SettingsStore;
            var quote = ToolingPolicy.Quote(manifest, agency.Designs, settings.ToolingCostMultiplier, settings.TooledLaunchMultiplier, settings.ToolingCombineMultiplier);
            if (!quote.Success) throw new InvalidOperationException(quote.Reason);
            return quote;
        }

        public static EconomyResult Execute(ClientStructure client, EconomyCommand command)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                var result = new EconomyResult { RequestId = command?.RequestId ?? Guid.Empty, Operation = command?.Operation ?? EconomyOperation.Quote, LaunchId = command?.LaunchId ?? Guid.Empty, VesselId = command?.VesselId ?? Guid.Empty };
                try
                {
                    if (!Enabled) throw new InvalidOperationException("Tooling is disabled.");
                    RequireActor(client);
                    if (command == null || command.RequestId == Guid.Empty || !Enum.IsDefined(typeof(EconomyOperation), command.Operation)) throw new ArgumentException("Invalid economy request.");
                    if (!ToolingEnabled && command.Operation != EconomyOperation.Delta && !IsTradeOperation(command.Operation)) throw new InvalidOperationException("Tooling gameplay is disabled.");
                    var sessionId = Session(client);
                    if (command.SessionId != sessionId || command.Sequence < 1) throw new InvalidOperationException("Economy session changed; refresh before retrying.");
                    var hash = Hash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(command)));
                    if (_document.Operations.TryGetValue(command.RequestId, out var previous))
                    {
                        if (previous.AgencyId != client.AgencyId || previous.ActorId != client.UniqueIdentifier || previous.RequestHash != hash) throw new InvalidOperationException("Operation ID was already used.");
                        return Copy(previous.Result);
                    }
                    if (command.Operation != EconomyOperation.Quote && _document.SessionSequences.TryGetValue(sessionId, out var lastSequence) && command.Sequence <= lastSequence)
                        throw new InvalidOperationException("Operation receipt expired; an old sequence cannot execute again.");
                    var candidate = Copy(_document);
                    var agency = Agency(candidate, client.AgencyId);
                    if (command.Operation == EconomyOperation.Quote)
                    {
                        result.Quote = Quote(agency, command.Manifest, command.ManifestHash);
                        result.Success = true; result.Revision = _document.Revision;
                        return result;
                    }
                    RetireTerminalLaunches(candidate);
                    switch (command.Operation)
                    {
                        case EconomyOperation.TradeCreate:
                        case EconomyOperation.TradeAccept:
                        case EconomyOperation.TradeDecline:
                        case EconomyOperation.TradeCancel:
                        case EconomyOperation.TradeDelivered:
                            ApplyTrade(candidate, client, command, result);
                            break;
                        case EconomyOperation.Tool:
                            result.Quote = Quote(agency, command.Manifest, command.ManifestHash);
                            if (!result.Quote.AlreadyTooled)
                            {
                                Charge(agency, result.Quote.ToolingCost);
                                agency.Designs.Add(new ToolingDesign { Fingerprint = result.Quote.Fingerprint, Manifest = Copy(command.Manifest), ToolingBasis = result.Quote.ToolingCost });
                            }
                            break;
                        case EconomyOperation.PrepareLaunch:
                            if (command.LaunchId == Guid.Empty || candidate.Launches.ContainsKey(command.LaunchId) || candidate.Launches.Count >= MaxLaunches) throw new InvalidOperationException("Launch identity unavailable.");
                            result.Quote = Quote(agency, command.Manifest, command.ManifestHash);
                            Charge(agency, result.Quote.LaunchCost);
                            var launch = new EconomyLaunch
                            {
                                LaunchId = command.LaunchId, AgencyId = client.AgencyId, ActorId = client.UniqueIdentifier,
                                SessionTicks = client.ConnectionTime.Ticks, SessionId = sessionId, CreatedSequence = command.Sequence, Token = Guid.NewGuid(), State = LaunchState.Prepared,
                                ExpiresUtcTicks = UtcNow().AddSeconds(60).Ticks, Manifest = Copy(command.Manifest),
                                Charge = UsesFunds ? result.Quote.LaunchCost : 0,
                                Multiplier = result.Quote.AlreadyTooled ? GeneralSettings.SettingsStore.TooledLaunchMultiplier : 1
                            };
                            candidate.Launches[launch.LaunchId] = launch;
                            result.LaunchToken = launch.Token; result.ExpiresUtcTicks = launch.ExpiresUtcTicks;
                            break;
                        case EconomyOperation.CancelLaunch:
                            if (command.LaunchId == Guid.Empty) throw new ArgumentException("Missing launch identity.");
                            if (candidate.Launches.TryGetValue(command.LaunchId, out var pending))
                            {
                                if (pending.AgencyId != client.AgencyId || pending.ActorId != client.UniqueIdentifier) throw new InvalidOperationException("Launch belongs to another pilot.");
                                if (pending.State == LaunchState.Registered) throw new InvalidOperationException("Launch is already registered.");
                                if (pending.State == LaunchState.Prepared) RefundPrepared(candidate, pending);
                            }
                            else candidate.Launches[command.LaunchId] = new EconomyLaunch { LaunchId = command.LaunchId, AgencyId = client.AgencyId, ActorId = client.UniqueIdentifier, SessionId = sessionId, CreatedSequence = command.Sequence, State = LaunchState.Cancelled };
                            break;
                        case EconomyOperation.Delta:
                            if (!ValidAmount(command.FundsDelta) || !ValidAmount(command.ScienceDelta)) throw new ArgumentException("Invalid resource change.");
                            var funds = agency.Funds + command.FundsDelta;
                            var science = agency.Science + command.ScienceDelta;
                            if (!ValidAmount(funds) || !ToolingPolicy.FiniteNonNegative(science)) throw new ArgumentException("Resource change exceeds limits.");
                            agency.Funds = funds; agency.Science = science;
                            break;
                        case EconomyOperation.BoardEva:
                            BoardEva(candidate, client, command);
                            break;
                        case EconomyOperation.Recover:
                            Recover(candidate, client, command);
                            break;
                        case EconomyOperation.Revert:
                        case EconomyOperation.RevertLaunch:
                            Revert(candidate, client, command);
                            break;
                        default: throw new ArgumentException("Unsupported economy operation.");
                    }
                    result.Success = true; result.Reason = "Operation committed."; result.Revision = _document.Revision + 1;
                    candidate.SessionSequences[sessionId] = command.Sequence;
                    candidate.Operations[command.RequestId] = new EconomyOperationReceipt { AgencyId = client.AgencyId, ActorId = client.UniqueIdentifier, SessionId = sessionId, Sequence = command.Sequence, RequestHash = hash, Result = Copy(result) };
                    while (candidate.Operations.Count > MaxOperations) candidate.Operations.Remove(candidate.Operations.First().Key);
                    Commit(candidate);
                    if (command.Operation == EconomyOperation.Revert || command.Operation == EconomyOperation.RevertLaunch) PublicationBlocked.Add(client);
                }
                catch (Exception e)
                {
                    result.Success = false;
                    result.RecoveryRequired = _error != null;
                    result.Reason = result.RecoveryRequired ? _error : e.Message;
                    result.Revision = _document.Revision;
                }
                return result;
            }
        }

        private static void RefundPrepared(EconomyDocument document, EconomyLaunch launch)
        {
            Agency(document, launch.AgencyId).Funds += launch.Charge;
            launch.State = LaunchState.Cancelled;
        }

        public static void CancelPending(ClientStructure client = null)
        {
            if (!Enabled || !Ready) return;
            lock (AgencyVesselMap.TransactionGate)
            {
                var candidate = Copy(_document);
                var pending = candidate.Launches.Values.Where(l => l.State == LaunchState.Prepared && (client != null ? l.ActorId == client.UniqueIdentifier && l.SessionTicks == client.ConnectionTime.Ticks : l.ExpiresUtcTicks <= UtcNow().Ticks)).ToArray();
                var closedSession = Guid.Empty;
                if (client != null) PublicationBlocked.Remove(client);
                if (client != null && Sessions.TryGetValue(client, out closedSession)) Sessions.Remove(client);
                if (pending.Length == 0 && closedSession == Guid.Empty && !TradeNeedsMaintenance(candidate)) return;
                if (closedSession != Guid.Empty)
                {
                    candidate.SessionSequences.Remove(closedSession);
                    foreach (var operation in candidate.Operations.Where(p => p.Value.SessionId == closedSession).Select(p => p.Key).ToArray()) candidate.Operations.Remove(operation);
                }
                foreach (var launch in pending) RefundPrepared(candidate, launch);
                PruneTrade(candidate);
                RetireTerminalLaunches(candidate);
                Commit(candidate);
            }
            Broadcast();
        }

        public static EconomyResult Register(ClientStructure client, VesselProtoMsgData message, string raw, global::Server.System.Vessel.Classes.Vessel vessel)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                var result = new EconomyResult { RequestId = message.EconomyLaunchId, LaunchId = message.EconomyLaunchId, VesselId = message.VesselId, Operation = EconomyOperation.RegisterLaunch };
                try
                {
                    RequireActor(client);
                    if (!_document.Launches.TryGetValue(message.EconomyLaunchId, out var saved) || saved.Token != message.EconomyLaunchToken || saved.AgencyId != client.AgencyId || saved.ActorId != client.UniqueIdentifier || saved.SessionTicks != client.ConnectionTime.Ticks) throw new InvalidOperationException("Launch token is invalid for this session.");
                    if (saved.State == LaunchState.Registered && saved.VesselId == message.VesselId) { result.Success = true; result.Revision = _document.Revision; return result; }
                    if (saved.State != LaunchState.Prepared || saved.ExpiresUtcTicks <= UtcNow().Ticks || VesselStoreSystem.VesselExists(message.VesselId)) throw new InvalidOperationException("Launch token expired or was consumed.");
                    var parts = vessel.Parts.GetAll().ToArray();
                    if (parts.Length != saved.Manifest.Parts.Length || message.EconomyManifestIndices.Length != parts.Length || message.EconomyManifestIndices.Distinct().Count() != parts.Length) throw new InvalidOperationException("Launch parts do not match manifest.");
                    var paid = new PaidPart[parts.Length];
                    for (var i = 0; i < parts.Length; i++)
                    {
                        var index = message.EconomyManifestIndices[i];
                        if (index < 0 || index >= saved.Manifest.Parts.Length) throw new InvalidOperationException("Invalid manifest part index.");
                        var declared = saved.Manifest.Parts[index];
                        if (parts[i].Value.Fields.GetSingle("name")?.Value != declared.Name) throw new InvalidOperationException("Launch part name mismatch.");
                        var multiplier = declared.IsScience ? 1 : saved.Multiplier;
                        paid[i] = new PaidPart { LaunchId = saved.LaunchId, FlightId = parts[i].Key, Name = declared.Name, Multiplier = multiplier, MaximumRefund = declared.UnitCost * multiplier };
                    }
                    var candidate = Copy(_document);
                    var launch = candidate.Launches[saved.LaunchId];
                    launch.State = LaunchState.Registered; launch.VesselId = message.VesselId; launch.OriginalProto = raw;
                    var record = new PaidVesselRecord { VesselId = message.VesselId, Parts = paid, Cargo = Copy(launch.Manifest.Cargo) };
                    foreach (var cargo in record.Cargo)
                    {
                        var partIndex = Array.IndexOf(message.EconomyManifestIndices, cargo.ContainerPartIndex);
                        if (partIndex >= 0) cargo.ContainerFlightId = parts[partIndex].Key;
                    }
                    launch.OriginalParts = Copy(record);
                    candidate.Vessels[message.VesselId] = record;
                    var ownership = AgencyVesselMap.ExportDocument();
                    ownership.Revision++;
                    ownership.Records[message.VesselId] = new VesselOwnershipRecord { VesselId = message.VesselId, OwnerAgencyId = client.AgencyId, Revision = ownership.Revision };
                    candidate.Journal = new EconomyVesselJournal { OwnershipAfter = ownership, Upserts = new Dictionary<Guid, string> { [message.VesselId] = raw } };
                    Commit(candidate);
                    result.Success = true; result.Revision = _document.Revision;
                }
                catch (Exception e) { result.RecoveryRequired = _error != null; result.Reason = _error ?? e.Message; }
                return result;
            }
        }

        public static bool UpdateCargoBindings(Guid vesselId, uint[] currentParts, ToolingCargo[] bindings)
        {
            if (bindings == null) return true;
            lock (AgencyVesselMap.TransactionGate)
            {
                try
                {
                    if (!Ready || bindings.Length > ToolingPolicy.MaxCargo) return false;
                    var parent = AgencyVesselMap.PendingSplitParent(vesselId);
                    var sourceId = parent == Guid.Empty ? vesselId : parent;
                    if (!_document.Vessels.TryGetValue(sourceId, out var saved)) return false;
                    var remaining = Copy(saved.Cargo).ToList();
                    var rebound = new List<ToolingCargo>();
                    foreach (var binding in bindings)
                    {
                        if (binding == null || binding.Count < 1 || !currentParts.Contains(binding.ContainerFlightId)) return false;
                        var count = binding.Count;
                        foreach (var pool in remaining.Where(c => c.Name == binding.Name && c.Count > 0).OrderBy(c => c.UnitCost))
                        {
                            var take = Math.Min(count, pool.Count);
                            if (take == 0) break;
                            var moved = Copy(pool);
                            moved.Count = take;
                            moved.ContainerFlightId = binding.ContainerFlightId;
                            moved.CrewName = binding.CrewName;
                            rebound.Add(moved);
                            pool.Count -= take;
                            count -= take;
                        }
                        if (count != 0)
                        {
                            if (!saved.Parts.Any(p => p.FlightId == binding.ContainerFlightId && p.Legacy) || !ToolingPolicy.FiniteNonNegative(binding.UnitCost)) return false;
                            var legacy = Copy(binding); legacy.Count = count; rebound.Add(legacy);
                        }
                    }
                    // Parts absent from a parent's latest proto may be an announced split whose
                    // child proto has not arrived yet. Keep their provenance until that partition.
                    rebound.AddRange(remaining.Where(c => c.Count > 0));
                    if (JsonConvert.SerializeObject(rebound) == JsonConvert.SerializeObject(saved.Cargo)) return true;
                    var next = Copy(_document);
                    next.Vessels[sourceId].Cargo = rebound.ToArray();
                    Commit(next);
                    return true;
                }
                catch { return false; }
            }
        }

        public static bool ValidatePublishedParts(Guid id, uint[] actualParts)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                return Ready && _document.Vessels.TryGetValue(id, out var paid) && actualParts.Length > 0 && actualParts.Distinct().Count() == actualParts.Length && actualParts.All(uid => paid.Parts.Any(p => p.FlightId == uid));
            }
        }

        public static EconomyResult RegisterEva(ClientStructure client, VesselProtoMsgData message, string raw, global::Server.System.Vessel.Classes.Vessel vessel)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                var result = new EconomyResult { Operation = EconomyOperation.RegisterLaunch, VesselId = message.VesselId };
                try
                {
                    RequireActor(client);
                    RequireVessel(client, message.EconomyParentVesselId);
                    if (string.IsNullOrWhiteSpace(message.EconomyEvaCrew) || vessel.Fields.GetSingle("type")?.Value != "EVA" || VesselStoreSystem.VesselExists(message.VesselId))
                        throw new InvalidOperationException("Invalid EVA registration.");
                    var parent = VesselStoreSystem.CurrentVessels[message.EconomyParentVesselId];
                    if (!parent.Parts.GetAllValues().Any(p => p.Fields.GetAll().Any(f => f.Key == "crew" && f.Value == message.EconomyEvaCrew)))
                        throw new InvalidOperationException("EVA crew is not aboard its parent vessel.");
                    var ids = AgencyVesselMap.PartIds(vessel);
                    if (ids.Length != 1 || vessel.Parts.GetAllValues().Any(p => !p.Fields.GetAll().Any(f => f.Key == "crew" && f.Value == message.EconomyEvaCrew)))
                        throw new InvalidOperationException("EVA crew identity does not match.");
                    var next = Copy(_document);
                    if (!next.Vessels.TryGetValue(message.EconomyParentVesselId, out var paidParent)) throw new InvalidOperationException("Parent payment provenance is unavailable.");
                    var cargo = paidParent.Cargo.Where(c => c.CrewName == message.EconomyEvaCrew).ToArray();
                    paidParent.Cargo = paidParent.Cargo.Where(c => c.CrewName != message.EconomyEvaCrew).ToArray();
                    foreach (var item in cargo) item.ContainerFlightId = ids[0];
                    next.Vessels[message.VesselId] = new PaidVesselRecord
                    {
                        VesselId = message.VesselId,
                        Parts = new[] { new PaidPart { FlightId = ids[0], Name = vessel.Parts.GetAllValues().First().Fields.GetSingle("name")?.Value, Multiplier = 0, MaximumRefund = 0 } },
                        Cargo = cargo
                    };
                    var ownership = AgencyVesselMap.ExportDocument();
                    ownership.Revision++;
                    var owner = AgencyVesselMap.Get(message.EconomyParentVesselId) ?? new VesselOwnershipRecord { OwnerAgencyId = client.AgencyId };
                    owner.VesselId = message.VesselId;
                    owner.Revision = ownership.Revision;
                    ownership.Records[message.VesselId] = owner;
                    next.Journal = new EconomyVesselJournal { OwnershipAfter = ownership, Upserts = new Dictionary<Guid, string> { [message.VesselId] = raw } };
                    Commit(next);
                    result.Success = true;
                    result.Revision = _document.Revision;
                }
                catch (Exception e) { result.RecoveryRequired = _error != null; result.Reason = _error ?? e.Message; }
                return result;
            }
        }

        private static void RequireVessel(ClientStructure client, Guid id)
        {
            if (!VesselStoreSystem.VesselExists(id) || !VesselOwnershipPolicy.CanControl(AgencyVesselMap.Get(id), client.AgencyId)) throw new InvalidOperationException("Craft recovery is not authorized.");
            if (LockSystem.LockQuery.ControlLockExists(id) && !LockSystem.LockQuery.ControlLockBelongsToPlayer(id, client.PlayerName)) throw new InvalidOperationException("Craft is controlled by another pilot.");
        }

        private static void BoardEva(EconomyDocument candidate, ClientStructure client, EconomyCommand command)
        {
            RequireVessel(client, command.VesselId);
            RequireVessel(client, command.ParentVesselId);
            if (command.VesselId == command.ParentVesselId || command.VesselData == null || command.VesselData.Length == 0 || command.VesselData.Length > 2 * 1024 * 1024 || string.IsNullOrWhiteSpace(command.CrewName))
                throw new InvalidOperationException("Invalid boarding envelope.");
            var eva = VesselStoreSystem.CurrentVessels[command.VesselId];
            var target = VesselStoreSystem.CurrentVessels[command.ParentVesselId];
            if (eva.Fields.GetSingle("type")?.Value != "EVA" || !eva.Parts.GetAllValues().Any(p => p.Fields.GetAll().Any(f => f.Key == "crew" && f.Value == command.CrewName)))
                throw new InvalidOperationException("Boarding crew identity mismatch.");
            var raw = new UTF8Encoding(false, true).GetString(command.VesselData);
            var merged = new global::Server.System.Vessel.Classes.Vessel(raw);
            if (!Guid.TryParse(merged.Fields.GetSingle("pid")?.Value, out var id) || id != command.ParentVesselId || !new HashSet<uint>(AgencyVesselMap.PartIds(target)).SetEquals(AgencyVesselMap.PartIds(merged)))
                throw new InvalidOperationException("Boarding must preserve the target's physical parts.");
            var seats = merged.Parts.GetAll().Where(p => p.Value.Fields.GetAll().Any(f => f.Key == "crew" && f.Value == command.CrewName)).ToArray();
            if (seats.Length != 1 || !candidate.Vessels.TryGetValue(command.VesselId, out var sourcePaid) || !candidate.Vessels.TryGetValue(command.ParentVesselId, out var targetPaid))
                throw new InvalidOperationException("Boarding seat or provenance is invalid.");
            if (sourcePaid.Cargo.Any(c => c.CrewName != command.CrewName)) throw new InvalidOperationException("Unexpected EVA cargo owner.");
            foreach (var cargo in sourcePaid.Cargo) cargo.ContainerFlightId = seats[0].Key;
            targetPaid.Cargo = targetPaid.Cargo.Concat(sourcePaid.Cargo).ToArray();
            candidate.Vessels.Remove(command.VesselId);
            var ownership = AgencyVesselMap.ExportDocument();
            ownership.Revision++;
            ownership.Records.Remove(command.VesselId);
            ownership.Constituents.Remove(command.VesselId);
            ownership.Absorbed.Add(command.VesselId);
            candidate.Journal = new EconomyVesselJournal
            {
                OwnershipAfter = ownership,
                Upserts = new Dictionary<Guid, string> { [command.ParentVesselId] = raw },
                Removals = new[] { command.VesselId }
            };
        }

        private static void Recover(EconomyDocument candidate, ClientStructure client, EconomyCommand command)
        {
            RequireVessel(client, command.VesselId);
            if (!candidate.Vessels.TryGetValue(command.VesselId, out var record)) throw new InvalidOperationException("Craft has no recoverable provenance.");
            var current = AgencyVesselMap.PartIds(VesselStoreSystem.CurrentVessels[command.VesselId]);
            if (command.VesselData != null && command.VesselData.Length > 0)
            {
                if (command.VesselData.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Recovery vessel is too large.");
                var final = new global::Server.System.Vessel.Classes.Vessel(new UTF8Encoding(false, true).GetString(command.VesselData));
                if (!Guid.TryParse(final.Fields.GetSingle("pid")?.Value, out var finalId) || finalId != command.VesselId) throw new InvalidOperationException("Recovery identity mismatch.");
                current = AgencyVesselMap.PartIds(final);
                if (current.Any(uid => !record.Parts.Any(p => p.FlightId == uid))) throw new InvalidOperationException("Recovery contains unpaid parts.");
            }
            if (command.RecoveredParts == null || command.RecoveredCargo == null || !ToolingPolicy.FiniteNonNegative(command.RecoveryFactor) || command.RecoveryFactor > 1 || command.RecoveredParts.Length != current.Length || command.RecoveredParts.Select(p => p.FlightId).Distinct().Count() != current.Length || !new HashSet<uint>(current).SetEquals(command.RecoveredParts.Select(p => p.FlightId))) throw new ArgumentException("Recovery part list does not match the current vessel.");
            var paid = record.Parts.ToDictionary(p => p.FlightId);
            double credit = 0;
            foreach (var part in command.RecoveredParts)
            {
                if (!ToolingPolicy.FiniteNonNegative(part.StockValue) || !paid.TryGetValue(part.FlightId, out var provenance)) throw new ArgumentException("Unknown recovered part.");
                credit += Math.Min(part.StockValue * command.RecoveryFactor * provenance.Multiplier, provenance.MaximumRefund);
                if (provenance.LaunchId != Guid.Empty && candidate.Launches.TryGetValue(provenance.LaunchId, out var launch)) launch.ExternallySettled = true;
            }
            var remainingCargo = Copy(record.Cargo).ToList();
            foreach (var cargo in command.RecoveredCargo)
            {
                if (cargo == null || cargo.Count < 1 || !ToolingPolicy.FiniteNonNegative(cargo.UnitCost)) throw new ArgumentException("Invalid cargo recovery.");
                var needed = cargo.Count;
                foreach (var available in remainingCargo.Where(c => c.Name == cargo.Name && c.Count > 0).OrderBy(c => c.UnitCost))
                {
                    var count = Math.Min(needed, available.Count);
                    credit += Math.Min(cargo.UnitCost, available.UnitCost) * count * command.RecoveryFactor;
                    available.Count -= count;
                    needed -= count;
                    if (needed == 0) break;
                }
                if (needed > 0)
                {
                    if (cargo.ContainerFlightId == 0 || !paid.TryGetValue(cargo.ContainerFlightId, out var host) || !host.Legacy)
                        throw new InvalidOperationException("Cargo exceeds paid provenance.");
                    credit += cargo.UnitCost * needed * command.RecoveryFactor;
                }
            }
            if (!ToolingPolicy.FiniteNonNegative(credit)) throw new ArgumentException("Recovery value exceeds limits.");
            if (UsesFunds) Agency(candidate, client.AgencyId).Funds += credit;
            candidate.Vessels.Remove(command.VesselId);
            var ownership = AgencyVesselMap.ExportDocument();
            ownership.Revision++;
            ownership.Records.Remove(command.VesselId);
            ownership.Constituents.Remove(command.VesselId);
            candidate.Journal = new EconomyVesselJournal { OwnershipAfter = ownership, Removals = new[] { command.VesselId } };
        }

        private static void Revert(EconomyDocument candidate, ClientStructure client, EconomyCommand command)
        {
            if (!GameplaySettings.SettingsStore.CanRevert) throw new InvalidOperationException("Reverting is disabled by the server.");
            if (!candidate.Launches.TryGetValue(command.LaunchId, out var launch) || launch.AgencyId != client.AgencyId || launch.ActorId != client.UniqueIdentifier || launch.State != LaunchState.Registered || launch.ExternallySettled) throw new InvalidOperationException("Launch cannot be reverted after external settlement.");
            var vessels = candidate.Vessels.Values.Where(v => v.Parts.Any(p => p.LaunchId == launch.LaunchId)).ToArray();
            if (vessels.Length == 0 || vessels.Any(v => v.Parts.Any(p => p.LaunchId != launch.LaunchId) || AgencyVesselMap.Get(v.VesselId)?.OwnerAgencyId != client.AgencyId)) throw new InvalidOperationException("Undock or recover separately; launch ownership has changed.");
            foreach (var vessel in vessels) RequireVessel(client, vessel.VesselId);
            var journal = new EconomyVesselJournal { Removals = vessels.Select(v => v.VesselId).Where(v => command.Operation != EconomyOperation.RevertLaunch || v != launch.VesselId).ToArray() };
            foreach (var vessel in vessels) candidate.Vessels.Remove(vessel.VesselId);
            if (command.Operation == EconomyOperation.RevertLaunch)
            {
                candidate.Vessels[launch.VesselId] = Copy(launch.OriginalParts);
                journal.Upserts[launch.VesselId] = launch.OriginalProto;
            }
            else
            {
                Agency(candidate, launch.AgencyId).Funds += launch.Charge;
                launch.State = LaunchState.Reverted;
            }
            var ownership = AgencyVesselMap.ExportDocument();
            ownership.Revision++;
            foreach (var removed in journal.Removals) { ownership.Records.Remove(removed); ownership.Constituents.Remove(removed); }
            if (command.Operation == EconomyOperation.RevertLaunch)
            {
                ownership.Constituents.Remove(launch.VesselId);
                ownership.Records[launch.VesselId] = new VesselOwnershipRecord { VesselId = launch.VesselId, OwnerAgencyId = launch.AgencyId, Revision = ownership.Revision };
            }
            journal.OwnershipAfter = ownership;
            candidate.Journal = journal;
        }

        public static void SetBalance(Guid agencyId, double? funds, double? science)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                if (!Ready) throw new InvalidOperationException("Economy recovery required.");
                var candidate = Copy(_document);
                var agency = Agency(candidate, agencyId);
                if (funds.HasValue) agency.Funds = funds.Value;
                if (science.HasValue) agency.Science = science.Value;
                Commit(candidate);
            }
            Broadcast();
        }

        public static (bool Success, string Message) TransferResources(Guid from, Guid to, LmpCommon.Agency.ResourceKind kind, double amount, string actor, bool admin)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                try
                {
                    if (!Ready || from == to || !ToolingPolicy.FiniteNonNegative(amount) || amount == 0) throw new InvalidOperationException("Invalid transfer.");
                    if (!AgencyStore.Agencies.TryGetValue(from, out var source) || !AgencyStore.Agencies.ContainsKey(to) || (!admin && !source.HasMember(actor))) throw new InvalidOperationException("Transfer is not authorized.");
                    var candidate = Copy(_document);
                    var sender = Agency(candidate, from); var recipient = Agency(candidate, to);
                    if (kind == LmpCommon.Agency.ResourceKind.Funds)
                    {
                        if (sender.Funds < amount) throw new InvalidOperationException("Insufficient funds.");
                        sender.Funds -= amount; recipient.Funds += amount;
                    }
                    else if (kind == LmpCommon.Agency.ResourceKind.Science)
                    {
                        if (sender.Science < amount) throw new InvalidOperationException("Insufficient science.");
                        sender.Science -= amount; recipient.Science += amount;
                    }
                    else throw new InvalidOperationException("Unsupported resource.");
                    Commit(candidate);
                }
                catch (Exception e) { return (false, _error ?? e.Message); }
            }
            Broadcast();
            return (true, "Transfer committed.");
        }

        private static EconomySnapshot BuildSnapshot(EconomyDocument document, Guid agencyId)
        {
            var snapshot = new EconomySnapshot { Ready = true, AgencyId = agencyId, Revision = document.Revision };
            if (document.Agencies.TryGetValue(agencyId, out var agency)) { snapshot.Funds = agency.Funds; snapshot.Science = agency.Science; snapshot.Designs = Copy(agency.Designs.ToArray()); }
            else if (AgencyStore.Agencies.TryGetValue(agencyId, out var source)) { snapshot.Funds = source.Funds; snapshot.Science = source.Science; }
            snapshot.Offers = TradeOffersFor(document, agencyId);
            snapshot.Entitlements = document.Entitlements.TryGetValue(agencyId, out var entitlements) ? Copy(entitlements.ToArray()) : Array.Empty<TradeEntitlement>();
            snapshot.Vessels = Copy(document.Vessels.Values.ToArray());
            snapshot.Launches = document.Launches.Values.Where(l => l.AgencyId == agencyId).Select(l => new LaunchReceiptSummary { LaunchId = l.LaunchId, VesselId = l.VesselId, Charge = l.Charge, State = l.State, ExpiresUtcTicks = l.ExpiresUtcTicks }).ToArray();
            return snapshot;
        }

        public static EconomySnapshot Snapshot(Guid agencyId)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                var snapshot = BuildSnapshot(_document, agencyId);
                snapshot.Ready = Ready;
                return snapshot;
            }
        }

        private static Guid Session(ClientStructure client)
        {
            if (!Sessions.TryGetValue(client, out var id)) Sessions[client] = id = Guid.NewGuid();
            return id;
        }

        private static void RetireTerminalLaunches(EconomyDocument document)
        {
            var liveSessions = new HashSet<Guid>(Sessions.Values);
            var referenced = new HashSet<Guid>(document.Vessels.Values.SelectMany(v => v.Parts).Select(p => p.LaunchId));
            foreach (var id in document.Launches.Where(p => !referenced.Contains(p.Key) && p.Value.State != LaunchState.Prepared && (!liveSessions.Contains(p.Value.SessionId) || document.SessionSequences.TryGetValue(p.Value.SessionId, out var last) && p.Value.CreatedSequence < last - MaxOperations)).Select(p => p.Key).ToArray()) document.Launches.Remove(id);
        }

        public static void SendTo(ClientStructure client)
        {
            if (!Enabled) return;
            var message = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyEconomySnapshotMsgData>();
            lock (AgencyVesselMap.TransactionGate)
            {
                message.Snapshot = Snapshot(client.AgencyId);
                message.Snapshot.SessionId = Session(client);
                message.Snapshot.LastSequence = _document.SessionSequences.TryGetValue(message.Snapshot.SessionId, out var sequence) ? sequence : 0;
            }
            MessageQueuer.SendToClient<AgencySrvMsg>(client, message);
        }

        public static void Broadcast()
        {
            if (!Enabled) return;
            foreach (var client in ClientRetriever.GetAuthenticatedClients()) SendTo(client);
        }

        public static void SendResult(ClientStructure client, EconomyResult result)
        {
            var message = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyEconomyResultMsgData>();
            message.Result = result;
            MessageQueuer.SendToClient<AgencySrvMsg>(client, message);
        }

        private static bool IsSettlement(EconomyCommand command) => command != null && (command.Operation == EconomyOperation.Recover || command.Operation == EconomyOperation.Revert || command.Operation == EconomyOperation.RevertLaunch || command.Operation == EconomyOperation.BoardEva);

        public static void HandleCommand(ClientStructure client, EconomyCommand command)
        {
            Dictionary<Guid, string> before;
            Dictionary<Guid, string> after;
            EconomyResult result;
            Guid actorAgency;
            lock (AgencyVesselMap.TransactionGate)
            {
                before = IsSettlement(command) ? VesselStoreSystem.CurrentVessels.ToDictionary(p => p.Key, p => p.Value.ToString()) : new Dictionary<Guid, string>();
                actorAgency = client.AgencyId;
                result = Execute(client, command);
                after = IsSettlement(command) ? VesselStoreSystem.CurrentVessels.ToDictionary(p => p.Key, p => p.Value.ToString()) : new Dictionary<Guid, string>();
            }
            PlaytestDiagnostics.Write("economy.command", () => $"operation={result.Operation} request={result.RequestId} agency={actorAgency} success={result.Success} recoveryRequired={result.RecoveryRequired} revision={result.Revision}");
            if (result.Success && (command.Operation == EconomyOperation.Recover || command.Operation == EconomyOperation.Revert || command.Operation == EconomyOperation.RevertLaunch || command.Operation == EconomyOperation.BoardEva))
            {
                foreach (var removed in before.Keys.Except(after.Keys))
                {
                    var removal = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselRemoveMsgData>();
                    removal.VesselId = removed;
                    removal.Reason = "Authoritative economy settlement";
                    foreach (var held in LockSystem.LockQuery.GetAllLocks().Where(l => l.VesselId == removed).ToArray())
                    {
                        if (!LockSystem.ReleaseLock(held)) continue;
                        var release = ServerContext.ServerMessageFactory.CreateNewMessageData<LockReleaseMsgData>();
                        release.Lock = held; release.LockResult = true;
                        MessageQueuer.SendToAllClients<LockSrvMsg>(release);
                    }
                    MessageQueuer.SendToAllClients<VesselSrvMsg>(removal);
                }
                foreach (var changed in after.Where(p => !before.TryGetValue(p.Key, out var old) || old != p.Value))
                {
                    var proto = ServerContext.ServerMessageFactory.CreateNewMessageData<VesselProtoMsgData>();
                    proto.VesselId = changed.Key;
                    proto.ForceReload = true;
                    proto.Data = Encoding.UTF8.GetBytes(changed.Value);
                    proto.NumBytes = proto.Data.Length;
                    proto.Reason = "Authoritative reverted vessel";
                    proto.EconomyLaunchId = proto.EconomyLaunchToken = proto.EconomyParentVesselId = Guid.Empty;
                    proto.EconomyEvaCrew = null;
                    proto.EconomyManifestIndices = Array.Empty<int>();
                    MessageQueuer.SendToAllClients<VesselSrvMsg>(proto);
                }
            }
            if (result.Success && IsTradeOperation(command.Operation))
            {
                VesselOwnershipSystem.Changed();
                foreach (var recipient in ClientRetriever.GetAuthenticatedClients()) AgencyNetwork.SendVesselMapSyncTo(recipient);
            }
            Broadcast();
            SendResult(client, result);
        }
    }
}
