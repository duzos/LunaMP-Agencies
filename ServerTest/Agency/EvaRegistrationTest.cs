using Lidgren.Network;
using LmpCommon.Agency;
using LmpCommon.Locks;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Client;
using Server.Context;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class EvaRegistrationTest
    {
        private static int _endpoint = 32500;

        private static string Tail => string.Concat(new[] { "ORBIT", "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }.Select(n => n + "\n{\n}\n"));
        private static string Proto(Guid id, uint uid, params string[] crew) => "pid = " + id.ToString("N") + "\nname = Fixture\nroot = 0\nPART\n{\nname = probe\nuid = " + uid + "\n" + string.Concat(crew.Select(c => "crew = " + c + "\n")) + "}\n" + Tail;
        private static string EvaProto(Guid id, uint uid, string crew) => "type = EVA\npid = " + id.ToString("N") + "\nname = " + crew + "\nroot = 0\nPART\n{\nname = kerbalEVA\nuid = " + uid + "\ncrew = " + crew + "\n}\n" + Tail;

        private sealed class Held : IDisposable
        {
            private readonly LockDefinition _lock;
            internal Held(AgencyEconomyTest.Fixture fixture, LockType type, Guid vessel)
            {
                _lock = new LockDefinition(type, fixture.Client.PlayerName, vessel);
                Assert.IsTrue(LockSystem.AcquireLock(_lock, true, out _));
            }
            public void Dispose() => LockSystem.ReleaseLock(_lock);
        }

        private static Guid Parent(AgencyEconomyTest.Fixture fixture, uint uid, params string[] crew)
        {
            var parent = AgencyEconomyTopologyTest.Launch(fixture, uid, out _);
            VesselStoreSystem.CurrentVessels[parent] = new Server.System.Vessel.Classes.Vessel(Proto(parent, uid, crew));
            return parent;
        }

        private static Guid Craft(AgencyEconomyTest.Fixture fixture, uint uid, params string[] crew)
        {
            var id = AgencyEconomyTopologyTest.Launch(fixture, uid, out _);
            VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(Proto(id, uid, crew));
            return id;
        }

        internal static void SendProto(ClientStructure client, Guid id, string raw, Action<VesselProtoMsgData> tweak = null)
        {
            var factory = new ClientMessageFactory();
            var data = factory.CreateNewMessageData<VesselProtoMsgData>();
            data.VesselId = id; data.Data = Encoding.UTF8.GetBytes(raw); data.NumBytes = data.Data.Length;
            tweak?.Invoke(data);
            var modControl = GeneralSettings.SettingsStore.ModControl;
            GeneralSettings.SettingsStore.ModControl = false;
            try { new Server.Message.VesselMsgReader().HandleMessage(client, factory.CreateNew<VesselCliMsg>(data)); }
            finally { GeneralSettings.SettingsStore.ModControl = modControl; }
        }

        private static EconomyResult LastResult(ClientStructure client) => client.SendMessageQueue.Select(m => m.Data).OfType<AgencyEconomyResultMsgData>().Last().Result;

        private static EconomyResult Register(AgencyEconomyTest.Fixture fixture, Guid parent, string crew, uint uid, ClientStructure client = null)
        {
            var id = Guid.NewGuid(); var raw = EvaProto(id, uid, crew);
            var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
            message.VesselId = id; message.EconomyParentVesselId = parent; message.EconomyEvaCrew = crew;
            return AgencyEconomyStore.RegisterEva(client ?? fixture.Client, message, raw, new Server.System.Vessel.Classes.Vessel(raw));
        }

        // A crewless parent proto from the lock holder, which is what leaves the departure ticket behind.
        private static void Depart(AgencyEconomyTest.Fixture fixture, Guid parent, uint uid, LockType lockType = LockType.Update)
        {
            using (new Held(fixture, lockType, parent)) SendProto(fixture.Client, parent, Proto(parent, uid));
        }

        private static ToolingCargo Cargo(string crew, uint container) => new ToolingCargo { Name = "cargo", Count = 1, ContainerFlightId = container, CrewName = crew };

        // ClientStructure equality is the endpoint, so a second player needs a connection of its own.
        internal static void OwnConnection(ClientStructure client, IPEndPoint endpoint)
        {
            var connection = (NetConnection)RuntimeHelpers.GetUninitializedObject(typeof(NetConnection));
            typeof(NetConnection).GetField("m_remoteEndPoint", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(connection, endpoint);
            typeof(ClientStructure).GetField("<Connection>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, connection);
        }

        internal static ClientStructure Clone(AgencyEconomyTest.Fixture fixture, string uniqueId, long connectionTicks)
        {
            var clone = (ClientStructure)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(fixture.Client, null);
            typeof(ClientStructure).GetField("<SendMessageQueue>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clone, new ConcurrentQueue<LmpCommon.Message.Interface.IServerMessageBase>());
            typeof(ClientStructure).GetField("<ConnectionTime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clone, new DateTime(connectionTicks, DateTimeKind.Utc));
            clone.UniqueIdentifier = uniqueId; clone.PlayerName = "Clone" + uniqueId;
            var endpoint = new IPEndPoint(IPAddress.Loopback, ++_endpoint);
            OwnConnection(clone, endpoint);
            ServerContext.Clients[endpoint] = clone;
            var agency = AgencyStore.Agencies[fixture.Client.AgencyId];
            if (!agency.HasMember(uniqueId)) agency.Members.Add(new Server.Agency.Agency.Member { UniqueId = uniqueId, DisplayName = clone.PlayerName });
            return clone;
        }

        [TestMethod]
        public void CrewlessParentArrivingFirstStillLetsTheEvaRegisterAndMovesItsCargo()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Parent(fixture, 2001, "Bob");
                Assert.IsTrue(AgencyEconomyStore.UpdateCargoBindings(parent, new uint[] { 2001 }, new[] { Cargo("Bob", 2001) }));
                Depart(fixture, parent, 2001);
                Assert.IsTrue(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"));
                CollectionAssert.AreEqual(new string[0], VesselStoreSystem.CurrentVessels[parent].Parts.GetAllValues().SelectMany(p => p.Fields.GetAll()).Where(f => f.Key == "crew").Select(f => f.Value).ToArray());
                var eva = Guid.NewGuid();
                SendProto(fixture.Client, eva, EvaProto(eva, 2002, "Bob"), m => { m.EconomyParentVesselId = parent; m.EconomyEvaCrew = "Bob"; });
                var result = LastResult(fixture.Client);
                Assert.IsTrue(result.Success, result.Reason);
                Assert.AreEqual(eva, result.RequestId, "RegisterEva results must not carry an empty request id.");
                Assert.AreEqual(eva, result.VesselId);
                Assert.IsTrue(VesselStoreSystem.VesselExists(eva));
                Assert.AreEqual(fixture.Client.AgencyId, AgencyVesselMap.Get(eva).OwnerAgencyId);
                Assert.AreEqual(0, fixture.Snapshot.Vessels.Single(v => v.VesselId == parent).Cargo.Length);
                Assert.AreEqual(2002u, fixture.Snapshot.Vessels.Single(v => v.VesselId == eva).Cargo.Single().ContainerFlightId);
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void WithoutADepartureTicketACrewlessParentRejectsTheEva()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Parent(fixture, 2011);
                var result = Register(fixture, parent, "Bob", 2012);
                Assert.IsFalse(result.Success);
                StringAssert.Contains(result.Reason, "not aboard");
                Assert.AreNotEqual(Guid.Empty, result.RequestId);
                Assert.IsFalse(VesselStoreSystem.VesselExists(result.VesselId));
            }
        }

        [TestMethod]
        public void ATicketBelongsToItsClientSession()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Parent(fixture, 2021, "Bob");
                Depart(fixture, parent, 2021);
                var teammate = Clone(fixture, "teammate", fixture.Client.ConnectionTime.Ticks);
                var reconnected = Clone(fixture, fixture.Client.UniqueIdentifier, fixture.Client.ConnectionTime.Ticks + 1);
                foreach (var other in new[] { teammate, reconnected })
                {
                    Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(other, parent, "Bob"));
                    var rejected = Register(fixture, parent, "Bob", 2022, other);
                    Assert.IsFalse(rejected.Success);
                    StringAssert.Contains(rejected.Reason, "not aboard");
                }
                var accepted = Register(fixture, parent, "Bob", 2022);
                Assert.IsTrue(accepted.Success, accepted.Reason);
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void CrewAboardAnotherCraftOfTheSameAgencyIsRejectedWithOrWithoutATicket(bool viaTicket)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Parent(fixture, 2031, "Bob");
                Craft(fixture, 2033, "Bob");
                if (viaTicket) Depart(fixture, parent, 2031);
                var result = Register(fixture, parent, "Bob", 2032);
                Assert.IsFalse(result.Success);
                StringAssert.Contains(result.Reason, "already aboard another craft");
                Assert.IsFalse(VesselStoreSystem.VesselExists(result.VesselId));
            }
        }

        [TestMethod]
        public void AKerbalNameAboardAnotherAgenciesCraftDoesNotBlockTheEva()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var rival = AgencySystem.CreateAgency("Rival", "rival", "Rival").Agency.Id;
                var rivalCraft = Guid.NewGuid();
                VesselStoreSystem.CurrentVessels[rivalCraft] = new Server.System.Vessel.Classes.Vessel(Proto(rivalCraft, 2043, "Jeb"));
                AgencyVesselMap.Set(rivalCraft, rival);
                var parent = Parent(fixture, 2041, "Jeb");
                Depart(fixture, parent, 2041);
                var result = Register(fixture, parent, "Jeb", 2042);
                Assert.IsTrue(result.Success, result.Reason);
            }
        }

        [TestMethod]
        public void ATicketIsConsumedByTheRegistration()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Parent(fixture, 2051, "Bob");
                Depart(fixture, parent, 2051);
                var first = Register(fixture, parent, "Bob", 2052);
                Assert.IsTrue(first.Success, first.Reason);
                Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"));
                // Take the first kerbal off the map so only the ticket (not the duplicate-crew scan) could admit a second EVA.
                VesselStoreSystem.CurrentVessels.TryRemove(first.VesselId, out _);
                var second = Register(fixture, parent, "Bob", 2053);
                Assert.IsFalse(second.Success);
                StringAssert.Contains(second.Reason, "not aboard");
            }
        }

        [TestMethod]
        public void ATicketIsDroppedWhenItsClientDisconnects()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Parent(fixture, 2061, "Bob");
                Depart(fixture, parent, 2061);
                Assert.IsTrue(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"));
                AgencyEconomyStore.CancelPending(fixture.Client);
                Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"));
                var result = Register(fixture, parent, "Bob", 2062);
                Assert.IsFalse(result.Success);
                StringAssert.Contains(result.Reason, "not aboard");
            }
        }

        [TestMethod]
        public void NoTicketWithoutControlOrAnUpdateLockOrWithToolingOff()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var parent = Parent(fixture, 2071, "Bob");
                var foreign = AgencySystem.CreateAgency("Foreign", "foreign", "Foreign").Agency.Id;
                AgencyVesselMap.Set(parent, foreign);
                Depart(fixture, parent, 2071);
                Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"), "A sender that cannot control the craft leaves no ticket.");
                AgencyVesselMap.Set(parent, fixture.Client.AgencyId);
                VesselStoreSystem.CurrentVessels[parent] = new Server.System.Vessel.Classes.Vessel(Proto(parent, 2071, "Bob"));
                using (new Held(fixture, LockType.Control, parent)) SendProto(fixture.Client, parent, Proto(parent, 2071));
                Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"), "The Control lock alone is not an update lock.");
                VesselStoreSystem.CurrentVessels[parent] = new Server.System.Vessel.Classes.Vessel(Proto(parent, 2071, "Bob"));
                GeneralSettings.SettingsStore.AgencyTooling = false;
                using (new Held(fixture, LockType.Update, parent)) AgencyEconomyStore.RecordCrewDepartures(fixture.Client, parent, new Server.System.Vessel.Classes.Vessel(Proto(parent, 2071)));
                GeneralSettings.SettingsStore.AgencyTooling = true;
                Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"), "Without tooling no ticket is created.");
                // The same proto from an authorised update-lock holder does leave one.
                Depart(fixture, parent, 2071);
                Assert.IsTrue(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"));
            }
        }

        [DataTestMethod]
        [DataRow(LockType.Update)]
        [DataRow(LockType.UnloadedUpdate)]
        public void ATicketIsCreatedAfterTheParentsControlLockMovedToTheEva(LockType lockType)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var parent = Parent(fixture, 2081, "Bob");
                var eva = Guid.NewGuid();
                // Vessel switch: the Control lock is already on the kerbal, never on the parent.
                using (new Held(fixture, LockType.Control, eva))
                    Depart(fixture, parent, 2081, lockType);
                Assert.IsTrue(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"));
                var result = Register(fixture, parent, "Bob", 2082);
                Assert.IsTrue(result.Success, result.Reason);
            }
        }

        [TestMethod]
        public void ATicketExpiresAfterTwoMinutes()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var start = DateTime.UtcNow;
                AgencyEconomyStore.UtcNow = () => start;
                var parent = Parent(fixture, 2091, "Bob");
                Depart(fixture, parent, 2091);
                AgencyEconomyStore.UtcNow = () => start.AddSeconds(121);
                Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, "Bob"));
                var expired = Register(fixture, parent, "Bob", 2092);
                Assert.IsFalse(expired.Success);
                StringAssert.Contains(expired.Reason, "not aboard");
                AgencyEconomyStore.UtcNow = () => start.AddSeconds(119);
                var live = Register(fixture, parent, "Bob", 2093);
                Assert.IsTrue(live.Success, live.Reason);
            }
        }

        [TestMethod]
        public void TheTicketStoreKeepsTheNewestTwoHundredFiftySix()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var names = Enumerable.Range(0, 300).Select(i => "Kerbal" + i.ToString("D3")).ToArray();
                var parent = Parent(fixture, 2101, names);
                Depart(fixture, parent, 2101);
                Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, names[0]));
                Assert.IsFalse(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, names[43]));
                Assert.IsTrue(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, names[44]));
                Assert.IsTrue(AgencyEconomyStore.HasCrewDeparture(fixture.Client, parent, names[299]));
                var result = Register(fixture, parent, names[299], 2102);
                Assert.IsTrue(result.Success, result.Reason);
            }
        }

        [TestMethod]
        public void ACrewmatesCargoBindingDoesNotStealTheEvaKerbalsCargoLabel()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var manifest = new ToolingManifest
                {
                    Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } },
                    Cargo = new[] { new ToolingCargo { Name = "cargo", Count = 1, UnitCost = 10, ContainerPartIndex = 0 }, new ToolingCargo { Name = "cargo", Count = 1, UnitCost = 25, ContainerPartIndex = 0 } }
                };
                var launch = Guid.NewGuid();
                var prepared = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = launch, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
                Assert.IsTrue(prepared.Success, prepared.Reason);
                var parent = Guid.NewGuid(); var raw = Proto(parent, 2111, "Bob", "Jeb");
                var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
                message.VesselId = parent; message.EconomyLaunchId = launch; message.EconomyLaunchToken = prepared.LaunchToken; message.EconomyManifestIndices = new[] { 0 };
                Assert.IsTrue(AgencyEconomyStore.Register(fixture.Client, message, raw, new Server.System.Vessel.Classes.Vessel(raw)).Success);
                VesselStoreSystem.CurrentVessels[parent] = new Server.System.Vessel.Classes.Vessel(raw);
                Assert.IsTrue(AgencyEconomyStore.UpdateCargoBindings(parent, new uint[] { 2111 }, new[] { Cargo("Bob", 2111), Cargo("Jeb", 2111) }));
                Assert.AreEqual(10d, fixture.Snapshot.Vessels.Single(v => v.VesselId == parent).Cargo.Single(c => c.CrewName == "Bob").UnitCost);
                // Bob steps out. The parent proto that follows binds only Jeb's item.
                using (new Held(fixture, LockType.Update, parent))
                    SendProto(fixture.Client, parent, Proto(parent, 2111, "Jeb"), m => m.EconomyCargo = new[] { Cargo("Jeb", 2111) });
                var cargo = fixture.Snapshot.Vessels.Single(v => v.VesselId == parent).Cargo;
                Assert.AreEqual(10d, cargo.Single(c => c.CrewName == "Bob").UnitCost, "Bob's label survives the binding of his crewmate.");
                Assert.AreEqual(25d, cargo.Single(c => c.CrewName == "Jeb").UnitCost);
                var result = Register(fixture, parent, "Bob", 2112);
                Assert.IsTrue(result.Success, result.Reason);
                var evaCargo = fixture.Snapshot.Vessels.Single(v => v.VesselId == result.VesselId).Cargo.Single();
                Assert.AreEqual(10d, evaCargo.UnitCost);
                Assert.AreEqual(2112u, evaCargo.ContainerFlightId);
            }
        }

        [TestMethod]
        public void BoardingBackSucceedsAfterATicketRegistration()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Parent(fixture, 2121, "Bob");
                Assert.IsTrue(AgencyEconomyStore.UpdateCargoBindings(parent, new uint[] { 2121 }, new[] { Cargo("Bob", 2121) }));
                Depart(fixture, parent, 2121);
                var registered = Register(fixture, parent, "Bob", 2122);
                Assert.IsTrue(registered.Success, registered.Reason);
                var boarding = new EconomyCommand { Operation = EconomyOperation.BoardEva, VesselId = registered.VesselId, ParentVesselId = parent, CrewName = "Bob", VesselData = Encoding.UTF8.GetBytes(Proto(parent, 2121, "Bob")) };
                var result = fixture.Execute(boarding);
                Assert.IsTrue(result.Success, result.Reason);
                Assert.IsFalse(VesselStoreSystem.VesselExists(registered.VesselId));
                Assert.AreEqual(2121u, fixture.Snapshot.Vessels.Single(v => v.VesselId == parent).Cargo.Single().ContainerFlightId);
                Assert.AreEqual(49875d, fixture.Snapshot.Funds);
            }
        }

        [TestMethod]
        public void BoardingAndRecoveryNameTheirOwnOperationWhenNotAuthorized()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var parent = Parent(fixture, 2131, "Bob");
                var board = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.BoardEva, VesselId = Guid.NewGuid(), ParentVesselId = parent, CrewName = "Bob", VesselData = Encoding.UTF8.GetBytes(Proto(parent, 2131, "Bob")) });
                Assert.IsFalse(board.Success);
                Assert.AreEqual("Craft boarding is not authorized.", board.Reason);
                var recover = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.Recover, VesselId = Guid.NewGuid() });
                Assert.IsFalse(recover.Success);
                Assert.AreEqual("Craft recovery is not authorized.", recover.Reason);
            }
        }
    }
}
