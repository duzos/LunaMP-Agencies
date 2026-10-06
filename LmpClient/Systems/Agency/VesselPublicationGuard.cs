using System;
using System.Collections.Concurrent;
using System.Threading;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Data.Vessel;

namespace LmpClient.Systems.Agency
{
    /// <summary>Serializes publication against speculative topology and invalidates work from old sessions.</summary>
    public static class VesselPublicationGuard
    {
        private static readonly object gate = new object();
        private static readonly ConcurrentDictionary<IMessageBase, long> stamps = new ConcurrentDictionary<IMessageBase, long>();
        private static long epoch;
        private static Guid operation;
        private static bool blocked;
        public static long CaptureEpoch() { lock (gate) return epoch; }
        public static bool Pending { get { lock (gate) return blocked; } }
        public static bool IsCurrent(long value) { lock (gate) return epoch == value && !blocked; }
        public static void Stamp(IMessageBase message, long value) => stamps[message] = value;
        internal static bool Begin(Guid id)
        {
            lock (gate) { if (blocked) return false; epoch++; operation = id; blocked = true; return true; }
        }
        internal static void Complete(Guid id)
        {
            lock (gate) { if (!blocked || operation != id) return; epoch++; blocked = false; operation = Guid.Empty; }
        }
        internal static void Reject() { lock (gate) { epoch++; blocked = true; operation = Guid.Empty; } }
        public static void ResetSession() { lock (gate) { epoch++; operation = Guid.Empty; blocked = false; stamps.Clear(); } }
        public static IDisposable EnterSend(IMessageBase message, out bool allowed)
        {
            Monitor.Enter(gate);
            var stamped = stamps.TryRemove(message, out var stamp);
            allowed = !(message.Data is VesselBaseMsgData) ||
                      (stamped && stamp == epoch && (!blocked || message.Data is VesselCoupleMsgData couple && couple.OperationId == operation && operation != Guid.Empty));
            return new SendScope();
        }
        private sealed class SendScope : IDisposable { public void Dispose() => Monitor.Exit(gate); }
    }
}
