using System;
using System.IO;
using System.Linq;
using KspControl.Contracts;

namespace KspControl.Bridge
{
    /// <summary>The Unity-touching inputs, isolated so the watcher and pump can be tested without the game.</summary>
    internal interface IEditorContextSource
    {
        /// <summary>The lease context observed this frame. Main thread only.</summary>
        LeaseContext CurrentContext();
        /// <summary>(install, save folder, agency key), or null when no save is loaded. Main thread only.</summary>
        GrantBinding CurrentBinding();
    }

    internal static class GrantMapping
    {
        /// <summary>Effects the bridge knows how to classify. The flight family (flight.autopilot) is granted per scene, not per facility.</summary>
        internal static readonly string[] KnownEffects = { "editor.replace_craft", "editor.restore_snapshot", "craft.write", AutopilotOperations.Effect };

        internal static TrustedExecutionGrant ToGrant(GrantPayload payload)
        {
            var known = payload.Operations.Distinct(StringComparer.Ordinal).Where(o => Array.IndexOf(KnownEffects, o) >= 0).ToArray();
            var facilityEffects = known.Where(o => !o.StartsWith("flight.", StringComparison.Ordinal));
            var permissions = facilityEffects
                .SelectMany(o => payload.Facilities.Select(f => new EffectPermission(o, (o.StartsWith("craft.", StringComparison.Ordinal) ? "ships:" : "editor:") + f))).ToList();
            var entities = payload.Facilities.Select(f => "editor:" + f).ToList();
            if (known.Contains(AutopilotOperations.Effect, StringComparer.Ordinal))
            {
                permissions.Add(new EffectPermission(AutopilotOperations.Effect, AutopilotOperations.Recipient));
                entities.Add(AutopilotOperations.Entity);
            }
            return new TrustedExecutionGrant(payload.GrantId, payload.Generation, GrantBinding.From(payload.Binding), payload.ExpiresAt, permissions, entities);
        }
    }

    /// <summary>
    /// Main-thread poll (1 Hz) of the grant file. A stat change triggers a full re-verify. The result is published
    /// to the authority as an immutable status, and a valid grant is provisioned while anything else drops it.
    /// </summary>
    internal sealed class GrantWatcher
    {
        private const long PollMilliseconds = 1000;
        private const int MaxFileBytes = 65536;
        private const int MaxUnreadablePolls = 3;
        private readonly ExecutionAuthority authority;
        private readonly string grantPath, keyPath;
        private readonly Func<GrantBinding> currentBinding;
        private readonly Func<long> clock;
        private readonly Func<DateTime> utcNow;
        private string signature;
        private GrantVerification verification;
        private int unreadablePolls;
        private long lastPoll = long.MinValue;
        private long prunedGeneration;
        public GrantStatusInfo Status { get; private set; } = GrantStatusInfo.Missing("not_polled");

        public GrantWatcher(ExecutionAuthority authority, string grantPath, string keyPath, Func<GrantBinding> currentBinding, Func<long> clock = null, Func<DateTime> utcNow = null)
        {
            this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
            this.grantPath = grantPath; this.keyPath = keyPath;
            this.currentBinding = currentBinding ?? throw new ArgumentNullException(nameof(currentBinding));
            this.clock = clock ?? (() => MonotonicClock.Milliseconds); this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Called every Update; does work at most once per second.</summary>
        public void Poll()
        {
            var now = clock();
            if (lastPoll != long.MinValue && now - lastPoll < PollMilliseconds) return;
            lastPoll = now; PollNow();
        }

        public void PollNow()
        {
            RefreshVerification();
            GrantBinding binding;
            try { binding = currentBinding(); } catch (Exception) { binding = null; }
            var verified = verification.Ok ? verification.Payload : null;
            var status = GrantEvaluator.Evaluate(verification, utcNow(), binding?.ToInfo(), authority.IsBurned, verified == null ? 0 : authority.HighestGeneration(verified.GrantId));
            if (verified != null && status.Detail != "generation_regressed") authority.NoteGeneration(verified.GrantId, verified.Generation);
            if (status.State == GrantStates.Valid)
            {
                try { authority.ProvisionGrant(GrantMapping.ToGrant(verified)); }
                catch (InvalidOperationException error) { status = Downgrade(status, error.Message); }
                catch (ArgumentException) { status = Downgrade(status, "malformed"); }
                if (status.State == GrantStates.Valid && verified.Generation != prunedGeneration) { prunedGeneration = verified.Generation; authority.PruneSuspensions(); }
            }
            else if (authority.CurrentGrantId != null) authority.DropGrant(status.State);
            Status = status;
            authority.PublishGrantStatus(status);
        }

        private static GrantStatusInfo Downgrade(GrantStatusInfo valid, string code)
        {
            string state;
            switch (code)
            {
                case "grant_suspended": state = GrantStates.Suspended; break;
                case "grant_expired": state = GrantStates.Expired; break;
                case "grant_generation_regressed": state = GrantStates.Revoked; break;
                case "grant_context_mismatch": state = GrantStates.BindingMismatch; break;
                case "malformed": state = GrantStates.Malformed; break;
                default: state = GrantStates.Missing; break;
            }
            return new GrantStatusInfo
            {
                Present = true, State = state, Detail = code, Id = valid.Id, Generation = valid.Generation, Operations = valid.Operations,
                Facilities = valid.Facilities, UnsavedCraftPolicy = valid.UnsavedCraftPolicy, ExpiresUtc = valid.ExpiresUtc
            };
        }

        private void RefreshVerification()
        {
            string current;
            try { current = Stat(grantPath) + "|" + Stat(keyPath); }
            catch (Exception) { current = "unstatable"; }
            if (verification != null && current == signature) return;
            try
            {
                if (!File.Exists(grantPath)) { Set(current, GrantVerification.Fail(GrantStates.Missing, "grant_file_missing")); return; }
                if (!File.Exists(keyPath)) { Set(current, GrantVerification.Fail(GrantStates.Missing, "key_unavailable")); return; }
                if (new FileInfo(grantPath).Length > MaxFileBytes) { Set(current, GrantVerification.Fail(GrantStates.Malformed, "envelope_size")); return; }
                if (new FileInfo(keyPath).Length > 64) { Set(current, GrantVerification.Fail(GrantStates.Missing, "key_unavailable")); return; }
                var key = File.ReadAllBytes(keyPath);
                var text = new System.Text.UTF8Encoding(false, false).GetString(File.ReadAllBytes(grantPath));
                Set(current, GrantCodec.Verify(text, key));
            }
            catch (Exception)
            {
                // A writer may briefly hold the file. Keep the previous verdict for a few polls, then fail closed.
                if (++unreadablePolls >= MaxUnreadablePolls || verification == null) { verification = GrantVerification.Fail(GrantStates.Missing, "grant_unreadable"); signature = null; }
            }
        }

        private void Set(string current, GrantVerification value)
        { signature = current; verification = value; unreadablePolls = 0; }

        private static string Stat(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "absent";
        }
    }

    /// <summary>The per-frame main-thread step: publish the context, poll the grant file, tick the authority.</summary>
    internal sealed class ControlPump
    {
        private readonly ExecutionAuthority authority;
        private readonly GrantWatcher watcher;
        private readonly IEditorContextSource source;
        private readonly EditorRevisionTracker tracker;
        public ControlPump(ExecutionAuthority authority, GrantWatcher watcher, IEditorContextSource source, EditorRevisionTracker tracker = null)
        { this.authority = authority; this.watcher = watcher; this.source = source; this.tracker = tracker; }

        public void Update()
        {
            // Every step is isolated so one failing input can never skip the rest, in particular Tick.
            // The tracker runs first: a human edit takes over the lease before the context (and its revision) is published.
            try { tracker?.Update(); } catch (Exception) { /* editor teardown: the next frame observes again */ }
            LeaseContext context = null; GrantBinding binding = null;
            try { context = source.CurrentContext(); } catch (Exception) { /* scene teardown: keep the last published context */ }
            try { binding = source.CurrentBinding(); } catch (Exception) { binding = null; /* invalid binding means binding_mismatch */ }
            if (context != null)
            {
                try { authority.UpdateContext(context, binding, watcher?.Status); }
                catch (Exception) { /* revision regression already revoked the lease */ }
            }
            try { watcher?.Poll(); } catch (Exception) { /* a bad file must never break the frame */ }
            try { authority.Tick(); } catch (Exception) { /* clock regression already burned the grant */ }
        }
    }
}
