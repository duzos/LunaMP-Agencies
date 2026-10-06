using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace LmpCommonTest
{
    [TestClass]
    public class VesselOwnershipPolicyTest
    {
        [TestMethod]
        public void OnlyOwnerAndCoOwnerCanControlOwnedCraft()
        {
            var owner = Guid.NewGuid(); var other = Guid.NewGuid();
            var record = new VesselOwnershipRecord { OwnerAgencyId = owner, CoOwnerAgencyIds = new[] { other } };
            Assert.IsTrue(VesselOwnershipPolicy.CanControl(record, owner));
            Assert.IsTrue(VesselOwnershipPolicy.CanControl(record, other));
            Assert.IsFalse(VesselOwnershipPolicy.CanControl(record, Guid.NewGuid()));
            Assert.IsFalse(VesselOwnershipPolicy.CanManage(record, other, true));
            Assert.IsFalse(VesselOwnershipPolicy.CanManage(record, owner, false));
            Assert.IsTrue(VesselOwnershipPolicy.CanManage(record, owner, true));
            Assert.IsTrue(VesselOwnershipPolicy.CanControl(null, other));
        }

        [TestMethod]
        public void OnlineForeignOwnerRequiresConsentEvenForCoOwner()
        {
            var actor = Guid.NewGuid();
            var record = new VesselOwnershipRecord { OwnerAgencyId = Guid.NewGuid(), CoOwnerAgencyIds = new[] { actor }, DockingPolicy = VesselDockingPolicy.CoOwners };
            Assert.AreEqual(DockingDecision.RequestConsent, VesselOwnershipPolicy.EvaluateDock(record, actor, true));
            Assert.AreEqual(DockingDecision.Allow, VesselOwnershipPolicy.EvaluateDock(record, actor, false));
            Assert.AreEqual(DockingDecision.Deny, VesselOwnershipPolicy.EvaluateDock(record, Guid.NewGuid(), false));
            record.DockingPolicy = VesselDockingPolicy.Anyone;
            Assert.AreEqual(DockingDecision.Allow, VesselOwnershipPolicy.EvaluateDock(record, Guid.NewGuid(), false));
            record.DockingPolicy = VesselDockingPolicy.Nobody;
            Assert.AreEqual(DockingDecision.Deny, VesselOwnershipPolicy.EvaluateDock(record, actor, false));
            Assert.AreEqual(DockingDecision.Allow, VesselOwnershipPolicy.EvaluateDock(null, actor, false));
        }
    }
}
