using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Context;
using System;
using System.IO;

namespace ServerTest.Agency
{
    [TestClass]
    public class AgencySystemTest
    {
        [TestInitialize]
        public void Setup()
        {
            ServerContext.UniverseDirectory = Path.Combine(Path.GetTempPath(), "LMPTestUniverse_" + Guid.NewGuid());
            Directory.CreateDirectory(ServerContext.UniverseDirectory);
            AgencyStore.AgenciesPath = Path.Combine(ServerContext.UniverseDirectory, "Agencies");
            Directory.CreateDirectory(AgencyStore.AgenciesPath);
            AgencyStore.Agencies.Clear();
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(ServerContext.UniverseDirectory))
                Directory.Delete(ServerContext.UniverseDirectory, true);
        }

        [TestMethod]
        public void CreateAgency_SetsOwnerAndFirstMember()
        {
            var (ok, _, a) = AgencySystem.CreateAgency("Kerbin Dynamics", "uid-1", "Jeb");
            Assert.IsTrue(ok);
            Assert.AreEqual("Kerbin Dynamics", a.Name);
            Assert.AreEqual("uid-1", a.OwnerUniqueId);
            Assert.IsTrue(a.HasMember("uid-1"));
        }

        [TestMethod]
        public void CreateAgency_RejectsDuplicateName()
        {
            AgencySystem.CreateAgency("Dupe", "uid-1", "A");
            var (ok, _, _) = AgencySystem.CreateAgency("Dupe", "uid-2", "B");
            Assert.IsFalse(ok);
        }

        [TestMethod]
        public void EnsureSoloAgency_IsIdempotent()
        {
            var first = AgencySystem.EnsureSoloAgency("uid-solo", "Bob");
            var second = AgencySystem.EnsureSoloAgency("uid-solo", "Bob");
            Assert.AreSame(first, second);
            Assert.IsTrue(first.IsSolo);
        }

        [TestMethod]
        public void PlayerInSingleAgency_CreatingAgencyLeavesOldOne()
        {
            AgencySystem.CreateAgency("Alpha", "uid-1", "Jeb");
            AgencySystem.CreateAgency("Beta", "uid-1", "Jeb");

            var alpha = AgencySystem.GetAgencyByName("Alpha");
            var beta = AgencySystem.GetAgencyByName("Beta");
            // Alpha should be auto-deleted (empty after owner leaves) or Jeb
            // removed from Alpha. Either way, Jeb must not be in Alpha.
            Assert.IsTrue(alpha == null || !alpha.HasMember("uid-1"),
                "Player must only belong to one agency at a time.");
            Assert.IsNotNull(beta);
            Assert.IsTrue(beta.HasMember("uid-1"));
        }

        [TestMethod]
        public void JoinRequest_ApproveAddsMemberAndClearsRequest()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Join Test", "owner-uid", "Owner");

            var post = AgencySystem.PostJoinRequest(a.Id, "joiner-uid", "Joiner");
            Assert.IsTrue(post.Success);
            Assert.AreEqual(1, a.PendingJoinRequests.Count);

            var approve = AgencySystem.ResolveJoinRequest(a.Id, "owner-uid", "joiner-uid", approve: true, isAdmin: false);
            Assert.IsTrue(approve.Success);
            Assert.IsTrue(a.HasMember("joiner-uid"));
            Assert.AreEqual(0, a.PendingJoinRequests.Count);
        }

        [TestMethod]
        public void JoinRequest_RejectLeavesStateIntact()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Reject Test", "owner-uid", "Owner");

            AgencySystem.PostJoinRequest(a.Id, "joiner-uid", "Joiner");
            var reject = AgencySystem.ResolveJoinRequest(a.Id, "owner-uid", "joiner-uid", approve: false, isAdmin: false);
            Assert.IsTrue(reject.Success);
            Assert.IsFalse(a.HasMember("joiner-uid"));
            Assert.AreEqual(0, a.PendingJoinRequests.Count);
        }

        [TestMethod]
        public void DuplicateJoinRequest_Rejected()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Dup Req", "owner-uid", "Owner");

            var first = AgencySystem.PostJoinRequest(a.Id, "joiner-uid", "Joiner");
            var second = AgencySystem.PostJoinRequest(a.Id, "joiner-uid", "Joiner");
            Assert.IsTrue(first.Success);
            Assert.IsFalse(second.Success);
        }

        [TestMethod]
        public void NonOwner_CannotApprove()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Auth Check", "owner-uid", "Owner");
            AgencySystem.PostJoinRequest(a.Id, "joiner-uid", "Joiner");
            var result = AgencySystem.ResolveJoinRequest(a.Id, "random-uid", "joiner-uid", approve: true, isAdmin: false);
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void OwnerTransferBeforeLeave_ThenLeaveSucceeds()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Ownerx", "owner-uid", "Owner");
            AgencySystem.PostJoinRequest(a.Id, "other-uid", "Other");
            AgencySystem.ResolveJoinRequest(a.Id, "owner-uid", "other-uid", approve: true, isAdmin: false);

            var blockedLeave = AgencySystem.LeaveAgency("owner-uid", "Owner");
            Assert.IsFalse(blockedLeave.Success, "Owner cannot leave while other members remain");

            var transfer = AgencySystem.TransferOwner(a.Id, "owner-uid", "other-uid", isAdmin: false);
            Assert.IsTrue(transfer.Success);

            var leave = AgencySystem.LeaveAgency("owner-uid", "Owner");
            Assert.IsTrue(leave.Success);
        }

        [TestMethod]
        public void TransferResources_Funds_MovesValuesAndRejectsOverdraft()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Alpha", "uid-1", "A");
            var (_, _, b) = AgencySystem.CreateAgency("Beta", "uid-2", "B");
            Server.Agency.AgencyScenarioStore.EnsureBaselineForAgency(a.Id, 500000, 0, 0);
            Server.Agency.AgencyScenarioStore.EnsureBaselineForAgency(b.Id, 100, 0, 0);
            AgencySystem.SetAgencyFunds(a, 500000, "seed");
            AgencySystem.SetAgencyFunds(b, 100, "seed");

            var ok = AgencySystem.TransferResources(a.Id, b.Id, LmpCommon.Agency.ResourceKind.Funds, 123456,
                actorUniqueId: "uid-1", isAdmin: false);
            Assert.IsTrue(ok.Success, ok.Message);
            Assert.AreEqual(500000 - 123456, a.Funds, 1e-6);
            Assert.AreEqual(100 + 123456, b.Funds, 1e-6);

            var overdraft = AgencySystem.TransferResources(a.Id, b.Id, LmpCommon.Agency.ResourceKind.Funds, 9_999_999_999,
                actorUniqueId: "uid-1", isAdmin: false);
            Assert.IsFalse(overdraft.Success, "Overdraft should be rejected");
        }

        [TestMethod]
        public void TransferResources_NonMember_Rejected()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Src", "member-uid", "M");
            var (_, _, b) = AgencySystem.CreateAgency("Dst", "other-uid", "O");
            Server.Agency.AgencyScenarioStore.EnsureBaselineForAgency(a.Id, 10000, 0, 0);
            Server.Agency.AgencyScenarioStore.EnsureBaselineForAgency(b.Id, 0, 0, 0);
            AgencySystem.SetAgencyFunds(a, 10000, "seed");

            var result = AgencySystem.TransferResources(a.Id, b.Id, LmpCommon.Agency.ResourceKind.Funds, 100,
                actorUniqueId: "attacker-uid", isAdmin: false);
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void AdminSetOwner_SwapsOwnerAndAutoJoinsNonMember()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Hostile Takeover", "orig-uid", "Orig");
            Assert.AreEqual("orig-uid", a.OwnerUniqueId);

            var result = AgencySystem.AdminSetOwner(a.Id, "new-uid");
            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual("new-uid", a.OwnerUniqueId);
            Assert.IsTrue(a.HasMember("new-uid"), "New owner auto-joins as a member");
            Assert.IsTrue(a.HasMember("orig-uid"), "Previous owner stays as a member");
        }

        [TestMethod]
        public void AdminMoveMember_MovesPlayerBetweenAgencies()
        {
            var (_, _, a) = AgencySystem.CreateAgency("Src", "src-uid", "S");
            AgencySystem.PostJoinRequest(a.Id, "player-uid", "P");
            AgencySystem.ResolveJoinRequest(a.Id, "src-uid", "player-uid", approve: true, isAdmin: false);

            var (_, _, b) = AgencySystem.CreateAgency("Dst", "dst-uid", "D");

            var move = AgencySystem.AdminMoveMember("player-uid", b.Id);
            Assert.IsTrue(move.Success);
            Assert.IsFalse(a.HasMember("player-uid") && !b.HasMember("player-uid"));
            Assert.IsTrue(b.HasMember("player-uid"));
        }
    }
}
