using System;
using System.Collections.Generic;

namespace LmpClient.Harmony
{
    /// <summary>
    /// Rate-limits the per-pair CommNet denial log: a pair logs at most once per interval.
    /// No Unity dependencies, so it is linked into LmpCommonTest.
    /// </summary>
    public sealed class CommNetDenialLogGate
    {
        private const int MaxTrackedPairs = 4096;
        private readonly long _intervalMs;
        private readonly Dictionary<KeyValuePair<Guid, Guid>, long> _lastLogged = new Dictionary<KeyValuePair<Guid, Guid>, long>();

        public CommNetDenialLogGate(long intervalMs) { _intervalMs = intervalMs; }

        /// <summary>True when this unordered pair has not been logged within the interval (and records it).</summary>
        public bool ShouldLog(Guid a, Guid b, long nowMs)
        {
            var key = a.CompareTo(b) <= 0 ? new KeyValuePair<Guid, Guid>(a, b) : new KeyValuePair<Guid, Guid>(b, a);
            if (_lastLogged.TryGetValue(key, out var last) && nowMs - last < _intervalMs) return false;
            if (_lastLogged.Count >= MaxTrackedPairs) _lastLogged.Clear();
            _lastLogged[key] = nowMs;
            return true;
        }
    }
}
