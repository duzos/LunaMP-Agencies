using LmpCommon.Agency;
using LmpCommon.Locks;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Client;
using Server.Command.Command;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Linq;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class SplitConfirmationTest
    {
        private static EconomyResult[] Results(ClientStructure client) => client.SendMessageQueue.Select(m => m.Data).OfType<AgencyEconomyResultMsgData>().Select(m => m.Result).ToArray();
        private static void Drain(ClientStructure client) { while (client.SendMessageQueue.TryDequeue(out _)) { } }
        // The part allow-list is not loaded in unit tests.
        private sealed class NoModControl : IDisposable
        {
            private readonly bool old = GeneralSettings.SettingsStore.ModControl;
            internal NoModControl() { GeneralSettings.SettingsStore.ModControl = false; }
            public void Dispose() { GeneralSettings.SettingsStore.ModControl = old; }
        }
        private static void Send(ClientStructure client, VesselBaseMsgData data) => new Server.Message.VesselMsgReader().HandleMessage(client, new ClientMessageFactory().CreateNew<VesselCliMsg>(data));

        private static void SendSplitChild(ClientStructure client, Guid child, uint part, Guid operation, Guid parent)
        {
            var proto = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
            var raw = "type = Debris\n" + VesselRemovalTest.Proto(child, part);
            proto.VesselId = child; proto.ForceReload = true; proto.Reason = "Confirmed local split";
            proto.Data = System.Text.Encoding.UTF8.GetBytes(raw); proto.NumBytes = proto.Data.Length;
            proto.EconomyManifestIndices = Array.Empty<int>(); proto.EconomySplitOperationId = operation;
            proto.EconomySplitParentData = System.Text.Encoding.UTF8.GetBytes(VesselRemovalTest.Proto(parent, 1));
            Send(client, proto);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RefusedDecoupleOrUndockFailsTheSplitOperationOnce(bool undock)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (new NoModControl())
            {
                var parent = AgencyEconomyTopologyTest.Launch(fixture, 6001, out _);
                var child = Guid.NewGuid(); var operation = Guid.NewGuid();
                Drain(fixture.Client);
                // The boundary part does not exist on the stored parent, so RestoreSplit refuses and no pending split exists.
                var factory = new ClientMessageFactory();
                if (undock) { var d = factory.CreateNewMessageData<VesselUndockMsgData>(); d.VesselId = parent; d.NewVesselId = child; d.PartFlightId = 999999; d.DockedInfoName = "x"; Send(fixture.Client, d); }
                else { var d = factory.CreateNewMessageData<VesselDecoupleMsgData>(); d.VesselId = parent; d.NewVesselId = child; d.PartFlightId = 999999; Send(fixture.Client, d); }
                Assert.IsFalse(AgencyVesselMap.IsPendingSplit(child));
                SendSplitChild(fixture.Client, child, 6002, operation, parent);
                var results = Results(fixture.Client);
                Assert.AreEqual(1, results.Length, "Exactly one result, and not a launch registration failure.");
                Assert.AreEqual(EconomyOperation.Split, results[0].Operation);
                Assert.AreEqual(operation, results[0].RequestId);
                Assert.IsFalse(results[0].Success);
                Assert.IsTrue(results[0].RecoveryRequired, "Every refused split makes the client reconnect with the real reason.");
                Assert.AreEqual(EconomyOperation.Split, results[0].Operation, "Not a launch registration failure.");
                Assert.IsFalse(VesselStoreSystem.VesselExists(child));
            }
        }

        [TestMethod]
        public void SplitOfADeletedParentFailsTheOperation()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (new NoModControl())
            {
                var parent = AgencyEconomyTopologyTest.Launch(fixture, 6101, out _);
                var child = Guid.NewGuid(); var operation = Guid.NewGuid();
                VesselStoreSystem.CurrentVessels.TryRemove(parent, out _);
                Drain(fixture.Client);
                var decouple = new ClientMessageFactory().CreateNewMessageData<VesselDecoupleMsgData>();
                decouple.VesselId = parent; decouple.NewVesselId = child; decouple.PartFlightId = 6101;
                Send(fixture.Client, decouple);
                SendSplitChild(fixture.Client, child, 6102, operation, parent);
                var result = Results(fixture.Client).Single();
                Assert.AreEqual(operation, result.RequestId);
                Assert.AreEqual(EconomyOperation.Split, result.Operation);
                Assert.IsFalse(result.Success);
                Assert.IsTrue(result.RecoveryRequired);
            }
        }

        private static void AssertRefusedWithOwnership(Action<AgencyEconomyTest.Fixture, Guid> arrange, uint uid)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (new NoModControl())
            {
                var oldOwnership = GeneralSettings.SettingsStore.AgencyVesselOwnership;
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                try
                {
                    var parent = AgencyEconomyTopologyTest.Launch(fixture, uid, out _);
                    arrange(fixture, parent);
                    Assert.IsFalse(VesselOwnershipSystem.CanControl(fixture.Client, parent), "Precondition: the sender cannot control the parent.");
                    var child = Guid.NewGuid(); var operation = Guid.NewGuid();
                    Drain(fixture.Client);
                    var decouple = new ClientMessageFactory().CreateNewMessageData<VesselDecoupleMsgData>();
                    decouple.VesselId = parent; decouple.NewVesselId = child; decouple.PartFlightId = uid;
                    Send(fixture.Client, decouple);
                    Assert.IsFalse(AgencyVesselMap.IsPendingSplit(child));
                    SendSplitChild(fixture.Client, child, uid + 1, operation, parent);
                    var result = Results(fixture.Client).Single();
                    Assert.AreEqual(operation, result.RequestId);
                    Assert.AreEqual(EconomyOperation.Split, result.Operation);
                    Assert.IsFalse(result.Success);
                    Assert.IsTrue(result.RecoveryRequired);
                }
                finally { GeneralSettings.SettingsStore.AgencyVesselOwnership = oldOwnership; }
            }
        }

        [TestMethod]
        public void SplitOfAForeignAgenciesParentFailsTheOperation() => AssertRefusedWithOwnership((fixture, parent) =>
        {
            var other = new Server.Agency.Agency { Id = Guid.NewGuid(), Name = "Other", OwnerUniqueId = "other-owner" };
            other.Members.Add(new Server.Agency.Agency.Member { UniqueId = "other-owner", DisplayName = "Other" });
            AgencyStore.Agencies[other.Id] = other;
            try { AgencyVesselMap.Set(parent, other.Id); }
            catch { AgencyStore.Agencies.TryRemove(other.Id, out _); throw; }
        }, 6301);

        [TestMethod]
        public void SplitBySenderWhoIsNoLongerAnAgencyMemberFailsTheOperation()
        {
            Server.Agency.Agency agency = null; System.Collections.Generic.List<Server.Agency.Agency.Member> members = null;
            try
            {
                AssertRefusedWithOwnership((fixture, parent) =>
                {
                    agency = AgencyStore.Agencies[fixture.Client.AgencyId];
                    members = agency.Members.ToList(); agency.Members.Clear();
                }, 6311);
            }
            finally { if (agency != null) agency.Members.AddRange(members); }
        }

        [TestMethod]
        public void ExceptionWhileResolvingASplitStillYieldsExactlyOneFailedResult()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (new NoModControl())
            {
                var parent = AgencyEconomyTopologyTest.Launch(fixture, 6401, out _);
                var child = Guid.NewGuid(); var operation = Guid.NewGuid();
                var decouple = new ClientMessageFactory().CreateNewMessageData<VesselDecoupleMsgData>();
                decouple.VesselId = parent; decouple.NewVesselId = child; decouple.PartFlightId = 6401;
                Send(fixture.Client, decouple);
                Assert.IsTrue(AgencyVesselMap.IsPendingSplit(child), "Precondition: the announcement was accepted.");
                Drain(fixture.Client);
                var old = AgencyVesselMap.PersistenceCheckpoint;
                AgencyVesselMap.PersistenceCheckpoint = _ => throw new InvalidOperationException("injected");
                try
                {
                    try { SendSplitChild(fixture.Client, child, 6402, operation, parent); } catch (InvalidOperationException) { }
                }
                finally { AgencyVesselMap.PersistenceCheckpoint = old; }
                var result = Results(fixture.Client).Single();
                Assert.AreEqual(operation, result.RequestId);
                Assert.AreEqual(EconomyOperation.Split, result.Operation);
                Assert.IsFalse(result.Success);
                Assert.IsTrue(result.RecoveryRequired);
            }
        }

        [TestMethod]
        public void DekesslerSkipsSplitAndLockedDebrisButRemovesTheRest()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var splitParent = AgencyEconomyTopologyTest.Launch(fixture, 6201, out _);
                var updated = AgencyEconomyTopologyTest.Launch(fixture, 6202, out _);
                var unrelated = AgencyEconomyTopologyTest.Launch(fixture, 6203, out _);
                foreach (var id in new[] { splitParent, updated, unrelated }) VesselRemovalTest.Prefix(id, "type = Debris\n");
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(splitParent, Guid.NewGuid(), 0, 6201));
                var update = new LockDefinition(LockType.Update, "SomePilot", updated);
                Assert.IsTrue(LockSystem.AcquireLock(update, true, out _));
                try
                {
                    new DekesslerCommand().Execute("");
                    Assert.IsTrue(VesselStoreSystem.VesselExists(splitParent), "A pending-split parent stays.");
                    Assert.IsTrue(VesselStoreSystem.VesselExists(updated), "Debris a player holds the Update lock on stays.");
                    Assert.IsFalse(VesselStoreSystem.VesselExists(unrelated), "Unrelated debris is still removed.");
                }
                finally { LockSystem.ReleaseLock(update); }
                new DekesslerCommand().Execute("");
                Assert.IsTrue(VesselStoreSystem.VesselExists(splitParent));
                Assert.IsFalse(VesselStoreSystem.VesselExists(updated), "Released debris is cleaned on the next pass.");
            }
        }
    }
}
