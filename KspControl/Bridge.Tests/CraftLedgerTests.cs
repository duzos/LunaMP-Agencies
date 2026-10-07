using System;
using System.Collections.Generic;
using System.IO;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    /// <summary>The ownership ledger: which ship files KspControl wrote, and with what hash. Pure over IOperationFiles.</summary>
    [TestClass]
    public class CraftLedgerTests
    {
        private const string LedgerFile = "C:\\ksp\\KspControlData\\Save\\ledger.json";
        private static readonly string HashA = new string('a', 64), HashB = new string('b', 64);
        private MemoryFiles files;

        [TestInitialize] public void Setup() { files = new MemoryFiles(); }

        private static LedgerEntry Entry(string name = "Probe.craft", string hash = null, string facility = "VAB", string request = "request-0001")
        { return new LedgerEntry { Facility = facility, FileName = name, Sha256 = hash ?? HashA, RequestId = request, SavedUtc = "2026-01-01T00:00:00.000Z" }; }

        private static string Json(string facility, string name, string sha, string request = "request-0001")
        { return "{\"facility\":\"" + facility + "\",\"fileName\":\"" + name + "\",\"sha256\":\"" + sha + "\",\"requestId\":\"" + request + "\",\"savedUtc\":\"x\"}"; }

        [TestMethod] public void AMissingLedgerIsEmptyAndHealthy()
        {
            var ledger = CraftLedger.Load(files, LedgerFile);
            Assert.AreEqual(LedgerState.Missing, ledger.State); Assert.IsTrue(ledger.Usable); Assert.IsNull(ledger.Find("VAB", "Probe.craft")); Assert.AreEqual(0, ledger.Count);
        }

        [TestMethod] public void ARecordedEntryRoundTripsThroughTheFile()
        {
            var ledger = CraftLedger.Load(files, LedgerFile);
            ledger.Record(Entry());
            ledger.Save(files, LedgerFile);
            var back = CraftLedger.Load(files, LedgerFile);
            Assert.AreEqual(LedgerState.Ok, back.State);
            var found = back.Find("VAB", "Probe.craft");
            Assert.IsNotNull(found); Assert.AreEqual(HashA, found.Sha256); Assert.AreEqual("request-0001", found.RequestId); Assert.AreEqual("2026-01-01T00:00:00.000Z", found.SavedUtc);
        }

        [TestMethod] public void LookupIsCaseInsensitiveBecauseTheShipsFolderIsOnWindowsAndPerFacility()
        {
            var ledger = CraftLedger.Load(files, LedgerFile); ledger.Record(Entry("Probe.craft"));
            Assert.IsNotNull(ledger.Find("VAB", "PROBE.CRAFT"));
            Assert.IsNull(ledger.Find("SPH", "Probe.craft"));
            Assert.IsNull(ledger.Find("VAB", "Other.craft"));
        }

        [TestMethod] public void RecordingTheSameFileAgainReplacesTheEntry()
        {
            var ledger = CraftLedger.Load(files, LedgerFile);
            ledger.Record(Entry(hash: HashA)); ledger.Record(Entry("PROBE.craft", HashB, request: "request-0002"));
            Assert.AreEqual(1, ledger.Count);
            Assert.AreEqual(HashB, ledger.Find("VAB", "Probe.craft").Sha256);
        }

        [TestMethod] public void PruningDropsEntriesWhoseFileIsGone()
        {
            var ledger = CraftLedger.Load(files, LedgerFile);
            ledger.Record(Entry("Keep.craft")); ledger.Record(Entry("Gone.craft"));
            var dropped = ledger.Prune(e => e.FileName == "Keep.craft");
            Assert.AreEqual(1, dropped); Assert.IsNotNull(ledger.Find("VAB", "Keep.craft")); Assert.IsNull(ledger.Find("VAB", "Gone.craft"));
        }

        [DataTestMethod]
        [DataRow("not json")] [DataRow("")] [DataRow("[]")] [DataRow("{}")] [DataRow("{\"version\":2,\"entries\":[]}")] [DataRow("{\"version\":1}")] [DataRow("{\"version\":1,\"entries\":{}}")]
        [DataRow("{\"version\":1,\"entries\":[5]}")] [DataRow("{\"version\":1,\"entries\":[{\"facility\":\"VAB\"}]}")]
        public void AnyUnreadableOrMalformedLedgerIsCorruptAndNeverGuessedAt(string text)
        {
            files.Put(LedgerFile, text);
            var ledger = CraftLedger.Load(files, LedgerFile);
            Assert.AreEqual(LedgerState.Corrupt, ledger.State); Assert.IsFalse(ledger.Usable);
            Assert.IsNull(ledger.Find("VAB", "a.craft"), "a corrupt ledger owns nothing");
        }

        [TestMethod] public void EntriesWithBadFieldsMakeTheLedgerCorrupt()
        {
            foreach (var entry in new[] { Json("LAUNCHPAD", "a.craft", HashA), Json("VAB", "../a.craft", HashA), Json("VAB", "a.craft", "SHORT"), Json("VAB", "a.txt", HashA), Json("VAB", "a.craft", HashA, "x") })
            {
                files.Put(LedgerFile, "{\"version\":1,\"entries\":[" + entry + "]}");
                Assert.AreEqual(LedgerState.Corrupt, CraftLedger.Load(files, LedgerFile).State, entry);
            }
        }

        [TestMethod] public void ADuplicateEntryMakesTheLedgerCorrupt()
        {
            files.Put(LedgerFile, "{\"version\":1,\"entries\":[" + Json("VAB", "a.craft", HashA) + "," + Json("VAB", "A.CRAFT", HashA) + "]}");
            Assert.AreEqual(LedgerState.Corrupt, CraftLedger.Load(files, LedgerFile).State);
        }

        [TestMethod] public void AnOversizeLedgerFileIsCorruptWithoutBeingParsed()
        {
            files.Put(LedgerFile, new string(' ', CraftLedger.MaxFileBytes + 1));
            Assert.AreEqual(LedgerState.Corrupt, CraftLedger.Load(files, LedgerFile).State);
        }

        [TestMethod] public void AnUnreadableFileIsCorruptNotMissing()
        {
            Assert.AreEqual(LedgerState.Corrupt, CraftLedger.Load(new DenyingReadFiles(), LedgerFile).State);
        }

        [TestMethod] public void AFullLedgerRefusesAnotherEntryInsteadOfLosingOne()
        {
            var ledger = CraftLedger.Load(files, LedgerFile);
            for (var i = 0; i < CraftLedger.MaxEntries; i++) Assert.IsTrue(ledger.Record(Entry("F" + i + ".craft")));
            Assert.IsFalse(ledger.Record(Entry("OneTooMany.craft")));
            Assert.IsTrue(ledger.Record(Entry("F0.craft", HashB)), "replacing an existing entry is always fine");
        }

        [TestMethod] public void SavingIsAnAtomicWriteOfStableText()
        {
            var ledger = CraftLedger.Load(files, LedgerFile); ledger.Record(Entry("B.craft")); ledger.Record(Entry("A.craft"));
            ledger.Save(files, LedgerFile);
            var first = files.Text(LedgerFile);
            ledger.Save(files, LedgerFile);
            Assert.AreEqual(first, files.Text(LedgerFile), "entries are ordered, so the same ledger prints the same bytes");
            Assert.IsTrue(first.IndexOf("A.craft", StringComparison.Ordinal) < first.IndexOf("B.craft", StringComparison.Ordinal));
            CollectionAssert.AreEqual(new[] { LedgerFile, LedgerFile }, files.Writes);
        }

        private sealed class DenyingReadFiles : IOperationFiles
        {
            public bool Exists(string path) { return true; }
            public byte[] ReadAllBytes(string path) { throw new IOException("denied"); }
            public void WriteAtomic(string path, byte[] bytes) { }
            public void CreateNew(string path, byte[] bytes) { }
            public void ReplaceExisting(string path, byte[] bytes) { }
            public void Delete(string path) { }
            public void Copy(string from, string to, bool overwrite) { }
            public IReadOnlyList<FileEntry> List(string directory, string suffix) { return new FileEntry[0]; }
            public bool IsReparsePoint(string path) { return false; }
        }
    }

    /// <summary>The overwrite decision: a human file is never replaced, and only a ledger-owned file whose hash still matches can be.</summary>
    [TestClass]
    public class SavePolicyTests
    {
        private static readonly string HashA = new string('a', 64), HashB = new string('b', 64);

        private static LedgerEntry Owned(string hash) { return new LedgerEntry { Facility = "VAB", FileName = "Probe.craft", Sha256 = hash, RequestId = "request-0001", SavedUtc = "x" }; }

        [TestMethod] public void ANewFileIsCreated()
        {
            var d = SavePolicy.Decide(false, null, null, null, LedgerState.Missing);
            Assert.AreEqual(SaveAction.Create, d.Action); Assert.IsNull(d.Reason);
        }

        [TestMethod] public void AnExistingFileWithoutAReplaceHashIsRefused()
        {
            var d = SavePolicy.Decide(true, HashA, null, Owned(HashA), LedgerState.Ok);
            Assert.AreEqual(SaveAction.Refuse, d.Action); Assert.AreEqual("file_exists", d.Reason);
        }

        [TestMethod] public void AHumanFileIsNeverReplacedEvenWithTheRightHash()
        {
            var d = SavePolicy.Decide(true, HashA, HashA, null, LedgerState.Ok);
            Assert.AreEqual(SaveAction.Refuse, d.Action); Assert.AreEqual("file_not_kspcontrol_owned", d.Reason);
            d = SavePolicy.Decide(true, HashA, HashA, null, LedgerState.Missing);
            Assert.AreEqual("file_not_kspcontrol_owned", d.Reason, "no ledger at all means we own nothing");
        }

        [TestMethod] public void AnOwnedFileWhoseHashIsNotTheOneTheCallerSawHasChanged()
        {
            var d = SavePolicy.Decide(true, HashA, HashB, Owned(HashA), LedgerState.Ok);
            Assert.AreEqual(SaveAction.Refuse, d.Action); Assert.AreEqual("file_changed", d.Reason);
        }

        [TestMethod] public void AnOwnedFileEditedSinceWeWroteItIsNoLongerOursToReplace()
        {
            var d = SavePolicy.Decide(true, HashB, HashB, Owned(HashA), LedgerState.Ok);
            Assert.AreEqual(SaveAction.Refuse, d.Action); Assert.AreEqual("file_changed", d.Reason);
            StringAssert.Contains(d.Detail, "since KspControl wrote it");
        }

        [TestMethod] public void AnOwnedUnchangedFileWithTheMatchingHashIsReplaced()
        {
            var d = SavePolicy.Decide(true, HashA, HashA, Owned(HashA), LedgerState.Ok);
            Assert.AreEqual(SaveAction.Replace, d.Action); Assert.IsNull(d.Reason); Assert.AreEqual(HashA, d.ReplacedSha256);
        }

        [TestMethod] public void AReplaceHashForAFileThatIsNotThereIsAChangedFile()
        {
            var d = SavePolicy.Decide(false, null, HashA, null, LedgerState.Missing);
            Assert.AreEqual(SaveAction.Refuse, d.Action); Assert.AreEqual("file_changed", d.Reason);
        }

        [TestMethod] public void ACorruptLedgerRefusesEveryCaseBeforeLookingAtTheFile()
        {
            foreach (var exists in new[] { false, true })
            {
                var d = SavePolicy.Decide(exists, exists ? HashA : null, null, null, LedgerState.Corrupt);
                Assert.AreEqual(SaveAction.Refuse, d.Action); Assert.AreEqual("ledger_unavailable", d.Reason);
            }
        }

        [TestMethod] public void AnUnreadableExistingFileCannotBeReplaced()
        {
            var d = SavePolicy.Decide(true, null, HashA, Owned(HashA), LedgerState.Ok);
            Assert.AreEqual(SaveAction.Refuse, d.Action); Assert.AreEqual("file_changed", d.Reason);
        }

        [TestMethod] public void TheReplaceHashFormatIsStrict()
        {
            Assert.IsTrue(OperationLimits.IsSha256(HashA));
            foreach (var bad in new[] { null, "", "ABC", HashA.ToUpperInvariant(), HashA + "a", HashA.Substring(1), "g" + HashA.Substring(1) }) Assert.IsFalse(OperationLimits.IsSha256(bad), bad);
            Assert.AreEqual(64, OperationLimits.Sha256Length);
        }
    }
}
