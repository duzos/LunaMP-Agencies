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

        private static Guid Launch(AgencyEconomyTest.Fixture fixture, uint uid, out Guid launchId)
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
                AgencyVesselMap.CancelPendingSplits();
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
    }
}
