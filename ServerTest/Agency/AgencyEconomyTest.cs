using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class AgencyEconomyTest
    {
        internal sealed class Fixture : IDisposable
        {
            private readonly AgencyTestScope scope = new AgencyTestScope();
            private readonly object savedDocument = Field("_document").GetValue(null);
            private readonly object savedError = Field("_error").GetValue(null);
            private readonly object savedInitialized = Field("Initialized").GetValue(null);
            private readonly bool oldEnabled = GeneralSettings.SettingsStore.AgencyTooling;
            private readonly Action<string> checkpoint = AgencyEconomyStore.PersistenceCheckpoint;
            private readonly Func<DateTime> clock = AgencyEconomyStore.UtcNow;
            private readonly System.Collections.Generic.KeyValuePair<IPEndPoint, ClientStructure>[] clients;
            private readonly System.Collections.Generic.KeyValuePair<Guid, Server.System.Vessel.Classes.Vessel>[] vessels;
            private readonly IDictionary sessions = (IDictionary)Field("Sessions").GetValue(null);
            private readonly DictionaryEntry[] oldSessions;
            private readonly System.Collections.Generic.HashSet<ClientStructure> blocked = (System.Collections.Generic.HashSet<ClientStructure>)Field("PublicationBlocked").GetValue(null);
            private readonly ClientStructure[] oldBlocked;
            public ClientStructure Client { get; }
            public Guid Session { get; private set; }
            private long sequence;
            public Fixture()
            {
                oldBlocked = blocked.ToArray(); blocked.Clear();
                clients = ServerContext.Clients.ToArray(); vessels = VesselStoreSystem.CurrentVessels.ToArray();
                var previousSessions = new System.Collections.Generic.List<DictionaryEntry>();
                var sessionEnumerator = sessions.GetEnumerator();
                while (sessionEnumerator.MoveNext()) previousSessions.Add(sessionEnumerator.Entry);
                oldSessions = previousSessions.ToArray(); sessions.Clear();
                ServerContext.Clients.Clear(); VesselStoreSystem.CurrentVessels.Clear();
                GeneralSettings.SettingsStore.AgencyTooling = true; GeneralSettings.SettingsStore.GameMode = GameMode.Career;
                AgencyEconomyStore.PersistenceCheckpoint = null;
                var connection = (NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));
                typeof(NetConnection).GetField("m_remoteEndPoint", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(connection, new IPEndPoint(IPAddress.Loopback, 32251));
                Client = (ClientStructure)RuntimeHelpers.GetUninitializedObject(typeof(ClientStructure));
                typeof(ClientStructure).GetField("<Connection>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Client, connection);
                typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Client, new ConcurrentQueue<IServerMessageBase>());
                Client.AgencyId = Guid.NewGuid(); Client.UniqueIdentifier = "economy-fixture"; Client.PlayerName = "EconomyFixture";
                Client.Authenticated = true; Client.ConnectionStatus = ConnectionStatus.Connected;
                ServerContext.Clients[Client.Endpoint] = Client;
                var agency = new Server.Agency.Agency { Id = Client.AgencyId, Name = "Fixture", OwnerUniqueId = Client.UniqueIdentifier, Funds = 50000, Science = 100 };
                agency.Members.Add(new Server.Agency.Agency.Member { UniqueId = Client.UniqueIdentifier, DisplayName = Client.PlayerName });
                AgencyStore.Agencies[agency.Id] = agency;
                AgencyScenarioStore.EnsureBaselineForAgency(agency.Id, agency.Funds, agency.Science, 0);
                AgencyEconomyStore.Load(); RefreshSession();
            }
            private static FieldInfo Field(string name) => typeof(AgencyEconomyStore).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            public void RefreshSession()
            {
                AgencyEconomyStore.SendTo(Client);
                var snapshot = Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyEconomySnapshotMsgData>().Last().Snapshot;
                Session = snapshot.SessionId; sequence = snapshot.LastSequence;
            }
            public EconomyResult Execute(EconomyCommand command)
            {
                if (command.RequestId == Guid.Empty) command.RequestId = Guid.NewGuid();
                if (command.SessionId == Guid.Empty) command.SessionId = Session;
                if (command.Sequence == 0) command.Sequence = ++sequence;
                return AgencyEconomyStore.Execute(Client, command);
            }
            public EconomySnapshot Snapshot => AgencyEconomyStore.Snapshot(Client.AgencyId);
            public void Dispose()
            {
                AgencyEconomyStore.PersistenceCheckpoint = checkpoint; AgencyEconomyStore.UtcNow = clock;
                Field("_document").SetValue(null, savedDocument); Field("_error").SetValue(null, savedError); Field("Initialized").SetValue(null, savedInitialized);
                blocked.Clear(); foreach (var old in oldBlocked) blocked.Add(old);
                sessions.Clear(); foreach (var old in oldSessions) sessions.Add(old.Key, old.Value);
                GeneralSettings.SettingsStore.AgencyTooling = oldEnabled;
                ServerContext.Clients.Clear(); foreach (var pair in clients) ServerContext.Clients[pair.Key] = pair.Value;
                VesselStoreSystem.CurrentVessels.Clear(); foreach (var pair in vessels) VesselStoreSystem.CurrentVessels[pair.Key] = pair.Value;
                scope.Dispose();
            }
        }
        private static ToolingManifest Manifest() => new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 }, new ToolingPart { Name = "science", UnitCost = 300, IsScience = true } } };
        private static EconomyCommand Purchase() { var m = Manifest(); return new EconomyCommand { Operation = EconomyOperation.Tool, Manifest = m, ManifestHash = ToolingPolicy.ManifestHash(m) }; }
        [TestMethod]
        public void FailedWriteDoesNotChargeAndCommittedFailureReplaysExactlyOnce()
        {
            using (var f = new Fixture())
            {
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "before-document") throw new IOException("injected"); };
                Assert.IsFalse(f.Execute(Purchase()).Success); Assert.AreEqual(50000d, f.Snapshot.Funds); Assert.AreEqual(0, f.Snapshot.Designs.Length);
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "committed") throw new IOException("injected after durable commit"); };
                var result = f.Execute(Purchase()); Assert.IsTrue(result.RecoveryRequired); Assert.IsFalse(AgencyEconomyStore.Ready);
                AgencyEconomyStore.PersistenceCheckpoint = null; AgencyEconomyStore.Load(); f.RefreshSession();
                Assert.IsTrue(AgencyEconomyStore.Ready); Assert.AreEqual(49000d, f.Snapshot.Funds); Assert.AreEqual(1, f.Snapshot.Designs.Length);
                Assert.IsTrue(f.Execute(Purchase()).Success); Assert.AreEqual(49000d, f.Snapshot.Funds);
            }
        }
        [TestMethod]
        public void RetiredOperationsCannotReplayAndDoNotExhaustEconomy()
        {
            using (var f = new Fixture())
            {
                var oldest = new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = 1 };
                Assert.IsTrue(f.Execute(oldest).Success);
                for (var i = 0; i < 520; i++)
                {
                    var result = f.Execute(new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = 1 });
                    Assert.IsTrue(result.Success, result.Reason);
                }
                Assert.AreEqual(50521d, f.Snapshot.Funds);
                Assert.IsFalse(f.Execute(oldest).Success, "Retired sequence must never reapply its delta.");
                Assert.AreEqual(50521d, f.Snapshot.Funds);
                Assert.IsTrue(f.Execute(new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = 2 }).Success);
                Assert.AreEqual(50523d, f.Snapshot.Funds);
            }
        }
        [TestMethod]
        public void CancelledLaunchCannotRegisterLateOrRefundTwice()
        {
            using (var f = new Fixture())
            {
                var m = Manifest();
                var prepare = f.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = Guid.NewGuid(), Manifest = m, ManifestHash = ToolingPolicy.ManifestHash(m) });
                Assert.IsTrue(prepare.Success, prepare.Reason); Assert.AreEqual(49600d, f.Snapshot.Funds);
                var cancel = new EconomyCommand { Operation = EconomyOperation.CancelLaunch, LaunchId = prepare.LaunchId, LaunchToken = prepare.LaunchToken };
                Assert.IsTrue(f.Execute(cancel).Success); Assert.IsTrue(f.Execute(cancel).Success); Assert.AreEqual(50000d, f.Snapshot.Funds);
                var id = Guid.NewGuid(); var raw = Proto(id);
                var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
                message.VesselId = id; message.EconomyLaunchId = prepare.LaunchId; message.EconomyLaunchToken = prepare.LaunchToken; message.EconomyManifestIndices = new[] { 0, 1 };
                Assert.IsFalse(AgencyEconomyStore.Register(f.Client, message, raw, new Server.System.Vessel.Classes.Vessel(raw)).Success);
                Assert.IsFalse(VesselStoreSystem.VesselExists(id)); Assert.AreEqual(50000d, f.Snapshot.Funds);
            }
        }
        private static string Proto(Guid id) => "pid = " + id.ToString("N") + "\nname = Fixture\nroot = 0\nPART\n{\nname = probe\nuid = 701\n}\nPART\n{\nname = science\nuid = 702\n}\n" + string.Concat(new[] { "ORBIT", "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }.Select(n => n + "\n{\n}\n"));
    }
}
