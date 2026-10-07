using System.Diagnostics;

namespace KspControl.Contracts
{
    /// <summary>Process-wide monotonic millisecond clock shared by loopback threads and the main thread.</summary>
    public static class MonotonicClock
    {
        /// <summary>Milliseconds since an arbitrary fixed origin. Never decreases; safe from any thread.</summary>
        public static long Milliseconds
        {
            get
            {
                long ticks = Stopwatch.GetTimestamp();
                long frequency = Stopwatch.Frequency;
                // Split the multiplication so a long-running process cannot overflow.
                return (ticks / frequency) * 1000 + (ticks % frequency) * 1000 / frequency;
            }
        }
    }
}
