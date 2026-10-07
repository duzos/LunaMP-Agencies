using LmpCommon.Agency;
using LmpCommon.Locks;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Data.Vessel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Server.Agency;
using Server.Client;
using Server.Command.Command;
using Server.Settings.Structures;
using Server.System;
using Server.System.Vessel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class VesselRemovalTest
    {
        internal static string Proto(Guid id, params uint[] uids) => "pid = " + id.ToString("N") + "\nname = Fixture\nroot = 0\n" + string.Concat(uids.Select(uid => "PART\n{\nname = probe\nuid = " + uid + "\n}\n")) + string.Concat(new[] { "ORBIT", "ACTIONGROUPS", "DISCOVERY", "FLIGHTPLAN", "CTRLSTATE", "VESSELMODULES" }.Select(n => n + "\n{\n}\n"));
        internal static string VesselFile(Guid id) => Path.Combine(VesselStoreSystem.VesselsPath, id + VesselStoreSystem.VesselFileFormat);
        internal static OwnershipDocument Doc => (OwnershipDocument)typeof(AgencyVesselMap).GetField("_document", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        // A stored craft with a file and, optionally, an ownership record; no payment provenance.
        internal static Guid Stored(Guid? owner, uint uid, string prefix = "")
        {
            var id = Guid.NewGuid(); var raw = prefix + Proto(id, uid);
            VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(raw);
            Directory.CreateDirectory(VesselStoreSystem.VesselsPath);
            File.WriteAllText(VesselFile(id), raw);
            if (owner.HasValue) AgencyVesselMap.Set(id, owner.Value);
            return id;
        }

        internal static void Prefix(Guid id, string prefix) => VesselStoreSystem.CurrentVessels[id] = new Server.System.Vessel.Classes.Vessel(prefix + VesselStoreSystem.CurrentVessels[id]);

        private sealed class StockMode : IDisposable
        {
            private readonly bool tooling = GeneralSettings.SettingsStore.AgencyTooling, trade = GeneralSettings.SettingsStore.AgencyTrade, ownership = GeneralSettings.SettingsStore.AgencyVesselOwnership,
                hide = GeneralSettings.SettingsStore.AgencyHideCraft, commnet = GeneralSettings.SettingsStore.AgencyCommNetOptIn;
            internal StockMode()
            {
                var s = GeneralSettings.SettingsStore;
                s.AgencyTooling = s.AgencyTrade = s.AgencyVesselOwnership = s.AgencyHideCraft = s.AgencyCommNetOptIn = false;
                Assert.IsFalse(AgencyVesselMap.AgencyRulesActive);
            }
            public void Dispose()
            {
                var s = GeneralSettings.SettingsStore;
                s.AgencyTooling = tooling; s.AgencyTrade = trade; s.AgencyVesselOwnership = ownership; s.AgencyHideCraft = hide; s.AgencyCommNetOptIn = commnet;
            }
        }

        private static void RemoveAsClient(ClientStructure client, Guid id, bool killList, bool killOnReceive = false)
        {
            var factory = new ClientMessageFactory();
            var data = factory.CreateNewMessageData<VesselRemoveMsgData>();
            data.VesselId = id; data.AddToKillList = killList; data.KillOnReceive = killOnReceive; data.Reason = "Destroyed";
            new Server.Message.VesselMsgReader().HandleMessage(client, factory.CreateNew<VesselCliMsg>(data));
        }

        private static T[] Received<T>(ClientStructure client) where T : class => client.SendMessageQueue.Select(m => m.Data).OfType<T>().ToArray();
        private static void Drain(ClientStructure client) { while (client.SendMessageQueue.TryDequeue(out _)) { } }
        private static byte[] OwnershipBytes() => File.Exists(AgencyVesselMap.OwnershipFilePath) ? File.ReadAllBytes(AgencyVesselMap.OwnershipFilePath) : new byte[0];

        // One launch with two parts; the second part is split off as debris of the same paid launch.
        private static Guid LaunchWithDebris(AgencyEconomyTest.Fixture fixture, uint first, uint second, out Guid launch, out Guid debris)
        {
            var manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 }, new ToolingPart { Name = "probe", UnitCost = 100 } } };
            launch = Guid.NewGuid();
            var prepared = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = launch, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
            Assert.IsTrue(prepared.Success, prepared.Reason);
            var id = Guid.NewGuid(); var raw = Proto(id, first, second);
            var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
            message.VesselId = id; message.EconomyLaunchId = launch; message.EconomyLaunchToken = prepared.LaunchToken; message.EconomyManifestIndices = new[] { 0, 1 };
            var registered = AgencyEconomyStore.Register(fixture.Client, message, raw, new Server.System.Vessel.Classes.Vessel(raw));
            Assert.IsTrue(registered.Success, registered.Reason);
            debris = Guid.NewGuid();
            Assert.IsTrue(AgencyVesselMap.RestoreSplit(id, debris, 0, second));
            Assert.IsTrue(AgencyVesselMap.ResolveSplit(debris, new[] { second }, "type = Debris\n" + Proto(debris, second), Proto(id, first)));
            return id;
        }

        [DataTestMethod]
        [DataRow("dekessler")]
        [DataRow("nuke")]
        [DataRow("clearvessels")]
        public void AdminCleanupRemovesOwnershipAndProvenanceInOneCommitPerPass(string command)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var prefix = command == "dekessler" ? "type = Debris\n" : command == "nuke" ? "landed = True\nlandedAt = KSC\n" : "type = Ship\nsit = LANDED\nsplashed = False\n";
                var ids = new[] { AgencyEconomyTopologyTest.Launch(fixture, 4001, out _), AgencyEconomyTopologyTest.Launch(fixture, 4002, out _), AgencyEconomyTopologyTest.Launch(fixture, 4003, out _) };
                foreach (var id in ids) Prefix(id, prefix);
                var ownershipRevision = AgencyVesselMap.CaptureEpoch(); var economyRevision = fixture.Snapshot.Revision;
                if (command == "dekessler") new DekesslerCommand().Execute("");
                else if (command == "nuke") new NukeCommand().Execute("");
                else Assert.IsTrue(new ClearVesselsCommand().Execute("* * * *"));
                Assert.AreEqual(ownershipRevision + 1, AgencyVesselMap.CaptureEpoch(), "One ownership commit per pass.");
                Assert.AreEqual(economyRevision + 1, fixture.Snapshot.Revision, "One economy commit per pass.");
                foreach (var id in ids)
                {
                    Assert.IsNull(AgencyVesselMap.Get(id));
                    Assert.IsFalse(AgencyVesselMap.IsDeleted(id));
                    Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                    Assert.IsFalse(File.Exists(VesselFile(id)));
                    Assert.IsFalse(fixture.Snapshot.Vessels.Any(v => v.VesselId == id));
                    Assert.IsTrue(VesselContext.RemovedVessels.ContainsKey(id), "Agency rules are active, so the admin kill list applies.");
                }
                Assert.AreEqual(49625d, fixture.Snapshot.Funds, "Cleanup refunds nothing.");
            }
        }

        [TestMethod]
        public void ProvenanceOnlyOrphanIsRemovedAndANoOpWritesNothing()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = AgencyEconomyTopologyTest.Launch(fixture, 4101, out _);
                Doc.Records.Remove(id);
                Assert.IsTrue(AgencyEconomyStore.HasProvenance(id));
                var before = AgencyVesselMap.CaptureEpoch();
                Assert.IsTrue(AgencyVesselMap.RemoveMany(new[] { id }, VesselRemovalMode.Ordinary, false));
                Assert.AreEqual(before + 1, AgencyVesselMap.CaptureEpoch());
                Assert.IsFalse(AgencyEconomyStore.HasProvenance(id));
                Assert.IsFalse(fixture.Snapshot.Vessels.Any(v => v.VesselId == id));
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                // Nothing left to remove: no revision bump, no write.
                var bytes = OwnershipBytes(); var economyRevision = fixture.Snapshot.Revision;
                Assert.IsFalse(AgencyVesselMap.RemoveMany(new[] { id, Guid.NewGuid(), Guid.Empty }, VesselRemovalMode.Ordinary, true));
                Assert.AreEqual(before + 1, AgencyVesselMap.CaptureEpoch());
                Assert.AreEqual(economyRevision, fixture.Snapshot.Revision);
                CollectionAssert.AreEqual(bytes, OwnershipBytes());
            }
        }

        [TestMethod]
        public void UnknownIdsWriteNothingButTheStoreEntryAndFileStillGo()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (new StockMode())
            {
                var id = Stored(null, 4201);
                var before = AgencyVesselMap.CaptureEpoch(); var bytes = OwnershipBytes();
                var result = VesselRemovalService.Remove(new[] { id }, "test", VesselRemovalMode.Ordinary, null, VesselRemovalOptions.AdminCleanup());
                Assert.IsTrue(result.Success);
                Assert.AreEqual(before, AgencyVesselMap.CaptureEpoch());
                CollectionAssert.AreEqual(bytes, OwnershipBytes());
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                Assert.IsFalse(File.Exists(VesselFile(id)));
                Assert.IsFalse(VesselContext.RemovedVessels.ContainsKey(id), "Pure stock admin cleanups keep no kill list.");
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RevertStillSucceedsAfterADebrisPieceOfTheLaunchIsDestroyedOrDekesslered(bool byClient)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = LaunchWithDebris(fixture, 4301, 4302, out var launch, out var debris);
                Assert.IsTrue(fixture.Snapshot.Vessels.Any(v => v.VesselId == debris));
                if (byClient) RemoveAsClient(fixture.Client, debris, false);
                else new DekesslerCommand().Execute("");
                Assert.IsFalse(VesselStoreSystem.VesselExists(debris));
                Assert.IsFalse(fixture.Snapshot.Vessels.Any(v => v.VesselId == debris));
                var revert = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.Revert, LaunchId = launch });
                Assert.IsTrue(revert.Success, revert.Reason);
                Assert.AreEqual(50000d, fixture.Snapshot.Funds, "The launch is refunded: removing debris never settled it.");
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
            }
        }

        [TestMethod]
        public void KillListFollowsTheCallersPermanentFlag()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                // Agency rules active (tooling): the admin pass kill-lists, a client remove only when it asks to.
                var debris = AgencyEconomyTopologyTest.Launch(fixture, 4401, out _);
                Prefix(debris, "type = Debris\n");
                new DekesslerCommand().Execute("");
                Assert.IsTrue(VesselContext.RemovedVessels.ContainsKey(debris));
                var kept = AgencyEconomyTopologyTest.Launch(fixture, 4402, out _);
                RemoveAsClient(fixture.Client, kept, false);
                Assert.IsFalse(VesselStoreSystem.VesselExists(kept));
                Assert.IsFalse(VesselContext.RemovedVessels.ContainsKey(kept), "AddToKillList=false stays re-sendable.");
                var killed = AgencyEconomyTopologyTest.Launch(fixture, 4403, out _);
                RemoveAsClient(fixture.Client, killed, true);
                Assert.IsTrue(VesselContext.RemovedVessels.ContainsKey(killed));
                // Pure stock: the same dekessler pass leaves the kill list alone.
                using (new StockMode())
                {
                    var stockDebris = Stored(fixture.Client.AgencyId, 4404, "type = Debris\n");
                    new DekesslerCommand().Execute("");
                    Assert.IsFalse(VesselStoreSystem.VesselExists(stockDebris));
                    Assert.IsFalse(VesselContext.RemovedVessels.ContainsKey(stockDebris));
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void AClientRemoveWithoutKillListLetsTheCraftBePublishedAgain(bool killList)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                GeneralSettings.SettingsStore.AgencyTooling = false;
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                var id = Stored(fixture.Client.AgencyId, 4501);
                RemoveAsClient(fixture.Client, id, killList);
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                Assert.IsNull(AgencyVesselMap.Get(id));
                EvaRegistrationTest.SendProto(fixture.Client, id, Proto(id, 4501));
                Assert.AreEqual(!killList, VesselStoreSystem.VesselExists(id));
                if (!killList) Assert.AreEqual(fixture.Client.AgencyId, AgencyVesselMap.Get(id).OwnerAgencyId);
                if (killList) Assert.IsTrue(VesselContext.RemovedVessels.ContainsKey(id));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TheVesselFileIsGoneSynchronouslyAfterTheRemovalCommit(bool economy)
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (economy ? null : new StockMode())
            {
                Guid id;
                if (economy) id = AgencyEconomyTopologyTest.Launch(fixture, 4601, out _);
                else id = Stored(fixture.Client.AgencyId, 4601);
                Assert.IsTrue(File.Exists(VesselFile(id)));
                // Holding the gate blocks any background store task (BackupLock is the same gate), so only a synchronous delete can pass.
                lock (AgencyVesselMap.TransactionGate)
                {
                    var result = VesselRemovalService.Remove(new[] { id }, "test", VesselRemovalMode.Ordinary, null, new VesselRemovalOptions());
                    Assert.IsTrue(result.Success);
                    Assert.IsFalse(File.Exists(VesselFile(id)));
                    Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                    Assert.IsNull(AgencyVesselMap.Get(id));
                }
            }
        }

        [TestMethod]
        public void StockModeSendsLightMessagesAndKeepsEachRemoversTargeting()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (new StockMode())
            {
                var watcher = EvaRegistrationTest.Clone(fixture, "watcher", 0);
                var both = new[] { fixture.Client, watcher };
                Action<ClientStructure> noSnapshots = client =>
                {
                    Assert.AreEqual(0, Received<AgencyVesselMapSyncMsgData>(client).Length, "No full map snapshot in stock mode.");
                    Assert.AreEqual(0, Received<AgencyEconomySnapshotMsgData>(client).Length);
                };
                // A client removal is relayed to the others only, unchanged.
                var own = Stored(fixture.Client.AgencyId, 4701);
                foreach (var client in both) Drain(client);
                RemoveAsClient(fixture.Client, own, false, true);
                Assert.AreEqual(0, Received<VesselRemoveMsgData>(fixture.Client).Length);
                var relayed = Received<VesselRemoveMsgData>(watcher).Single();
                Assert.AreEqual(own, relayed.VesselId); Assert.IsTrue(relayed.KillOnReceive);
                foreach (var client in both)
                {
                    var entry = Received<AgencyVesselMapEntryMsgData>(client).Single();
                    Assert.AreEqual(own, entry.VesselId); Assert.AreEqual(Guid.Empty, entry.AgencyId);
                    noSnapshots(client);
                }
                // A server cleanup reaches everyone.
                var debris = Stored(fixture.Client.AgencyId, 4702, "type = Debris\n");
                foreach (var client in both) Drain(client);
                new DekesslerCommand().Execute("");
                foreach (var client in both)
                {
                    Assert.AreEqual(debris, Received<VesselRemoveMsgData>(client).Single().VesselId);
                    noSnapshots(client);
                }
                // A stock couple relays the couple to the others, and the weak vessel's removal to all, sender included.
                var dominant = Stored(fixture.Client.AgencyId, 4703); var weak = Stored(fixture.Client.AgencyId, 4704);
                foreach (var client in both) Drain(client);
                var factory = new ClientMessageFactory();
                var couple = factory.CreateNewMessageData<VesselCoupleMsgData>();
                couple.VesselId = dominant; couple.CoupledVesselId = weak;
                new Server.Message.VesselMsgReader().HandleMessage(fixture.Client, factory.CreateNew<VesselCliMsg>(couple));
                Assert.AreEqual(0, Received<VesselCoupleMsgData>(fixture.Client).Length);
                Assert.AreEqual(1, Received<VesselCoupleMsgData>(watcher).Length);
                foreach (var client in both)
                {
                    var removed = Received<VesselRemoveMsgData>(client).Single();
                    Assert.AreEqual(weak, removed.VesselId); Assert.AreEqual("Coupled/Docked", removed.Reason);
                    noSnapshots(client);
                }
                Assert.IsNull(AgencyVesselMap.Get(weak), "The weak vessel's record goes with it.");
                Assert.IsFalse(VesselContext.RemovedVessels.ContainsKey(weak), "A coupled vessel may undock, so it is not kill-listed.");
            }
        }

        [TestMethod]
        public void AgencyRulesSendOneMapResyncPerPassAndRelayClientRemovesToOthers()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var watcher = EvaRegistrationTest.Clone(fixture, "watcher", 0);
                var ids = new[] { AgencyEconomyTopologyTest.Launch(fixture, 4801, out _), AgencyEconomyTopologyTest.Launch(fixture, 4802, out _), AgencyEconomyTopologyTest.Launch(fixture, 4803, out _) };
                foreach (var id in ids) Prefix(id, "type = Debris\n");
                Drain(fixture.Client); Drain(watcher);
                new DekesslerCommand().Execute("");
                foreach (var client in new[] { fixture.Client, watcher })
                {
                    Assert.AreEqual(3, Received<VesselRemoveMsgData>(client).Length);
                    Assert.AreEqual(1, Received<AgencyVesselMapSyncMsgData>(client).Length, "One resync for the whole pass.");
                }
                var own = AgencyEconomyTopologyTest.Launch(fixture, 4804, out _);
                Drain(fixture.Client); Drain(watcher);
                RemoveAsClient(fixture.Client, own, false);
                Assert.AreEqual(0, Received<VesselRemoveMsgData>(fixture.Client).Length);
                Assert.AreEqual(1, Received<VesselRemoveMsgData>(watcher).Length);
                Assert.AreEqual(1, Received<AgencyVesselMapSyncMsgData>(watcher).Length);
            }
        }

        [TestMethod]
        public async Task AProtoQueuedBeforeADekesslerPassDoesNotResurrectTheVesselInStockMode()
        {
            var modControl = GeneralSettings.SettingsStore.ModControl;
            GeneralSettings.SettingsStore.ModControl = false;
            try
            {
                using (var fixture = new AgencyEconomyTest.Fixture())
                using (new StockMode())
                {
                    var id = Stored(fixture.Client.AgencyId, 4901, "type = Debris\n");
                    var fresh = Guid.NewGuid();
                    Task queued, queuedFresh;
                    lock (AgencyVesselMap.TransactionGate)
                    {
                        // The store write of an update for an existing vessel waits on the gate; the pass then removes the vessel.
                        queued = VesselDataUpdater.RawConfigNodeInsertOrUpdate(id, "type = Debris\n" + Proto(id, 4901), false);
                        queuedFresh = VesselDataUpdater.RawConfigNodeInsertOrUpdate(fresh, Proto(fresh, 4902), true);
                        new DekesslerCommand().Execute("");
                    }
                    await Task.WhenAll(queued, queuedFresh);
                    Assert.IsFalse(VesselStoreSystem.VesselExists(id), "The queued update must not re-add a removed vessel.");
                    Assert.IsTrue(VesselStoreSystem.VesselExists(fresh), "A genuinely new vessel is still inserted.");
                    VesselStoreSystem.CurrentVessels.TryRemove(fresh, out _);
                }
            }
            finally { GeneralSettings.SettingsStore.ModControl = modControl; }
        }

        [TestMethod]
        public void StockDekesslerRemovesCrewedDebrisAndKeepsTheMapInSync()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            using (new StockMode())
            {
                var agency = fixture.Client.AgencyId;
                var plain = Stored(agency, 5001, "type = Debris\n");
                var crewed = Guid.NewGuid(); var raw = "type = Debris\n" + Proto(crewed, 5002).Replace("name = probe", "name = probe\ncrew = Jeb");
                VesselStoreSystem.CurrentVessels[crewed] = new Server.System.Vessel.Classes.Vessel(raw); AgencyVesselMap.Set(crewed, agency);
                var ship = Stored(agency, 5003, "type = Ship\n");
                Assert.AreEqual(3, AgencyVesselMap.GetOwnershipSnapshot().Records.Length);
                new DekesslerCommand().Execute("");
                Assert.IsFalse(VesselStoreSystem.VesselExists(plain));
                Assert.IsFalse(VesselStoreSystem.VesselExists(crewed), "Stock mode matches upstream: crewed debris goes too.");
                Assert.IsTrue(VesselStoreSystem.VesselExists(ship));
                CollectionAssert.AreEqual(new[] { ship }, AgencyVesselMap.GetOwnershipSnapshot().Records.Select(r => r.VesselId).ToArray(), "No record outlives its vessel.");
                // With agency rules active crewed debris is kept.
                GeneralSettings.SettingsStore.AgencyVesselOwnership = true;
                VesselStoreSystem.CurrentVessels[crewed] = new Server.System.Vessel.Classes.Vessel(raw); AgencyVesselMap.Set(crewed, agency);
                new DekesslerCommand().Execute("");
                Assert.IsTrue(VesselStoreSystem.VesselExists(crewed));
            }
        }

        [TestMethod]
        public void AdminCleanupsSkipBusyCraftWhenAgencyRulesAreActive()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var controlled = AgencyEconomyTopologyTest.Launch(fixture, 5101, out _);
                var splitting = AgencyEconomyTopologyTest.Launch(fixture, 5102, out _);
                foreach (var id in new[] { controlled, splitting }) Prefix(id, "type = Debris\n");
                var child = Guid.NewGuid();
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(splitting, child, 0, 5102));
                var control = new LockDefinition(LockType.Control, "SomePilot", controlled);
                Assert.IsTrue(LockSystem.AcquireLock(control, true, out _));
                try
                {
                    new DekesslerCommand().Execute("");
                    Assert.IsTrue(VesselStoreSystem.VesselExists(controlled));
                    Assert.IsTrue(VesselStoreSystem.VesselExists(splitting));
                    Assert.IsNotNull(AgencyVesselMap.Get(controlled));
                }
                finally { LockSystem.ReleaseLock(control); }
                new DekesslerCommand().Execute("");
                Assert.IsTrue(VesselStoreSystem.VesselExists(splitting), "A pending-split parent stays.");
                Assert.IsFalse(VesselStoreSystem.VesselExists(controlled));
            }
        }

        [TestMethod]
        public void ClientRemovalReleasesTheVesselsLocksAndTellsEveryone()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var watcher = EvaRegistrationTest.Clone(fixture, "watcher", 0);
                var id = AgencyEconomyTopologyTest.Launch(fixture, 5201, out _);
                var update = new LockDefinition(LockType.Update, fixture.Client.PlayerName, id);
                var unloaded = new LockDefinition(LockType.UnloadedUpdate, "OtherPilot", id);
                Assert.IsTrue(LockSystem.AcquireLock(update, true, out _)); Assert.IsTrue(LockSystem.AcquireLock(unloaded, true, out _));
                Drain(fixture.Client); Drain(watcher);
                try
                {
                    RemoveAsClient(fixture.Client, id, false);
                    Assert.IsFalse(LockSystem.LockQuery.LockExists(LockType.Update, id, null));
                    Assert.IsFalse(LockSystem.LockQuery.LockExists(LockType.UnloadedUpdate, id, null));
                    foreach (var client in new[] { fixture.Client, watcher })
                        Assert.AreEqual(2, Received<LockReleaseMsgData>(client).Count(m => m.Lock.VesselId == id));
                }
                finally { LockSystem.ReleaseLock(update); LockSystem.ReleaseLock(unloaded); }
            }
        }

        [TestMethod]
        public void ARemovalPassFailingBeforeAnythingIsDurableRestoresTheKillList()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var paid = AgencyEconomyTopologyTest.Launch(fixture, 5301, out _);
                var options = new VesselRemovalOptions { Permanent = true };
                try
                {
                    AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "before-document") throw new IOException("injected"); };
                    var failed = VesselRemovalService.Remove(new[] { paid }, "test", VesselRemovalMode.Ordinary, null, options);
                    Assert.IsFalse(failed.Success);
                    Assert.IsFalse(VesselContext.RemovedVessels.ContainsKey(paid), "A pass that wrote nothing keeps no kill-list entry.");
                    Assert.IsTrue(VesselStoreSystem.VesselExists(paid));
                    Assert.IsTrue(File.Exists(VesselFile(paid)));
                    Assert.IsNotNull(AgencyVesselMap.Get(paid));
                    Assert.IsTrue(AgencyEconomyStore.Ready);
                }
                finally { AgencyEconomyStore.PersistenceCheckpoint = null; }
                using (new StockMode())
                {
                    var stock = Stored(fixture.Client.AgencyId, 5302);
                    try
                    {
                        AgencyVesselMap.PersistenceCheckpoint = point => { if (point == "before-document") throw new IOException("injected"); };
                        Assert.IsFalse(VesselRemovalService.Remove(new[] { stock }, "test", VesselRemovalMode.Ordinary, null, options).Success);
                    }
                    finally { AgencyVesselMap.PersistenceCheckpoint = null; }
                    Assert.IsFalse(VesselContext.RemovedVessels.ContainsKey(stock));
                    Assert.IsTrue(VesselStoreSystem.VesselExists(stock));
                    Assert.IsTrue(File.Exists(VesselFile(stock)));
                    Assert.IsTrue(AgencyVesselMap.Ready);
                }
            }
        }

        [TestMethod]
        public void ARemovalPassFailingAfterTheJournalIsDurableKeepsTheKillListAndRecoversOnReload()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var id = AgencyEconomyTopologyTest.Launch(fixture, 5311, out _);
                try
                {
                    AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "committed") throw new IOException("injected after the durable commit"); };
                    var failed = VesselRemovalService.Remove(new[] { id }, "test", VesselRemovalMode.Ordinary, null, new VesselRemovalOptions { Permanent = true });
                    Assert.IsFalse(failed.Success);
                    Assert.IsFalse(AgencyEconomyStore.Ready);
                    Assert.IsTrue(VesselContext.RemovedVessels.ContainsKey(id), "Recovery owns the store state; the kill list stays.");
                }
                finally { AgencyEconomyStore.PersistenceCheckpoint = null; }
                AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                Assert.IsNull(AgencyVesselMap.Get(id));
                Assert.IsFalse(File.Exists(VesselFile(id)));
            }
        }

        [TestMethod]
        public void AbsorbedMarkersSurviveRemovalAndAbsorbedCraftAreNotAdvertised()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var dominant = AgencyEconomyTopologyTest.Launch(fixture, 5401, out _);
                var weak = AgencyEconomyTopologyTest.Launch(fixture, 5402, out _);
                var raw = Proto(dominant, 5401, 5402);
                AgencyVesselMap.CommitCouple(Guid.NewGuid(), fixture.Client.UniqueIdentifier, dominant, weak, raw, new Server.System.Vessel.Classes.Vessel(raw), 5401, 5402);
                Assert.IsTrue(AgencyVesselMap.IsAbsorbed(weak));
                Assert.IsNotNull(AgencyVesselMap.Get(weak), "The record stays for undocking.");
                Assert.IsFalse(AgencyVesselMap.GetOwnershipSnapshot().Records.Any(r => r.VesselId == weak), "The client list excludes absorbed craft.");
                Assert.IsFalse(AgencyVesselMap.Snapshot.ContainsKey(weak));
                Assert.IsTrue(AgencyVesselMap.GetOwnershipSnapshot().Records.Any(r => r.VesselId == dominant));
                Assert.IsTrue(AgencyVesselMap.RemoveMany(new[] { weak }, VesselRemovalMode.Ordinary, false));
                Assert.IsNull(AgencyVesselMap.Get(weak));
                Assert.IsTrue(AgencyVesselMap.IsAbsorbed(weak), "RemoveMany never clears an absorbed marker.");
                Assert.IsTrue(AgencyVesselMap.RemoveMany(new[] { dominant, weak }, VesselRemovalMode.Ordinary, false));
                Assert.IsTrue(AgencyVesselMap.IsAbsorbed(weak));
                EvaRegistrationTest.SendProto(fixture.Client, weak, Proto(weak, 5402));
                Assert.IsFalse(VesselStoreSystem.VesselExists(weak), "A docked-away vessel cannot be republished.");
                AgencyVesselMap.Load(); AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyVesselMap.IsAbsorbed(weak));
            }
        }

        [TestMethod]
        public void DeletedIdsArePrunedByAgeAndCapped()
        {
            var document = new OwnershipDocument();
            var ancient = Guid.NewGuid(); var boundary = Guid.NewGuid(); var stale = Guid.NewGuid(); var recent = Guid.NewGuid();
            document.Deleted[ancient] = 1; document.Deleted[stale] = 5000 - AgencyVesselMap.DeletedRetentionRevisions - 1;
            document.Deleted[boundary] = 5000 - AgencyVesselMap.DeletedRetentionRevisions; document.Deleted[recent] = 4999;
            AgencyVesselMap.PruneDeleted(document, 5000);
            CollectionAssert.AreEquivalent(new[] { boundary, recent }, document.Deleted.Keys.ToArray());

            var capped = new OwnershipDocument();
            var oldest = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var id in oldest) capped.Deleted[id] = 8000;
            for (var i = 0; i < VesselOwnershipPolicy.MaxRecords; i++) capped.Deleted[Guid.NewGuid()] = 9000;
            AgencyVesselMap.PruneDeleted(capped, 10000);
            Assert.AreEqual(VesselOwnershipPolicy.MaxRecords, capped.Deleted.Count);
            Assert.IsFalse(oldest.Any(capped.Deleted.ContainsKey), "The cap drops the oldest entries.");
            AgencyVesselMap.Validate(capped);
            capped.Deleted[Guid.NewGuid()] = 9000;
            Assert.ThrowsException<InvalidDataException>(() => AgencyVesselMap.Validate(capped));
        }

        [TestMethod]
        public void DeletingPrunesAncientDeletionsAndRemembersTheNewOne()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var ancient = Guid.NewGuid();
                Doc.Deleted[ancient] = 0; Doc.Revision = AgencyVesselMap.DeletedRetentionRevisions + 10;
                var id = AgencyEconomyTopologyTest.Launch(fixture, 5501, out _);
                Assert.IsTrue(AgencyVesselMap.DeleteCraft(id).Success);
                Assert.IsFalse(AgencyVesselMap.IsDeleted(ancient));
                Assert.IsTrue(AgencyVesselMap.IsDeleted(id));
                Assert.AreEqual(AgencyVesselMap.CaptureEpoch(), Doc.Deleted[id]);
            }
        }

        [TestMethod]
        public void EveryGuardedWriterRejectsADeletedId()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var agency = fixture.Client.AgencyId;
                var gone = AgencyEconomyTopologyTest.Launch(fixture, 5601, out _);
                Assert.IsTrue(AgencyVesselMap.DeleteCraft(gone).Success);
                Assert.ThrowsException<InvalidOperationException>(() => AgencyVesselMap.Set(gone, agency));
                Assert.ThrowsException<InvalidOperationException>(() => AgencyVesselMap.RegisterNew(gone, agency));
                var mutate = AgencyVesselMap.Mutate(gone, agency, true, VesselOwnershipOperation.Claim, Guid.Empty, VesselDockingPolicy.Nobody);
                Assert.IsFalse(mutate.Success); StringAssert.Contains(mutate.Reason, "deleted");

                var live = AgencyEconomyTopologyTest.Launch(fixture, 5602, out _);
                Assert.IsFalse(AgencyVesselMap.RestoreSplit(live, gone, 0, 5602), "A deleted id cannot become a split child.");
                VesselStoreSystem.CurrentVessels[gone] = new Server.System.Vessel.Classes.Vessel(Proto(gone, 5601));
                var pendingChild = Guid.NewGuid();
                Assert.IsFalse(AgencyVesselMap.RestoreSplit(gone, pendingChild, 0, 5601), "A deleted id cannot be a split parent.");
                Assert.IsFalse(AgencyVesselMap.IsPendingSplit(pendingChild));
                VesselStoreSystem.CurrentVessels.TryRemove(gone, out _);

                var child = Guid.NewGuid();
                Assert.IsTrue(AgencyVesselMap.RestoreSplit(live, child, 0, 5602));
                Doc.Deleted[child] = 1;
                Assert.IsFalse(AgencyVesselMap.ResolveSplit(child, new uint[] { 5602 }, Proto(child, 5602), Proto(live)));
                var split = AgencyVesselMap.ExportDocument(); split.Revision++;
                Assert.ThrowsException<InvalidOperationException>(() => AgencyEconomyStore.CommitSplit(split, live, child, new uint[] { 5602 }));
                Doc.Deleted.Remove(child);
                AgencyVesselMap.ClearPendingSplits();

                // A launch registration or an EVA registration for a deleted id fails before anything is written.
                var manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } } };
                var launch = Guid.NewGuid();
                var prepared = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = launch, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
                var raw = Proto(gone, 5601);
                var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
                message.VesselId = gone; message.EconomyLaunchId = launch; message.EconomyLaunchToken = prepared.LaunchToken; message.EconomyManifestIndices = new[] { 0 };
                var revision = fixture.Snapshot.Revision;
                var registered = AgencyEconomyStore.Register(fixture.Client, message, raw, new Server.System.Vessel.Classes.Vessel(raw));
                Assert.IsFalse(registered.Success); StringAssert.Contains(registered.Reason, "deleted");
                Func<Guid, Guid, EconomyResult> eva = (evaId, parentId) =>
                {
                    var evaRaw = "type = EVA\n" + Proto(evaId, 5603).Replace("name = probe", "name = kerbalEVA\ncrew = Bob");
                    var evaMessage = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
                    evaMessage.VesselId = evaId; evaMessage.EconomyParentVesselId = parentId; evaMessage.EconomyEvaCrew = "Bob";
                    return AgencyEconomyStore.RegisterEva(fixture.Client, evaMessage, evaRaw, new Server.System.Vessel.Classes.Vessel(evaRaw));
                };
                var evaResult = eva(gone, live);
                Assert.IsFalse(evaResult.Success); StringAssert.Contains(evaResult.Reason, "deleted");
                evaResult = eva(Guid.NewGuid(), gone);
                Assert.IsFalse(evaResult.Success); StringAssert.Contains(evaResult.Reason, "deleted");
                Assert.AreEqual(revision, fixture.Snapshot.Revision);

                // RevertLaunch would recreate the launch's craft.
                var reverted = AgencyEconomyTopologyTest.Launch(fixture, 5604, out var revertLaunch);
                Doc.Deleted[reverted] = 1;
                var revert = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.RevertLaunch, LaunchId = revertLaunch });
                Assert.IsFalse(revert.Success); StringAssert.Contains(revert.Reason, "deleted");
                Assert.IsTrue(VesselStoreSystem.VesselExists(reverted));
                Doc.Deleted.Remove(reverted);
            }
        }

        [TestMethod]
        public void AnInvalidOwnershipCandidateFailsBeforeTheJournalIsPersisted()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var economy = typeof(AgencyEconomyStore);
                var current = economy.GetField("_document", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                var candidate = JsonConvert.DeserializeObject<EconomyDocument>(JsonConvert.SerializeObject(current));
                var bad = new OwnershipDocument(); var id = Guid.NewGuid();
                bad.Records[id] = new VesselOwnershipRecord { VesselId = id }; bad.Deleted[id] = 1;
                candidate.Journal = new EconomyVesselJournal { OwnershipAfter = bad };
                var before = File.ReadAllBytes(AgencyEconomyStore.FilePath);
                var persist = economy.GetMethod("Persist", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(EconomyDocument) }, null);
                var error = Assert.ThrowsException<TargetInvocationException>(() => persist.Invoke(null, new object[] { candidate }));
                Assert.IsInstanceOfType(error.InnerException, typeof(InvalidDataException));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(AgencyEconomyStore.FilePath));
            }
        }

        [TestMethod]
        public void ALaunchThatWouldOverflowTheOwnershipDocumentIsRefusedWithoutBrickingRecovery()
        {
            using (var fixture = new AgencyEconomyTest.Fixture())
            {
                var manifest = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "probe", UnitCost = 100 } } };
                var launch = Guid.NewGuid();
                var prepared = fixture.Execute(new EconomyCommand { Operation = EconomyOperation.PrepareLaunch, LaunchId = launch, Manifest = manifest, ManifestHash = ToolingPolicy.ManifestHash(manifest) });
                Assert.IsTrue(prepared.Success, prepared.Reason);
                for (var i = 0; i < VesselOwnershipPolicy.MaxRecords; i++) { var key = Guid.NewGuid(); Doc.Records[key] = new VesselOwnershipRecord { VesselId = key }; }
                var id = Guid.NewGuid(); var raw = Proto(id, 5701);
                var message = new ClientMessageFactory().CreateNewMessageData<VesselProtoMsgData>();
                message.VesselId = id; message.EconomyLaunchId = launch; message.EconomyLaunchToken = prepared.LaunchToken; message.EconomyManifestIndices = new[] { 0 };
                var result = AgencyEconomyStore.Register(fixture.Client, message, raw, new Server.System.Vessel.Classes.Vessel(raw));
                Assert.IsFalse(result.Success);
                Assert.IsFalse(result.RecoveryRequired);
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.IsFalse(AgencyEconomyStore.HasPendingJournal);
                Assert.IsFalse(VesselStoreSystem.VesselExists(id));
                Assert.IsFalse(fixture.Snapshot.Vessels.Any(v => v.VesselId == id));
            }
        }
    }
}
