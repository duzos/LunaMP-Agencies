using System;
using System.IO;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    internal sealed class FakeSource : IEditorContextSource
    {
        public LeaseContext Context = AuthorityHelpers.Ctx();
        public GrantBinding Binding = AuthorityHelpers.Bind();
        public LeaseContext CurrentContext() { return Context; }
        public GrantBinding CurrentBinding() { return Binding; }
    }

    [TestClass]
    public class GrantWatcherTests
    {
        private static readonly byte[] Key = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        private string dir, grantPath, keyPath;
        private long now, stamp;
        private DateTime utc;
        private MemorySuspensionStore store;
        private ExecutionAuthority authority;
        private GrantWatcher watcher;
        private ControlPump pump;
        private FakeSource source;

        [TestInitialize]
        public void Setup()
        {
            dir = Path.Combine(Path.GetTempPath(), "ksp-watcher-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            grantPath = Path.Combine(dir, "grant.json"); keyPath = Path.Combine(dir, "grant.key");
            now = 10000; utc = AuthorityHelpers.Utc0.AddMinutes(10); store = new MemorySuspensionStore(); stamp = 0;
            File.WriteAllBytes(keyPath, Key);
            Build(store);
        }
        [TestCleanup] public void Cleanup() { try { Directory.Delete(dir, true); } catch (IOException) { } }

        private void Build(MemorySuspensionStore withStore)
        {
            authority = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, withStore, () => utc);
            source = new FakeSource();
            watcher = new GrantWatcher(authority, grantPath, keyPath, source.CurrentBinding, () => now, () => utc);
            pump = new ControlPump(authority, watcher, source);
        }
        private static GrantPayload Payload(long generation = 1, Action<GrantPayload> change = null)
        {
            var p = new GrantPayload
            {
                GrantId = "grant", Generation = generation, IssuedUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0), ExpiresUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0.AddHours(1)),
                Binding = new GrantBindingInfo { InstallId = "install", SaveFolder = "save", Agency = "agency" },
                Operations = new[] { "editor.replace_craft", "editor.restore_snapshot", "craft.write", "unknown.op" }, Facilities = new[] { "VAB" },
                UnsavedCraftPolicy = "refuse", MaxParts = 250, SpendLimitFunds = 0, Revoked = false
            };
            if (change != null) change(p);
            return p;
        }
        private void Write(GrantPayload payload) { WriteText(GrantCodec.Encode(payload, Key)); }
        private void WriteText(string text)
        { File.WriteAllText(grantPath, text); File.SetLastWriteTimeUtc(grantPath, AuthorityHelpers.Utc0.AddSeconds(++stamp)); }
        private void Frame(long advance = 1000) { now += advance; pump.Update(); }
        private GrantStatusInfo Status => authority.Status().Grant;

        [TestMethod] public void MissingFileIsMissingAndNothingIsProvisioned()
        {
            Frame(); Assert.AreEqual(GrantStates.Missing, Status.State); Assert.AreEqual("grant_file_missing", Status.Detail); Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void ValidFileProvisionsAndPublishesStatusWithoutSecrets()
        {
            Write(Payload()); Frame();
            Assert.AreEqual(GrantStates.Valid, Status.State); Assert.AreEqual("grant", authority.CurrentGrantId); Assert.AreEqual(1, authority.CurrentGrantGeneration);
            CollectionAssert.AreEqual(new[] { "VAB" }, Status.Facilities); Assert.AreEqual("refuse", Status.UnsavedCraftPolicy);
            Assert.IsNotNull(authority.AcquireLease(30000, "from watcher grant"));
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(authority.Status());
            StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("payload|\"mac\"|\"key\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }
        [TestMethod] public void UnknownOperationsAreListedButGrantNoPermission()
        {
            var grant = GrantMapping.ToGrant(Payload());
            Assert.IsTrue(grant.Allows(new ClassifiedEffect("editor.replace_craft", "editor:VAB", 1)));
            Assert.IsTrue(grant.Allows(new ClassifiedEffect("craft.write", "ships:VAB", 1)));
            Assert.IsFalse(grant.Allows(new ClassifiedEffect("craft.write", "editor:VAB", 1)));
            Assert.IsFalse(grant.Allows(new ClassifiedEffect("unknown.op", "editor:VAB", 1)));
            Assert.IsTrue(grant.AllowsEntity("editor:VAB")); Assert.IsFalse(grant.AllowsEntity("editor:SPH"));
        }
        [TestMethod] public void PollsAtOneHertzAndReverifiesOnlyOnStatChange()
        {
            Write(Payload()); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State);
            Write(Payload(2, p => p.Revoked = true));
            now += 500; pump.Update(); Assert.AreEqual(GrantStates.Valid, Status.State, "inside the 1 s window nothing is re-read");
            now += 500; pump.Update(); Assert.AreEqual(GrantStates.Revoked, Status.State);
        }
        [TestMethod] public void UnchangedStatKeepsTheCachedVerdictUntilTheFileChanges()
        {
            Write(Payload()); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State);
            var stampBefore = File.GetLastWriteTimeUtc(grantPath);
            var tampered = File.ReadAllText(grantPath).Replace("\"payload\":\"e", "\"payload\":\"f"); // same length
            File.WriteAllText(grantPath, tampered); File.SetLastWriteTimeUtc(grantPath, stampBefore);
            Frame(); Assert.AreEqual(GrantStates.Valid, Status.State, "same size and timestamp: no re-read");
            File.SetLastWriteTimeUtc(grantPath, stampBefore.AddSeconds(5));
            Frame(); Assert.AreEqual(GrantStates.InvalidMac, Status.State, "a stat change forces a full re-verify");
            Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void TamperedFileDropsTheGrantAndTheLease()
        {
            Write(Payload()); Frame(); var lease = authority.AcquireLease(30000, "x");
            var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(grantPath));
            var bytes = Convert.FromBase64String((string)json["payload"]); bytes[20] ^= 1; json["payload"] = Convert.ToBase64String(bytes); WriteText(json.ToString());
            Frame(); Assert.AreEqual(GrantStates.InvalidMac, Status.State); Assert.IsNull(authority.CurrentGrantId); Assert.IsFalse(authority.Heartbeat(lease));
        }
        [TestMethod] public void GarbageOversizeAndMissingKeyAreHandled()
        {
            WriteText("this is not json"); Frame(); Assert.AreEqual(GrantStates.Malformed, Status.State);
            WriteText(new string('x', 70000)); Frame(); Assert.AreEqual(GrantStates.Malformed, Status.State); Assert.AreEqual("envelope_size", Status.Detail);
            Write(Payload()); File.WriteAllBytes(keyPath, new byte[31]); Frame(); Assert.AreEqual(GrantStates.Missing, Status.State); Assert.AreEqual("key_unavailable", Status.Detail);
            File.WriteAllBytes(keyPath, Key); File.SetLastWriteTimeUtc(keyPath, AuthorityHelpers.Utc0.AddSeconds(500)); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State);
            File.Delete(keyPath); Frame(); Assert.AreEqual(GrantStates.Missing, Status.State); Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void RevokedFileDropsLeaseAndGrantAndHigherRearmRestoresIt()
        {
            Write(Payload()); Frame(); var lease = authority.AcquireLease(30000, "x");
            Write(Payload(2, p => p.Revoked = true)); Frame();
            Assert.AreEqual(GrantStates.Revoked, Status.State); Assert.IsNull(authority.CurrentGrantId); Assert.IsFalse(authority.Heartbeat(lease));
            Write(Payload(3)); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State); Assert.AreEqual(3, authority.CurrentGrantGeneration);
        }
        [TestMethod] public void StopSuspendsTheGenerationAcrossRestartAndRearmClearsIt()
        {
            Write(Payload()); Frame(); authority.Stop(); Frame();
            Assert.AreEqual(GrantStates.Suspended, Status.State); Assert.IsNull(authority.CurrentGrantId);
            Frame(); Assert.AreEqual(GrantStates.Suspended, Status.State, "the unchanged file never revives a stopped generation");
            // KSP restart: new authority and watcher over the same persisted store
            Build(store); Frame(); Assert.AreEqual(GrantStates.Suspended, Status.State); Assert.IsNull(authority.CurrentGrantId);
            Write(Payload(2)); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State); Assert.AreEqual(2, authority.CurrentGrantGeneration);
        }
        [TestMethod] public void OlderGenerationReplayedAfterANewerOneIsRefused()
        {
            Write(Payload(5)); Frame(); Write(Payload(4)); Frame();
            Assert.AreEqual(GrantStates.Revoked, Status.State); Assert.AreEqual("generation_regressed", Status.Detail); Assert.IsNull(authority.CurrentGrantId);
            Write(Payload(5)); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State, "the same generation is fine again");
        }
        [TestMethod] public void ReplayAfterRevokeIsRefused()
        {
            Write(Payload(1)); Frame(); Write(Payload(2, p => p.Revoked = true)); Frame();
            Write(Payload(1)); Frame(); Assert.AreEqual(GrantStates.Revoked, Status.State); Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void ExpiredFileIsExpiredAndNotProvisioned()
        {
            utc = AuthorityHelpers.Utc0.AddHours(2); Write(Payload()); Frame();
            Assert.AreEqual(GrantStates.Expired, Status.State); Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void GrantExpiryWhileProvisionedDropsItOnTheNextFrames()
        {
            Write(Payload()); Frame(); Assert.IsNotNull(authority.CurrentGrantId);
            utc = AuthorityHelpers.Utc0.AddHours(1).AddSeconds(1); Frame(); Frame();
            Assert.IsNull(authority.CurrentGrantId); Assert.AreEqual(GrantStates.Expired, Status.State);
        }
        [TestMethod] public void BindingMismatchIsReportedAndBindingChangeDropsThenReprovisionsTheSameGeneration()
        {
            source.Binding = AuthorityHelpers.Bind(agency: "someone-else"); Write(Payload()); Frame();
            Assert.AreEqual(GrantStates.BindingMismatch, Status.State); Assert.IsNull(authority.CurrentGrantId);
            source.Binding = AuthorityHelpers.Bind(); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State); Assert.AreEqual(1, authority.CurrentGrantGeneration);
            var lease = authority.AcquireLease(30000, "x");
            source.Binding = AuthorityHelpers.Bind(save: "other-save"); pump.Update();
            Assert.IsNull(authority.CurrentGrantId, "UpdateContext drops on the very next frame, before the watcher polls"); Assert.IsFalse(authority.Heartbeat(lease));
            Frame(); Assert.AreEqual(GrantStates.BindingMismatch, Status.State);
            source.Binding = AuthorityHelpers.Bind(); Frame();
            Assert.AreEqual(GrantStates.Valid, Status.State); Assert.AreEqual(1, authority.CurrentGrantGeneration); Assert.IsFalse(authority.IsBurned("grant", 1));
        }
        [TestMethod] public void NoSaveLoadedMeansNoBindingAndNoGrant()
        {
            Write(Payload()); Frame(); Assert.IsNotNull(authority.CurrentGrantId);
            source.Binding = null; Frame(); Assert.IsNull(authority.CurrentGrantId); Assert.AreEqual(GrantStates.BindingMismatch, Status.State);
        }
        [TestMethod] public void SentinelAgencyAppliesOnlyWhileOffline()
        {
            Write(Payload(1, p => p.Binding.Agency = GrantBindingKey.AgencyKey(Guid.Empty, "save")));
            source.Binding = AuthorityHelpers.Bind(agency: GrantBindingKey.AgencyKey(Guid.Empty, "save")); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State);
            source.Binding = AuthorityHelpers.Bind(agency: GrantBindingKey.AgencyKey(Guid.NewGuid(), "save")); Frame(); Assert.AreEqual(GrantStates.BindingMismatch, Status.State); Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void SceneChangeKeepsTheGrantAndDropsTheLease()
        {
            Write(Payload()); Frame(); var lease = authority.AcquireLease(30000, "x");
            source.Context = AuthorityHelpers.Ctx("epoch2", "scene:SPACECENTER", ready: false); pump.Update();
            Assert.AreEqual("grant", authority.CurrentGrantId); Assert.IsFalse(authority.Heartbeat(lease));
            source.Context = AuthorityHelpers.Ctx("epoch3", "editor:VAB"); pump.Update();
            Assert.IsNotNull(authority.AcquireLease(30000, "new scene"));
        }
        [TestMethod] public void OlderGenerationStaysRefusedAfterStopAndRestart()
        {
            Write(Payload(1)); Frame(); Write(Payload(2)); Frame(); authority.Stop(); Frame();
            Build(store); Write(Payload(1)); Frame(); // an unexpired older envelope reappears after the restart
            Assert.AreEqual(GrantStates.Revoked, Status.State); Assert.AreEqual("generation_regressed", Status.Detail); Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void OlderGenerationStaysRefusedAfterRevokeAndRestart()
        {
            Write(Payload(2)); Frame(); Write(Payload(3, p => p.Revoked = true)); Frame();
            Build(store); Write(Payload(2)); Frame();
            Assert.AreEqual(GrantStates.Revoked, Status.State); Assert.IsNull(authority.CurrentGrantId);
            Write(Payload(4)); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State);
        }
        [TestMethod] public void StopAfterABindingDropStillBurnsTheWatchersLastVerifiedGeneration()
        {
            Write(Payload()); Frame(); source.Binding = AuthorityHelpers.Bind(agency: "elsewhere"); Frame();
            Assert.IsNull(authority.CurrentGrantId); authority.Stop();
            source.Binding = AuthorityHelpers.Bind(); Frame(); Assert.AreEqual(GrantStates.Suspended, Status.State);
        }
        [TestMethod] public void PumpIsolatesEveryStepSoTickAlwaysRuns()
        {
            Write(Payload()); Frame(); var lease = authority.AcquireLease(30000, "x");
            var throwing = new ThrowingSource(); var brokenPump = new ControlPump(authority, watcher, throwing);
            now += 2500; brokenPump.Update(); // watchdog deadline passed; both inputs throw
            Assert.IsFalse(authority.Heartbeat(lease), "Tick ran despite the throwing source");
        }
        [TestMethod] public void InvalidBindingYieldsBindingMismatchNotASkippedPoll()
        {
            Write(Payload()); Frame(); Assert.AreEqual(GrantStates.Valid, Status.State);
            var pump2 = new ControlPump(authority, new GrantWatcher(authority, grantPath, keyPath, () => { throw new ArgumentException("invalid_identifier"); }, () => now, () => utc), new ThrowingSource { ContextOk = true });
            now += 1000; pump2.Update();
            Assert.AreEqual(GrantStates.BindingMismatch, Status.State); Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void OversizedKeyFileIsRejectedWithoutReadingIt()
        {
            Write(Payload()); File.WriteAllBytes(keyPath, new byte[5000]); Frame();
            Assert.AreEqual(GrantStates.Missing, Status.State); Assert.AreEqual("key_unavailable", Status.Detail);
        }
        [TestMethod] public void UnreadableSuspensionStoreFailsClosedAsSuspended()
        {
            var broken = new MemorySuspensionStore { FailLoad = true }; Build(broken); Write(Payload()); Frame();
            Assert.AreEqual(GrantStates.Suspended, Status.State); Assert.IsNull(authority.CurrentGrantId);
        }
        [TestMethod] public void OldSuspensionsArePrunedWhenAHigherGenerationIsProvisioned()
        {
            Write(Payload()); Frame(); authority.Stop(); Assert.AreEqual(1, store.Load().Count(s => s.Reason != "seen"));
            utc = AuthorityHelpers.Utc0.AddDays(45).AddMinutes(10); Write(Payload(2, p => { p.IssuedUtc = GrantPayload.FormatUtc(utc); p.ExpiresUtc = GrantPayload.FormatUtc(utc.AddHours(1)); })); Frame();
            Assert.AreEqual(GrantStates.Valid, Status.State); Assert.AreEqual(0, store.Load().Count(s => s.Reason != "seen"));
        }
    }

    internal sealed class ThrowingSource : IEditorContextSource
    {
        public bool ContextOk;
        public LeaseContext CurrentContext() { if (ContextOk) return AuthorityHelpers.Ctx(); throw new InvalidOperationException("scene teardown"); }
        public GrantBinding CurrentBinding() { throw new ArgumentException("invalid_identifier"); }
    }

    [TestClass]
    public class SuspensionStoreTests
    {
        private string dir, path;
        [TestInitialize] public void Setup() { dir = Path.Combine(Path.GetTempPath(), "ksp-suspension-tests", Guid.NewGuid().ToString("N")); path = Path.Combine(dir, "nested", "suspensions.json"); }
        [TestCleanup] public void Cleanup() { try { Directory.Delete(dir, true); } catch (IOException) { } }

        [TestMethod] public void MissingFileIsEmptyAndRoundTripCreatesDirectories()
        {
            var store = new FileSuspensionStore(path); Assert.AreEqual(0, store.Load().Count);
            store.Save(new[] { new Suspension { GrantId = "g", Generation = 3, Reason = "stop", Utc = "2026-01-01T00:00:00.000Z" } });
            var back = new FileSuspensionStore(path).Load(); Assert.AreEqual(1, back.Count); Assert.AreEqual("g", back[0].GrantId); Assert.AreEqual(3L, back[0].Generation); Assert.AreEqual("stop", back[0].Reason);
        }
        [TestMethod] public void WritesAreAtomicAndLeaveNoTemporaryFiles()
        {
            var store = new FileSuspensionStore(path);
            for (var i = 1; i <= 5; i++) store.Save(new[] { new Suspension { GrantId = "g", Generation = i, Reason = "fault", Utc = "2026-01-01T00:00:00.000Z" } });
            Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)).Length); Assert.AreEqual(5L, store.Load()[0].Generation);
        }
        [TestMethod] public void CorruptOrInvalidFilesThrowInvalidData()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); var store = new FileSuspensionStore(path);
            foreach (var text in new[] { "not json", "{}", "null", "[{\"grantId\":\"\",\"generation\":1}]", "[{\"grantId\":\"g\",\"generation\":0}]", "[null]" })
            { File.WriteAllText(path, text); Assert.ThrowsException<InvalidDataException>(() => store.Load(), text); }
            File.WriteAllBytes(path, new byte[300000]); Assert.ThrowsException<InvalidDataException>(() => store.Load());
        }
        [TestMethod] public void FileStoreBacksAnAuthorityAcrossRestart()
        {
            long now = 5000; var utc = AuthorityHelpers.Utc0;
            var first = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, new FileSuspensionStore(path), () => utc);
            first.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind()); first.ProvisionGrant(AuthorityHelpers.Grant()); first.Stop();
            var second = new ExecutionAuthority(() => now, GrantMapping.KnownEffects, 2000, new FileSuspensionStore(path), () => utc);
            second.UpdateContext(AuthorityHelpers.Ctx(), AuthorityHelpers.Bind());
            Assert.ThrowsException<InvalidOperationException>(() => second.ProvisionGrant(AuthorityHelpers.Grant()));
            second.ProvisionGrant(AuthorityHelpers.Grant(2));
        }
    }
}
