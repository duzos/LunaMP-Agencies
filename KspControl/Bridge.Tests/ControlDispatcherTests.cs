using System;
using System.Linq;
using System.Reflection;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>Records every call and would throw if anything reached for Unity. The inline path must only ever use these members.</summary>
    internal sealed class RecordingAuthority : IControlAuthority
    {
        public int Calls;
        public Func<object> UnityAccess = () => { throw new InvalidOperationException("Unity was touched from a loopback worker"); };
        public Exception Throw;
        private void Enter() { Calls++; if (Throw != null) throw Throw; }
        public string AcquireLease(long durationMilliseconds, string purpose, string grantId = null, long grantGeneration = 0) { Enter(); return new string('a', 32); }
        public void RenewLease(string id, long durationMilliseconds) { Enter(); }
        public bool ReleaseLease(string id) { Enter(); return true; }
        public bool Heartbeat(string id) { Enter(); return true; }
        public string LeaseFailureReason(string id) { Enter(); return ControlReasons.LeaseInvalid; }
        public LeaseDetails DescribeLease(string id) { Enter(); return new LeaseDetails { LeaseId = id, GrantId = "g", Generation = 1, Epoch = "e", Entity = "editor:VAB", Purpose = "p", ExpiresInSeconds = 30 }; }
        public ControlStatusInfo Status() { Enter(); return new ControlStatusInfo(); }
        public string PublishedEpoch { get { return "epoch"; } }
        public long PublishedRevision { get { return 0; } }
    }

    [TestClass]
    public class ControlDispatcherTests
    {
        private long now;
        private ExecutionAuthority authority;
        private ControlDispatcher dispatcher;
        private static BridgeRequest Req(string op, string lease = null, JObject args = null) =>
            new BridgeRequest { RequestId = "request-12345678", Operation = op, LeaseId = lease, Arguments = args ?? new JObject() };
        private static JObject Acquire(string purpose = "build a probe", int seconds = 60) => new JObject { ["purpose"] = purpose, ["durationSeconds"] = seconds };

        [TestInitialize]
        public void Setup()
        {
            now = 5000;
            authority = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, new MemorySuspensionStore(), () => AuthorityHelpers.Utc0);
            authority.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind(), AuthorityHelpers.ValidStatus());
            authority.ProvisionGrant(AuthorityHelpers.Grant());
            dispatcher = new ControlDispatcher(authority);
        }

        [TestMethod] public void StatusReportsGrantLeaseAndCooldownWithoutSecrets()
        {
            var response = dispatcher.Handle(Req(ControlOperations.Status));
            Assert.AreEqual("completed", response.Status); Assert.AreEqual("epoch1", response.WorldEpoch);
            var data = response.Data;
            Assert.AreEqual(GrantStates.Valid, (string)data["grant"]["state"]); Assert.AreEqual("grant", (string)data["grant"]["id"]); Assert.IsFalse((bool)data["lease"]["held"]); Assert.AreEqual(0, (int)data["cooldownSeconds"]);
            var text = data.ToString().ToLowerInvariant();
            foreach (var word in new[] { "payload", "\"mac\"", "\"key\"", "secret" }) Assert.IsFalse(text.Contains(word), word);
        }
        [TestMethod] public void AcquireReturnsLeaseDetailsAndRefusesASecond()
        {
            var response = dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire()));
            Assert.AreEqual("completed", response.Status, response.ReasonCode); var id = (string)response.Data["leaseId"];
            Assert.IsTrue(ControlLimits.IsLeaseId(id)); Assert.AreEqual("build a probe", (string)response.Data["purpose"]); Assert.AreEqual("grant", (string)response.Data["grantId"]);
            Assert.AreEqual(1, (int)response.Data["generation"]); Assert.AreEqual("epoch1", (string)response.Data["epoch"]); Assert.AreEqual("editor:VAB", (string)response.Data["entity"]); Assert.AreEqual(60, (int)response.Data["expiresInSeconds"]);
            Assert.AreEqual(ControlReasons.ControlBusy, dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())).ReasonCode);
            Assert.AreEqual(true, (bool)dispatcher.Handle(Req(ControlOperations.Status)).Data["lease"]["held"]);
        }
        [TestMethod] public void AcquireWithoutAGrantReportsTheGrantState()
        {
            authority.DropGrant("test"); authority.PublishGrantStatus(new GrantStatusInfo { Present = true, State = GrantStates.Revoked });
            Assert.AreEqual(ControlReasons.GrantRevoked, dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())).ReasonCode);
            authority.PublishGrantStatus(GrantStatusInfo.Missing());
            Assert.AreEqual(ControlReasons.GrantMissing, dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())).ReasonCode);
        }
        [TestMethod] public void HeartbeatRenewAndReleaseLifecycle()
        {
            var id = (string)dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())).Data["leaseId"];
            now += 1000; Assert.AreEqual("completed", dispatcher.Handle(Req(ControlOperations.Heartbeat, id)).Status);
            var renewed = dispatcher.Handle(Req(ControlOperations.Renew, id, new JObject { ["durationSeconds"] = 120 })); Assert.AreEqual("completed", renewed.Status, renewed.ReasonCode); Assert.AreEqual(120, (int)renewed.Data["expiresInSeconds"]);
            Assert.AreEqual("completed", dispatcher.Handle(Req(ControlOperations.Release, id)).Status);
            var gone = dispatcher.Handle(Req(ControlOperations.Heartbeat, id)); Assert.AreEqual("failed", gone.Status); Assert.AreEqual(ControlReasons.LeaseInvalid, gone.ReasonCode);
        }
        [TestMethod] public void LeaseFailuresUseDistinctReasonCodes()
        {
            var id = (string)dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire(seconds: 30))).Data["leaseId"];
            authority.HumanTakeover(); Assert.AreEqual(ControlReasons.AuthorityRevoked, dispatcher.Handle(Req(ControlOperations.Heartbeat, id)).ReasonCode);
            now += 31000; authority.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind());
            var second = (string)dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire(seconds: 30))).Data["leaseId"];
            now += 30000; Assert.AreEqual(ControlReasons.LeaseExpired, dispatcher.Handle(Req(ControlOperations.Heartbeat, second)).ReasonCode);
            Assert.AreEqual(ControlReasons.LeaseInvalid, dispatcher.Handle(Req(ControlOperations.Heartbeat, new string('f', 32))).ReasonCode);
        }
        [TestMethod] public void CooldownAfterTakeoverIsReported()
        {
            dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())); authority.HumanTakeover();
            Assert.AreEqual(ControlReasons.HumanActivityCooldown, dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())).ReasonCode);
            Assert.AreEqual(30, (int)dispatcher.Handle(Req(ControlOperations.Status)).Data["cooldownSeconds"]);
        }
        [TestMethod] public void StaleContextRefusesAcquireButNotHeartbeat()
        {
            var id = (string)dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())).Data["leaseId"];
            now += 1500;
            Assert.AreEqual("completed", dispatcher.Handle(Req(ControlOperations.Heartbeat, id)).Status);
            dispatcher.Handle(Req(ControlOperations.Release, id));
            Assert.AreEqual(ControlReasons.EditorUnavailable, dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())).ReasonCode);
        }
        [DataTestMethod]
        [DataRow(null, 60)] [DataRow("", 60)] [DataRow("   ", 60)] [DataRow("bad\u0007purpose", 60)] [DataRow("ok", 29)] [DataRow("ok", 301)] [DataRow("ok", -1)]
        public void AcquireValidatesArguments(string purpose, int seconds)
        {
            var args = new JObject { ["durationSeconds"] = seconds }; if (purpose != null) args["purpose"] = purpose;
            var response = dispatcher.Handle(Req(ControlOperations.Acquire, null, args));
            Assert.AreEqual("failed", response.Status); Assert.AreEqual(ControlReasons.InvalidArgument, response.ReasonCode); Assert.IsNotNull(response.Data["detail"]);
            Assert.IsFalse((bool)dispatcher.Handle(Req(ControlOperations.Status)).Data["lease"]["held"]);
        }
        [TestMethod] public void WrongTypesAndOversizeAreInvalidArgument()
        {
            Assert.AreEqual(ControlReasons.InvalidArgument, dispatcher.Handle(Req(ControlOperations.Acquire, null, new JObject { ["purpose"] = 5, ["durationSeconds"] = 60 })).ReasonCode);
            Assert.AreEqual(ControlReasons.InvalidArgument, dispatcher.Handle(Req(ControlOperations.Acquire, null, new JObject { ["purpose"] = "x", ["durationSeconds"] = "60" })).ReasonCode);
            Assert.AreEqual(ControlReasons.InvalidArgument, dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire(new string('p', 129)))).ReasonCode);
            Assert.AreEqual("completed", dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire(new string('p', 128)))).Status);
            foreach (var op in new[] { ControlOperations.Heartbeat, ControlOperations.Release, ControlOperations.Renew })
                foreach (var bad in new[] { null, "", "short", new string('z', 32) })
                    Assert.AreEqual(ControlReasons.InvalidArgument, dispatcher.Handle(Req(op, bad, new JObject { ["durationSeconds"] = 60 })).ReasonCode, op + " " + bad);
        }
        [TestMethod] public void LeaseIdMayArriveInArgumentsAndIsCaseInsensitive()
        {
            var id = (string)dispatcher.Handle(Req(ControlOperations.Acquire, null, Acquire())).Data["leaseId"];
            Assert.AreEqual("completed", dispatcher.Handle(Req(ControlOperations.Heartbeat, null, new JObject { ["leaseId"] = id.ToUpperInvariant() })).Status);
        }
        [TestMethod] public void UnknownControlOperationIsUnavailable()
            => Assert.AreEqual(ControlReasons.OperationUnavailable, dispatcher.Handle(Req("control.launch")).ReasonCode);
        [TestMethod] public void InternalErrorsNeverLeakMessages()
        {
            var fake = new RecordingAuthority { Throw = new InvalidOperationException("secret path C:\\Users\\x\\key.bin") };
            var response = new ControlDispatcher(fake).Handle(Req(ControlOperations.Status));
            Assert.AreEqual("authority_unavailable", response.ReasonCode); Assert.IsFalse((response.Data ?? new JObject()).ToString().Contains("secret"));
            fake.Throw = new NullReferenceException("C:\\private"); Assert.AreEqual("authority_unavailable", new ControlDispatcher(fake).Handle(Req(ControlOperations.Heartbeat, new string('a', 32))).ReasonCode);
        }
        [TestMethod] public void InlineHandlersOnlyUseTheFakeAuthorityAndNeverReachForUnity()
        {
            var fake = new RecordingAuthority(); var handler = new ControlDispatcher(fake);
            var id = new string('a', 32);
            foreach (var request in new[] { Req(ControlOperations.Status), Req(ControlOperations.Acquire, null, Acquire()), Req(ControlOperations.Renew, id, new JObject { ["durationSeconds"] = 30 }), Req(ControlOperations.Heartbeat, id), Req(ControlOperations.Release, id) })
                Assert.AreEqual("completed", handler.Handle(request).Status, request.Operation);
            Assert.IsTrue(fake.Calls >= 5);
        }
        [TestMethod] public void TheInlinePathIsCompiledWithoutAnyUnityReference()
        {
            foreach (var type in new[] { typeof(ControlDispatcher), typeof(ExecutionAuthority), typeof(LoopbackServer), typeof(GrantWatcher) })
                foreach (var name in type.Assembly.GetReferencedAssemblies().Select(a => a.Name))
                    Assert.IsFalse(name.StartsWith("UnityEngine", StringComparison.Ordinal) || name == "Assembly-CSharp", type.Name + " references " + name);
            var ctor = typeof(ControlDispatcher).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
            CollectionAssert.AreEqual(new[] { typeof(IControlAuthority) }, ctor.GetParameters().Select(p => p.ParameterType).ToArray());
        }
    }
}
