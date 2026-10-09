using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LmpCommon;
using LmpCommon.Agency;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Agency;
using Server.Context;
using Server.System;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class AgencyIdentityTest
    {
        private static AgencySetIdentityMsgData Command(AgencyEconomyTest.Fixture f) => new LmpCommon.Message.ClientMessageFactory().CreateNewMessageData<AgencySetIdentityMsgData>().Configure(f.Client.AgencyId);

        [TestMethod]
        public void OwnerMutationPersistsAndSurvivesReloadWithRevision()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                f.Client.AgencyIdentityProtocol = 1;
                var result = AgencyIdentitySystem.Set(f.Client, Command(f)); Assert.IsTrue(result.Success, result.Message);
                var id = f.Client.AgencyId; AgencyStore.Agencies.Clear(); AgencyStore.LoadExistingAgencies();
                var identity = AgencyStore.Agencies[id].ToIdentity();
                Assert.AreEqual(1L, identity.Revision); Assert.IsTrue(identity.HasColour); Assert.AreEqual((byte)42, identity.Red);
                Assert.AreEqual(AgencyIdentityDefaults.DefaultFlagUrl, identity.FlagUrl);
                Assert.IsTrue(f.Client.SendMessageQueue.Any(m => m.Data is AgencyIdentityUpsertMsgData));
            }
        }

        [TestMethod]
        public void RejectsUnauthorizedStaleAndUnsupportedActorsWithoutMutation()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                var a = AgencyStore.Agencies[f.Client.AgencyId]; var cmd = Command(f);
                Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, cmd).Success);
                f.Client.AgencyIdentityProtocol = 2; Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, cmd).Success);
                f.Client.AgencyIdentityProtocol = 1; f.Client.Authenticated = false; Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, cmd).Success);
                f.Client.Authenticated = true; a.OwnerUniqueId = "new-owner"; Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, cmd).Success);
                a.OwnerUniqueId = f.Client.UniqueIdentifier; a.Members.Clear(); Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, cmd).Success);
                a.Members.Add(new Server.Agency.Agency.Member { UniqueId = f.Client.UniqueIdentifier });
                f.Client.AgencyId = Guid.NewGuid(); Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, cmd).Success);
                f.Client.AgencyId = a.Id; cmd.ExpectedRevision = 2; Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, cmd).Success);
                Assert.AreEqual(0L, a.IdentityRevision); Assert.IsFalse(a.HasColour);
            }
        }

        [TestMethod]
        public void PersistenceFailureRollsBackAndPublishesNothing()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                f.Client.AgencyIdentityProtocol = 1; var a = AgencyStore.Agencies[f.Client.AgencyId]; AgencyStore.PersistAgency(a);
                var before = File.ReadAllBytes(AgencyStore.MetaPath(a.Id));
                try
                {
                    AgencyIdentitySystem.PersistenceCheckpoint = _ => throw new IOException("test");
                    Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, Command(f)).Success);
                    Assert.AreEqual(0L, a.IdentityRevision); Assert.IsFalse(a.HasColour);
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(AgencyStore.MetaPath(a.Id)));
                    Assert.IsFalse(f.Client.SendMessageQueue.Any(m => m.Data is AgencyIdentityUpsertMsgData));
                }
                finally { AgencyIdentitySystem.PersistenceCheckpoint = null; }
            }
        }

        [TestMethod]
        public void OnlyExactCapabilityReceivesIdentitySnapshots()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                foreach (var capability in new[] { 0, 2 })
                { f.Client.AgencyIdentityProtocol = capability; AgencyIdentitySystem.SendSnapshot(f.Client); }
                Assert.IsFalse(f.Client.SendMessageQueue.Any(m => m.Data is AgencyIdentitySnapshotMsgData));
                f.Client.AgencyIdentityProtocol = 1; AgencyIdentitySystem.SendSnapshot(f.Client);
                Assert.AreEqual(1, f.Client.SendMessageQueue.Count(m => m.Data is AgencyIdentitySnapshotMsgData));
            }
        }

        [TestMethod]
        public void SyncedFlagsRequireMatchingHashAndRejectAmbiguousDuplicateUrls()
        {
            using (var scope = new AgencyTestScope())
            {
                var bytes = new byte[] { 1, 2, 3 }; var hash = Common.CalculateSha256Hash(bytes).Replace("-", "");
                var folder = Path.Combine(FlagSystem.FlagPath, "one"); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "Custom$Flags$test.png"), bytes);
                Assert.IsTrue(AgencyIdentitySystem.ValidateFlag("Custom/Flags/test", hash));
                Assert.IsFalse(AgencyIdentitySystem.ValidateFlag("Custom/Flags/test", new string('0', 64)));
                Assert.IsFalse(AgencyIdentitySystem.ValidateFlag("../test", hash));
                folder = Path.Combine(FlagSystem.FlagPath, "two"); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "Custom$Flags$test.png"), new byte[] { 4 });
                Assert.IsFalse(AgencyIdentitySystem.ValidateFlag("Custom/Flags/test", hash));
            }
        }

        [TestMethod]
        public void UnuploadedModFlagsAreAcceptedByReferenceUnlessASyncedFileHasThatName()
        {
            using (var scope = new AgencyTestScope())
            {
                // No Flags directory at all yet.
                Assert.IsTrue(AgencyIdentitySystem.ValidateFlag("FlagPack/Flags/Kerbin flag (blue)", string.Empty));
                var folder = Path.Combine(FlagSystem.FlagPath, "one"); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "Custom$Flags$test.png"), new byte[] { 1 });
                Assert.IsTrue(AgencyIdentitySystem.ValidateFlag("SCANsat/Flags/SCANsat_Flag", string.Empty));
                Assert.IsTrue(AgencyIdentitySystem.ValidateFlag("Squad/Agencies/R&D", null));
                Assert.IsFalse(AgencyIdentitySystem.ValidateFlag("Custom/Flags/test", string.Empty));
                Assert.IsFalse(AgencyIdentitySystem.ValidateFlag("../x", string.Empty));
                Assert.IsFalse(AgencyIdentitySystem.ValidateFlag("FlagPack/Flags/x.png", string.Empty));
            }
        }

        [TestMethod]
        public void ModFlagReferenceWithSpacesAndPunctuationSurvivesReload()
        {
            const string url = "FlagPack/Flags/Krikler7's UK flag (fixed for Wales), v1.2 & co.";
            using (var f = new AgencyEconomyTest.Fixture())
            {
                f.Client.AgencyIdentityProtocol = 1;
                var cmd = Command(f); cmd.FlagUrl = url; cmd.FlagSha256 = string.Empty;
                var result = AgencyIdentitySystem.Set(f.Client, cmd); Assert.IsTrue(result.Success, result.Message);
                var id = f.Client.AgencyId; AgencyStore.Agencies.Clear(); AgencyStore.LoadExistingAgencies();
                Assert.AreEqual(url, AgencyStore.Agencies[id].ToIdentity().FlagUrl);
            }
        }

        [TestMethod]
        public void LegacyMetadataHasDefaultIdentity()
        {
            using (var scope = new AgencyTestScope())
            {
                var id = Guid.NewGuid(); Directory.CreateDirectory(AgencyStore.AgencyDirectory(id));
                File.WriteAllText(AgencyStore.MetaPath(id), "id = " + id.ToString("N") + "\nname = Old\n");
                AgencyStore.LoadExistingAgencies(); var identity = AgencyStore.Agencies[id].ToIdentity();
                Assert.AreEqual(0L, identity.Revision); Assert.IsFalse(identity.HasColour); Assert.AreEqual(AgencyIdentityDefaults.DefaultFlagUrl, identity.FlagUrl);
            }
        }

        [TestMethod]
        public void RemovedOrDisconnectedClientCannotCustomize()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                f.Client.AgencyIdentityProtocol = 1;
                f.Client.ConnectionStatus = LmpCommon.Enums.ConnectionStatus.Disconnected;
                Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, Command(f)).Success);
                f.Client.ConnectionStatus = LmpCommon.Enums.ConnectionStatus.Connected;
                ServerContext.Clients.TryRemove(f.Client.Endpoint, out _);
                Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, Command(f)).Success);
            }
        }

        [TestMethod]
        public void IdentitySaveSerializesWithRemovalAndCannotResurrectAgency()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            using (var captured = new ManualResetEventSlim())
            using (var resume = new ManualResetEventSlim())
            using (var attempted = new ManualResetEventSlim())
            {
                f.Client.AgencyIdentityProtocol = 1; var id = f.Client.AgencyId;
                Task save = null, remove = null; bool removalCouldOvertake = true;
                try
                {
                    AgencyIdentitySystem.PersistenceCheckpoint = _ => { captured.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
                    save = Task.Run(() => Assert.IsTrue(AgencyIdentitySystem.Set(f.Client, Command(f)).Success));
                    Assert.IsTrue(captured.Wait(TimeSpan.FromSeconds(10)));
                    remove = Task.Run(() =>
                    {
                        removalCouldOvertake = Monitor.TryEnter(AgencyLaunchSiteStore.MutationLock);
                        if (removalCouldOvertake) Monitor.Exit(AgencyLaunchSiteStore.MutationLock);
                        attempted.Set();
                        Assert.IsTrue(AgencyLaunchSiteStore.RemoveAgencyAndAssignments(id).Success);
                        AgencyStore.DeleteAgencyFiles(id);
                    });
                    Assert.IsTrue(attempted.Wait(TimeSpan.FromSeconds(10))); Assert.IsFalse(removalCouldOvertake);
                    resume.Set(); Assert.IsTrue(Task.WaitAll(new[] { save, remove }, TimeSpan.FromSeconds(10)));
                    Assert.IsFalse(AgencyIdentitySystem.Set(f.Client, Command(f)).Success);
                    Assert.IsFalse(File.Exists(AgencyStore.MetaPath(id)));
                    AgencyStore.LoadExistingAgencies(); Assert.IsFalse(AgencyStore.Agencies.ContainsKey(id));
                }
                finally
                {
                    resume.Set();
                    try { Task.WaitAll(new[] { save, remove }.Where(t => t != null).ToArray(), TimeSpan.FromSeconds(10)); }
                    finally { AgencyIdentitySystem.PersistenceCheckpoint = null; }
                }
            }
        }
    }
    internal static class IdentityTestCommand
    {
        internal static AgencySetIdentityMsgData Configure(this AgencySetIdentityMsgData data, Guid id)
        { data.AgencyId = id; data.FlagUrl = AgencyIdentityDefaults.DefaultFlagUrl; data.HasColour = true; data.Red = 42; return data; }
    }
}
