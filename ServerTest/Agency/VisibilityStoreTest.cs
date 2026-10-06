using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
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
    public class VisibilityStoreTest
    {
        private sealed class Fixture : IDisposable
        {
            private readonly AgencyTestScope scope = new AgencyTestScope();
            private readonly System.Collections.Generic.KeyValuePair<IPEndPoint, ClientStructure>[] clients = ServerContext.Clients.ToArray();
            private readonly System.Collections.Generic.KeyValuePair<Guid, Server.System.Vessel.Classes.Vessel>[] vessels = VesselStoreSystem.CurrentVessels.ToArray();
            public readonly ClientStructure Owner, Other;
            public readonly Guid Vessel = Guid.NewGuid();
            public Fixture()
            {
                ServerContext.Clients.Clear(); VesselStoreSystem.CurrentVessels.Clear();
                GeneralSettings.SettingsStore.AgencyHideCraft = true;
                Owner = AddClient(32131); Other = AddClient(32132);
                VesselStoreSystem.CurrentVessels[Vessel] = (Server.System.Vessel.Classes.Vessel)RuntimeHelpers.GetUninitializedObject(typeof(Server.System.Vessel.Classes.Vessel));
                AgencyVesselMap.Set(Vessel, Owner.AgencyId); AgencyVisibilityStore.Load();
            }
            private static ClientStructure AddClient(int port)
            {
                var connection = (NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));
                typeof(NetConnection).GetField("m_remoteEndPoint", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(connection, new IPEndPoint(IPAddress.Loopback, port));
                var client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
                typeof(ClientStructure).GetField("<Connection>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(client, connection);
                typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(client, new ConcurrentQueue<IServerMessageBase>());
                client.AgencyId = Guid.NewGuid(); client.UniqueIdentifier = "owner" + port; client.PlayerName = client.UniqueIdentifier; client.Authenticated = true; client.ConnectionStatus = ConnectionStatus.Connected;
                ServerContext.Clients[client.Endpoint] = client;
                var agency = new Server.Agency.Agency { Id = client.AgencyId, OwnerUniqueId = client.UniqueIdentifier };
                agency.Members.Add(new Server.Agency.Agency.Member { UniqueId = client.UniqueIdentifier, DisplayName = client.PlayerName });
                AgencyStore.Agencies[agency.Id] = agency;
                return client;
            }
            public AgencyVisibilityCommandMsgData Command(VisibilityOperation operation, VisibilityOverride rule = VisibilityOverride.Inherit, bool enabled = true)
            {
                var command = new ClientMessageFactory().CreateNewMessageData<AgencyVisibilityCommandMsgData>();
                command.RequestId = Guid.NewGuid(); command.VesselId = Vessel; command.TargetAgencyId = Other.AgencyId;
                command.Operation = operation; command.Rule = rule; command.Enabled = enabled;
                command.ExpectedOwnershipRevision = AgencyVesselMap.Get(Vessel).Revision;
                return command;
            }
            public void Dispose()
            {
                ServerContext.Clients.Clear(); foreach (var pair in clients) ServerContext.Clients[pair.Key] = pair.Value;
                VesselStoreSystem.CurrentVessels.Clear(); foreach (var pair in vessels) VesselStoreSystem.CurrentVessels[pair.Key] = pair.Value;
                scope.Dispose();
            }
        }

        [TestMethod]
        public void F3OffGrantsAndOverridesSurviveReloadAndOwnerReturnCannotReviveOldRule()
        {
            using (var fixture = new Fixture())
            {
                Assert.IsFalse(GeneralSettings.SettingsStore.AgencyVesselOwnership);
                Assert.IsTrue(AgencyVisibilityStore.Mutate(fixture.Owner, fixture.Command(VisibilityOperation.SetAgencyShare)).Success);
                var rule = fixture.Command(VisibilityOperation.SetCraftOverride, VisibilityOverride.Deny);
                Assert.IsTrue(AgencyVisibilityStore.Mutate(fixture.Owner, rule).Success);
                AgencyVisibilityStore.Load();
                var snapshot = AgencyVisibilityStore.GetSnapshot();
                Assert.IsTrue(snapshot.Ready); Assert.AreEqual(1, snapshot.AgencyGrants.Length); Assert.AreEqual(VisibilityOverride.Deny, snapshot.CraftOverrides.Single().Rule);
                AgencyVisibilityStore.SendTo(fixture.Other);
                var reconnect = fixture.Other.SendMessageQueue.Select(m => m.Data).OfType<AgencyVisibilitySnapshotMsgData>().Last();
                Assert.AreEqual(snapshot.Revision, reconnect.Revision); Assert.AreEqual(fixture.Vessel, reconnect.Endpoints.Single().VesselId);
                AgencyVesselMap.Set(fixture.Vessel, fixture.Other.AgencyId); AgencyVesselMap.Set(fixture.Vessel, fixture.Owner.AgencyId);
                AgencyVisibilityStore.Load();
                Assert.AreEqual(0, AgencyVisibilityStore.GetSnapshot().CraftOverrides.Length);
                Assert.IsFalse(AgencyVisibilityStore.Mutate(fixture.Owner, rule).Success, "A stale queued edit cannot revive an old title stamp.");
                AgencyStore.Agencies.TryRemove(fixture.Other.AgencyId, out _);
                Assert.AreEqual(0, AgencyVisibilityStore.GetSnapshot().AgencyGrants.Length);
            }
        }

        [TestMethod]
        public void OnlyAgencyOwnerCanMutateAndFailedWritePreservesDocumentAndRevision()
        {
            using (var fixture = new Fixture())
            {
                var command = fixture.Command(VisibilityOperation.SetCraftOverride, VisibilityOverride.Allow);
                command.TargetAgencyId = fixture.Owner.AgencyId;
                Assert.IsFalse(AgencyVisibilityStore.Mutate(fixture.Other, command).Success);
                command.TargetAgencyId = fixture.Other.AgencyId;
                AgencyStore.Agencies[fixture.Owner.AgencyId].OwnerUniqueId = "another-member";
                Assert.IsFalse(AgencyVisibilityStore.Mutate(fixture.Owner, command).Success);
                AgencyStore.Agencies[fixture.Owner.AgencyId].OwnerUniqueId = fixture.Owner.UniqueIdentifier;
                Assert.IsTrue(AgencyVisibilityStore.Mutate(fixture.Owner, command).Success);
                var bytes = File.ReadAllBytes(AgencyVisibilityStore.FilePath);
                var before = AgencyVisibilityStore.GetSnapshot().Revision;
                AgencyVisibilityStore.PersistenceCheckpoint = _ => throw new IOException("injected");
                Assert.IsFalse(AgencyVisibilityStore.Mutate(fixture.Owner, fixture.Command(VisibilityOperation.SetCraftOverride, VisibilityOverride.Deny)).Success);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(AgencyVisibilityStore.FilePath));
                Assert.AreEqual(before, AgencyVisibilityStore.GetSnapshot().Revision);
                Assert.AreEqual(VisibilityOverride.Allow, AgencyVisibilityStore.GetSnapshot().CraftOverrides.Single().Rule);
                AgencyVisibilityStore.PersistenceCheckpoint = null;
                File.WriteAllText(AgencyVisibilityStore.FilePath, "{broken"); AgencyVisibilityStore.Load();
                Assert.IsFalse(AgencyVisibilityStore.GetSnapshot().Ready);
                Assert.IsFalse(AgencyVisibilityStore.Mutate(fixture.Owner, command).Success);
                Assert.AreEqual("{broken", File.ReadAllText(AgencyVisibilityStore.FilePath));
            }
        }
    }
}
