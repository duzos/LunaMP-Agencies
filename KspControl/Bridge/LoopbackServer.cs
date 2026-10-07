using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using KspControl.Contracts;

namespace KspControl.Bridge
{
    internal sealed class LoopbackServer : IDisposable
    {
        private readonly string token;
        private readonly ObservationQueue queue;
        private readonly TcpListener listener;
        private volatile bool stopped;
        private TcpClient active;
        public LoopbackServer(ObservationQueue queue, string token, int port)
        {
            this.queue = queue; this.token = token;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(4);
            new Thread(Run) { IsBackground = true, Name = "KspControl loopback" }.Start();
        }
        private void Run()
        {
            while (!stopped)
            {
                try
                {
                    using (var client = listener.AcceptTcpClient())
                    using (var lifetime = new Timer(_ => { try { client.Close(); } catch { } }, null, 5000, Timeout.Infinite))
                    {
                        active = client; client.ReceiveTimeout = 2000; client.SendTimeout = 2000;
                        using (var stream = client.GetStream())
                        {
                            var request = BridgeFrames.Read<BridgeRequest>(stream);
                            if (request == null) continue;
                            BridgeResponse response;
                            if (!TokenMatches(token, request.Token)) response = ObservationQueue.Failure(request, "unauthorized");
                            else if (request.ProtocolVersion != 1) response = ObservationQueue.Failure(request, "protocol_mismatch");
                            else if (string.IsNullOrEmpty(request.RequestId) || request.RequestId.Length > 128) response = ObservationQueue.Failure(null, "invalid_request");
                            else
                            {
                                request.Token = null;
                                var result = queue.Enqueue(request, DateTime.UtcNow.AddSeconds(2));
                                response = result.Wait(2200) ? result.Result : ObservationQueue.Failure(request, "simulation_not_responding");
                            }
                            BridgeFrames.Write(stream, response);
                        }
                    }
                }
                catch (Exception) { /* Network and malformed-input diagnostics deliberately exclude payloads. */ }
                finally { active = null; }
            }
        }
        internal static bool TokenMatches(string expected, string supplied)
        {
            if (expected == null || supplied == null || supplied.Length != expected.Length) return false;
            var difference = 0;
            for (var i = 0; i < expected.Length; i++) difference |= expected[i] ^ supplied[i];
            return difference == 0;
        }
        public void Dispose() { stopped = true; listener.Stop(); active?.Close(); queue.Stop(); }
    }
}
