using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using KspControl.Contracts;

namespace KspControl.Bridge
{
    /// <summary>
    /// One worker thread per accepted socket (plan R3-§6.1). control.* operations are served inline on the worker
    /// and are never refused for queue reasons. Every other operation needs one of a few queue-wait slots,
    /// then enqueues for the main thread; a blocked request therefore delays only its own worker.
    /// </summary>
    internal sealed class LoopbackServer : IDisposable
    {
        private readonly string token;
        private readonly ObservationQueue queue;
        private readonly Func<BridgeRequest, BridgeResponse> inlineHandler;
        private readonly TcpListener listener;
        private readonly int maxSockets, maxWaiters;
        private readonly object clientsGate = new object();
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly Thread acceptThread;
        private int waiters;
        private volatile bool stopped;

        public int Port { get; }
        /// <summary>Test hook: number of sockets currently open.</summary>
        internal int OpenSockets { get { lock (clientsGate) return clients.Count; } }
        /// <summary>Test hook: workers currently holding a queue-wait slot.</summary>
        internal int WaitingSlots { get { return Volatile.Read(ref waiters); } }

        public LoopbackServer(ObservationQueue queue, string token, int port, Func<BridgeRequest, BridgeResponse> inlineHandler = null,
            int maxSockets = ControlLimits.MaxOpenSockets, int maxWaiters = ControlLimits.MaxQueueWaiters)
        {
            this.queue = queue; this.token = token; this.inlineHandler = inlineHandler; this.maxSockets = maxSockets; this.maxWaiters = maxWaiters;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(64);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptThread = new Thread(Accept) { IsBackground = true, Name = "KspControl loopback accept" };
            acceptThread.Start();
        }

        private void Accept()
        {
            while (!stopped)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception)
                {
                    if (stopped) return;
                    Thread.Sleep(10); continue;
                }
                bool admitted;
                lock (clientsGate) { admitted = !stopped && clients.Count < maxSockets; if (admitted) clients.Add(client); }
                if (!admitted) { try { client.Close(); } catch { } continue; }
                try { new Thread(() => Serve(client)) { IsBackground = true, Name = "KspControl loopback worker" }.Start(); }
                catch (Exception) { Release(client); }
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (var lifetime = new Timer(_ => { try { client.Close(); } catch { } }, null, 5000, Timeout.Infinite))
                {
                    client.ReceiveTimeout = 2000; client.SendTimeout = 2000;
                    using (var stream = client.GetStream())
                    {
                        var request = BridgeFrames.Read<BridgeRequest>(stream);
                        if (request == null) return;
                        BridgeFrames.Write(stream, Respond(request));
                    }
                }
            }
            catch (Exception) { /* Network and malformed-input diagnostics deliberately exclude payloads. */ }
            finally { Release(client); }
        }

        private BridgeResponse Respond(BridgeRequest request)
        {
            if (!TokenMatches(token, request.Token)) return ObservationQueue.Failure(request, "unauthorized");
            if (request.ProtocolVersion != 1) return ObservationQueue.Failure(request, "protocol_mismatch");
            if (string.IsNullOrEmpty(request.RequestId) || request.RequestId.Length > 128) return ObservationQueue.Failure(null, "invalid_request");
            request.Token = null;
            if (ControlOperations.IsControl(request.Operation))
            {
                if (inlineHandler == null) return ObservationQueue.Failure(request, ControlReasons.OperationUnavailable);
                try { return inlineHandler(request); }
                catch (Exception) { return ObservationQueue.Failure(request, "authority_unavailable"); }
            }
            if (Interlocked.Increment(ref waiters) > maxWaiters) { Interlocked.Decrement(ref waiters); return ObservationQueue.Failure(request, ControlReasons.BridgeBusy); }
            try
            {
                var result = queue.Enqueue(request, DateTime.UtcNow.AddSeconds(2));
                return result.Wait(2200) ? result.Result : ObservationQueue.Failure(request, "simulation_not_responding");
            }
            finally { Interlocked.Decrement(ref waiters); }
        }

        private void Release(TcpClient client)
        {
            lock (clientsGate) clients.Remove(client);
            try { client.Close(); } catch { }
        }

        internal static bool TokenMatches(string expected, string supplied)
        {
            if (expected == null || supplied == null || supplied.Length != expected.Length) return false;
            var difference = 0;
            for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ supplied[i];
            return difference == 0;
        }

        public void Dispose()
        {
            stopped = true;
            try { listener.Stop(); } catch { }
            TcpClient[] open;
            lock (clientsGate) { open = new TcpClient[clients.Count]; clients.CopyTo(open); }
            foreach (var client in open) { try { client.Close(); } catch { } }
            queue.Stop();
        }
    }
}
