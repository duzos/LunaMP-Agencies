using System;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    /// <summary>The lease moving from the editor to the scene a launch loads (editor:VAB to vessel:guid), and what a grant says about it.</summary>
    [TestClass]
    public class LaunchTransitionTests
    {
        private const string Vessel = "vessel:11111111-2222-3333-4444-555555555555";
        private long now;
        private ExecutionAuthority authority;
        private string lease;
        private ExecutionTicket ticket;

        private static LeaseContext Ctx(string epoch = "epoch1", string entity = "editor:VAB", bool ready = true) { return AuthorityHelpers.Ctx(epoch, entity, 0, ready); }
        private static GrantBinding Bind() { return AuthorityHelpers.Bind(); }

        private void Arm(TrustedExecutionGrant grant = null)
        {
            now = 1000;
            authority = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, new MemorySuspensionStore(), () => AuthorityHelpers.Utc0);
            authority.UpdateContext(Ctx(), Bind(), AuthorityHelpers.ValidStatus());
            authority.ProvisionGrant(grant ?? AuthorityHelpers.Grant());
            lease = authority.AcquireLease(300000, "launch");
            ticket = authority.Admit(lease, 0, new[] { new ClassifiedEffect("editor.launch", "editor:VAB", 0) });
        }

        private static TrustedExecutionGrant FlightGrant()
        {
            return new TrustedExecutionGrant("grant", 1, AuthorityHelpers.Bind(), AuthorityHelpers.Utc0.AddHours(1),
                new[] { new EffectPermission("editor.launch", "editor:VAB"), new EffectPermission("flight.control", "vessel:any") }, new[] { "editor:VAB" });
        }

        [TestInitialize] public void Setup() { Arm(); }

        [TestMethod] public void ASceneChangeRevokesTheLeaseUnlessALaunchTransitionIsArmed()
        {
            authority.BeginOperation(ticket, "op-1");
            authority.UpdateContext(Ctx("epoch2", "scene:FLIGHT", false), Bind());
            Assert.IsFalse(authority.LeaseHeld);
            Assert.AreEqual("authority_revoked", authority.LeaseFailureReason(lease));
        }

        [TestMethod] public void AnArmedTransitionCarriesTheLeaseAcrossTheSceneChange()
        {
            authority.BeginOperation(ticket, "op-1"); authority.BeginTransition(ticket);
            authority.UpdateContext(Ctx("epoch2", "scene:FLIGHT", false), Bind());
            Assert.IsTrue(authority.LeaseHeld);
            var details = authority.DescribeLease(lease);
            Assert.AreEqual("epoch2", details.Epoch); Assert.AreEqual("scene:FLIGHT", details.Entity);
            authority.UpdateContext(Ctx("epoch2", Vessel, false), Bind());
            Assert.AreEqual(Vessel, authority.DescribeLease(lease).Entity, "the lease follows the context until the transition ends");
            Assert.IsTrue(authority.TicketCurrent(ticket));
        }

        [TestMethod] public void ATransitionNeedsTheOperationLock()
        {
            Assert.ThrowsException<InvalidOperationException>(() => authority.BeginTransition(ticket));
        }

        [TestMethod] public void WithFlightOperationsSuccessKeepsTheLeaseOnTheVessel()
        {
            Arm(FlightGrant());
            authority.BeginOperation(ticket, "op-1"); authority.BeginTransition(ticket);
            authority.UpdateContext(Ctx("epoch2", "scene:FLIGHT", false), Bind());
            Assert.IsTrue(authority.FinishTransition(ticket, Vessel));
            authority.EndOperation("op-1");
            authority.UpdateContext(Ctx("epoch2", Vessel, false), Bind());
            Assert.IsTrue(authority.LeaseHeld);
            Assert.AreEqual(Vessel, authority.DescribeLease(lease).Entity);
            authority.UpdateContext(Ctx("epoch2", "vessel:99999999-2222-3333-4444-555555555555", false), Bind());
            Assert.IsFalse(authority.LeaseHeld, "after the transition a context change revokes again");
        }

        [TestMethod] public void WithoutFlightOperationsSuccessReleasesTheLease()
        {
            authority.BeginOperation(ticket, "op-1"); authority.BeginTransition(ticket);
            authority.UpdateContext(Ctx("epoch2", "scene:FLIGHT", false), Bind());
            Assert.IsFalse(authority.FinishTransition(ticket, Vessel));
            Assert.IsFalse(authority.LeaseHeld); Assert.IsNull(authority.DescribeLease(lease));
        }

        [TestMethod] public void AFinishWithNoSceneChangeKeepsTheLease()
        {
            authority.BeginOperation(ticket, "op-1"); authority.BeginTransition(ticket);
            Assert.IsTrue(authority.FinishTransition(ticket, null));
            Assert.IsTrue(authority.LeaseHeld); Assert.AreEqual("editor:VAB", authority.DescribeLease(lease).Entity);
        }

        [TestMethod] public void AFinishAfterTheSceneMovedWithoutASuccessReleasesTheLease()
        {
            authority.BeginOperation(ticket, "op-1"); authority.BeginTransition(ticket);
            authority.UpdateContext(Ctx("epoch2", "scene:FLIGHT", false), Bind());
            Assert.IsFalse(authority.FinishTransition(ticket, null));
            Assert.IsFalse(authority.LeaseHeld);
        }

        [TestMethod] public void FinishingAnUnarmedTransitionDoesNothing()
        {
            Assert.IsFalse(authority.FinishTransition(ticket, null));
            Assert.IsTrue(authority.LeaseHeld);
        }

        [TestMethod] public void StopOrATakeoverEndsTheTransitionAndTheTicket()
        {
            authority.BeginOperation(ticket, "op-1"); authority.BeginTransition(ticket);
            Assert.IsTrue(authority.TicketCurrent(ticket));
            authority.HumanTakeover();
            Assert.IsFalse(authority.TicketCurrent(ticket));
            Arm();
            authority.BeginOperation(ticket, "op-2"); authority.BeginTransition(ticket);
            authority.Stop();
            Assert.IsFalse(authority.TicketCurrent(ticket));
            authority.UpdateContext(Ctx("epoch2", "scene:FLIGHT", false), Bind());
            Assert.IsFalse(authority.LeaseHeld);
        }

        [TestMethod] public void ATicketIsCurrentAcrossAnArmedSceneChange()
        {
            authority.BeginOperation(ticket, "op-1"); authority.BeginTransition(ticket);
            authority.UpdateContext(Ctx("epoch2", "scene:LOADING", false), Bind());
            Assert.IsTrue(authority.TicketCurrent(ticket), "ticket currency ignores the scene, which is expected to change");
            Assert.IsFalse(authority.TicketCurrent(null));
        }

        [TestMethod] public void TheSpendLimitTravelsFromTheSignedGrantToTheReportedStatus()
        {
            var payload = new GrantPayload
            {
                GrantId = "grant", Generation = 1, IssuedUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0), ExpiresUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0.AddHours(8)),
                Binding = new GrantBindingInfo { InstallId = "install", SaveFolder = "save", Agency = "agency" },
                Operations = new[] { "editor.launch" }, Facilities = new[] { "VAB" }, UnsavedCraftPolicy = "refuse", MaxParts = 100, SpendLimitFunds = 7500
            };
            Assert.IsNull(GrantCodec.Validate(payload));
            var status = GrantEvaluator.Evaluate(GrantVerification.Success(payload), AuthorityHelpers.Utc0.AddMinutes(1), payload.Binding, (id, g) => false, 0);
            Assert.AreEqual(GrantStates.Valid, status.State); Assert.AreEqual(7500L, status.SpendLimitFunds);
            Assert.AreEqual(7500L, (long)JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(status))["spendLimitFunds"]);
            authority.PublishGrantStatus(status);
            Assert.AreEqual(7500L, authority.Status().Grant.SpendLimitFunds);
        }

        [TestMethod] public void ALaunchFamilyGrantMapsToTheLaunchEffectOnTheFacility()
        {
            var payload = new GrantPayload
            {
                GrantId = "grant", Generation = 1, IssuedUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0), ExpiresUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0.AddHours(8)),
                Binding = new GrantBindingInfo { InstallId = "install", SaveFolder = "save", Agency = "agency" },
                Operations = new[] { "editor.launch", "flight.control" }, Facilities = new[] { "VAB" }, UnsavedCraftPolicy = "refuse", MaxParts = 100, SpendLimitFunds = 1
            };
            var grant = GrantMapping.ToGrant(payload);
            Assert.IsTrue(grant.Allows(new ClassifiedEffect("editor.launch", "editor:VAB", 0)));
            Assert.IsFalse(grant.Allows(new ClassifiedEffect("editor.launch", "editor:SPH", 0)));
            Assert.IsTrue(GrantMapping.KnownEffects.Contains("editor.launch"));
            Assert.IsFalse(grant.AllowsFlight, "flight operations are not classified yet, so a launch releases the lease until they are");
        }
    }
}
