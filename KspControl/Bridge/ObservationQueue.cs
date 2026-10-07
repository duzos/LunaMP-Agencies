using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KspControl.Contracts;

namespace KspControl.Bridge
{
    /// <summary>Network threads enqueue immutable requests; only Unity's Update drains them.</summary>
    public sealed class ObservationQueue
    {
        private readonly Queue<Pending> pending = new Queue<Pending>();
        private readonly object gate = new object();
        private readonly int capacity;
        public ObservationQueue(int capacity = 8) { this.capacity = capacity; }
        public Task<BridgeResponse> Enqueue(BridgeRequest request, DateTime deadline)
        {
            lock (gate)
            {
                if (pending.Count >= capacity) return Task.FromResult(Failure(request, "queue_full"));
                var item = new Pending(request, deadline); pending.Enqueue(item); return item.Completion.Task;
            }
        }
        public void Drain(Func<BridgeRequest, BridgeResponse> observe, DateTime now, int maximum = 2)
        {
            for (var i = 0; i < maximum; ++i)
            {
                Pending item;
                lock (gate) { if (pending.Count == 0) return; item = pending.Dequeue(); }
                if (now >= item.Deadline) { item.Completion.TrySetResult(Failure(item.Request, "expired")); continue; }
                try { item.Completion.TrySetResult(observe(item.Request)); }
                catch { item.Completion.TrySetResult(Failure(item.Request, "observation_unavailable")); }
            }
        }
        public void Stop()
        {
            lock (gate) while (pending.Count != 0)
            { var item = pending.Dequeue(); item.Completion.TrySetResult(Failure(item.Request, "bridge_stopped")); }
        }
        public static BridgeResponse Failure(BridgeRequest request, string code) => new BridgeResponse
        { RequestId = request?.RequestId, Status = "failed", ReasonCode = code };
        private sealed class Pending
        {
            public readonly BridgeRequest Request;
            public readonly DateTime Deadline;
            public readonly TaskCompletionSource<BridgeResponse> Completion = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            public Pending(BridgeRequest request, DateTime deadline) { Request = request; Deadline = deadline; }
        }
    }
}
