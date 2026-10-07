using System;
using KspControl.Bridge;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class ExecutionAuthorityTests
    {
        private long now;
        private ExecutionAuthority authority;
        private ExecutionContext context;
        private string lease;
        private ClassifiedEffect[] effects;
        [TestInitialize] public void Setup()
        {
            now = 1000; context = Context(); effects = new[] { new ClassifiedEffect("gear.set", "part1", 1) };
            authority = new ExecutionAuthority(() => now, new[] { "gear.set", "decouple" });
            authority.UpdateContext(context); Provision(); lease = authority.AcquireLease("grant", 1, 10000);
        }
        private static ExecutionContext Context(string install = "install", string world = "world", string agency = "agency", string vessel = "craft", long revision = 1, bool controlled = true, bool ready = true)
            => new ExecutionContext(install, world, agency, vessel, revision, controlled, ready);
        private void Provision(long generation = 1, long expires = 20000)
            => authority.ProvisionTrustedGrant(new TrustedExecutionGrant("grant", generation, context, expires,
                new[] { new EffectPermission("gear.set", "part1"), new EffectPermission("gear.set", "part2") }));
        private ExecutionTicket Admit() => authority.Admit(lease, 1, effects);

        [TestMethod] public void UnchangedCompleteEffectsPassAtDispatch()
        { var ticket = Admit(); authority.ValidateForDispatch(ticket, context, effects); }
        [TestMethod] public void StopBetweenAdmissionAndExecutionDenies()
        { var ticket = Admit(); authority.Stop(); Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, effects)); }
        [TestMethod] public void HumanTakeoverDeniesHeartbeatAndOldDispatch()
        { var ticket = Admit(); authority.HumanTakeover(); Assert.IsFalse(authority.Heartbeat(lease)); Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, effects)); }
        [TestMethod] public void FreshGrantAndLeaseDoNotReviveOldTicket()
        {
            var ticket = Admit(); authority.Stop(); Provision(2); lease = authority.AcquireLease("grant", 2, 10000);
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, effects));
            authority.ValidateForDispatch(Admit(), context, effects);
        }
        [TestMethod] public void GrantGenerationCannotBeReusedAfterRevocation()
        { authority.Stop(); Assert.ThrowsException<InvalidOperationException>(() => Provision()); }
        [DataTestMethod]
        [DataRow("other", "world", "agency", "craft")]
        [DataRow("install", "other", "agency", "craft")]
        [DataRow("install", "world", "other", "craft")]
        [DataRow("install", "world", "agency", "other")]
        public void IdentityChangeRevokesDispatch(string install, string world, string agency, string vessel)
        {
            var ticket = Admit();
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, Context(install, world, agency, vessel), effects));
            Assert.IsFalse(authority.Heartbeat(lease));
        }
        [TestMethod] public void LossOfOwnershipOrSceneReadinessRevokes()
        {
            var ticket = Admit();
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, Context(controlled: false), effects));
            authority.UpdateContext(context); Provision(2); lease = authority.AcquireLease("grant", 2, 10000); ticket = Admit();
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, Context(ready: false), effects));
        }
        [TestMethod] public void ContextRevisionChangeRejectsStaleAction()
        { var ticket = Admit(); Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, Context(revision: 2), effects)); }
        [TestMethod] public void RevisionRollbackCannotReviveOldTicket()
        { var ticket = Admit(); authority.UpdateContext(Context(revision: 2)); Assert.ThrowsException<InvalidOperationException>(() => authority.UpdateContext(context)); Assert.IsFalse(authority.Heartbeat(lease)); }
        [TestMethod] public void UnknownOrMixedDeniedEffectsRejectWholeAdmission()
        {
            Assert.ThrowsException<InvalidOperationException>(() => authority.Admit(lease, 1, new[] { effects[0], new ClassifiedEffect("unknown", "part1", 1) }));
            Assert.ThrowsException<InvalidOperationException>(() => authority.Admit(lease, 1, new[] { effects[0], new ClassifiedEffect("decouple", "part2", 1) }));
            Assert.ThrowsException<InvalidOperationException>(() => authority.Admit(lease, 1, new[] { effects[0], new ClassifiedEffect("gear.set", "foreign", 1) }));
        }
        [TestMethod] public void ChangedBindingsOrSymmetryRejectDispatch()
        {
            var ticket = Admit();
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, new[] { effects[0], new ClassifiedEffect("gear.set", "part2", 1) }));
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, new[] { new ClassifiedEffect("gear.set", "part1", 2) }));
        }
        [TestMethod] public void LeaseExpiryCannotBeRenewedByLateHeartbeat()
        {
            authority.Stop(); Provision(2); lease = authority.AcquireLease("grant", 2, 500); var ticket = Admit(); now += 500;
            Assert.IsFalse(authority.Heartbeat(lease)); Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, effects));
        }
        [TestMethod] public void WatchdogFailureRevokesEvenWithoutExplicitStop()
        { var ticket = Admit(); now += 2000; authority.Tick(); Assert.IsFalse(authority.Heartbeat(lease)); Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, effects)); }
        [TestMethod] public void TimelyHeartbeatDoesNotExtendGrantExpiry()
        {
            authority.Stop(); Provision(2, 2500); lease = authority.AcquireLease("grant", 2, 10000); var ticket = Admit();
            now = 2000; Assert.IsTrue(authority.Heartbeat(lease)); now = 2500;
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, effects));
        }
        [TestMethod] public void MonotonicClockRollbackRevokes()
        { var ticket = Admit(); now--; Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, effects)); }
        [TestMethod] public void EmptyEffectSetsAndWrongRevisionDenied()
        {
            Assert.ThrowsException<InvalidOperationException>(() => authority.Admit(lease, 1, new ClassifiedEffect[0]));
            Assert.ThrowsException<InvalidOperationException>(() => authority.Admit(lease, 0, effects));
        }
        [TestMethod] public void InputsAreDetachedFromCallerArrays()
        {
            var ticket = Admit(); effects[0] = new ClassifiedEffect("gear.set", "part2", 1);
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, context, effects));
        }
    }
}
