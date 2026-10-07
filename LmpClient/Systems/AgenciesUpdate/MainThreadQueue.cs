using System;
using System.Collections.Concurrent;

namespace LmpClient.Systems.AgenciesUpdate
{
    /// <summary>
    /// Work enqueued from any thread and run by whoever calls <see cref="Drain"/> (the Unity main thread).
    /// No Unity dependencies, so it is linked into LmpCommonTest.
    /// </summary>
    public sealed class MainThreadQueue
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public void Enqueue(Action work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            _queue.Enqueue(work);
        }

        /// <summary>Runs everything queued so far on the calling thread. A throwing item does not stop the rest.</summary>
        public int Drain()
        {
            var ran = 0;
            while (_queue.TryDequeue(out var work))
            {
                ran++;
                try { work(); }
                catch (Exception e) { LunaLog.LogError($"[LMP]: Queued agencies update work failed: {e.Message}"); }
            }
            return ran;
        }
    }
}
