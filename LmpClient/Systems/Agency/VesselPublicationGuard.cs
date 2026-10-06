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
        private static Guid splitParent, splitChild;
        private static bool blocked;
        public static long CaptureEpoch() { lock (gate) return epoch; }
        public static bool Pending { get { lock (gate) return blocked; } }
        public static bool IsCurrent(long value) { lock (gate) return epoch == value && !blocked; }
        public static void Stamp(IMessageBase message, long value) => stamps[message] = value;
        internal static bool Begin(Guid id, Guid parent = default(Guid), Guid child = default(Guid))
        {
            lock (gate) { if (blocked) return false; epoch++; operation = id; splitParent = parent; splitChild = child; blocked = true; return true; }
        }
        internal static bool AdvanceSplit(Guid previous, Guid next, Guid parent, Guid child)
        {
            lock (gate) { if (!blocked || operation != previous || splitChild == Guid.Empty) return false;
                epoch++; operation = next; splitParent = parent; splitChild = child; return true; }
        }
        internal static void Complete(Guid id)
        {
            lock (gate) { if (!blocked || operation != id) return; epoch++; blocked = false; operation = splitParent = splitChild = Guid.Empty; }
        }
        internal static void Reject() { lock (gate) { epoch++; blocked = true; operation = splitParent = splitChild = Guid.Empty; } }
        public static void ResetSession() { lock (gate) { epoch++; operation = splitParent = splitChild = Guid.Empty; blocked = false; stamps.Clear(); } }
        private static bool IsSplitPublication(VesselBaseMsgData data)
        {
            if (splitChild == Guid.Empty || operation == Guid.Empty) return false;
            if (data is VesselProtoMsgData proto) return proto.VesselId == splitChild && proto.EconomySplitOperationId == operation;
            if (data is VesselDecoupleMsgData decouple) return decouple.VesselId == splitParent && decouple.NewVesselId == splitChild;
            if (data is VesselUndockMsgData undock) return undock.VesselId == splitParent && undock.NewVesselId == splitChild;
            return false;
        }
        public static IDisposable EnterSend(IMessageBase message, out bool allowed)
        {
            Monitor.Enter(gate);
            var stamped = stamps.TryRemove(message, out var stamp);
            allowed = !(message.Data is VesselBaseMsgData) ||
                      (stamped && stamp == epoch && (!blocked || IsSplitPublication((VesselBaseMsgData)message.Data) || message.Data is VesselCoupleMsgData couple && couple.OperationId == operation && operation != Guid.Empty));
            return new SendScope();
        }
        private sealed class SendScope : IDisposable { public void Dispose() => Monitor.Exit(gate); }
    }
}
