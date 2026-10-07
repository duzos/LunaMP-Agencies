using System;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    /// <summary>The standing live spend cap: min(grant limit, 100000, 25% of the confirmed balance at the grant's first acquire), fixed per generation.</summary>
    [TestClass]
    public class LaunchSpendCapTests
    {
        private long now;
        private MemorySpendCapStore store;
        private double? funds;
        private ExecutionAuthority authority;

        private static TrustedExecutionGrant Grant(long generation, long spend, params string[] operations)
        {
            var ops = operations.Length == 0 ? new[] { "editor.launch" } : operations;
            var permissions = new EffectPermission[ops.Length];
            for (var i = 0; i < ops.Length; i++) permissions[i] = new EffectPermission(ops[i], "editor:VAB");
            return new TrustedExecutionGrant("grant", generation, AuthorityHelpers.Bind(), AuthorityHelpers.Utc0.AddHours(100), permissions, new[] { "editor:VAB" }, spend);
        }

        private ExecutionAuthority New()
        {
            var a = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, new MemorySuspensionStore(), () => AuthorityHelpers.Utc0);
            a.ConfigureSpendCap(store, () => funds);
            a.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind(), AuthorityHelpers.ValidStatus());
            return a;
        }

        private long? Cap(ExecutionAuthority a, long generation = 1)
        {
            var status = AuthorityHelpers.ValidStatus("grant", generation);
            a.PublishGrantStatus(status);
            return a.Status().Grant.EffectiveSpendCap;
        }

        [TestInitialize] public void Setup() { now = 1000; store = new MemorySpendCapStore(); funds = 200000; authority = New(); }

        [TestMethod] public void TheCapIsAQuarterOfTheBalanceWhenThatIsTheSmallest()
        {
            authority.ProvisionGrant(Grant(1, 500000));
            authority.AcquireLease(300000, "launch");
            Assert.AreEqual(50000L, Cap(authority));
        }

        [TestMethod] public void AGameWithoutFundsCapsAtTheGrantLimitAndTheStandingMaximum()
        {
            funds = double.PositiveInfinity; // the provider's signal for a mode with no economy (sandbox): launches cost nothing
            authority.ProvisionGrant(Grant(1, 30000));
            authority.AcquireLease(300000, "launch");
            Assert.AreEqual(30000L, Cap(authority));
        }

        [TestMethod] public void TheCapNeverExceedsOneHundredThousand()
        {
            funds = 5000000;
            authority.ProvisionGrant(Grant(1, 900000));
            authority.AcquireLease(300000, "launch");
            Assert.AreEqual(100000L, Cap(authority));
        }

        [TestMethod] public void TheSignedLimitCapsItWhenSmaller()
        {
            authority.ProvisionGrant(Grant(1, 8000));
            authority.AcquireLease(300000, "launch");
            Assert.AreEqual(8000L, Cap(authority));
        }

        [TestMethod] public void EarningFundsLaterNeverEnlargesIt()
        {
            authority.ProvisionGrant(Grant(1, 500000));
            var lease = authority.AcquireLease(300000, "launch");
            funds = 4000000; authority.ReleaseLease(lease);
            authority.AcquireLease(300000, "again");
            Assert.AreEqual(50000L, Cap(authority));
        }

        [TestMethod] public void ASpendingDropNeverShrinksItEither()
        {
            authority.ProvisionGrant(Grant(1, 500000));
            var lease = authority.AcquireLease(300000, "launch");
            funds = 100; authority.ReleaseLease(lease);
            authority.AcquireLease(300000, "again");
            Assert.AreEqual(50000L, Cap(authority));
        }

        [TestMethod] public void ARestartReadsTheSameCapFromTheStore()
        {
            authority.ProvisionGrant(Grant(1, 500000));
            authority.AcquireLease(300000, "launch");
            funds = 4000000;
            var restarted = New();
            restarted.ProvisionGrant(Grant(1, 500000));
            restarted.AcquireLease(300000, "after restart");
            Assert.AreEqual(50000L, Cap(restarted));
            Assert.AreEqual(1, store.Saves, "fixed once");
        }

        [TestMethod] public void AnUnknownBalanceFixesNothingUntilItIsKnown()
        {
            funds = null;
            authority.ProvisionGrant(Grant(1, 500000));
            var lease = authority.AcquireLease(300000, "launch");
            Assert.IsNull(Cap(authority), "no cap yet, so the host authorises no launch spend");
            Assert.AreEqual(0, store.Saves);
            authority.ReleaseLease(lease); funds = 40000;
            authority.AcquireLease(300000, "again");
            Assert.AreEqual(10000L, Cap(authority));
        }

        [TestMethod] public void EachGenerationFixesItsOwnCap()
        {
            authority.ProvisionGrant(Grant(1, 500000));
            var lease = authority.AcquireLease(300000, "launch");
            authority.ReleaseLease(lease); funds = 80000;
            authority.ProvisionGrant(Grant(2, 500000));
            authority.AcquireLease(300000, "rearmed");
            Assert.AreEqual(20000L, Cap(authority, 2));
        }

        [TestMethod] public void AGrantWithoutTheLaunchOperationFixesNoCap()
        {
            authority.ProvisionGrant(Grant(1, 500000, "editor.replace_craft"));
            authority.AcquireLease(300000, "build");
            Assert.IsNull(Cap(authority)); Assert.AreEqual(0, store.Saves);
        }

        [TestMethod] public void AnUnreadableStoreFailsClosedWithNoCap()
        {
            store.FailLoad = true;
            authority.ProvisionGrant(Grant(1, 500000));
            authority.AcquireLease(300000, "launch");
            Assert.IsNull(Cap(authority));
        }
    }
}
