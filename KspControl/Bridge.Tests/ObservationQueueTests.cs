using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class ObservationQueueTests
    {
        private static BridgeRequest Request(string id = "r") => new BridgeRequest { RequestId = id, Operation = "game.context" };
        [TestMethod] public void ExpiredWorkNeverCallsGame()
        {
            var queue = new ObservationQueue(); var now = DateTime.UtcNow;
            var task = queue.Enqueue(Request(), now); var called = false;
            queue.Drain(r => { called = true; return new BridgeResponse(); }, now);
            Assert.IsFalse(called); Assert.AreEqual("expired", task.Result.ReasonCode);
        }
        [TestMethod] public void FullQueueFailsWithoutUnboundedAccumulation()
        {
            var queue = new ObservationQueue(1); queue.Enqueue(Request(), DateTime.MaxValue);
            Assert.AreEqual("queue_full", queue.Enqueue(Request(), DateTime.MaxValue).Result.ReasonCode);
        }
        [TestMethod] public void StopCompletesPendingRequestsWithoutObserving()
        {
            var queue = new ObservationQueue(); var task = queue.Enqueue(Request(), DateTime.MaxValue);
            queue.Stop(); Assert.AreEqual("bridge_stopped", task.Result.ReasonCode);
        }
        [TestMethod] public void DrainRespectsFrameWorkLimit()
        {
            var queue = new ObservationQueue(); var first = queue.Enqueue(Request("first"), DateTime.MaxValue);
            var second = queue.Enqueue(Request("second"), DateTime.MaxValue);
            queue.Drain(r => new BridgeResponse { RequestId = r.RequestId }, DateTime.UtcNow, 1);
            Assert.AreEqual("first", first.Result.RequestId); Assert.IsFalse(second.IsCompleted);
        }
        [TestMethod] public void ObservationExceptionNeverReturnsPrivateExceptionMessage()
        {
            var queue = new ObservationQueue(); var task = queue.Enqueue(Request(), DateTime.MaxValue);
            queue.Drain(r => throw new Exception("hidden foreign vessel name"), DateTime.UtcNow);
            Assert.AreEqual("observation_unavailable", task.Result.ReasonCode);
            Assert.AreEqual(0, task.Result.Data.Count);
        }
        [TestMethod] public void TokenComparisonRejectsMissingAndChangedToken()
        {
            Assert.IsTrue(LoopbackServer.TokenMatches("testsecret", "testsecret"));
            Assert.IsFalse(LoopbackServer.TokenMatches("testsecret", "testsecrex"));
            Assert.IsFalse(LoopbackServer.TokenMatches("testsecret", null));
        }
        [TestMethod] public async Task LoopbackRejectsUnauthenticatedRequestBeforeQueue()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var queue = new ObservationQueue();
            using (var server = new LoopbackServer(queue, "testsecret", port))
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                client.ReceiveTimeout = 3000;
                using (var stream = client.GetStream())
                {
                    BridgeFrames.Write(stream, Request());
                    Assert.AreEqual("unauthorized", BridgeFrames.Read<BridgeResponse>(stream).ReasonCode);
                }
                var observed = false;
                queue.Drain(r => { observed = true; return new BridgeResponse(); }, DateTime.UtcNow);
                Assert.IsFalse(observed);
            }
        }
    }
}
