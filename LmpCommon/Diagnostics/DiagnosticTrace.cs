using System;
using System.Globalization;
using System.Text;

namespace LmpCommon.Diagnostics
{
    /// <summary>Opt-in, bounded diagnostics shared by the server and Unity client.</summary>
    public sealed class DiagnosticTrace
    {
        private readonly object _gate = new object();
        private readonly Action<string> _sink;
        private readonly Func<DateTime> _utcNow;
        private readonly int _eventLimit;
        private readonly int _trafficLimit;
        private readonly string _session = Guid.NewGuid().ToString("N");
        private volatile bool _enabled;
        private DateTime _window;
        private long _sequence;
        private int _events;
        private int _traffic;
        private long _suppressedEvents;
        private long _suppressedTraffic;

        public DiagnosticTrace(Action<string> sink, Func<DateTime> utcNow = null, int eventLimit = 120, int trafficLimit = 30)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            if (eventLimit < 1 || trafficLimit < 1) throw new ArgumentOutOfRangeException(nameof(eventLimit));
            _eventLimit = eventLimit;
            _trafficLimit = trafficLimit;
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                lock (_gate)
                {
                    if (_enabled == value) return;
                    _enabled = value;
                    _window = default(DateTime);
                    _events = _traffic = 0;
                    _suppressedEvents = _suppressedTraffic = 0;
                }
            }
        }

        public void Write(string eventName, Func<string> details, bool traffic = false)
        {
            if (!_enabled) return;
            // Logging must never become a new gameplay failure path.
            try
            {
                lock (_gate)
                {
                    if (!_enabled) return;
                    var now = _utcNow();
                    if (_window == default(DateTime) || now < _window || now - _window >= TimeSpan.FromSeconds(5))
                    {
                        if (_suppressedEvents > 0 || _suppressedTraffic > 0)
                            Emit(now, "suppressed", "events=" + _suppressedEvents + " traffic=" + _suppressedTraffic);
                        _window = now;
                        _events = _traffic = 0;
                        _suppressedEvents = _suppressedTraffic = 0;
                    }
                    if (traffic ? _traffic >= _trafficLimit : _events >= _eventLimit)
                    {
                        if (traffic) _suppressedTraffic++; else _suppressedEvents++;
                        return;
                    }
                    if (traffic) _traffic++; else _events++;
                    string detail;
                    try { detail = details?.Invoke() ?? string.Empty; }
                    catch (Exception e) { detail = "diagnostic-detail-failed type=" + e.GetType().Name; }
                    Emit(now, eventName, detail);
                }
            }
            catch { /* Diagnostic failures must not propagate into gameplay. */ }
        }

        private void Emit(DateTime now, string eventName, string details)
        {
            var line = "[LMP-DIAG] utc=" + now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                + " session=" + _session + " seq=" + (++_sequence).ToString(CultureInfo.InvariantCulture)
                + " event=" + OneLine(eventName, 100) + " " + OneLine(details, 1800);
            try { _sink(line); }
            catch { /* No recursive error logging if the sink itself is unavailable. */ }
        }

        private static string OneLine(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var result = new StringBuilder(Math.Min(value.Length, limit) + 12);
            for (var i = 0; i < value.Length && i < limit; i++)
                result.Append(char.IsControl(value[i]) ? ' ' : value[i]);
            if (value.Length > limit) result.Append("[truncated]");
            return result.ToString();
        }
    }
}
