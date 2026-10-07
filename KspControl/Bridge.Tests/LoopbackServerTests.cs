using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>Real sockets, real threads. Runs on both net10.0 and net472.</summary>
    [TestClass]
    public class LoopbackServerTests
    {
        private const string Token = "0123456789012345678901234567890123456789";
        private ObservationQueue queue;
        private ExecutionAuthority authority;
        private ControlDispatcher dispatcher;
        private LoopbackServer server;
        private long fakeNow;
        private bool useFakeClock;
        private int inlineCalls, unityCalls;

        [TestInitialize]
        public void Setup() { useFakeClock = false; fakeNow = 100000; Start(); }
        [TestCleanup] public void Cleanup() { if (server != null) server.Dispose(); }

        private void Start(bool fakeClock = false, int maxSockets = ControlLimits.MaxOpenSockets)
        {
            if (server != null) server.Dispose();
            useFakeClock = fakeClock;
            queue = new ObservationQueue(); // never drained: no main thread exists in these tests
            Func<long> clock = () => useFakeClock ? Interlocked.Read(ref fakeNow) : MonotonicClock.Milliseconds;
            authority = new ExecutionAuthority(clock, GrantMapping.KnownEffects, 2000, new MemorySuspensionStore(), () => DateTime.UtcNow);
            var binding = AuthorityHelpers.Bind();
            authority.UpdateContext(AuthorityHelpers.Ctx(), binding, AuthorityHelpers.ValidStatus());
            authority.ProvisionGrant(new TrustedExecutionGrant("grant", 1, binding, DateTime.UtcNow.AddHours(1), new[] { new EffectPermission("editor.replace_craft", "editor:VAB") }, new[] { "editor:VAB" }));
            dispatcher = new ControlDispatcher(authority);
            inlineCalls = 0; unityCalls = 0;
            server = new LoopbackServer(queue, Token, 0, request => { Interlocked.Increment(ref inlineCalls); return dispatcher.Handle(request); }, maxSockets);
        }

        private static BridgeResponse Send(int port, string op, string lease = null, JObject args = null, string token = Token, string requestId = "request-12345678", int protocol = 1, int timeoutMs = 6000)
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = timeoutMs; client.SendTimeout = timeoutMs;
                var stream = client.GetStream();
                BridgeFrames.Write(stream, new BridgeRequest { ProtocolVersion = protocol, Token = token, RequestId = requestId, Operation = op, LeaseId = lease, Arguments = args ?? new JObject() });
                return BridgeFrames.Read<BridgeResponse>(stream);
            }
        }
        private BridgeResponse Send(string op, string lease = null, JObject args = null, string token = Token, string requestId = "request-12345678", int protocol = 1)
        { return Send(server.Port, op, lease, args, token, requestId, protocol); }
        private void Refresh() { authority.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind()); }
        private static JObject Acquire() { return new JObject { ["purpose"] = "loopback test", ["durationSeconds"] = 60 }; }
        private static void WaitUntil(Func<bool> condition, int milliseconds = 3000, string message = "condition")
        {
            var end = Stopwatch.StartNew();
            while (!condition()) { if (end.ElapsedMilliseconds > milliseconds) Assert.Fail("timed out waiting for " + message); Thread.Sleep(5); }
        }
        private Task<BridgeResponse> SendQueued(string id) { return Task.Run(() => Send(server.Port, "game.context", requestId: id)); }

        // ---- the headline requirement: queued work never starves control ----

        [TestMethod] public void ThreeQueuedRequestsWaitAndTheFourthIsBridgeBusyWhileControlStaysResponsive()
        {
            Refresh(); var lease = (string)Send(ControlOperations.Acquire, null, Acquire()).Data["leaseId"];
            var blocked = Enumerable.Range(0, 3).Select(i => SendQueued("queued-request-" + i)).ToArray();
            WaitUntil(() => server.WaitingSlots == 3, 3000, "three waiting slots");

            var clock = Stopwatch.StartNew(); var fourth = Send("game.context", requestId: "queued-request-4");
            Assert.AreEqual("failed", fourth.Status); Assert.AreEqual(ControlReasons.BridgeBusy, fourth.ReasonCode); Assert.IsTrue(clock.ElapsedMilliseconds < 500, "bridge_busy took " + clock.ElapsedMilliseconds);

            clock.Restart(); var beat = Send(ControlOperations.Heartbeat, lease); clock.Stop();
            Assert.AreEqual("completed", beat.Status, beat.ReasonCode); Assert.IsTrue(clock.ElapsedMilliseconds < 200, "heartbeat took " + clock.ElapsedMilliseconds + " ms");
            Assert.IsTrue(blocked.All(t => !t.IsCompleted), "the three queued requests are still waiting");
            Assert.AreEqual(3, server.WaitingSlots);
            foreach (var t in blocked) Assert.IsTrue(t.Wait(4000), "queued request must end by its own timeout");
            foreach (var t in blocked) Assert.AreEqual("simulation_not_responding", t.Result.ReasonCode);
            WaitUntil(() => server.WaitingSlots == 0, 2000, "slots released");
            Assert.AreEqual(0, unityCalls);
        }
        [TestMethod] public void StatusAndAcquireAreAnsweredWhileEverySlotIsBlocked()
        {
            Refresh();
            var blocked = Enumerable.Range(0, 3).Select(i => SendQueued("queued-request-" + i)).ToArray();
            WaitUntil(() => server.WaitingSlots == 3, 3000, "three waiting slots");
            var clock = Stopwatch.StartNew(); var status = Send(ControlOperations.Status); Assert.IsTrue(clock.ElapsedMilliseconds < 200, "status took " + clock.ElapsedMilliseconds);
            Assert.AreEqual("completed", status.Status); Assert.AreEqual(GrantStates.Valid, (string)status.Data["grant"]["state"]);
            clock.Restart(); var acquired = Send(ControlOperations.Acquire, null, Acquire()); Assert.IsTrue(clock.ElapsedMilliseconds < 200, "acquire took " + clock.ElapsedMilliseconds);
            Assert.AreEqual("completed", acquired.Status, acquired.ReasonCode);
            Assert.AreEqual("completed", Send(ControlOperations.Release, (string)acquired.Data["leaseId"]).Status);
            Assert.AreEqual(3, server.WaitingSlots, "control never consumed a queue slot");
            Task.WaitAll(blocked.Cast<Task>().ToArray());
        }
        [TestMethod] public void HeartbeatsKeepTheLeaseAliveWhileAQueuedRequestTimesOut()
        {
            Refresh(); var lease = (string)Send(ControlOperations.Acquire, null, Acquire()).Data["leaseId"];
            var queued = SendQueued("queued-request-1"); WaitUntil(() => server.WaitingSlots == 1, 3000, "queued request waiting");
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 2600 || !queued.IsCompleted)
            {
                Assert.AreEqual("completed", Send(ControlOperations.Heartbeat, lease).Status, "watchdog must not revoke at " + clock.ElapsedMilliseconds + " ms");
                Thread.Sleep(400); if (clock.ElapsedMilliseconds > 6000) Assert.Fail("queued request never ended");
            }
            Assert.AreEqual("simulation_not_responding", queued.Result.ReasonCode);
            Assert.IsTrue((bool)Send(ControlOperations.Status).Data["lease"]["held"]);
            // and the freed slot is usable again
            var again = SendQueued("queued-request-2"); WaitUntil(() => server.WaitingSlots == 1, 3000, "slot reusable"); Assert.IsTrue(again.Wait(4000));
        }
        [TestMethod] public void QueuedRequestsStillReachTheMainThreadWhenItDrains()
        {
            var pending = SendQueued("queued-request-1"); WaitUntil(() => server.WaitingSlots == 1, 3000, "queued");
            queue.Drain(request => { Interlocked.Increment(ref unityCalls); return new BridgeResponse { RequestId = request.RequestId, Status = "completed" }; });
            Assert.IsTrue(pending.Wait(3000)); Assert.AreEqual("completed", pending.Result.Status); Assert.AreEqual(1, unityCalls);
        }
        [TestMethod] public void StalePublishedContextRefusesAcquireOverTheWireButAcceptsHeartbeat()
        {
            Start(fakeClock: true); Refresh();
            var lease = (string)Send(ControlOperations.Acquire, null, Acquire()).Data["leaseId"];
            Interlocked.Add(ref fakeNow, 1500); // the main thread is stalled: no UpdateContext
            Assert.AreEqual("completed", Send(ControlOperations.Heartbeat, lease).Status);
            Send(ControlOperations.Release, lease);
            Assert.AreEqual(ControlReasons.EditorUnavailable, Send(ControlOperations.Acquire, null, Acquire()).ReasonCode);
            Refresh(); Assert.AreEqual("completed", Send(ControlOperations.Acquire, null, Acquire()).Status);
        }

        // ---- transport rules ----

        [TestMethod] public void BadTokenIsRefusedOnTheInlinePathAndNeverReachesTheHandler()
        {
            foreach (var token in new[] { "wrong-token-wrong-token-wrong-token-wrong", "", null, Token + "x", Token.Substring(1) })
            { var response = Send(ControlOperations.Status, token: token); Assert.AreEqual("unauthorized", response.ReasonCode, "token " + token); }
            Assert.AreEqual(0, inlineCalls);
            Assert.AreEqual("unauthorized", Send("game.context", token: "nope").ReasonCode);
        }
        [TestMethod] public void ProtocolAndRequestIdChecksApplyToControlOperations()
        {
            Assert.AreEqual("protocol_mismatch", Send(ControlOperations.Status, protocol: 2).ReasonCode);
            Assert.AreEqual("invalid_request", Send(ControlOperations.Status, requestId: "").ReasonCode);
            Assert.AreEqual("invalid_request", Send(ControlOperations.Status, requestId: new string('r', 129)).ReasonCode);
            Assert.AreEqual(0, inlineCalls);
            Assert.AreEqual("completed", Send(ControlOperations.Status, requestId: new string('r', 128)).Status);
        }
        [TestMethod] public void ResponseEchoesTheRequestIdAndNoTokenIsRetained()
        {
            var response = Send(ControlOperations.Status, requestId: "echo-this-id-123"); Assert.AreEqual("echo-this-id-123", response.RequestId);
        }
        [TestMethod] public void UnavailableInlineHandlerFailsControlOperationsCleanly()
        {
            server.Dispose(); server = new LoopbackServer(new ObservationQueue(), Token, 0);
            Assert.AreEqual(ControlReasons.OperationUnavailable, Send(ControlOperations.Status).ReasonCode);
        }
        [TestMethod] public void ASocketBeyondTheCapIsClosedWithoutAResponseAndCapacityReturns()
        {
            Start(maxSockets: 32);
            var idle = new List<TcpClient>();
            try
            {
                for (var i = 0; i < 32; i++) { var c = new TcpClient(); c.Connect(IPAddress.Loopback, server.Port); idle.Add(c); }
                WaitUntil(() => server.OpenSockets == 32, 3000, "32 open sockets");
                using (var extra = new TcpClient())
                {
                    extra.Connect(IPAddress.Loopback, server.Port); extra.ReceiveTimeout = 1500;
                    var clock = Stopwatch.StartNew();
                    try { var read = extra.GetStream().Read(new byte[16], 0, 16); Assert.AreEqual(0, read, "closed without a response"); }
                    catch (IOException) { /* reset is also a close */ }
                    Assert.IsTrue(clock.ElapsedMilliseconds < 1400, "the 33rd socket must be closed promptly, took " + clock.ElapsedMilliseconds);
                }
                Assert.AreEqual(32, server.OpenSockets);
            }
            finally { foreach (var c in idle) c.Close(); }
            WaitUntil(() => server.OpenSockets == 0, 4000, "idle sockets reaped");
            Assert.AreEqual("completed", Send(ControlOperations.Status).Status);
        }
        [TestMethod] public void ASocketThatSendsNothingIsReapedByTheReceiveTimeout()
        {
            using (var idle = new TcpClient())
            {
                idle.Connect(IPAddress.Loopback, server.Port); WaitUntil(() => server.OpenSockets == 1, 2000, "registered");
                WaitUntil(() => server.OpenSockets == 0, 4000, "reaped");
            }
        }
        [TestMethod] public void DisposeClosesActiveWorkersAndStopsAccepting()
        {
            var port = server.Port;
            var queued = SendQueued("queued-request-1"); WaitUntil(() => server.WaitingSlots == 1, 3000, "queued");
            var idle = new TcpClient(); idle.Connect(IPAddress.Loopback, port); WaitUntil(() => server.OpenSockets == 2, 2000, "two open");
            var clock = Stopwatch.StartNew(); server.Dispose();
            // The waiting worker is released at once: either it still writes bridge_stopped, or its socket was already closed.
            try { Assert.IsTrue(queued.Wait(2000), "the waiting worker is released by Dispose"); Assert.AreEqual("bridge_stopped", queued.Result.ReasonCode); }
            catch (AggregateException error) { Assert.IsTrue(error.InnerExceptions.All(e => e is EndOfStreamException || e is IOException), error.ToString()); }
            Assert.IsTrue(clock.ElapsedMilliseconds < 1500, "Dispose must not wait for the queue timeout");
            WaitUntil(() => server.OpenSockets == 0, 2000, "all sockets closed");
            try { Assert.AreEqual(0, idle.GetStream().Read(new byte[4], 0, 4)); } catch (IOException) { }
            idle.Close();
            Assert.ThrowsException<SocketException>(() => { using (var late = new TcpClient()) late.Connect(IPAddress.Loopback, port); });
            Assert.AreEqual("bridge_stopped", queue.Enqueue(new BridgeRequest { RequestId = "x" }, DateTime.MaxValue).Result.ReasonCode);
        }
        [TestMethod] public void ConcurrentControlTrafficIsServedInParallel()
        {
            Refresh(); var lease = (string)Send(ControlOperations.Acquire, null, Acquire()).Data["leaseId"];
            var failures = 0;
            var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            {
                for (var n = 0; n < 5; n++)
                {
                    var r = Send(i % 2 == 0 ? ControlOperations.Heartbeat : ControlOperations.Status, i % 2 == 0 ? lease : null);
                    if (r.Status != "completed") Interlocked.Increment(ref failures);
                }
            })).ToArray();
            Task.WaitAll(tasks); Assert.AreEqual(0, failures);
        }
        [TestMethod] public void TokenComparisonIsLengthAndContentStrict()
        {
            Assert.IsTrue(LoopbackServer.TokenMatches("abc", "abc")); Assert.IsFalse(LoopbackServer.TokenMatches("abc", "abd")); Assert.IsFalse(LoopbackServer.TokenMatches("abc", "ab"));
            Assert.IsFalse(LoopbackServer.TokenMatches(null, "abc")); Assert.IsFalse(LoopbackServer.TokenMatches("abc", null));
        }
    }
}
