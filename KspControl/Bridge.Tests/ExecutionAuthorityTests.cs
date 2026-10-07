using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    internal static class AuthorityHelpers
    {
        public static readonly DateTime Utc0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static LeaseContext Ctx(string epoch = "epoch1", string entity = "editor:VAB", long revision = 0, bool ready = true) => new LeaseContext(epoch, entity, revision, ready);
        public static GrantBinding Bind(string install = "install", string save = "save", string agency = "agency") => new GrantBinding(install, save, agency);
        public static GrantStatusInfo ValidStatus(string id = "grant", long generation = 1) => new GrantStatusInfo
        { Present = true, State = GrantStates.Valid, Id = id, Generation = generation, Operations = new[] { "editor.replace_craft" }, Facilities = new[] { "VAB" }, UnsavedCraftPolicy = "refuse" };
        public static TrustedExecutionGrant Grant(long generation = 1, string id = "grant", double hours = 1, GrantBinding binding = null, string[] facilities = null)
        {
            facilities = facilities ?? new[] { "VAB" };
            var permissions = new List<EffectPermission>();
            foreach (var f in facilities)
            {
                permissions.Add(new EffectPermission("editor.replace_craft", "editor:" + f));
                permissions.Add(new EffectPermission("editor.restore_snapshot", "editor:" + f));
                permissions.Add(new EffectPermission("craft.write", "ships:" + f));
            }
            return new TrustedExecutionGrant(id, generation, binding ?? Bind(), Utc0.AddHours(hours), permissions, facilities.Select(f => "editor:" + f));
        }
    }

    [TestClass]
    public class ExecutionAuthorityTests
    {
        private long now;
        private DateTime utc;
        private MemorySuspensionStore store;
        private ExecutionAuthority authority;
        private string lease;
        private ClassifiedEffect[] effects;

        private static LeaseContext Ctx(string epoch = "epoch1", string entity = "editor:VAB", long revision = 0, bool ready = true) => AuthorityHelpers.Ctx(epoch, entity, revision, ready);
        private static GrantBinding Bind(string install = "install", string save = "save", string agency = "agency") => AuthorityHelpers.Bind(install, save, agency);

        [TestInitialize]
        public void Setup()
        {
            now = 1000; utc = AuthorityHelpers.Utc0; store = new MemorySuspensionStore();
            effects = new[] { new ClassifiedEffect("editor.replace_craft", "editor:VAB", 1) };
            authority = New();
            authority.UpdateContext(Ctx(), Bind(), AuthorityHelpers.ValidStatus());
            Provision();
            lease = authority.AcquireLease(30000, "test purpose");
        }

        private ExecutionAuthority New() => new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, store, () => utc);
        private void Provision(long generation = 1, double hours = 1, string id = "grant") => authority.ProvisionGrant(AuthorityHelpers.Grant(generation, id, hours));
        private void Refresh(LeaseContext context = null) => authority.UpdateContext(context ?? Ctx(), Bind());
        private ExecutionTicket Admit() => authority.Admit(lease, 0, effects);
        private static void Throws(string code, Action action)
        { var error = Assert.ThrowsException<InvalidOperationException>(action); Assert.AreEqual(code, error.Message); }
        private void Tickle(long milliseconds) { now += milliseconds; Refresh(); }

        // ---- carried over behaviour ----

        [TestMethod] public void UnchangedCompleteEffectsPassAtDispatch()
        { var ticket = Admit(); authority.ValidateForDispatch(ticket, Ctx(), effects); }
        [TestMethod] public void StopBetweenAdmissionAndExecutionDenies()
        { var ticket = Admit(); authority.Stop(); Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, Ctx(), effects)); }
        [TestMethod] public void HumanTakeoverDeniesHeartbeatAndOldDispatch()
        { var ticket = Admit(); authority.HumanTakeover(); Assert.IsFalse(authority.Heartbeat(lease)); Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, Ctx(), effects)); }
        [TestMethod] public void FreshGenerationAndLeaseDoNotReviveOldTicket()
        {
            var ticket = Admit(); authority.Stop(); Refresh(); Provision(2); now += 31000; Refresh(); lease = authority.AcquireLease(30000, "again");
            Assert.ThrowsException<InvalidOperationException>(() => authority.ValidateForDispatch(ticket, Ctx(), effects));
            authority.ValidateForDispatch(Admit(), Ctx(), effects);
        }
        [TestMethod] public void StaleRevisionAndEmptyEffectsDenied()
        {
            Throws("stale_revision", () => authority.Admit(lease, 5, effects));
            Throws("invalid_effect_set", () => authority.Admit(lease, 0, new ClassifiedEffect[0]));
        }
        [TestMethod] public void ContextRevisionChangeRejectsStaleAction()
        { var ticket = Admit(); Refresh(Ctx(revision: 1)); Throws("stale_context", () => authority.ValidateForDispatch(ticket, Ctx(revision: 1), effects)); }
        [TestMethod] public void UnknownOrMixedDeniedEffectsRejectWholeAdmission()
        {
            Throws("effect_denied_or_unknown", () => authority.Admit(lease, 0, new[] { effects[0], new ClassifiedEffect("unknown", "editor:VAB", 1) }));
            Throws("effect_denied_or_unknown", () => authority.Admit(lease, 0, new[] { effects[0], new ClassifiedEffect("editor.replace_craft", "editor:SPH", 1) }));
            Throws("effect_denied_or_unknown", () => authority.Admit(lease, 0, new[] { effects[0], new ClassifiedEffect("craft.write", "editor:VAB", 1) }));
        }
        [TestMethod] public void ChangedBindingsOrSymmetryRejectDispatch()
        {
            var ticket = Admit();
            Throws("effects_changed", () => authority.ValidateForDispatch(ticket, Ctx(), new[] { effects[0], new ClassifiedEffect("editor.restore_snapshot", "editor:VAB", 1) }));
            Throws("effects_changed", () => authority.ValidateForDispatch(ticket, Ctx(), new[] { new ClassifiedEffect("editor.replace_craft", "editor:VAB", 2) }));
        }
        [TestMethod] public void InputsAreDetachedFromCallerArrays()
        {
            var ticket = Admit(); effects[0] = new ClassifiedEffect("editor.restore_snapshot", "editor:VAB", 1);
            Throws("effects_changed", () => authority.ValidateForDispatch(ticket, Ctx(), effects));
        }
        [TestMethod] public void LeaseExpiryCannotBeRenewedByLateHeartbeat()
        {
            now += 30000; Assert.IsFalse(authority.Heartbeat(lease)); Assert.AreEqual(ControlReasons.LeaseExpired, authority.LeaseFailureReason(lease));
        }
        [TestMethod] public void TimelyHeartbeatDoesNotExtendGrantExpiry()
        {
            utc = AuthorityHelpers.Utc0; authority.Stop(); Refresh(); authority.ProvisionGrant(AuthorityHelpers.Grant(2, "grant", 40.0 / 3600)); // 40 s
            now += 31000; Refresh(); lease = authority.AcquireLease(300000, "bounded");
            now += 1500; Assert.IsTrue(authority.Heartbeat(lease)); now += 1500;
            Assert.IsTrue(authority.Heartbeat(lease)); utc = AuthorityHelpers.Utc0.AddSeconds(40); now += 8000;
            Assert.IsFalse(authority.Heartbeat(lease));
        }
        [TestMethod] public void RenewExtendsFromNowButNeverPastGrantDeadline()
        {
            now += 1000; authority.Heartbeat(lease); authority.RenewLease(lease, 300000);
            Assert.AreEqual(300, authority.Status().Lease.ExpiresInSeconds);
            // one-hour grant: a 300 s lease can never exceed it
            Assert.IsTrue(authority.Status().Lease.ExpiresInSeconds <= 3600);
            Assert.ThrowsException<ArgumentException>(() => authority.RenewLease(lease, 29999));
            Throws(ControlReasons.LeaseInvalid, () => authority.RenewLease("other", 30000));
        }
        [TestMethod] public void ReleaseEndsOnlyTheNamedLease()
        {
            Assert.IsFalse(authority.ReleaseLease("other")); Assert.IsTrue(authority.ReleaseLease(lease));
            Assert.IsFalse(authority.Heartbeat(lease)); Assert.AreEqual(ControlReasons.LeaseInvalid, authority.LeaseFailureReason(lease));
            Assert.IsNotNull(authority.CurrentGrantId, "release keeps the grant");
        }

        // ---- R1-§5 mapping table: one test per row ----

        [TestMethod] public void MapClockRegressionBurnsFaultAndPersists()
        {
            now--; Throws(ControlReasons.ClockRegressed, () => authority.Tick());
            Assert.IsNull(authority.CurrentGrantId); Assert.IsTrue(authority.IsBurned("grant", 1));
            var saved = store.Load(); Assert.AreEqual(1, saved.Count); Assert.AreEqual("fault", saved[0].Reason); Assert.AreEqual(1, saved[0].Generation);
            Refresh(); Throws("grant_suspended", () => Provision());
        }
        [TestMethod] public void MapGrantDeadlineBurnsExpiredWithoutPersisting()
        {
            now += 3600 * 1000L; Refresh(); authority.Tick();
            Assert.IsNull(authority.CurrentGrantId); Assert.IsTrue(authority.IsBurned("grant", 1));
            Assert.AreEqual(0, store.Load().Count, "expiry is not persisted");
            Assert.IsFalse(authority.Heartbeat(lease));
        }
        [TestMethod] public void MapWallClockExpiryWinsWhenEarlier()
        {
            utc = AuthorityHelpers.Utc0.AddHours(2); authority.Heartbeat(lease); authority.Tick();
            Assert.IsNull(authority.CurrentGrantId); Assert.IsFalse(authority.Heartbeat(lease));
        }
        [TestMethod] public void MapLeaseDeadlineRevokesLeaseOnly()
        {
            now += 29000; Tickle(0); authority.Heartbeat(lease); now += 1000; authority.Heartbeat(lease); // keep alive, then let the lease deadline pass
            now = 1000 + 30000; authority.Tick();
            Assert.IsFalse(authority.Heartbeat(lease)); Assert.AreEqual(ControlReasons.LeaseExpired, authority.LeaseFailureReason(lease));
            Assert.AreEqual("grant", authority.CurrentGrantId);
            Refresh(); authority.AcquireLease(30000, "next");
        }
        [TestMethod] public void MapWatchdogRevokesLeaseOnly()
        {
            now += 2000; authority.Tick();
            Assert.IsFalse(authority.Heartbeat(lease)); Assert.AreEqual(ControlReasons.LeaseExpired, authority.LeaseFailureReason(lease));
            Assert.AreEqual("grant", authority.CurrentGrantId);
        }
        [TestMethod] public void MapRevisionRegressionRevokesLeaseOnly()
        {
            Refresh(Ctx(revision: 3));
            Throws("revision_regressed", () => authority.UpdateContext(Ctx(revision: 2), Bind()));
            Assert.IsFalse(authority.Heartbeat(lease)); Assert.AreEqual(ControlReasons.AuthorityRevoked, authority.LeaseFailureReason(lease));
            Assert.AreEqual("grant", authority.CurrentGrantId);
        }
        [DataTestMethod]
        [DataRow("epoch2", "editor:VAB")]
        [DataRow("epoch1", "editor:SPH")]
        public void MapEpochOrEntityChangeRevokesLeaseKeepsGrant(string epoch, string entity)
        {
            Refresh(Ctx(epoch, entity));
            Assert.IsFalse(authority.Heartbeat(lease)); Assert.AreEqual(ControlReasons.AuthorityRevoked, authority.LeaseFailureReason(lease));
            Assert.AreEqual("grant", authority.CurrentGrantId);
        }
        [TestMethod] public void MapBindingChangeDropsGrantThenReprovisionsSameGeneration()
        {
            authority.UpdateContext(Ctx(), Bind(agency: "other-agency"));
            Assert.IsNull(authority.CurrentGrantId); Assert.IsFalse(authority.Heartbeat(lease));
            Assert.IsFalse(authority.IsBurned("grant", 1), "a binding drop never burns");
            Throws("grant_context_mismatch", () => Provision());
            authority.UpdateContext(Ctx(), Bind()); Provision();
            Assert.AreEqual("grant", authority.CurrentGrantId); Assert.AreEqual(1, authority.CurrentGrantGeneration);
        }
        [TestMethod] public void MapSceneNotReadyRefusesWithoutRevoking()
        {
            var ticket = Admit();
            Refresh(Ctx(ready: false));
            Assert.IsTrue(authority.Heartbeat(lease), "lease survives");
            Throws(ControlReasons.EditorUnavailable, () => authority.Admit(lease, 0, effects));
            Throws(ControlReasons.EditorUnavailable, () => authority.ValidateForDispatch(ticket, Ctx(ready: false), effects));
            Refresh(Ctx(ready: true));
            authority.ValidateForDispatch(ticket, Ctx(), effects);
            Assert.IsNotNull(Admit());
        }
        [TestMethod] public void MapStopBurnsPersistsAndSurvivesRestart()
        {
            authority.Stop();
            Assert.IsNull(authority.CurrentGrantId); Assert.IsFalse(authority.Heartbeat(lease));
            var saved = store.Load(); Assert.AreEqual(1, saved.Count); Assert.AreEqual("stop", saved[0].Reason);
            // "restart": a new instance over the same store refuses the same generation
            var restarted = New(); restarted.UpdateContext(Ctx(), Bind());
            Assert.IsTrue(restarted.IsBurned("grant", 1));
            Throws("grant_suspended", () => restarted.ProvisionGrant(AuthorityHelpers.Grant(1)));
            // a higher generation (rearm) is allowed
            restarted.ProvisionGrant(AuthorityHelpers.Grant(2)); Assert.AreEqual(2, restarted.CurrentGrantGeneration);
        }
        [TestMethod] public void MapHumanTakeoverRevokesLeaseAndStartsCooldown()
        {
            authority.HumanTakeover();
            Assert.IsFalse(authority.Heartbeat(lease)); Assert.AreEqual("grant", authority.CurrentGrantId);
            Assert.AreEqual(30, authority.Status().CooldownSeconds);
            now += 29000; Refresh(); Throws(ControlReasons.HumanActivityCooldown, () => authority.AcquireLease(30000, "too soon"));
            now += 1000; Refresh(); Assert.AreEqual(0, authority.Status().CooldownSeconds);
            Assert.IsNotNull(authority.AcquireLease(30000, "after cooldown"));
        }
        [TestMethod] public void MapProvisionRequiresBindingNotBurnedNotExpiredAndNoRegression()
        {
            Throws("grant_context_mismatch", () => authority.ProvisionGrant(AuthorityHelpers.Grant(2, "grant", 1, Bind(save: "other"))));
            Throws("grant_expired", () => authority.ProvisionGrant(AuthorityHelpers.Grant(2, "grant", 0)));
            authority.ProvisionGrant(AuthorityHelpers.Grant(5)); Refresh();
            Throws("grant_generation_regressed", () => authority.ProvisionGrant(AuthorityHelpers.Grant(4)));
            authority.DropGrant("test"); Refresh();
            authority.ProvisionGrant(AuthorityHelpers.Grant(5)); // equal generation after a non-burning drop
            Assert.AreEqual(5, authority.CurrentGrantGeneration);
        }
        [TestMethod] public void ProvisioningNewerGenerationReplacesGrantAndRevokesLease()
        {
            authority.ProvisionGrant(AuthorityHelpers.Grant(2));
            Assert.IsFalse(authority.Heartbeat(lease)); Assert.AreEqual(2, authority.CurrentGrantGeneration);
        }
        [TestMethod] public void SceneChangeKeepsGrantAndDropsLease()
        {
            Refresh(Ctx("epoch2", "scene:SPACECENTER", ready: false));
            Assert.AreEqual("grant", authority.CurrentGrantId); Assert.IsFalse(authority.Heartbeat(lease));
            Refresh(Ctx("epoch3", "editor:VAB")); Assert.IsNotNull(authority.AcquireLease(30000, "back in editor"));
        }

        // ---- admission rules ----

        [TestMethod] public void AcquireRulesBusyFacilityStaleContextAndDuration()
        {
            Throws(ControlReasons.ControlBusy, () => authority.AcquireLease(30000, "second"));
            authority.ReleaseLease(lease); Refresh();
            Assert.ThrowsException<ArgumentException>(() => authority.AcquireLease(29999, "short"));
            Assert.ThrowsException<ArgumentException>(() => authority.AcquireLease(300001, "long"));
            Refresh(Ctx(entity: "editor:SPH")); Throws(ControlReasons.FacilityMismatch, () => authority.AcquireLease(30000, "wrong facility"));
            Refresh(Ctx(ready: false)); Throws(ControlReasons.EditorUnavailable, () => authority.AcquireLease(30000, "not ready"));
        }
        [TestMethod] public void StalePublishedContextRefusesAcquireButAcceptsHeartbeat()
        {
            now += 1500; // main thread stalled: no UpdateContext for 1.5 s, but heartbeats keep arriving
            Assert.IsTrue(authority.Heartbeat(lease));
            authority.ReleaseLease(lease);
            Throws(ControlReasons.EditorUnavailable, () => authority.AcquireLease(30000, "stalled"));
            Refresh(); Assert.IsNotNull(authority.AcquireLease(30000, "fresh"));
        }
        [TestMethod] public void AcquireWithoutGrantReportsPublishedState()
        {
            var fresh = New();
            fresh.UpdateContext(Ctx(), Bind(), new GrantStatusInfo { Present = true, State = GrantStates.Expired });
            Throws(ControlReasons.GrantExpired, () => fresh.AcquireLease(30000, "x"));
            fresh.PublishGrantStatus(GrantStatusInfo.Missing());
            Throws(ControlReasons.GrantMissing, () => fresh.AcquireLease(30000, "x"));
        }
        [TestMethod] public void LeaseDeadlineNeverExceedsGrantDeadline()
        {
            authority.Stop(); Refresh(); authority.ProvisionGrant(AuthorityHelpers.Grant(2, "grant", 60.0 / 3600)); now += 31000; Refresh();
            lease = authority.AcquireLease(300000, "x");
            Assert.IsTrue(authority.Status().Lease.ExpiresInSeconds <= 60);
        }

        // ---- operation lock and rebase ----

        [TestMethod] public void RebaseAllowedInsideOperationAndRestoresDispatchValidity()
        {
            var ticket = Admit(); authority.BeginOperation(ticket, "op1");
            Refresh(Ctx(revision: 1)); // our own load changed the published revision
            Throws("stale_context", () => authority.ValidateForDispatch(ticket, Ctx(revision: 1), effects));
            authority.RebaseUnderOperation(ticket, "op1", 1);
            authority.ValidateForDispatch(ticket, Ctx(revision: 1), effects);
            authority.EndOperation("op1");
        }
        [TestMethod] public void RebaseOutsideOperationThrows()
        {
            var ticket = Admit();
            Throws("rebase_outside_operation", () => authority.RebaseUnderOperation(ticket, "op1", 1));
            authority.BeginOperation(ticket, "op1");
            Throws("rebase_outside_operation", () => authority.RebaseUnderOperation(ticket, "other", 1));
            authority.EndOperation("op1");
            Throws("rebase_outside_operation", () => authority.RebaseUnderOperation(ticket, "op1", 1));
        }
        [TestMethod] public void RebaseAfterLeaseLossThrows()
        {
            var ticket = Admit(); authority.BeginOperation(ticket, "op1"); authority.HumanTakeover();
            Throws("rebase_outside_operation", () => authority.RebaseUnderOperation(ticket, "op1", 1));
        }
        [TestMethod] public void SecondOperationCannotStartWhileOneIsActive()
        {
            var ticket = Admit(); authority.BeginOperation(ticket, "op1");
            Throws("operation_in_progress", () => authority.BeginOperation(ticket, "op2"));
        }

        // ---- persistence failure modes ----

        [TestMethod] public void UnreadableSuspensionsFailClosed()
        {
            store.FailLoad = true; var closed = New(); closed.UpdateContext(Ctx(), Bind());
            Assert.IsTrue(closed.SuspensionsUnreadable);
            Throws("grant_suspended", () => closed.ProvisionGrant(AuthorityHelpers.Grant(1)));
            Assert.IsTrue(closed.IsBurned("anything", 9));
        }
        [TestMethod] public void FailedPersistIsFlaggedButStillBurnsInMemory()
        {
            store.FailSave = true; authority.Stop();
            Assert.IsTrue(authority.PersistFailed); Assert.IsTrue(authority.IsBurned("grant", 1));
        }
        [TestMethod] public void PruneRemovesOnlyOldLowerGenerations()
        {
            authority.Stop();
            utc = AuthorityHelpers.Utc0.AddDays(31); authority.PruneSuspensions(1); Assert.AreEqual(1, store.Load().Count, "same generation is kept");
            authority.PruneSuspensions(2); Assert.AreEqual(0, store.Load().Count);
        }

        // ---- thread model ----

        [TestMethod] public void WorkerCallsAndMainThreadUpdatesDoNotCorruptState()
        {
            var errors = new List<Exception>(); var stop = false;
            var workers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            {
                try { while (!Volatile.Read(ref stop)) { authority.Heartbeat(lease); authority.Status(); } }
                catch (Exception error) { lock (errors) errors.Add(error); }
            })).ToArray();
            for (var i = 0; i < 2000; i++) { Refresh(); }
            Volatile.Write(ref stop, true); Task.WaitAll(workers);
            Assert.AreEqual(0, errors.Count, errors.Count == 0 ? "" : errors[0].ToString());
        }
    }

    [TestClass]
    public class MonotonicClockTests
    {
        [TestMethod] public void NeverDecreasesAcrossThreads()
        {
            var failures = 0;
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                long last = MonotonicClock.Milliseconds;
                for (var i = 0; i < 200000; i++) { var next = MonotonicClock.Milliseconds; if (next < last) Interlocked.Increment(ref failures); last = next; }
            })).ToArray();
            Task.WaitAll(tasks);
            Assert.AreEqual(0, failures);
        }
        [TestMethod] public void AdvancesWithRealTime()
        {
            var start = MonotonicClock.Milliseconds; Thread.Sleep(60); var elapsed = MonotonicClock.Milliseconds - start;
            Assert.IsTrue(elapsed >= 50 && elapsed < 2000, "elapsed " + elapsed);
        }
    }
}
