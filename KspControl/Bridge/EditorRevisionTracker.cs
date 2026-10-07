using System;
using System.Diagnostics;
using KspControl.Contracts;
using Pure = KspControl.EditorModel;

namespace KspControl.Bridge
{
    internal enum EditorEventKind
    {
        // Bare events: they only say "something may have changed" and are settled by the fingerprint.
        ShipModified, PartEvent, VariantApplied, ShipCrewModified, SetBackup,
        // Generation hints: the identity check settles them.
        Restart, Started, RestoreState,
        // Human-input events.
        PartPicked, PartPlaced, PartDeleted, PodPicked, PodDeleted, Undo, Redo, Load,
        // The part action window was shown or created: a human-input signal only inside an operation lock or grace window.
        PawShown
    }

    /// <summary>Where the tracker is relative to a KspControl operation (plan R3-§6.4 and R3-§7).</summary>
    internal enum OperationWindow
    {
        None,
        /// <summary>Operation lock held, outside the synchronous load call.</summary>
        Locked,
        /// <summary>Inside the synchronous LoadShipFromFile call: every raised event belongs to the operation.</summary>
        Dispatch,
        /// <summary>post_unlock_grace: locks still held, human input still means takeover.</summary>
        Grace
    }

    internal enum FingerprintMode { Full, DirtyTracked }

    /// <summary>What the tracker needs from the trust layer. Implemented over ExecutionAuthority in the bridge.</summary>
    internal interface ITakeoverSink
    {
        bool LeaseHeld { get; }
        void HumanTakeover();
    }

    internal sealed class AuthorityTakeoverSink : ITakeoverSink
    {
        private readonly ExecutionAuthority authority;
        public AuthorityTakeoverSink(ExecutionAuthority authority) { this.authority = authority ?? throw new ArgumentNullException(nameof(authority)); }
        public bool LeaseHeld => authority.LeaseHeld;
        public void HumanTakeover() { authority.HumanTakeover(); }
    }

    internal sealed class EditorObservation
    {
        public bool InEditor { get; set; }
        public long Generation { get; set; }
        public long EditRevision { get; set; }
        /// <summary>Full fingerprint hex (UI fields included), or null when it could not be computed.</summary>
        public string Fingerprint { get; set; }
        public bool FingerprintOk => Fingerprint != null;
        public EditorUi Ui { get; set; }
    }

    internal sealed class OperationReport
    {
        public int ObservedEvents { get; set; }
        public bool HumanInputDuringOperation { get; set; }
        public bool TakeoverDuringGrace { get; set; }
        public int GraceFingerprintChanges { get; set; }
        /// <summary>Monotonic millisecond stamp of the latest fingerprint change seen while in grace (or when grace began).</summary>
        public long LastGraceChangeAt { get; set; }
    }

    internal enum TokenCheck { Fresh, Invalid, Unavailable, Stale }

    /// <summary>
    /// Revision, generation, fingerprint and human-takeover tracking (plan R3-§7). Everything runs on the main thread:
    /// game events call <see cref="OnEvent"/>, the pump calls <see cref="Update"/> every frame, and requests call
    /// <see cref="Observe"/>. No fingerprint is computed inside an event handler, so a save fired from within a handler can never recurse.
    ///
    /// Outside an operation, with a lease held, any change (fingerprint, generation, UI field, selected part, human-input event)
    /// is a human takeover. With no lease, the same changes only bump the revision. A bare modification event with an unchanged
    /// fingerprint does nothing, which absorbs animation re-fires. During an operation, events are attributed to it (see the windows).
    /// </summary>
    internal sealed class EditorRevisionTracker
    {
        /// <summary>Per-poll fingerprint cost above which polling switches to the cheap-hash layer.</summary>
        internal const double DirtyTrackedCostMilliseconds = 20.0;
        internal const long DirtyThrottleMilliseconds = 250;
        internal const long LeasePollMilliseconds = 1000;
        internal const long CheapFullRefreshMilliseconds = 10000;
        internal const long GracePollMilliseconds = 500;
        private const int CostSamples = 5;

        private readonly IEditorPort port;
        private readonly ITakeoverSink sink;
        private readonly Func<long> clock;
        private readonly Func<double> costClock;
        private readonly Pure.RoundtripVolatileKeys registry;
        private readonly double[] costs = new double[CostSamples];
        private int costCount, costNext;

        private object shipId;
        private OperationWindow window;
        private bool hasBaseline, dirty, selectedHeld, capturing, checkOk, leaseSeen, suppressTakeover;
        private string bodyText, bodyHash, fingerprint, cheap;
        private EditorUi ui;
        private long lastFullAt = long.MinValue, lastCheapAt = long.MinValue;
        private readonly OperationReport report = new OperationReport();

        public long Generation { get; private set; }
        public long EditRevision { get; private set; }
        public int SelfEvents { get; private set; }
        public double LastPollCostMilliseconds { get; private set; }
        public string LastError { get; private set; }
        public OperationWindow Window => window;
        public OperationReport Report => report;

        public EditorRevisionTracker(IEditorPort port, ITakeoverSink sink, Func<long> clock = null, Func<double> costClock = null, Pure.RoundtripVolatileKeys registry = null)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.clock = clock ?? (() => MonotonicClock.Milliseconds);
            this.costClock = costClock ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
            this.registry = registry ?? Pure.RoundtripVolatileKeys.Default();
        }

        /// <summary>Full when the measured fingerprint cost stays under the threshold, dirty-tracked otherwise (worst of the last samples).</summary>
        public FingerprintMode Mode
        {
            get
            {
                var worst = 0.0;
                for (var i = 0; i < costCount; i++) if (costs[i] > worst) worst = costs[i];
                return worst > DirtyTrackedCostMilliseconds ? FingerprintMode.DirtyTracked : FingerprintMode.Full;
            }
        }

        public static bool IsHumanInput(EditorEventKind kind)
        {
            switch (kind)
            {
                case EditorEventKind.PartPicked: case EditorEventKind.PartPlaced: case EditorEventKind.PartDeleted:
                case EditorEventKind.PodPicked: case EditorEventKind.PodDeleted: case EditorEventKind.Undo: case EditorEventKind.Redo: case EditorEventKind.Load:
                    return true;
                default: return false;
            }
        }

        // ---- main thread entry points ----

        /// <summary>A game event fired. Cheap and re-entrancy safe: never captures the craft.</summary>
        public void OnEvent(EditorEventKind kind)
        {
            if (capturing) { SelfEvents++; return; } // raised synchronously by our own native save
            if (!port.InEditor) return;
            if (window == OperationWindow.Dispatch) { report.ObservedEvents++; dirty = true; return; }
            if (window == OperationWindow.None) CheckGeneration();
            if (IsHumanInput(kind)) { Change(ChangeKind.HumanInput); dirty = true; return; }
            if (kind == EditorEventKind.PawShown)
            {
                // A PAW is only a human-input signal while an operation owns the editor; otherwise a fingerprint change tells the truth.
                if (window == OperationWindow.Locked || window == OperationWindow.Grace) Change(ChangeKind.HumanInput);
                return;
            }
            if (window != OperationWindow.None) report.ObservedEvents++;
            dirty = true;
            if (kind == EditorEventKind.Restart || kind == EditorEventKind.Started || kind == EditorEventKind.RestoreState) CheckGeneration();
        }

        /// <summary>Called every frame before the context is published, so the lease context carries the current revision.</summary>
        public void Update()
        {
            if (!port.EditorScene) { Reset(); return; }
            if (!port.InEditor) return; // a transient null ship keeps the last known identity and window
            if (window == OperationWindow.Dispatch) return; // synchronous region: nothing observes between its steps
            SyncLease();
            CheckGeneration();
            CheckSelected();
            CheckUi();
            SchedulePoll(clock());
        }

        /// <summary>
        /// The state seen by a request. A forced observation recomputes the fingerprint now, which catches changes that fire no
        /// event and edits that were reverted (ABA).
        /// </summary>
        public EditorObservation Observe(bool force)
        {
            if (!port.EditorScene) Reset();
            if (!port.InEditor) return new EditorObservation { Generation = Generation, EditRevision = EditRevision };
            if (window != OperationWindow.Dispatch)
            {
                SyncLease();
                CheckGeneration(); CheckSelected(); CheckUi();
                var now = clock();
                if (force || !hasBaseline || (dirty && Due(now, lastFullAt, DirtyThrottleMilliseconds))) Check(now);
            }
            return new EditorObservation { InEditor = true, Generation = Generation, EditRevision = EditRevision, Fingerprint = hasBaseline && checkOk ? fingerprint : null, Ui = ui };
        }

        /// <summary>A native-save capture for read paths, under the same self-event guard as the tracker's own checks. Null on failure.</summary>
        public EditorCraft CaptureGuarded()
        {
            var was = capturing; capturing = true;
            try { return port.Capture(); }
            catch (Exception) { return null; }
            finally { capturing = was; }
        }

        /// <summary>The identity check only: cheap enough for any read path.</summary>
        public long RefreshGeneration()
        {
            if (port.InEditor && window != OperationWindow.Dispatch) CheckGeneration();
            return Generation;
        }

        /// <summary>Admission staleness: every token component equal, plus a freshly recomputed fingerprint.</summary>
        public TokenCheck CheckToken(string tokenText, string worldEpoch)
        {
            Pure.EditorRevisionToken token;
            if (!Pure.EditorRevisionToken.TryParse(tokenText, out token)) return TokenCheck.Invalid;
            var observed = Observe(true);
            if (!observed.InEditor || !observed.FingerprintOk) return TokenCheck.Unavailable;
            return token.Matches(worldEpoch, observed.Generation, observed.EditRevision, observed.Fingerprint) ? TokenCheck.Fresh : TokenCheck.Stale;
        }

        /// <summary>Encodes the token for a fresh observation, or null when the fingerprint is unavailable.</summary>
        public string Token(string worldEpoch, EditorObservation observed)
        {
            if (observed == null || !observed.InEditor || !observed.FingerprintOk) return null;
            return Pure.EditorRevisionToken.Create(worldEpoch, observed.Generation, observed.EditRevision, observed.Fingerprint).Encode();
        }

        // ---- operation windows (driven by the operation runner) ----

        public void BeginOperation()
        {
            window = OperationWindow.Locked;
            report.ObservedEvents = 0; report.HumanInputDuringOperation = false; report.TakeoverDuringGrace = false;
            report.GraceFingerprintChanges = 0; report.LastGraceChangeAt = 0;
        }

        public void BeginDispatch() { window = OperationWindow.Dispatch; }
        public void EndDispatch() { window = OperationWindow.Locked; }

        /// <summary>Starts post_unlock_grace. The baseline is captured now (silently), so later changes are measured against the finished operation.</summary>
        public void BeginGrace()
        {
            window = OperationWindow.Locked; // silent while the new baseline is adopted
            if (port.InEditor) { CheckGeneration(); CheckUi(); Check(clock()); }
            report.LastGraceChangeAt = clock();
            window = OperationWindow.Grace;
        }

        /// <summary>Ends the operation: the end state becomes the baseline. Returns the revision to rebase the ticket to.</summary>
        public long EndOperation()
        {
            window = OperationWindow.Locked; // adopt silently
            if (port.InEditor) { CheckGeneration(); CheckUi(); Check(clock()); }
            window = OperationWindow.None;
            dirty = false; selectedHeld = port.InEditor && port.HasSelectedPart;
            return EditRevision;
        }

        // ---- internals ----

        private enum ChangeKind { HumanInput, Selected, Generation, Ui, Fingerprint }

        private void Change(ChangeKind kind)
        {
            EditRevision = checked(EditRevision + 1);
            switch (window)
            {
                case OperationWindow.Dispatch:
                    return;
                case OperationWindow.Locked:
                    if (kind == ChangeKind.HumanInput || kind == ChangeKind.Selected) { report.HumanInputDuringOperation = true; Takeover(); }
                    return;
                case OperationWindow.Grace:
                    if (kind == ChangeKind.Fingerprint) { report.GraceFingerprintChanges++; report.LastGraceChangeAt = clock(); return; }
                    report.TakeoverDuringGrace = true; Takeover(); return;
                default:
                    Takeover(); return;
            }
        }

        /// <summary>True when an interval has elapsed since <paramref name="last"/>, or nothing has happened yet (long.MinValue). Avoids overflow on the unset stamp.</summary>
        private static bool Due(long now, long last, long interval) { return last == long.MinValue || now - last >= interval; }

        private void Takeover()
        { if (!suppressTakeover && sink.LeaseHeld) sink.HumanTakeover(); }

        /// <summary>
        /// A lease is acquired on a worker thread, so the tracker sees it a frame later. Whatever changed before the lease existed
        /// (including edits that fire no event) is not a takeover: re-baseline silently on the false-to-true transition.
        /// </summary>
        private void SyncLease()
        {
            var held = sink.LeaseHeld;
            if (held && !leaseSeen && window == OperationWindow.None)
            {
                suppressTakeover = true;
                try { CheckGeneration(); CheckUi(); Check(clock()); }
                finally { suppressTakeover = false; }
            }
            leaseSeen = held;
        }

        private void CheckGeneration()
        {
            var id = port.ShipIdentity;
            if (id == null || ReferenceEquals(id, shipId)) return;
            var first = shipId == null;
            shipId = id; Generation = checked(Generation + 1);
            hasBaseline = false; dirty = true;
            // The first sight of a ship after entering the editor is adoption, not a change.
            if (first) EditRevision = checked(EditRevision + 1); else Change(ChangeKind.Generation);
        }

        private void CheckSelected()
        {
            var held = port.HasSelectedPart;
            if (held && !selectedHeld) { selectedHeld = true; Change(ChangeKind.Selected); }
            else if (!held) selectedHeld = false;
        }

        private void CheckUi()
        {
            if (!hasBaseline) return;
            var current = port.ReadUi();
            if (current == null || current.Equals(ui)) return;
            ui = current; fingerprint = Fingerprint(ui);
            Change(ChangeKind.Ui);
        }

        private void SchedulePoll(long now)
        {
            switch (window)
            {
                case OperationWindow.Locked: return; // no fingerprint work while locked
                case OperationWindow.Grace: if (Due(now, lastFullAt, GracePollMilliseconds)) Check(now); return;
            }
            // With no lease nothing polls: a revision is settled lazily at the next observation or admission, which always recaptures.
            // One capture establishes a missing baseline (so later edits are measured against it); a failing capture retries only once a second.
            if (!hasBaseline) { if (Due(now, lastFullAt, LastError == null ? DirtyThrottleMilliseconds : LeasePollMilliseconds)) Check(now); return; }
            if (!sink.LeaseHeld) return;
            if (dirty && Due(now, lastFullAt, DirtyThrottleMilliseconds)) { Check(now); return; }
            if (Mode == FingerprintMode.Full)
            {
                if (Due(now, lastFullAt, LeasePollMilliseconds)) Check(now);
                return;
            }
            if (!Due(now, lastCheapAt, LeasePollMilliseconds)) return;
            lastCheapAt = now;
            string hash = null;
            try { hash = port.CheapHash(); } catch (Exception) { hash = null; }
            if (hash == null || !string.Equals(hash, cheap, StringComparison.Ordinal) || Due(now, lastFullAt, CheapFullRefreshMilliseconds)) Check(now);
        }

        /// <summary>Captures the craft and compares it with the baseline. Never raises events or takeover from inside the capture.</summary>
        private void Check(long now)
        {
            lastFullAt = now; dirty = false;
            EditorCraft capture;
            string body, hash;
            var started = costClock();
            capturing = true;
            try
            {
                capture = port.Capture();
                if (capture == null) { LastError = "capture_unavailable"; checkOk = false; dirty = true; return; }
                body = Pure.CraftFingerprint.Project(capture.Craft, registry, null, null, null);
                hash = Pure.CraftFingerprint.HashProjection(body);
            }
            catch (Exception error) { LastError = error.GetType().Name; checkOk = false; dirty = true; return; }
            finally { capturing = false; RecordCost(costClock() - started); }
            LastError = null; checkOk = true;
            // The cheap layer is only needed (and only paid for) once the fingerprint is expensive.
            string cheapNow = null;
            if (Mode == FingerprintMode.DirtyTracked) { try { cheapNow = port.CheapHash(); } catch (Exception) { cheapNow = null; } }
            cheap = cheapNow;
            var wasBaseline = hasBaseline;
            var uiChanged = wasBaseline && !capture.Ui.Equals(ui);
            var bodyChanged = wasBaseline && !string.Equals(hash, bodyHash, StringComparison.Ordinal);
            bodyText = body; bodyHash = hash; ui = capture.Ui; fingerprint = Fingerprint(ui); hasBaseline = true;
            if (uiChanged) Change(ChangeKind.Ui);
            if (bodyChanged) Change(ChangeKind.Fingerprint);
        }

        private string Fingerprint(EditorUi value)
        {
            return Pure.CraftFingerprint.HashProjection(Pure.CraftFingerprint.UiPrefix(value.Name, value.Description, value.FlagUrl) + bodyText);
        }

        private void RecordCost(double milliseconds)
        {
            if (milliseconds < 0) milliseconds = 0;
            LastPollCostMilliseconds = milliseconds;
            costs[costNext] = milliseconds; costNext = (costNext + 1) % CostSamples; if (costCount < CostSamples) costCount++;
        }

        private void Reset()
        {
            shipId = null; hasBaseline = false; dirty = false; selectedHeld = false; window = OperationWindow.None;
        }
    }
}
