using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using LmpCommon.Agency;
using LmpCommon.Locks;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using Server.System;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class CraftDeletionTest
    {
        private static int _port = 32700;

        private static AgencyVesselOwnershipResultMsgData Delete(ClientStructure client, Guid id)
        {
            var request = new ClientMessageFactory().CreateNewMessageData<AgencyVesselOwnershipCommandMsgData>();
            request.RequestId = Guid.NewGuid(); request.VesselId = id; request.Operation = VesselOwnershipOperation.Delete;
            VesselOwnershipSystem.Command(client, request);
            return client.SendMessageQueue.Select(m => m.Data).OfType<AgencyVesselOwnershipResultMsgData>().Last();
        }

        private static AgencyVesselOwnershipResultMsgData Delete(AgencyEconomyTest.Fixture fixture, Guid id) => Delete(fixture.Client, id);

        private static ClientStructure Player(AgencyEconomyTest.Fixture fixture, string uniqueId, Guid agencyId, bool agencyOwner)
        {
            var player = (ClientStructure)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(fixture.Client, null);
            typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, new ConcurrentQueue<IServerMessageBase>());
            player.UniqueIdentifier = uniqueId; player.PlayerName = "Player" + uniqueId; player.AgencyId = agencyId;
            var endpoint = new IPEndPoint(IPAddress.Loopback, ++_port);
            EvaRegistrationTest.OwnConnection(player, endpoint);
            ServerContext.Clients[endpoint] = player;
            if (!AgencyStore.Agencies.TryGetValue(agencyId, out var agency))
                AgencyStore.Agencies[agencyId] = agency = new Server.Agency.Agency { Id = agencyId, Name = "Agency" + uniqueId, OwnerUniqueId = agencyOwner ? uniqueId : "nobody", Funds = 1000, Science = 10 };
            if (!agency.HasMember(uniqueId)) agency.Members.Add(new Server.Agency.Agency.Member { UniqueId = uniqueId, DisplayName = player.PlayerName });
            return player;
        }

        [TestMethod]
        public void OwnerCanDeleteOrphanButMemberAndForeignOwnerCannot()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                Directory.CreateDirectory(VesselStoreSystem.VesselsPath);
                var id = Guid.NewGuid(); AgencyVesselMap.Set(id, fixture.Client.AgencyId);
                var agency = AgencyStore.Agencies[fixture.Client.AgencyId];
                agency.OwnerUniqueId = "another-member";
                Assert.IsFalse(Delete(fixture, id).Success);
                agency.OwnerUniqueId = fixture.Client.UniqueIdentifier;
                var foreign = AgencySystem.CreateAgency("Foreign", "foreign", "Foreign").Agency.Id;
                AgencyVesselMap.Set(id, foreign);
                Assert.IsFalse(Delete(fixture, id).Success);
                AgencyVesselMap.Set(id, fixture.Client.AgencyId);
                var deleted = Delete(fixture, id); Assert.IsTrue(deleted.Success, deleted.Reason);
                Assert.IsNull(AgencyVesselMap.Get(id));
                AgencyVesselMap.Load(); AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyVesselMap.IsDeleted(id));
                Assert.IsFalse(AgencyVesselMap.Mutate(id, fixture.Client.AgencyId, true, VesselOwnershipOperation.Claim, Guid.Empty, VesselDockingPolicy.Nobody).Success);
                Assert.ThrowsException<InvalidOperationException>(() => AgencyVesselMap.RegisterNew(id, fixture.Client.AgencyId));
                Assert.IsFalse(AgencyVesselMap.CanApplyEpoch(id, AgencyVesselMap.CaptureEpoch()));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PaidDeletionSurvivesInterruptedProjectionWithoutRefundOrRevertOrReupload(bool interrupted)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var id = AgencyEconomyTopologyTest.Launch(fixture, 1401, out var launch);
                var raw = VesselStoreSystem.CurrentVessels[id].ToString();
                if (interrupted) AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "committed") throw new IOException("interrupted"); };
                Assert.AreEqual(!interrupted, Delete(fixture, id).Success);
                AgencyEconomyStore.PersistenceCheckpoint = null;
                AgencyEconomyStore.Load(); AgencyVesselMap.Load(); AgencyEconomyStore.Load(); fixture.RefreshSession();
                Assert.IsTrue(AgencyVesselMap.IsDeleted(id));
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                Assert.IsFalse(File.Exists(VesselRemovalTest.VesselFile(id)));
                Assert.IsFalse(fixture.Snapshot.Vessels.Any(v => v.VesselId == id));
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
                Assert.IsFalse(fixture.Execute(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = launch }).Success);
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
                // The deleted craft cannot be uploaded again, even across the restart above.
                EvaRegistrationTest.SendProto(fixture.Client, id, raw);
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                Assert.IsNull(AgencyVesselMap.Get(id));
                Assert.IsTrue(VesselContext.RemovedVessels.ContainsKey(id));
            }
        }

        [TestMethod]
        public void OwnershipOnlyDeletionReplaysFileRemovalAfterFailure()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = AgencyEconomyTopologyTest.Launch(fixture, 1402, out _);
                GeneralSettings.SettingsStore.AgencyTooling = false;
                var trade = GeneralSettings.SettingsStore.AgencyTrade;
                GeneralSettings.SettingsStore.AgencyTrade = false;
                try
                {
                    AgencyVesselMap.PersistenceCheckpoint = point => { if (point == "before-deleted-vessel-projection") throw new IOException("interrupted"); };
                    Assert.IsFalse(AgencyVesselMap.DeleteCraft(id).Success);
                    Assert.IsFalse(AgencyVesselMap.Ready);
                    Assert.IsTrue(VesselContext.RemovedVessels.ContainsKey(id), "A committed deletion keeps its kill-list entry while recovery is pending.");
                    AgencyVesselMap.PersistenceCheckpoint = null;
                    AgencyVesselMap.Load();
                    Assert.IsTrue(AgencyVesselMap.Ready);
                    Assert.IsTrue(AgencyVesselMap.IsDeleted(id));
                    Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                    Assert.IsFalse(File.Exists(VesselRemovalTest.VesselFile(id)));
                }
                finally { AgencyVesselMap.PersistenceCheckpoint = null; GeneralSettings.SettingsStore.AgencyTrade = trade; }
            }
        }

        [TestMethod]
        public void DeletionIsAuthorizedOnlyForTheOwningAgencysOwnerOfAnIdleWholeCraft()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var owner = fixture.Client;
                var member = Player(fixture, "member", owner.AgencyId, false);
                var rivalAgency = Guid.NewGuid();
                var rival = Player(fixture, "rival", rivalAgency, true);
                var id = AgencyEconomyTopologyTest.Launch(fixture, 1501, out _);
                var epoch = AgencyVesselMap.CaptureEpoch();
                Action<string> denied = why =>
                {
                    var result = Delete(owner, id);
                    Assert.IsFalse(result.Success, why);
                    Assert.IsFalse(AgencyVesselMap.IsDeleted(id), why);
                    Assert.IsTrue(VesselStoreSystem.VesselExists(id), why);
                    Assert.AreEqual(epoch, AgencyVesselMap.CaptureEpoch(), why);
                };

                Assert.IsFalse(Delete(member, id).Success, "A member who is not the agency owner is denied.");
                Assert.IsFalse(Delete(rival, id).Success, "A foreign agency is denied.");
                Assert.IsTrue(AgencyVesselMap.Mutate(id, owner.AgencyId, true, VesselOwnershipOperation.AddCoOwner, rivalAgency, VesselDockingPolicy.Nobody).Success);
                epoch = AgencyVesselMap.CaptureEpoch();
                Assert.IsFalse(Delete(rival, id).Success, "A co-owner is denied.");
                Assert.IsFalse(AgencyVesselMap.IsDeleted(id));

                // Foreign parts docked into the craft.
                Doc.Constituents[id] = new List<VesselConstituent> { new VesselConstituent { OriginalVesselId = id, PartUids = new uint[] { 1501 }, Ownership = new VesselOwnershipRecord { VesselId = id, OwnerAgencyId = rivalAgency } } };
                denied("foreign parts");
                StringAssert.Contains(Delete(owner, id).Reason, "Undock");
                Doc.Constituents.Remove(id);

                // Controlled craft.
                var control = new LockDefinition(LockType.Control, owner.PlayerName, id);
                Assert.IsTrue(LockSystem.AcquireLock(control, true, out _));
                try { denied("controlled craft"); }
                finally { LockSystem.ReleaseLock(control); }

                // Crewed and EVA craft.
                var raw = VesselStoreSystem.CurrentVessels[id].ToString();
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(raw.Replace("name = probe", "name = probe\ncrew = Jeb"));
                denied("crewed craft");
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel("type = EVA\n" + raw);
                denied("EVA craft");
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(raw);

                // A craft in the middle of a split, as parent or child.
                var child = Guid.NewGuid();
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(id, child, 0, 1501));
                epoch = AgencyVesselMap.CaptureEpoch();
                denied("split parent");
                Assert.IsFalse(Delete(owner, child).Success, "A pending split child is denied.");
                Assert.IsFalse(AgencyVesselMap.IsDeleted(child));
                AgencyVesselMap.CancelPendingSplits();
                epoch = AgencyVesselMap.CaptureEpoch();

                // A craft that was docked away.
                var absorbed = AgencyEconomyTopologyTest.Launch(fixture, 1502, out _);
                var merged = VesselRemovalTest.Proto(id, 1501, 1502);
                AgencyVesselMap.CommitCouple(Guid.NewGuid(), owner.UniqueIdentifier, id, absorbed, merged, new Server.System.Vessel.Classes.Vessel(merged), 1501, 1502);
                epoch = AgencyVesselMap.CaptureEpoch();
                Assert.IsTrue(AgencyVesselMap.IsAbsorbed(absorbed));
                Assert.IsFalse(Delete(owner, absorbed).Success, "An absorbed craft is denied.");
                Assert.IsFalse(AgencyVesselMap.IsDeleted(absorbed));
                Assert.AreEqual(epoch, AgencyVesselMap.CaptureEpoch());

                // An orphaned record (no vessel file) of the owner's agency is allowed.
                var orphan = Guid.NewGuid(); AgencyVesselMap.Set(orphan, owner.AgencyId);
                var removed = Delete(owner, orphan);
                Assert.IsTrue(removed.Success, removed.Reason);
                Assert.IsTrue(AgencyVesselMap.IsDeleted(orphan));
                Assert.IsNull(AgencyVesselMap.Get(orphan));

                // Finally the owner can delete the whole craft.
                var whole = Delete(owner, id);
                Assert.IsTrue(whole.Success, whole.Reason);
                Assert.IsTrue(AgencyVesselMap.IsDeleted(id));
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
            }
        }

        private static OwnershipDocument Doc => VesselRemovalTest.Doc;

        [TestMethod]
        public void DeletionReleasesLocksAndTellsEveryoneIncludingTheRequester()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var watcher = Player(fixture, "watcher", Guid.NewGuid(), true);
                var id = AgencyEconomyTopologyTest.Launch(fixture, 1601, out _);
                var update = new LockDefinition(LockType.Update, "SomeoneElse", id);
                Assert.IsTrue(LockSystem.AcquireLock(update, true, out _));
                try
                {
                    while (fixture.Client.SendMessageQueue.TryDequeue(out _)) { }
                    var result = Delete(fixture, id);
                    Assert.IsTrue(result.Success, result.Reason);
                    Assert.IsFalse(LockSystem.LockQuery.LockExists(LockType.Update, id, null));
                    foreach (var client in new[] { fixture.Client, watcher })
                    {
                        var messages = client.SendMessageQueue.Select(m => m.Data).ToArray();
                        Assert.AreEqual(1, messages.OfType<LockReleaseMsgData>().Count(m => m.Lock.VesselId == id));
                        var removal = messages.OfType<VesselRemoveMsgData>().Single(m => m.VesselId == id);
                        Assert.IsTrue(removal.AddToKillList);
                    }
                }
                finally { LockSystem.ReleaseLock(update); }
            }
        }

        [TestMethod]
        public void DebrisCleanupRemovesOwnershipAndPaymentWithoutDeletingOrRefunding()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = AgencyEconomyTopologyTest.Launch(fixture, 1404, out _);
                VesselRemovalTest.Prefix(id, "type = Debris\n");
                new Server.Command.Command.DekesslerCommand().Execute("");
                Assert.IsNull(AgencyVesselMap.Get(id));
                Assert.IsFalse(AgencyVesselMap.IsDeleted(id), "A cleanup is an ordinary removal.");
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                Assert.IsFalse(fixture.Snapshot.Vessels.Any(v => v.VesselId == id));
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void CrewedAndSplittingCraftCannotBeDeletedDirectly()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = AgencyEconomyTopologyTest.Launch(fixture, 1403, out _);
                var raw = VesselStoreSystem.CurrentVessels[id].ToString();
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(raw.Replace("name = probe", "name = probe\ncrew = Jeb"));
                Assert.IsFalse(AgencyVesselMap.DeleteCraft(id).Success);
                VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(raw);
                var child = Guid.NewGuid(); Assert.IsTrue(AgencyVesselMap.RestoreSplit(id, child, 0, 1403));
                Assert.IsFalse(AgencyVesselMap.DeleteCraft(id).Success);
                Assert.IsFalse(AgencyVesselMap.DeleteCraft(child).Success);
                Assert.IsFalse(AgencyVesselMap.IsDeleted(id));
            }
        }
    }
}
