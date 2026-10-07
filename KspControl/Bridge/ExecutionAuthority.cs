using System;
using System.Collections.Generic;
using System.Linq;

namespace KspControl.Bridge
{
    // Pure policy only. There is deliberately no transport endpoint, Unity callback or actuator here.
    // A future main-thread adapter must resolve the COMPLETE current effect set itself, rather
    // than trust a model-supplied list, and call ValidateForDispatch immediately before each effect.
    internal sealed class ExecutionContext
    {
        public string Install { get; }
        public string World { get; }
        public string Agency { get; }
        public string Vessel { get; }
        public long Revision { get; }
        public bool OwnedAndControlled { get; }
        public bool SceneReady { get; }
        public ExecutionContext(string install, string world, string agency, string vessel, long revision, bool ownedAndControlled = true, bool sceneReady = true)
        {
            Install = Required(install); World = Required(world); Agency = Required(agency); Vessel = Required(vessel);
            if (revision < 0) throw new ArgumentException("invalid_revision");
            Revision = revision; OwnedAndControlled = ownedAndControlled; SceneReady = sceneReady;
        }
        internal bool SameIdentity(ExecutionContext other) => other != null && Install == other.Install && World == other.World && Agency == other.Agency && Vessel == other.Vessel;
        internal static string Required(string value)
        { if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new ArgumentException("invalid_identifier"); return value; }
    }

    internal sealed class ClassifiedEffect
    {
        public string Operation { get; }
        public string Recipient { get; }
        public long ModuleGeneration { get; }
        public ClassifiedEffect(string operation, string recipient, long moduleGeneration)
        {
            Operation = ExecutionContext.Required(operation); Recipient = ExecutionContext.Required(recipient);
            if (moduleGeneration < 0) throw new ArgumentException("invalid_module_generation"); ModuleGeneration = moduleGeneration;
        }
        internal bool Same(ClassifiedEffect other) => Operation == other.Operation && Recipient == other.Recipient && ModuleGeneration == other.ModuleGeneration;
    }

    internal sealed class EffectPermission
    {
        public string Operation { get; }
        public string Recipient { get; }
        public EffectPermission(string operation, string recipient)
        { Operation = ExecutionContext.Required(operation); Recipient = ExecutionContext.Required(recipient); }
    }

    internal sealed class TrustedExecutionGrant
    {
        public string Id { get; }
        public long Generation { get; }
        public ExecutionContext Binding { get; }
        public long ExpiresAtMilliseconds { get; }
        private readonly EffectPermission[] permissions;
        public TrustedExecutionGrant(string id, long generation, ExecutionContext binding, long expiresAtMilliseconds, IEnumerable<EffectPermission> permissions)
        {
            Id = ExecutionContext.Required(id); if (generation <= 0) throw new ArgumentException("invalid_grant_generation");
            Generation = generation; Binding = binding ?? throw new ArgumentNullException(nameof(binding)); ExpiresAtMilliseconds = expiresAtMilliseconds;
            this.permissions = (permissions ?? throw new ArgumentNullException(nameof(permissions))).Take(1025).ToArray();
            if (this.permissions.Length == 0 || this.permissions.Length > 1024 || this.permissions.Any(p => p == null)) throw new ArgumentException("invalid_permissions");
        }
        internal bool Allows(ClassifiedEffect effect) => permissions.Any(p => p.Operation == effect.Operation && p.Recipient == effect.Recipient);
    }

    internal sealed class ExecutionTicket
    {
        internal string LeaseId { get; }
        internal string GrantId { get; }
        internal long GrantGeneration { get; }
        internal long AuthorityGeneration { get; }
        internal ExecutionContext Context { get; }
        internal IReadOnlyList<ClassifiedEffect> Effects { get; }
        internal ExecutionTicket(string leaseId, TrustedExecutionGrant grant, long authorityGeneration, ExecutionContext context, ClassifiedEffect[] effects)
        { LeaseId = leaseId; GrantId = grant.Id; GrantGeneration = grant.Generation; AuthorityGeneration = authorityGeneration; Context = context; Effects = Array.AsReadOnly(effects); }
    }

    internal sealed class ExecutionAuthority
    {
        private readonly object gate = new object();
        private readonly Func<long> monotonicMilliseconds;
        private readonly HashSet<string> knownEffects;
        private readonly Dictionary<string, long> grantedGenerations = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly long watchdogMilliseconds;
        private ExecutionContext context;
        private TrustedExecutionGrant grant;
        private string leaseId;
        private long leaseDeadline, heartbeatDeadline, generation, lastClock;

        public ExecutionAuthority(Func<long> monotonicMilliseconds, IEnumerable<string> knownEffects, long watchdogMilliseconds = 2000)
        {
            this.monotonicMilliseconds = monotonicMilliseconds ?? throw new ArgumentNullException(nameof(monotonicMilliseconds));
            this.knownEffects = new HashSet<string>((knownEffects ?? throw new ArgumentNullException(nameof(knownEffects))).Select(ExecutionContext.Required), StringComparer.Ordinal);
            if (watchdogMilliseconds < 1 || watchdogMilliseconds > 5000) throw new ArgumentException("invalid_watchdog");
            this.watchdogMilliseconds = watchdogMilliseconds; lastClock = monotonicMilliseconds();
        }
        public void UpdateContext(ExecutionContext observed)
        {
            if (observed == null) throw new ArgumentNullException(nameof(observed));
            lock (gate)
            {
                if (observed.SameIdentity(context) && observed.Revision < context.Revision)
                { Revoke(); throw new InvalidOperationException("revision_regressed"); }
                if (!observed.SameIdentity(context) || !observed.OwnedAndControlled || !observed.SceneReady) Revoke();
                context = observed;
                CheckExpiry();
            }
        }
        // Only trusted local provisioning may call this. Never expose through ordinary MCP tools.
        public void ProvisionTrustedGrant(TrustedExecutionGrant value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            lock (gate)
            {
                var now = CheckExpiry();
                if (context == null || !context.OwnedAndControlled || !context.SceneReady || !value.Binding.SameIdentity(context)) throw new InvalidOperationException("grant_context_mismatch");
                if (value.ExpiresAtMilliseconds <= now) throw new InvalidOperationException("grant_expired");
                if (grantedGenerations.TryGetValue(value.Id, out var previous) && value.Generation <= previous) throw new InvalidOperationException("grant_generation_reused");
                Revoke(); grant = value; grantedGenerations[value.Id] = value.Generation;
            }
        }
        public string AcquireLease(string grantId, long grantGeneration, long durationMilliseconds)
        {
            lock (gate)
            {
                var now = CheckExpiry();
                if (grant == null || grant.Id != grantId || grant.Generation != grantGeneration) throw new InvalidOperationException("grant_unavailable");
                if (leaseId != null) throw new InvalidOperationException("control_busy");
                if (durationMilliseconds <= 0 || durationMilliseconds > 300000 || now > long.MaxValue - durationMilliseconds) throw new ArgumentException("invalid_lease_duration");
                leaseId = Guid.NewGuid().ToString("N"); leaseDeadline = Math.Min(now + durationMilliseconds, grant.ExpiresAtMilliseconds);
                heartbeatDeadline = Deadline(now); return leaseId;
            }
        }
        public bool Heartbeat(string id)
        {
            lock (gate) { var now = CheckExpiry(); if (leaseId == null || id != leaseId) return false; heartbeatDeadline = Deadline(now); return true; }
        }
        public void Tick() { lock (gate) CheckExpiry(); }
        public void Stop() { lock (gate) Revoke(); }
        public void HumanTakeover() => Stop();

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
        // This grants no enduring authority. A future caller must recheck before every consequential
        // callback, and reconcile partial effects rather than replay after failure.
        public void ValidateForDispatch(ExecutionTicket ticket, ExecutionContext current, IEnumerable<ClassifiedEffect> completeCurrentEffects)
        {
            if (ticket == null) throw new ArgumentNullException(nameof(ticket));
            lock (gate)
            {
                UpdateContext(current); RequireAuthority(ticket.LeaseId);
                if (ticket.AuthorityGeneration != generation || ticket.GrantId != grant.Id || ticket.GrantGeneration != grant.Generation) throw new InvalidOperationException("authority_revoked");
                if (!ticket.Context.SameIdentity(context) || ticket.Context.Revision != context.Revision) throw new InvalidOperationException("stale_context");
                var effects = ValidateEffects(completeCurrentEffects);
                // Preserve multiplicity and callback order; changed bindings or symmetry are stale.
                if (effects.Length != ticket.Effects.Count || effects.Where((t, i) => !t.Same(ticket.Effects[i])).Any()) throw new InvalidOperationException("effects_changed");
            }
        }
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
            if (leaseId == null || leaseId != id || grant == null || context == null || !context.OwnedAndControlled || !context.SceneReady) throw new InvalidOperationException("authority_unavailable");
        }
        private long CheckExpiry()
        {
            var now = monotonicMilliseconds();
            if (now < lastClock) { Revoke(); throw new InvalidOperationException("clock_regressed"); }
            lastClock = now;
            if ((grant != null && now >= grant.ExpiresAtMilliseconds) || (leaseId != null && (now >= leaseDeadline || now >= heartbeatDeadline))) Revoke();
            return now;
        }
        private long Deadline(long now) => now > long.MaxValue - watchdogMilliseconds ? long.MaxValue : now + watchdogMilliseconds;
        private void Revoke() { generation = checked(generation + 1); leaseId = null; grant = null; }
    }
}
