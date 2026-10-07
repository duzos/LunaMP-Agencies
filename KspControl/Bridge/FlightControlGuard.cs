using System;

namespace KspControl.Bridge
{
    /// <summary>
    /// Owns the one fly-by-wire callback the bridge may hold, and only while a lease is held. It is the single place that writes the throttle
    /// and the single place that undoes it. The callback asks the guard for the throttle every vessel tick; the guard answers null (so the
    /// callback removes itself) the moment the lease is gone, by Stop, expiry, watchdog, takeover, vessel switch or scene change.
    /// </summary>
    /// <remarks>
    /// Threading: <see cref="OnLeaseEnded"/> may run on any thread and only sets a flag. Everything else runs on the main thread: the vessel
    /// callback, <see cref="Update"/> and the engage/release calls. Release is idempotent.
    /// Policy: a lease that ends for any reason except an agent release or a human takeover leaves the vessel neutral: throttle zero and time warp
    /// back to real time. Release and takeover only remove the callback, so a human's controls (and a deliberate hand-over) are preserved.
    /// </remarks>
    internal sealed class FlightControlGuard
    {
        private readonly IFlightInputPort port;
        private readonly Func<bool> leaseHeld;
        private volatile string pendingReason;
        private bool engaged;
        private bool warpCommanded;
        private float target;

        public FlightControlGuard(IFlightInputPort port, Func<bool> leaseHeld)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.leaseHeld = leaseHeld ?? throw new ArgumentNullException(nameof(leaseHeld));
        }

        /// <summary>True while the callback is installed.</summary>
        public bool Engaged { get { return engaged; } }
        public float CommandedThrottle { get { return target; } }
        /// <summary>The reason the guard last released, for status and tests.</summary>
        public string LastRelease { get; private set; }

        /// <summary>Wire this to <see cref="ExecutionAuthority.LeaseEnded"/>. Safe from any thread.</summary>
        public void OnLeaseEnded(string reason) { pendingReason = string.IsNullOrEmpty(reason) ? "lease_ended" : reason; }

        /// <summary>Installs the callback if needed and commands the throttle. Fails closed: no lease, no callback, no write.</summary>
        public bool Engage(float throttle)
        {
            var stale = pendingReason;
            if (stale != null) Release(stale);
            if (!leaseHeld()) return false;
            if (float.IsNaN(throttle) || throttle < 0f || throttle > 1f) return false;
            if (!engaged)
            {
                if (!port.Attach(Tick)) return false;
                engaged = true;
            }
            target = throttle;
            port.WriteThrottle(throttle);
            return true;
        }

        /// <summary>Records that the bridge raised time warp, so a neutralising release also drops it.</summary>
        public void NoteWarp(bool raised) { warpCommanded = raised; }

        /// <summary>The vessel callback. Returns the throttle to apply, or null after releasing so the callback removes itself.</summary>
        private float? Tick()
        {
            var reason = pendingReason;
            if (reason == null && !leaseHeld()) reason = "lease_lost";
            if (reason != null) { Release(reason); return null; }
            return engaged ? target : (float?)null;
        }

        /// <summary>The per-frame check for the case where no vessel tick comes (paused, scene teardown).</summary>
        public void Update()
        {
            var reason = pendingReason;
            if (reason == null && (engaged || warpCommanded) && !leaseHeld()) reason = "lease_lost";
            if (reason != null) Release(reason);
        }

        /// <summary>Stop, Stop button, OnDestroy and every revocation end here.</summary>
        public void Release(string reason)
        {
            pendingReason = null;
            if (!engaged && !warpCommanded) return;
            var neutral = !(reason == "released" || reason == "human_takeover");
            LastRelease = reason;
            if (neutral)
            {
                // Zero first, then remove the callback: even a failing Detach leaves the throttle cut.
                Safely(() => port.WriteThrottle(0f));
                if (warpCommanded) Safely(port.CancelWarp);
            }
            if (engaged) Safely(port.Detach);
            engaged = false; warpCommanded = false; target = 0f;
        }

        private static void Safely(Action action) { try { action(); } catch (Exception) { /* teardown: the vessel may already be gone */ } }
    }
}
