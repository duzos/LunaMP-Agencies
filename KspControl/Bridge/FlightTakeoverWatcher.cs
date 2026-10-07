using System;

namespace KspControl.Bridge
{
    /// <summary>
    /// Manual-control detection for flight: while a lease is held, a player who holds a flight control (stick, throttle or stage key) for a few
    /// consecutive frames takes the lease over. It reads the raw hardware input, never the vessel state the bridge itself writes, so the bridge
    /// cannot take over from itself.
    /// </summary>
    internal sealed class FlightTakeoverWatcher
    {
        private readonly IFlightHumanInput input;
        private readonly ExecutionAuthority authority;
        private readonly int debounceFrames;
        private int streak;

        public FlightTakeoverWatcher(IFlightHumanInput input, ExecutionAuthority authority, int debounceFrames = 3)
        {
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
            this.debounceFrames = Math.Max(1, debounceFrames);
        }

        /// <summary>Once per frame, on the main thread.</summary>
        public void Update()
        {
            if (!authority.LeaseHeld) { streak = 0; return; }
            bool pressed;
            try { pressed = input.PlayerIsInputting(); } catch (Exception) { pressed = false; }
            streak = pressed ? streak + 1 : 0;
            if (streak < debounceFrames) return;
            streak = 0;
            authority.HumanTakeover();
        }
    }
}
