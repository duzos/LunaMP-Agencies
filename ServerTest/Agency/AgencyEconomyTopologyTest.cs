using LmpCommon.Agency;
using LmpCommon.Message;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.System;
using System;
using System.IO;
using System.Linq;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class AgencyEconomyTopologyTest
    {
        private static string Proto(Guid id, params uint[] ids) => "pid = " + id.ToString("N") + "\nname = Fixture\nroot = 0\n" + string.Concat(ids.Select(uid => "PART\n{\nname = probe\nuid = " + uid + "\n}\n")) + string.Concat(new[] { "ORBIT", "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }.Select(n => n + "\n{\n}\n"));

        internal static Guid Launch(AgencyEconomyTest.Fixture fixture, uint uid, out Guid launchId)
        {
            var manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } }, Cargo = new[] { new ToolingCargo { Name = "cargo", Count = 1, UnitCost = 25, ContainerPartIndex = 0 } } };
            launchId = Guid.NewGuid();
            var result = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = launchId, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
            Assert.IsTrue(result.Success, result.Reason);
            var id = Guid.NewGuid();
            var raw = Proto(id, uid);
            var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
            message.VesselId = id; message.EconomyLaunchId = launchId; message.EconomyLaunchToken = result.LaunchToken; message.EconomyManifestIndices = new[] { 0 };
            var registered = AgencyEconomyStore.Register(fixture.Client, message, raw, new Server.System.Vessel.Classes.Vessel(raw));
            Assert.IsTrue(registered.Success, registered.Reason);
            return id;
        }

        [TestMethod]
        public void CouplingCrashReplaysPaidPartsAndOwnershipThenSplitPartitionsContainers()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var dominant = Launch(fixture, 801, out _);
                var weak = Launch(fixture, 802, out _);
                var raw = Proto(dominant, 801, 802);
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "committed") throw new IOException("committed fault"); };
                Assert.ThrowsException<IOException>(() => AgencyVesselMap.CommitCouple(Guid.NewGuid(), fixture.Client.UniqueIdentifier, dominant, weak, raw, new Server.System.Vessel.Classes.Vessel(raw), 801, 802));
                Assert.IsFalse(AgencyEconomyStore.Ready);
                AgencyEconomyStore.PersistenceCheckpoint = null;
                AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.IsFalse(VesselStoreSystem.VesselExists(weak));
                Assert.IsTrue(AgencyVesselMap.IsAbsorbed(weak));
                Assert.AreEqual(2, fixture.Snapshot.Vessels.Single(v => v.VesselId == dominant).Parts.Length);
                var child = Guid.NewGuid();
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(dominant, child, 802, 802));
                Assert.IsFalse(AgencyVesselMap.ResolveSplit(child, new uint[] { 802 }, Proto(child, 802), Proto(dominant, 801, 802)), "Overlapping parent and child parts must not be published.");
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(child));
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "vessel-written") throw new IOException("split projection interrupted"); };
                Assert.ThrowsException<IOException>(() => AgencyVesselMap.ResolveSplit(child, new uint[] { 802 }, Proto(child, 802), Proto(dominant, 801)));
                Assert.IsFalse(AgencyEconomyStore.Ready);
                AgencyEconomyStore.PersistenceCheckpoint = null;
                AgencyEconomyStore.Load();
                CollectionAssert.AreEqual(new uint[] { 801 }, AgencyVesselMap.PartIds(VesselStoreSystem.CurrentVessels[dominant]));
                CollectionAssert.AreEqual(new uint[] { 802 }, AgencyVesselMap.PartIds(VesselStoreSystem.CurrentVessels[child]));
                var savedParent = new Server.System.Vessel.Classes.Vessel(File.ReadAllText(Path.Combine(VesselStoreSystem.VesselsPath, dominant + VesselStoreSystem.VesselFileFormat)));
                CollectionAssert.AreEqual(new uint[] { 801 }, AgencyVesselMap.PartIds(savedParent));
                Assert.IsTrue(File.Exists(Path.Combine(VesselStoreSystem.VesselsPath, child + VesselStoreSystem.VesselFileFormat)));
                var state = fixture.Snapshot;
                CollectionAssert.AreEqual(new uint[] { 801 }, state.Vessels.Single(v => v.VesselId == dominant).Parts.Select(p => p.FlightId).ToArray());
                Assert.AreEqual(802u, state.Vessels.Single(v => v.VesselId == child).Cargo.Single().ContainerFlightId);
                Assert.AreEqual(801u, state.Vessels.Single(v => v.VesselId == dominant).Cargo.Single().ContainerFlightId);
            }
        }

        [TestMethod]
        public void MissingRequesterCannotCancelOtherPlayersPendingSplits()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var firstParent = Launch(fixture, 1201, out _);
                var secondParent = Launch(fixture, 1202, out _);
                var firstChild = Guid.NewGuid();
                var secondChild = Guid.NewGuid();
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(firstParent, firstChild, 0, 1201, null, "first", 10));
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(secondParent, secondChild, 0, 1202, null, "second", 20));

                // A rejected handshake has an endpoint but no authenticated identity.
                var rejected = (Server.Client.ClientStructure)typeof(object)
                    .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(fixture.Client, null);
                var connection = (Lidgren.Network.NetConnection)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Lidgren.Network.NetConnection));
                typeof(Lidgren.Network.NetConnection).GetField("m_remoteEndPoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(connection, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 32499));
                typeof(Server.Client.ClientStructure).GetField("<Connection>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(rejected, connection);
                rejected.Authenticated = false;
                rejected.UniqueIdentifier = null;
                rejected.ConnectionStatus = LmpCommon.Enums.ConnectionStatus.Disconnected;
                Server.Context.ServerContext.Clients[rejected.Endpoint] = rejected;
                Server.Client.ClientConnectionHandler.DisconnectClient(rejected);
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(firstChild), "A rejected handshake must not cancel an authenticated player's split.");
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(secondChild));

                AgencyVesselMap.CancelPendingSplits(null, 10);
                AgencyVesselMap.CancelPendingSplits(string.Empty, 20);
                AgencyVesselMap.CancelPendingSplits(null, 10, firstChild);
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(firstChild));
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(secondChild));

                AgencyVesselMap.CancelPendingSplits("first", 11);
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(firstChild), "A stale connection must not cancel a different session.");
                AgencyVesselMap.CancelPendingSplits("first", 10);
                Assert.IsFalse(AgencyVesselMap.IsPendingSplit(firstChild));
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(secondChild), "Disconnect cleanup must preserve the other requester.");

                AgencyVesselMap.ClearPendingSplits();
                Assert.IsFalse(AgencyVesselMap.IsPendingSplit(secondChild));
                Assert.IsTrue(VesselStoreSystem.VesselExists(firstParent));
                Assert.IsTrue(VesselStoreSystem.VesselExists(secondParent));
            }
        }

        [TestMethod]
        public void AbandonedSplitCleanupIsSessionBoundAndStartupRestoresUsableParent()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Launch(fixture, 1101, out _);
                var child = Guid.NewGuid();
                var actor = fixture.Client.UniqueIdentifier;
                var ticks = fixture.Client.ConnectionTime.Ticks;
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(parent, child, 0, 1101, null, actor, ticks));
                Assert.IsTrue(AgencyVesselMap.IsSplitParent(parent));
                AgencyVesselMap.CancelPendingSplits(actor, ticks + 1);
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(child), "A different connection cannot cancel this operation.");
                AgencyVesselMap.CancelPendingSplits(actor, ticks);
                Assert.IsFalse(AgencyVesselMap.IsSplitParent(parent));
                Assert.IsTrue(AgencyVesselMap.CanApplyEpoch(parent, AgencyVesselMap.CaptureEpoch()));
                Assert.IsFalse(VesselStoreSystem.VesselExists(child));
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(parent, child, 0, 1101, null, actor, ticks));
                AgencyVesselMap.Load();
                AgencyEconomyStore.Load();
                AgencyVesselMap.ClearPendingSplits();
                Assert.IsFalse(AgencyVesselMap.IsSplitParent(parent));
                Assert.IsTrue(AgencyVesselMap.CanApplyEpoch(parent, AgencyVesselMap.CaptureEpoch()));
                Assert.AreEqual(1101u, fixture.Snapshot.Vessels.Single(v => v.VesselId == parent).Parts.Single().FlightId);
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void ConsecutiveCapturedSplitsPreserveThreeIndependentCraftAndPayment()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Launch(fixture, 1001, out _);
                var weakA = Launch(fixture, 1002, out _);
                var weakB = Launch(fixture, 1003, out _);
                var firstMerge = Proto(parent, 1001, 1002);
                AgencyVesselMap.CommitCouple(Guid.NewGuid(), fixture.Client.UniqueIdentifier, parent, weakA, firstMerge, new Server.System.Vessel.Classes.Vessel(firstMerge), 1001, 1002);
                var secondMerge = Proto(parent, 1001, 1002, 1003);
                AgencyVesselMap.CommitCouple(Guid.NewGuid(), fixture.Client.UniqueIdentifier, parent, weakB, secondMerge, new Server.System.Vessel.Classes.Vessel(secondMerge), 1001, 1003);
                var childA = Guid.NewGuid();
                var childB = Guid.NewGuid();
                // These pairs represent immutable captures in physical stage callback order.
                var firstParent = Proto(parent, 1001, 1003);
                var secondParent = Proto(parent, 1001);
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(parent, childA, 1002, 1002));
                Assert.IsTrue(AgencyVesselMap.ResolveSplit(childA, new uint[] { 1002 }, Proto(childA, 1002), firstParent));
                CollectionAssert.AreEqual(new uint[] { 1001, 1003 }, AgencyVesselMap.PartIds(VesselStoreSystem.CurrentVessels[parent]));
                CollectionAssert.AreEqual(new uint[] { 1001, 1003 }, fixture.Snapshot.Vessels.Single(v => v.VesselId == parent).Parts.Select(p => p.FlightId).ToArray());
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(parent, childB, 1003, 1003));
                Assert.IsTrue(AgencyVesselMap.ResolveSplit(childB, new uint[] { 1003 }, Proto(childB, 1003), secondParent));
                var ids = new[] { parent, childA, childB };
                var expected = new uint[] { 1001, 1002, 1003 };
                for (var index = 0; index < ids.Length; index++)
                {
                    CollectionAssert.AreEqual(new[] { expected[index] }, AgencyVesselMap.PartIds(VesselStoreSystem.CurrentVessels[ids[index]]));
                    var paid = fixture.Snapshot.Vessels.Single(v => v.VesselId == ids[index]);
                    Assert.AreEqual(expected[index], paid.Parts.Single().FlightId);
                    Assert.AreEqual(expected[index], paid.Cargo.Single().ContainerFlightId);
                    Assert.AreEqual(fixture.Client.AgencyId, AgencyVesselMap.Get(ids[index]).OwnerAgencyId);
                }
                Assert.AreEqual(49625d, fixture.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void DestructionBetweenSplitsBurnsMissingPartsAndCargoAcrossJournalReplay()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Launch(fixture, 1201, out _);
                var merged = new System.Collections.Generic.List<uint> { 1201 };
                foreach (var uid in new uint[] { 1202, 1203, 1204 })
                {
                    var weak = Launch(fixture, uid, out _);
                    merged.Add(uid);
                    var raw = Proto(parent, merged.ToArray());
                    AgencyVesselMap.CommitCouple(Guid.NewGuid(), fixture.Client.UniqueIdentifier, parent, weak, raw, new Server.System.Vessel.Classes.Vessel(raw), 1201, uid);
                }
                var first = Guid.NewGuid();
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(parent, first, 1202, 1202));
                Assert.IsTrue(AgencyVesselMap.ResolveSplit(first, new uint[] { 1202 }, Proto(first, 1202), Proto(parent, 1201, 1203, 1204)));
                var second = Guid.NewGuid();
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(parent, second, 1203, 1203));
                var epoch = AgencyVesselMap.CaptureEpoch();
                foreach (var invalidParent in new[] { Proto(parent), Proto(parent, 1201, 1201), Proto(parent, 1201, 1203), Proto(parent, 9999), Proto(Guid.NewGuid(), 1201) })
                    Assert.IsFalse(AgencyVesselMap.ResolveSplit(second, new uint[] { 1203 }, Proto(second, 1203), invalidParent));
                foreach (var invalidChild in new[] { new uint[0], new uint[] { 1203, 1203 }, new uint[] { 1203, 9999 }, new uint[] { 1204 } })
                    Assert.IsFalse(AgencyVesselMap.ResolveSplit(second, invalidChild, Proto(second, invalidChild), Proto(parent, 1201)));
                Assert.AreEqual(epoch, AgencyVesselMap.CaptureEpoch());
                // 1204 was destroyed after the first capture. Interrupt the second split's
                // projection to verify the lost part and its cargo stay burned on replay.
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "vessel-written") throw new IOException("split projection interrupted"); };
                Assert.ThrowsException<IOException>(() => AgencyVesselMap.ResolveSplit(second, new uint[] { 1203 }, Proto(second, 1203), Proto(parent, 1201)));
                AgencyEconomyStore.PersistenceCheckpoint = null;
                AgencyEconomyStore.Load();
                AgencyVesselMap.Load();
                AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                var ids = new[] { parent, first, second };
                for (var i = 0; i < ids.Length; i++)
                {
                    var expected = (uint)(1201 + i);
                    CollectionAssert.AreEqual(new[] { expected }, AgencyVesselMap.PartIds(VesselStoreSystem.CurrentVessels[ids[i]]));
                    var paid = fixture.Snapshot.Vessels.Single(v => v.VesselId == ids[i]);
                    Assert.AreEqual(expected, paid.Parts.Single().FlightId);
                    Assert.AreEqual(expected, paid.Cargo.Single().ContainerFlightId);
                    Assert.IsFalse(AgencyEconomyStore.ValidatePublishedParts(ids[i], new uint[] { expected, 1204 }));
                }
                var ownership = Newtonsoft.Json.JsonConvert.DeserializeObject<OwnershipDocument>(File.ReadAllText(AgencyVesselMap.OwnershipFilePath));
                CollectionAssert.AreEqual(new uint[] { 1201 }, ownership.Constituents[parent].SelectMany(c => c.PartUids).ToArray());
                Assert.IsFalse(ownership.Constituents.Values.SelectMany(c => c).Any(c => c.PartUids.Contains(1204u)));
                fixture.RefreshSession();
                var recovery = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.Recover, VesselId = parent, VesselData = System.Text.Encoding.UTF8.GetBytes(Proto(parent, 1201, 1204)) });
                Assert.IsFalse(recovery.Success);
                StringAssert.Contains(recovery.Reason, "unpaid parts");
                Assert.AreEqual(49500d, fixture.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void EvaBoardingMovesCrewCargoOnceWithoutChargingOrLosingPayment()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Launch(fixture, 980, out _);
                var parentRaw = Proto(parent, 980).Replace("name = probe", "name = probe\ncrew = Bob");
                VesselStoreSystem.CurrentVessels[parent] = new Server.System.Vessel.Classes.Vessel(parentRaw);
                Assert.IsTrue(AgencyEconomyStore.UpdateCargoBindings(parent, new uint[] { 980 }, new[] { new ToolingCargo { Name = "cargo", Count = 1, ContainerFlightId = 980, CrewName = "Bob" } }));
                var eva = Guid.NewGuid();
                var evaRaw = "type = EVA\n" + Proto(eva, 981).Replace("name = probe", "name = kerbalEVA\ncrew = Bob");
                var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
                message.VesselId = eva; message.EconomyParentVesselId = parent; message.EconomyEvaCrew = "Bob";
                var registered = AgencyEconomyStore.RegisterEva(fixture.Client, message, evaRaw, new Server.System.Vessel.Classes.Vessel(evaRaw));
                Assert.IsTrue(registered.Success, registered.Reason);
                Assert.AreEqual(0, fixture.Snapshot.Vessels.Single(v => v.VesselId == parent).Cargo.Length);
                Assert.AreEqual(981u, fixture.Snapshot.Vessels.Single(v => v.VesselId == eva).Cargo.Single().ContainerFlightId);
                var boarding = new EconomyCommand { Operation = EconomyOperation.BoardEva, VesselId = eva, ParentVesselId = parent, CrewName = "Bob", VesselData = System.Text.Encoding.UTF8.GetBytes(parentRaw) };
                var result = fixture.Execute(boarding);
                Assert.IsTrue(result.Success, result.Reason);
                Assert.IsTrue(fixture.Execute(boarding).Success);
                Assert.IsFalse(VesselStoreSystem.VesselExists(eva));
                Assert.AreEqual(980u, fixture.Snapshot.Vessels.Single(v => v.VesselId == parent).Cargo.Single().ContainerFlightId);
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
            }
        }

        [TestMethod]
        public async System.Threading.Tasks.Task LateRawScenarioPreservesEconomyAndUnrelatedStateAsync()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                Assert.IsTrue(fixture.Execute(new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = -10, ScienceDelta = -5 }).Success);
                await AgencyScenarioUpdater.RawConfigNodeInsertOrUpdate(fixture.Client.AgencyId, "Funding", "{\nname = Funding\nfunds = 1\n}\n");
                await AgencyScenarioUpdater.RawConfigNodeInsertOrUpdate(fixture.Client.AgencyId, "ResearchAndDevelopment", "{\nname = ResearchAndDevelopment\nsci = 1\nTech\n{\nid = testTech\n}\n}\n");
                Assert.AreEqual("49990", AgencyScenarioStore.GetOrNull(fixture.Client.AgencyId, "Funding").GetValue("funds").Value);
                var research = AgencyScenarioStore.GetOrNull(fixture.Client.AgencyId, "ResearchAndDevelopment");
                Assert.AreEqual("95", research.GetValue("sci").Value);
                Assert.AreEqual("testTech", research.GetNodes("Tech").Single().Value.GetValue("id").Value);
            }
        }

        [TestMethod]
        public void RevertLaunchBlocksOldConnectionPublicationAndRestoresOriginalState()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = Launch(fixture, 950, out var launch);
                var beforeEpoch = AgencyVesselMap.CaptureEpoch();
                var result = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.RevertLaunch, LaunchId = launch });
                Assert.IsTrue(result.Success, result.Reason);
                Assert.IsFalse(AgencyEconomyStore.MayPublish(fixture.Client));
                Assert.IsFalse(AgencyVesselMap.CanApplyEpoch(id, beforeEpoch), "Previously queued updates cannot overwrite the reverted proto.");
                Assert.IsTrue(VesselStoreSystem.VesselExists(id));
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void TransferAwayAndBackPermanentlyInvalidatesLaunchRevert()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = Launch(fixture, 901, out var launch);
                var original = fixture.Client.AgencyId;
                AgencyVesselMap.Set(id, Guid.NewGuid());
                AgencyVesselMap.Set(id, original);
                var result = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = launch });
                Assert.IsFalse(result.Success);
                Assert.IsTrue(VesselStoreSystem.VesselExists(id));
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
            }
        }

        [DataTestMethod]
        [DataRow(EconomyOperation.Revert, false)]
        [DataRow(EconomyOperation.RevertLaunch, false)]
        [DataRow(EconomyOperation.Revert, true)]
        [DataRow(EconomyOperation.RevertLaunch, true)]
        public void RevertPermissionGatesBothSettlementPaths(EconomyOperation operation, bool allowed)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = Launch(fixture, 975, out var launch);
                var before = fixture.Snapshot;
                var ownership = AgencyVesselMap.CaptureEpoch();
                Server.Settings.Structures.GameplaySettings.SettingsStore.CanRevert = allowed;
                var result = fixture.Execute(new EconomyCommand { Operation = operation, LaunchId = launch });
                Assert.AreEqual(allowed, result.Success, result.Reason);
                if (!allowed)
                {
                    StringAssert.Contains(result.Reason, "disabled by the server");
                    Assert.IsFalse(result.RecoveryRequired);
                    Assert.AreEqual(before.Revision, fixture.Snapshot.Revision);
                    Assert.AreEqual(before.Funds, fixture.Snapshot.Funds);
                    Assert.AreEqual(ownership, AgencyVesselMap.CaptureEpoch());
                    Assert.IsTrue(VesselStoreSystem.VesselExists(id));
                    Assert.IsTrue(fixture.Snapshot.Vessels.Any(v => v.VesselId == id));
                    Assert.IsTrue(AgencyEconomyStore.MayPublish(fixture.Client));
                    // The refusal did not consume or settle the launch: changing the policy
                    // allows this same request to execute normally.
                    Server.Settings.Structures.GameplaySettings.SettingsStore.CanRevert = true;
                    var retry = fixture.Execute(new EconomyCommand { Operation = operation, LaunchId = launch });
                    Assert.IsTrue(retry.Success, retry.Reason);
                }
                Assert.IsFalse(AgencyEconomyStore.MayPublish(fixture.Client));
                Assert.AreEqual(operation == EconomyOperation.Revert ? 50000d : before.Funds, fixture.Snapshot.Funds);
                Assert.AreEqual(operation == EconomyOperation.RevertLaunch, VesselStoreSystem.VesselExists(id));
            }
        }
    }
}
