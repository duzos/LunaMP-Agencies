using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Contracts;

namespace KspControl.Bridge
{
    // Pure policy only: no Unity or KSP types, so this file links into the unit-test assembly.
    // Thread model (plan R2-§5): the main thread calls UpdateContext, ProvisionGrant, DropGrant, BurnGrant,
    // Stop, HumanTakeover, Admit, ValidateForDispatch and the operation/rebase calls. Loopback workers call
    // only AcquireLease, RenewLease, ReleaseLease, Heartbeat and Status, which read authority-held fields
    // under the gate plus the monotonic clock and never touch the game.
    internal static class Identifiers
    {
        // Two methods, not an optional parameter: method groups passed to Select would otherwise bind the index overload.
        internal static string Required(string value) { return Required(value, 256); }
        internal static string Required(string value, int max)
        { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException("invalid_identifier"); return value; }
        /// <summary>Agency keys are "offline:" plus a save folder of up to 256 characters, matching the grant payload limit.</summary>
        internal const int MaxAgency = 300;
    }

    /// <summary>What a grant is bound to. Deliberately has no world or vessel: a scene change must not invalidate it.</summary>
    internal sealed class GrantBinding : IEquatable<GrantBinding>
    {
        public string Install { get; }
        public string SaveFolder { get; }
        public string Agency { get; }
        public GrantBinding(string install, string saveFolder, string agency)
        { Install = Identifiers.Required(install); SaveFolder = Identifiers.Required(saveFolder); Agency = Identifiers.Required(agency, Identifiers.MaxAgency); }
        public bool Equals(GrantBinding other) => other != null
            && string.Equals(Install, other.Install, StringComparison.OrdinalIgnoreCase)
            && string.Equals(SaveFolder, other.SaveFolder, StringComparison.Ordinal)
            && string.Equals(Agency, other.Agency, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => Equals(obj as GrantBinding);
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Install) ^ StringComparer.Ordinal.GetHashCode(SaveFolder);
        internal GrantBindingInfo ToInfo() => new GrantBindingInfo { InstallId = Install, SaveFolder = SaveFolder, Agency = Agency };
        internal static GrantBinding From(GrantBindingInfo info) => new GrantBinding(info.InstallId, info.SaveFolder, info.Agency);
    }

    /// <summary>What a lease is bound to. Entity is the stable facility id such as "editor:VAB".</summary>
    internal sealed class LeaseContext
    {
        public string Epoch { get; }
        public string Entity { get; }
        public long Revision { get; }
        public bool SceneReady { get; }
        public LeaseContext(string epoch, string entity, long revision, bool sceneReady)
        {
            Epoch = Identifiers.Required(epoch); Entity = Identifiers.Required(entity);
            if (revision < 0) throw new ArgumentException("invalid_revision");
            Revision = revision; SceneReady = sceneReady;
        }
        internal bool SameIdentity(LeaseContext other) => other != null && Epoch == other.Epoch && Entity == other.Entity;
    }

    internal sealed class ClassifiedEffect
    {
        public string Operation { get; }
        public string Recipient { get; }
        public long ModuleGeneration { get; }
        public ClassifiedEffect(string operation, string recipient, long moduleGeneration)
        {
            Operation = Identifiers.Required(operation); Recipient = Identifiers.Required(recipient);
            if (moduleGeneration < 0) throw new ArgumentException("invalid_module_generation"); ModuleGeneration = moduleGeneration;
        }
        internal bool Same(ClassifiedEffect other) => Operation == other.Operation && Recipient == other.Recipient && ModuleGeneration == other.ModuleGeneration;
    }

    internal sealed class EffectPermission
    {
        public string Operation { get; }
        public string Recipient { get; }
        public EffectPermission(string operation, string recipient)
        { Operation = Identifiers.Required(operation); Recipient = Identifiers.Required(recipient); }
    }

    internal sealed class TrustedExecutionGrant
    {
        public string Id { get; }
        public long Generation { get; }
        public GrantBinding Binding { get; }
        public DateTime ExpiresUtc { get; }
        private readonly EffectPermission[] permissions;
        private readonly string[] entities;
        /// <param name="entities">Lease entities this grant may bind to, for example "editor:VAB".</param>
        public TrustedExecutionGrant(string id, long generation, GrantBinding binding, DateTime expiresUtc, IEnumerable<EffectPermission> permissions, IEnumerable<string> entities)
        {
            Id = Identifiers.Required(id); if (generation <= 0) throw new ArgumentException("invalid_grant_generation");
            Generation = generation; Binding = binding ?? throw new ArgumentNullException(nameof(binding)); ExpiresUtc = expiresUtc.ToUniversalTime();
            this.permissions = (permissions ?? throw new ArgumentNullException(nameof(permissions))).Take(1025).ToArray();
            if (this.permissions.Length > 1024 || this.permissions.Any(p => p == null)) throw new ArgumentException("invalid_permissions");
            this.entities = (entities ?? throw new ArgumentNullException(nameof(entities))).Select(e => Identifiers.Required(e)).ToArray();
        }
        internal bool Allows(ClassifiedEffect effect) => permissions.Any(p => p.Operation == effect.Operation && p.Recipient == effect.Recipient);
        internal bool AllowsEntity(string entity) => entities.Contains(entity, StringComparer.Ordinal);
    }

    internal sealed class ExecutionTicket
    {
        internal string LeaseId { get; }
        internal string GrantId { get; }
        internal long GrantGeneration { get; }
        internal long AuthorityGeneration { get; }
        internal LeaseContext Context { get; private set; }
        internal IReadOnlyList<ClassifiedEffect> Effects { get; }
        internal ExecutionTicket(string leaseId, TrustedExecutionGrant grant, long authorityGeneration, LeaseContext context, ClassifiedEffect[] effects)
        { LeaseId = leaseId; GrantId = grant.Id; GrantGeneration = grant.Generation; AuthorityGeneration = authorityGeneration; Context = context; Effects = Array.AsReadOnly(effects); }
        internal void Rebase(long revision) { Context = new LeaseContext(Context.Epoch, Context.Entity, revision, Context.SceneReady); }
    }

    /// <summary>The slice of the authority that loopback workers may call. It reads authority-held state and the clock only.</summary>
    internal interface IControlAuthority
    {
        string AcquireLease(long durationMilliseconds, string purpose, string grantId = null, long grantGeneration = 0);
        void RenewLease(string id, long durationMilliseconds);
        bool ReleaseLease(string id);
        bool Heartbeat(string id);
        string LeaseFailureReason(string id);
        LeaseDetails DescribeLease(string id);
        ControlStatusInfo Status();
        string PublishedEpoch { get; }
        long PublishedRevision { get; }
    }

    internal sealed class LeaseDetails
    {
        public string LeaseId, GrantId, Epoch, Entity, Purpose;
        public long Generation, ExpiresInSeconds;
    }

    internal sealed class ExecutionAuthority : IControlAuthority
    {
        private const long MaxGrantMilliseconds = 7L * 24 * 3600 * 1000;
        private const long CooldownMilliseconds = ControlLimits.CooldownSeconds * 1000L;
        internal const string SeenReason = "seen";
        private static readonly TimeSpan SuspensionRetention = TimeSpan.FromDays(30);
        private readonly object gate = new object();
        private readonly Func<long> monotonicMilliseconds;
        private readonly Func<DateTime> utcNow;
        private readonly HashSet<string> knownEffects;
        private readonly ISuspensionStore store;
        private readonly Dictionary<string, long> highestGenerations = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> burned = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Suspension> suspensions = new List<Suspension>();
        private readonly bool suspensionsUnreadable;
        private readonly long watchdogMilliseconds;
        private LeaseContext context;
        private long contextPublishedAt = long.MinValue;
        private GrantBinding binding;
        private GrantStatusInfo publishedStatus = GrantStatusInfo.Missing();
        private TrustedExecutionGrant grant;
        private long grantDeadline;
        private string leaseId, leasePurpose, leaseEpoch, leaseEntity, lastEndedLeaseId, lastEndedReason, operationId;
        private long leaseDeadline, heartbeatDeadline, generation, lastClock, cooldownUntil;

        public ExecutionAuthority(Func<long> monotonicMilliseconds, IEnumerable<string> knownEffects, long watchdogMilliseconds = ControlLimits.WatchdogMilliseconds,
            ISuspensionStore store = null, Func<DateTime> utcNow = null)
        {
            this.monotonicMilliseconds = monotonicMilliseconds ?? throw new ArgumentNullException(nameof(monotonicMilliseconds));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.knownEffects = new HashSet<string>((knownEffects ?? throw new ArgumentNullException(nameof(knownEffects))).Select(e => Identifiers.Required(e)), StringComparer.Ordinal);
            if (watchdogMilliseconds < 1 || watchdogMilliseconds > 5000) throw new ArgumentException("invalid_watchdog");
            this.watchdogMilliseconds = watchdogMilliseconds; lastClock = monotonicMilliseconds();
            this.store = store ?? new MemorySuspensionStore();
            try
            {
                foreach (var item in this.store.Load())
                {
                    suspensions.Add(item);
                    // "seen" entries only record the highest generation per grant id so an older file cannot return after a restart.
                    if (item.Reason != SeenReason) burned.Add(Key(item.GrantId, item.Generation));
                    long known; if (!highestGenerations.TryGetValue(item.GrantId, out known) || item.Generation > known) highestGenerations[item.GrantId] = item.Generation;
                }
            }
            catch (Exception) { suspensionsUnreadable = true; } // Fail closed: no grant is provisioned if suspensions cannot be read.
        }

        public bool SuspensionsUnreadable => suspensionsUnreadable;
        public bool PersistFailed { get; private set; }

        // ---- main thread only ----

        /// <summary>Publishes the lease context and binding observed this frame. The binding change drops the grant, never the world or scene.</summary>
        public void UpdateContext(LeaseContext observed, GrantBinding observedBinding, GrantStatusInfo status = null)
        {
            if (observed == null) throw new ArgumentNullException(nameof(observed));
            lock (gate)
            {
                var now = CheckExpiry();
                if (status != null) publishedStatus = status;
                binding = observedBinding;
                if (grant != null && (observedBinding == null || !grant.Binding.Equals(observedBinding))) DropGrant("binding_changed");
                var previous = context;
                context = observed; contextPublishedAt = now;
                if (previous != null && observed.SameIdentity(previous) && observed.Revision < previous.Revision)
                { RevokeLease("revision_regressed"); throw new InvalidOperationException("revision_regressed"); }
                if (previous != null && !observed.SameIdentity(previous)) RevokeLease("context_changed");
                else if (leaseId != null && (leaseEpoch != observed.Epoch || leaseEntity != observed.Entity)) RevokeLease("context_changed");
            }
        }

        public void PublishGrantStatus(GrantStatusInfo status)
        { if (status == null) throw new ArgumentNullException(nameof(status)); lock (gate) publishedStatus = status; }

        /// <summary>Only the trusted watcher may call this. Never expose through ordinary tools.</summary>
        public void ProvisionGrant(TrustedExecutionGrant value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            lock (gate)
            {
                var now = CheckExpiry();
                if (binding == null || !value.Binding.Equals(binding)) throw new InvalidOperationException("grant_context_mismatch");
                if (IsBurnedLocked(value.Id, value.Generation)) throw new InvalidOperationException("grant_suspended");
                var remaining = value.ExpiresUtc - utcNow();
                if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("grant_expired");
                long previousHighest;
                if (highestGenerations.TryGetValue(value.Id, out previousHighest) && value.Generation < previousHighest) throw new InvalidOperationException("grant_generation_regressed");
                if (grant != null && grant.Id == value.Id && grant.Generation == value.Generation) return;
                if (grant != null) RevokeLease("grant_replaced");
                var milliseconds = Math.Min((long)remaining.TotalMilliseconds, MaxGrantMilliseconds);
                grant = value; grantDeadline = now + milliseconds; RaiseHighest(value.Id, value.Generation);
            }
        }

        /// <summary>Records a generation seen in a verified file so an older file cannot be replayed.</summary>
        public void NoteGeneration(string grantId, long grantGeneration)
        {
            lock (gate) RaiseHighest(grantId, grantGeneration);
        }

        public long HighestGeneration(string grantId)
        { lock (gate) { long h; return highestGenerations.TryGetValue(grantId, out h) ? h : 0; } }

        public bool IsBurned(string grantId, long grantGeneration)
        { lock (gate) return IsBurnedLocked(grantId, grantGeneration); }

        /// <summary>True while a lease is held. Cheap enough to read every frame.</summary>
        public bool LeaseHeld { get { lock (gate) return leaseId != null; } }
        public string CurrentGrantId { get { lock (gate) return grant?.Id; } }
        public long CurrentGrantGeneration { get { lock (gate) return grant?.Generation ?? 0; } }

        /// <summary>Clears the lease and the grant but leaves the generation usable again.</summary>
        public void DropGrant(string reason)
        { lock (gate) { RevokeLease(reason); grant = null; grantDeadline = 0; } }

        /// <summary>Drops the grant and refuses its (id, generation) from now on. "stop" and "fault" are persisted.</summary>
        public void BurnGrant(string reason)
        {
            lock (gate)
            {
                var burning = grant;
                DropGrant(reason);
                if (burning != null) BurnPair(burning.Id, burning.Generation, reason);
            }
        }

        /// <summary>
        /// Burns the provisioned grant. With nothing provisioned (for example dropped for a binding change) it burns the
        /// last verified, unrevoked, unexpired (id, generation) the watcher published, so Stop cannot be sidestepped.
        /// </summary>
        public void Stop()
        {
            lock (gate)
            {
                if (grant != null) { BurnGrant("stop"); return; }
                RevokeLease("stop");
                var seen = publishedStatus;
                if (seen.Id != null && seen.Generation.HasValue && (seen.State == GrantStates.Valid || seen.State == GrantStates.BindingMismatch || seen.State == GrantStates.NotYetApplicable))
                    BurnPair(seen.Id, seen.Generation.Value, "stop");
            }
        }

        private void BurnPair(string id, long grantGeneration, string reason)
        {
            burned.Add(Key(id, grantGeneration)); RaiseHighest(id, grantGeneration);
            if (reason == "stop" || reason == "fault") Persist(id, grantGeneration, reason);
        }

        /// <summary>True when a persistence write failed, so a Stop may not survive a restart. Shown in status and the panel.</summary>
        public bool StopPersistFailed { get { lock (gate) return PersistFailed; } }

        private void RaiseHighest(string id, long grantGeneration)
        {
            long known;
            if (highestGenerations.TryGetValue(id, out known) && grantGeneration <= known) return;
            highestGenerations[id] = grantGeneration;
            suspensions.RemoveAll(s => s.Reason == SeenReason && s.GrantId == id);
            suspensions.Add(new Suspension { GrantId = id, Generation = grantGeneration, Reason = SeenReason, Utc = GrantPayload.FormatUtc(utcNow()) });
            try { store.Save(suspensions); PersistFailed = false; } catch (Exception) { PersistFailed = true; }
        }

        public void HumanTakeover()
        { lock (gate) { var now = monotonicMilliseconds(); RevokeLease("human_takeover"); cooldownUntil = now + CooldownMilliseconds; } }

        public void Tick() { lock (gate) CheckExpiry(); }

        /// <summary>
        /// Drops suspensions older than 30 days whose generation is below the highest known for the same grant id, and every
        /// "seen" record older than 30 days (it only guards against an old file returning, and a watcher poll after a restart notes
        /// the verified generation again). Stop and fault entries of the highest generation are kept.
        /// </summary>
        public void PruneSuspensions()
        {
            lock (gate)
            {
                var cutoff = utcNow() - SuspensionRetention;
                var kept = suspensions.Where(s => !(Age(s) < cutoff && (s.Reason == SeenReason || s.Generation < HighestOf(s.GrantId)))).ToList();
                if (kept.Count == suspensions.Count) return;
                suspensions.Clear(); suspensions.AddRange(kept);
                try { store.Save(suspensions); PersistFailed = false; } catch (Exception) { PersistFailed = true; }
            }
        }

        public ExecutionTicket Admit(string id, long expectedRevision, IEnumerable<ClassifiedEffect> completeEffects)
        {
            lock (gate)
            {
                CheckExpiry(); RequireAuthority(id);
                if (context.Revision != expectedRevision) throw new InvalidOperationException("stale_revision");
                var effects = ValidateEffects(completeEffects);
                return new ExecutionTicket(id, grant, generation, context, effects);
            }
        }

        // This grants no enduring authority. A caller must recheck before every consequential callback
        // and reconcile partial effects rather than replay after failure.
        public void ValidateForDispatch(ExecutionTicket ticket, LeaseContext current, IEnumerable<ClassifiedEffect> completeCurrentEffects)
        { ValidateForDispatch(ticket, current, null, completeCurrentEffects); }

        public void ValidateForDispatch(ExecutionTicket ticket, LeaseContext current, GrantBinding currentBinding, IEnumerable<ClassifiedEffect> completeCurrentEffects)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));
            lock (gate)
            {
                UpdateContext(current, currentBinding ?? binding); RequireAuthority(ticket.LeaseId);
                if (ticket.AuthorityGeneration != generation || ticket.GrantId != grant.Id || ticket.GrantGeneration != grant.Generation) throw new InvalidOperationException("authority_revoked");
                if (!ticket.Context.SameIdentity(context) || ticket.Context.Revision != context.Revision) throw new InvalidOperationException("stale_context");
                var effects = ValidateEffects(completeCurrentEffects);
                // Preserve multiplicity and callback order; changed bindings or symmetry are stale.
                if (effects.Length != ticket.Effects.Count || effects.Where((t, i) => !t.Same(ticket.Effects[i])).Any()) throw new InvalidOperationException("effects_changed");
            }
        }

        /// <summary>Marks the operation lock for this ticket's lease. Rebase is allowed only while it is held (including grace).</summary>
        public void BeginOperation(ExecutionTicket ticket, string id)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));
            lock (gate)
            {
                CheckExpiry(); RequireAuthority(ticket.LeaseId); RequireTicketCurrent(ticket);
                if (operationId != null) throw new InvalidOperationException("operation_in_progress");
                operationId = Identifiers.Required(id);
            }
        }

        public void EndOperation(string id)
        { lock (gate) { if (operationId == id) operationId = null; } }

        /// <summary>Replaces the ticket's revision after the operation's own edit. Only valid during that operation's lock or grace.</summary>
        public void RebaseUnderOperation(ExecutionTicket ticket, string id, long newRevision)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));
            if (newRevision < 0) throw new ArgumentException("invalid_revision");
            lock (gate)
            {
                if (id == null || operationId != id) throw new InvalidOperationException("rebase_outside_operation");
                RequireAuthority(ticket.LeaseId); RequireTicketCurrent(ticket);
                ticket.Rebase(newRevision);
            }
        }

        // ---- loopback workers (any thread): authority-held state and the clock only ----

        /// <summary>Acquires against the currently provisioned grant, or the named one.</summary>
        public string AcquireLease(long durationMilliseconds, string purpose, string grantId = null, long grantGeneration = 0)
        {
            lock (gate)
            {
                var now = CheckExpiry();
                if (grant == null) throw new InvalidOperationException(ControlReasons.ForGrantState(publishedStatus.State == GrantStates.Valid ? GrantStates.Missing : publishedStatus.State));
                if (grantId != null && (grant.Id != grantId || grant.Generation != grantGeneration)) throw new InvalidOperationException("grant_unavailable");
                if (leaseId != null) throw new InvalidOperationException(ControlReasons.ControlBusy);
                if (now < cooldownUntil) throw new InvalidOperationException(ControlReasons.HumanActivityCooldown);
                if (durationMilliseconds < ControlLimits.DurationMinSeconds * 1000L || durationMilliseconds > ControlLimits.DurationMaxSeconds * 1000L) throw new ArgumentException("invalid_lease_duration");
                // A stalled main thread cannot vouch for the scene, so new leases are refused. Existing leases keep their heartbeats.
                if (context == null || now - contextPublishedAt > ControlLimits.ContextStaleMilliseconds || !context.SceneReady) throw new InvalidOperationException(ControlReasons.EditorUnavailable);
                if (!grant.AllowsEntity(context.Entity)) throw new InvalidOperationException(ControlReasons.FacilityMismatch);
                leaseId = Guid.NewGuid().ToString("N"); leasePurpose = purpose ?? ""; leaseEpoch = context.Epoch; leaseEntity = context.Entity;
                leaseDeadline = Math.Min(now + durationMilliseconds, grantDeadline); heartbeatDeadline = Deadline(now);
                return leaseId;
            }
        }

        /// <summary>Extends the lease from now, never past the grant deadline.</summary>
        public void RenewLease(string id, long durationMilliseconds)
        {
            lock (gate)
            {
                var now = CheckExpiry();
                if (durationMilliseconds < ControlLimits.DurationMinSeconds * 1000L || durationMilliseconds > ControlLimits.DurationMaxSeconds * 1000L) throw new ArgumentException("invalid_lease_duration");
                if (leaseId == null || id != leaseId || grant == null) throw new InvalidOperationException(EndedReason(id));
                leaseDeadline = Math.Min(now + durationMilliseconds, grantDeadline); heartbeatDeadline = Deadline(now);
            }
        }

        public bool ReleaseLease(string id)
        {
            lock (gate) { CheckExpiry(); if (leaseId == null || id != leaseId) return false; RevokeLease("released"); return true; }
        }

        public bool Heartbeat(string id)
        {
            lock (gate) { var now = CheckExpiry(); if (leaseId == null || id != leaseId) return false; heartbeatDeadline = Deadline(now); return true; }
        }

        /// <summary>Why a lease id is not usable, as a reason code. Unknown ids are lease_invalid.</summary>
        public string LeaseFailureReason(string id)
        { lock (gate) return EndedReason(id); }

        public ControlStatusInfo Status()
        {
            lock (gate)
            {
                long now;
                try { now = CheckExpiry(); } catch (InvalidOperationException) { now = lastClock; }
                var info = publishedStatus;
                var shown = new GrantStatusInfo
                {
                    Present = info.Present, State = info.State, Detail = info.Detail, Id = info.Id, Generation = info.Generation, Operations = info.Operations,
                    Facilities = info.Facilities, UnsavedCraftPolicy = info.UnsavedCraftPolicy, ExpiresUtc = info.ExpiresUtc
                };
                if (shown.State == GrantStates.Valid && shown.Id != null && shown.Generation.HasValue && IsBurnedLocked(shown.Id, shown.Generation.Value))
                { shown.State = GrantStates.Suspended; shown.Detail = suspensionsUnreadable ? "suspensions_unreadable" : null; }
                var lease = new LeaseStatusInfo { Held = leaseId != null };
                if (leaseId != null) { lease.Purpose = leasePurpose; lease.ExpiresInSeconds = Math.Max(0, (leaseDeadline - now + 999) / 1000); }
                return new ControlStatusInfo { Grant = shown, Lease = lease, CooldownSeconds = Math.Max(0, (cooldownUntil - now + 999) / 1000), StopPersistFailed = PersistFailed };
            }
        }

        public LeaseDetails DescribeLease(string id)
        {
            lock (gate)
            {
                long now;
                try { now = CheckExpiry(); } catch (InvalidOperationException) { now = lastClock; }
                if (leaseId == null || id != leaseId || grant == null) return null;
                return new LeaseDetails { LeaseId = leaseId, GrantId = grant.Id, Generation = grant.Generation, Epoch = leaseEpoch, Entity = leaseEntity, Purpose = leasePurpose,
                    ExpiresInSeconds = Math.Max(0, (leaseDeadline - now + 999) / 1000) };
            }
        }
        public string PublishedEpoch { get { lock (gate) return context?.Epoch; } }
        public long PublishedRevision { get { lock (gate) return context?.Revision ?? 0; } }

        // ---- internals (call with the gate held) ----

        private ClassifiedEffect[] ValidateEffects(IEnumerable<ClassifiedEffect> supplied)
        {
            var effects = (supplied ?? throw new ArgumentNullException(nameof(supplied))).Take(1025).ToArray();
            if (effects.Length == 0 || effects.Length > 1024) throw new InvalidOperationException("invalid_effect_set");
            foreach (var effect in effects)
                if (effect == null || !knownEffects.Contains(effect.Operation) || !grant.Allows(effect)) throw new InvalidOperationException("effect_denied_or_unknown");
            return effects;
        }

        private void RequireAuthority(string id)
        {
            if (leaseId == null || leaseId != id || grant == null || context == null) throw new InvalidOperationException("authority_unavailable");
            // Not ready is a refusal, not a revocation: the editor may simply be restarting.
            if (!context.SceneReady) throw new InvalidOperationException(ControlReasons.EditorUnavailable);
        }

        private void RequireTicketCurrent(ExecutionTicket ticket)
        {
            if (ticket.AuthorityGeneration != generation || ticket.GrantId != grant.Id || ticket.GrantGeneration != grant.Generation) throw new InvalidOperationException("authority_revoked");
        }

        private long CheckExpiry()
        {
            var now = monotonicMilliseconds();
            if (now < lastClock) { lastClock = now; BurnGrant("fault"); throw new InvalidOperationException(ControlReasons.ClockRegressed); }
            lastClock = now;
            if (grant != null && (now >= grantDeadline || utcNow() >= grant.ExpiresUtc)) BurnGrant("expired");
            if (leaseId != null)
            {
                if (now >= leaseDeadline) RevokeLease("lease_expired");
                else if (now >= heartbeatDeadline) RevokeLease("watchdog");
            }
            return now;
        }

        private long Deadline(long now) => now > long.MaxValue - watchdogMilliseconds ? long.MaxValue : now + watchdogMilliseconds;

        /// <summary>Increments the authority generation and clears only the lease.</summary>
        private void RevokeLease(string reason)
        {
            generation = checked(generation + 1);
            if (leaseId != null) { lastEndedLeaseId = leaseId; lastEndedReason = reason; }
            leaseId = null; leasePurpose = null; leaseEpoch = null; leaseEntity = null; operationId = null;
        }

        private string EndedReason(string id)
        {
            if (id == null || id != lastEndedLeaseId) return ControlReasons.LeaseInvalid;
            switch (lastEndedReason)
            {
                case "lease_expired": case "watchdog": return ControlReasons.LeaseExpired;
                case "released": return ControlReasons.LeaseInvalid;
                default: return ControlReasons.AuthorityRevoked;
            }
        }

        private bool IsBurnedLocked(string grantId, long grantGeneration) => suspensionsUnreadable || burned.Contains(Key(grantId, grantGeneration));
        private static string Key(string grantId, long grantGeneration) => grantId + "#" + grantGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private void Persist(string grantId, long grantGeneration, string reason)
        {
            suspensions.Add(new Suspension { GrantId = grantId, Generation = grantGeneration, Reason = reason, Utc = GrantPayload.FormatUtc(utcNow()) });
            try { store.Save(suspensions); PersistFailed = false; } catch (Exception) { PersistFailed = true; }
        }

        private long HighestOf(string id) { long h; return highestGenerations.TryGetValue(id, out h) ? h : 0; }

        private static DateTime Age(Suspension s)
        { DateTime value; return GrantCodec.TryParseUtc(s.Utc, out value) ? value : DateTime.MinValue; }
    }
}
