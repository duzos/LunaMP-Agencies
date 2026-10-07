using System;
using System.Collections.Generic;
using System.Linq;

namespace LmpClient.Harmony
{
    /// <summary>
    /// Decides which suppressed UI exceptions get a full log entry. The first failure of each key is logged in full;
    /// later ones are only counted and reported as one summary line at most once per interval. Pure logic with an
    /// injected clock so it can be unit tested outside Unity.
    /// </summary>
    internal sealed class UiFaultLogThrottle
    {
        private readonly double _summaryIntervalSeconds;
        private readonly HashSet<string> _seen = new HashSet<string>();
        private readonly Dictionary<string, long> _pending = new Dictionary<string, long>();
        private double? _lastSummaryAt;

        public UiFaultLogThrottle(double summaryIntervalSeconds)
        {
            _summaryIntervalSeconds = summaryIntervalSeconds;
        }

        public long TotalSuppressed { get; private set; }

        /// <summary>Returns true when the caller should log this failure in full (first time this key is seen).</summary>
        public bool Record(string key, double now)
        {
            if (_lastSummaryAt == null) _lastSummaryAt = now;
            if (_seen.Add(key)) return true;

            _pending.TryGetValue(key, out var count);
            _pending[key] = count + 1;
            TotalSuppressed++;
            return false;
        }

        /// <summary>
        /// Returns a summary of occurrences counted since the previous summary, or null when nothing is pending or the
        /// interval since the previous summary (or the first logged failure) has not elapsed yet.
        /// </summary>
        public string TakeSummary(double now)
        {
            if (_pending.Count == 0) return null;
            if (_lastSummaryAt != null && now - _lastSummaryAt.Value < _summaryIntervalSeconds) return null;

            var summary = string.Join(", ", _pending.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} x{p.Value}"));
            _pending.Clear();
            _lastSummaryAt = now;
            return summary;
        }
    }

    internal enum UiFaultAction
    {
        None,
        /// <summary>Rebuild missing internal state in place and mark the component dirty.</summary>
        SoftRepair,
        /// <summary>Disable and re-enable the component so it re-runs its own initialisation.</summary>
        Restart,
        /// <summary>Give up on this component: leave it disabled so its content renders unmasked.</summary>
        Disable
    }

    /// <summary>
    /// Per-instance escalation for a misbehaving UI component: soft repair first, then a bounded number of
    /// disable/enable restarts spaced by a grace period, then permanent disable. An instance that has been quiet for
    /// <c>healthyAfterSeconds</c> is considered recovered and starts again from a soft repair.
    /// </summary>
    internal sealed class UiFaultRepairPolicy
    {
        private sealed class State
        {
            public bool Repaired;
            public int Restarts;
            public bool Disabled;
            public double LastActionAt;
            public double LastFailureAt;
        }

        private readonly double _graceSeconds;
        private readonly double _healthyAfterSeconds;
        private readonly int _maxRestarts;
        private readonly Dictionary<int, State> _states = new Dictionary<int, State>();

        public UiFaultRepairPolicy(double graceSeconds, double healthyAfterSeconds, int maxRestarts)
        {
            _graceSeconds = graceSeconds;
            _healthyAfterSeconds = healthyAfterSeconds;
            _maxRestarts = maxRestarts;
        }

        public int DisabledCount => _states.Values.Count(s => s.Disabled);

        public UiFaultAction OnFailure(int instanceId, double now)
        {
            if (!_states.TryGetValue(instanceId, out var state))
            {
                state = new State();
                _states[instanceId] = state;
            }

            if (state.Disabled) return UiFaultAction.None;

            if (state.Repaired && now - state.LastFailureAt >= _healthyAfterSeconds)
            {
                state.Repaired = false;
                state.Restarts = 0;
            }
            state.LastFailureAt = now;

            if (!state.Repaired)
            {
                state.Repaired = true;
                state.LastActionAt = now;
                return UiFaultAction.SoftRepair;
            }

            // Several methods of the same instance can fail in one frame; only escalate once the last action had time to work.
            if (now - state.LastActionAt < _graceSeconds) return UiFaultAction.None;

            state.LastActionAt = now;
            if (state.Restarts < _maxRestarts)
            {
                state.Restarts++;
                return UiFaultAction.Restart;
            }

            state.Disabled = true;
            return UiFaultAction.Disable;
        }

        public void Forget(int instanceId) => _states.Remove(instanceId);
    }
}
