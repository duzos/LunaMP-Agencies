using LmpCommon.Agency;
using LmpCommon.Message.Data.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Server.Agency;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ServerTest.Agency
{
    /// <summary>Plan 40 slice S1: tooling blueprints are content-addressed files; the economy document and snapshots only carry refs.</summary>
    [TestClass, DoNotParallelize]
    public class AgencyToolingBlueprintTest
    {
        private static readonly byte[] Craft = Encoding.UTF8.GetBytes(AgencyStockTest.Craft);
        private static readonly byte[] Edited = Encoding.UTF8.GetBytes("ship = Probe Mk2\ntype = VAB\nPART\n{\npart = probe_7\n}\n");
        private static ToolingManifest Probe() => AgencyStockTest.Probe();
        private static string Fp => AgencyStockTest.ProbeFingerprint;
        private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static string FileFor(byte[] bytes) => Path.Combine(AgencyEconomyStore.BlueprintDirectory, Sha(bytes) + ".craft");
        private static EconomyResult Fetch(AgencyEconomyTest.Fixture f, string fp) => f.Execute(new EconomyCommand { Operation = EconomyOperation.FetchBlueprint, StockFingerprint = fp });
        private static EconomyDocument Document() => AgencyStockTest.Document();

        [TestMethod]
        public void ToolWithAMatchingBlueprintStoresAFileAndFetchReturnsIdenticalBytes()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                var tooled = AgencyStockTest.Tool(f.Execute, Probe(), Craft, "VAB", "  My Probe  ");
                Assert.IsTrue(tooled.Success, tooled.Reason);
                Assert.AreEqual("My Probe", f.Snapshot.Designs.Single().Name);
                CollectionAssert.AreEqual(Craft, File.ReadAllBytes(FileFor(Craft)));
                var info = f.Snapshot.DesignBlueprints.Single();
                Assert.AreEqual(Fp, info.Fingerprint); Assert.AreEqual("VAB", info.Editor); Assert.AreEqual(Sha(Craft), info.Hash); Assert.AreEqual(Craft.Length, info.Bytes); Assert.AreEqual("My Probe", info.Name);
                var revision = Document().Revision;
                var fetched = Fetch(f, Fp);
                Assert.IsTrue(fetched.Success, fetched.Reason);
                CollectionAssert.AreEqual(Craft, fetched.BlueprintData);
                Assert.AreEqual(Sha(Craft), fetched.BlueprintHash);
                Assert.AreEqual("VAB", fetched.BlueprintEditor);
                Assert.AreEqual(revision, Document().Revision, "A fetch is read-only.");
                Assert.IsFalse(Document().Operations.ContainsKey(fetched.RequestId), "A fetch leaves no receipt.");
            }
        }

        [TestMethod]
        public void UnsafeDesignNamesAreDroppedWithoutRefusingTheTool()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe(), name: "bad\u0001name").Success);
                Assert.IsNull(f.Snapshot.Designs.Single().Name);
            }
        }

        [DataTestMethod]
        [DataRow("mismatch", "does not match")]
        [DataRow("oversized", "storage limit")]
        [DataRow("editor", "does not match")]
        public void StorageMissesStillToolAndChargeWithTheReasonSet(string kind, string reason)
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                var bytes = kind == "mismatch" ? Encoding.UTF8.GetBytes("ship = X\ntype = VAB\nPART\n{\npart = other_1\n}\n")
                    : kind == "oversized" ? Encoding.UTF8.GetBytes("ship = X\ntype = VAB\n" + new string(' ', TradeLimits.MaxBlueprintBytes) + "PART\n{\npart = probe_1\n}\n")
                    : Craft;
                var funds = f.Snapshot.Funds;
                var tooled = AgencyStockTest.Tool(f.Execute, Probe(), bytes, kind == "editor" ? "SPH" : "VAB");
                Assert.IsTrue(tooled.Success, tooled.Reason);
                StringAssert.Contains(tooled.Reason, "Tooled; craft not saved (" + reason);
                Assert.AreEqual(1, f.Snapshot.Designs.Length);
                Assert.IsTrue(f.Snapshot.Funds < funds);
                Assert.AreEqual(0, f.Snapshot.DesignBlueprints.Length);
                Assert.IsFalse(Directory.Exists(AgencyEconomyStore.BlueprintDirectory) && Directory.GetFiles(AgencyEconomyStore.BlueprintDirectory).Any());
            }
        }

        [TestMethod]
        public void AToolOnAnAlreadyTooledDesignAttachesAndReplacesForFreeAfterTheCooldown()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                var now = DateTime.UtcNow;
                AgencyEconomyStore.UtcNow = () => now;
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe()).Success);
                var funds = f.Snapshot.Funds;
                Assert.AreEqual(0, f.Snapshot.DesignBlueprints.Length);
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe(), Craft).Success);
                Assert.AreEqual(Sha(Craft), f.Snapshot.DesignBlueprints.Single().Hash);
                var early = AgencyStockTest.Tool(f.Execute, Probe(), Edited);
                Assert.IsTrue(early.Success);
                StringAssert.Contains(early.Reason, "saved less than a minute ago");
                Assert.AreEqual(Sha(Craft), f.Snapshot.DesignBlueprints.Single().Hash);
                Assert.IsFalse(File.Exists(FileFor(Edited)));
                now = now.AddSeconds(61);
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe(), Edited).Success);
                Assert.AreEqual(Sha(Edited), f.Snapshot.DesignBlueprints.Single().Hash);
                Assert.IsTrue(File.Exists(FileFor(Edited)));
                Assert.IsFalse(File.Exists(FileFor(Craft)), "The replaced file is deleted once nothing references it.");
                Assert.AreEqual(funds, f.Snapshot.Funds, "Saving a craft to an existing design is free.");
            }
        }

        private static string FakeHash(int i) => i.ToString("x64");

        [TestMethod]
        public void AFullAgencyCapOrServerCapSkipsTheStoreButStillTools()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                // Sixteen 512 KB refs fill the agency's 8 MB.
                var document = Document();
                var agency = document.Agencies[f.Client.AgencyId];
                for (var i = 0; i < 16; i++)
                {
                    var m = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "filler" + i, UnitCost = 1 } } };
                    var fp = ToolingPolicy.Fingerprint(m);
                    agency.Designs.Add(new ToolingDesign { Fingerprint = fp, Manifest = m });
                    agency.Blueprints[fp] = new ToolingBlueprintRef { Fingerprint = fp, Name = "f", Editor = "VAB", Hash = FakeHash(i), Size = ToolingLimits.MaxToolingBlueprintBytes };
                }
                var full = AgencyStockTest.Tool(f.Execute, Probe(), Craft);
                Assert.IsTrue(full.Success, full.Reason);
                StringAssert.Contains(full.Reason, "storage limit");
                Assert.AreEqual(17, f.Snapshot.Designs.Length);
                Assert.IsFalse(File.Exists(FileFor(Craft)));

                // Move the fillers to 32 other agency rows: 512 distinct hashes of 512 KB is the 256 MB server cap.
                agency.Blueprints.Clear();
                agency.Designs.RemoveAll(d => d.Fingerprint != Fp);
                for (var a = 0; a < 32; a++)
                {
                    var other = new EconomyAgency();
                    for (var i = 0; i < 16; i++)
                    {
                        var m = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "filler" + i, UnitCost = 1 } } };
                        var fp = ToolingPolicy.Fingerprint(m);
                        other.Designs.Add(new ToolingDesign { Fingerprint = fp, Manifest = m });
                        other.Blueprints[fp] = new ToolingBlueprintRef { Fingerprint = fp, Name = "f", Editor = "VAB", Hash = FakeHash(a * 16 + i + 1), Size = ToolingLimits.MaxToolingBlueprintBytes };
                    }
                    document.Agencies[Guid.NewGuid()] = other;
                }
                var server = AgencyStockTest.Tool(f.Execute, Probe(), Craft);
                Assert.IsTrue(server.Success, server.Reason);
                StringAssert.Contains(server.Reason, "storage limit");
                Assert.IsFalse(File.Exists(FileFor(Craft)));
            }
        }

        [TestMethod]
        public void FetchFailsCleanlyForAnotherAgencyAnUntooledDesignOrAMissingFile()
        {
            using (var t = new AgencyTradeTest.Fixture())
            {
                Assert.IsTrue(AgencyStockTest.Tool(t.Economy.Execute, Probe(), Craft).Success);
                Assert.IsFalse(t.Buy(new EconomyCommand { Operation = EconomyOperation.FetchBlueprint, StockFingerprint = Fp }).Success, "Another agency's craft is never returned.");
                Assert.IsFalse(Fetch(t.Economy, "not-a-design").Success);
                File.Delete(FileFor(Craft));
                var missing = Fetch(t.Economy, Fp);
                Assert.IsFalse(missing.Success);
                Assert.AreEqual("Saved craft unavailable; save it to tooling again.", missing.Reason);
                File.WriteAllBytes(FileFor(Craft), Edited);
                Assert.IsFalse(Fetch(t.Economy, Fp).Success, "A file whose bytes no longer hash to its name is refused.");
                Assert.IsTrue(AgencyEconomyStore.Ready);
            }
        }

        [TestMethod]
        public void BlueprintBytesAreNeverInTheDocumentOrASnapshot()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe()).Success);
                var bare = AgencyEconomyWire.Size(f.Snapshot);
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe(), Craft).Success);
                var file = File.ReadAllText(AgencyEconomyStore.FilePath);
                var wire = JsonConvert.SerializeObject(f.Snapshot); // the wire payload is this JSON (AgencyEconomyWire)
                foreach (var text in new[] { file, wire })
                {
                    Assert.IsFalse(text.Contains(Convert.ToBase64String(Craft)));
                    Assert.IsFalse(text.Contains("probe_1"));
                    Assert.IsTrue(text.Contains(Sha(Craft)), "Only the hash reference is carried.");
                }
                Assert.IsTrue(AgencyEconomyWire.Size(f.Snapshot) - bare < 400, "The snapshot grows by metadata only.");
                CollectionAssert.AreEqual(Craft, File.ReadAllBytes(FileFor(Craft)));
            }
        }

        [TestMethod]
        public void LoadRemovesOnlyOrphanedHexCraftAndTempFiles()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe(), Craft).Success);
                var dir = AgencyEconomyStore.BlueprintDirectory;
                var orphan = Path.Combine(dir, FakeHash(7) + ".craft");
                var temp = Path.Combine(dir, FakeHash(8) + ".craft.tmp");
                var notes = Path.Combine(dir, "notes.txt");
                var shortName = Path.Combine(dir, new string('a', 63) + ".craft");
                var upper = Path.Combine(dir, new string('A', 64) + ".craft");
                foreach (var path in new[] { orphan, temp, notes, shortName, upper }) File.WriteAllText(path, "x");
                AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready);
                Assert.IsFalse(File.Exists(orphan)); Assert.IsFalse(File.Exists(temp));
                Assert.IsTrue(File.Exists(notes)); Assert.IsTrue(File.Exists(shortName)); Assert.IsTrue(File.Exists(upper));
                Assert.IsTrue(File.Exists(FileFor(Craft)), "A referenced file stays.");
            }
        }

        [TestMethod]
        public void AFailedCommitLeavesNoNewFileAndNeverDeletesAReferencedOne()
        {
            using (var t = new AgencyTradeTest.Fixture())
            {
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "before-document") throw new IOException("injected"); };
                Assert.IsFalse(AgencyStockTest.Tool(t.Economy.Execute, Probe(), Craft).Success);
                Assert.IsFalse(File.Exists(FileFor(Craft)), "The file written before the failed commit is removed.");
                Assert.AreEqual(0, t.Economy.Snapshot.Designs.Length);
                AgencyEconomyStore.PersistenceCheckpoint = null;

                // The buyer stores the same bytes; a later failed Tool of the same craft must not delete the shared, referenced file.
                Assert.IsTrue(AgencyStockTest.Tool(t.Buy, Probe(), Craft).Success);
                Assert.IsTrue(File.Exists(FileFor(Craft)));
                AgencyEconomyStore.PersistenceCheckpoint = point => { if (point == "before-document") throw new IOException("injected"); };
                Assert.IsFalse(AgencyStockTest.Tool(t.Economy.Execute, Probe(), Craft).Success);
                Assert.IsTrue(File.Exists(FileFor(Craft)));
            }
        }

        [TestMethod]
        public void ARefusedToolWritesNoFileAndADiskErrorIsASkip()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                AgencyEconomyStore.SetBalance(f.Client.AgencyId, 1, null);
                var poor = AgencyStockTest.Tool(f.Execute, Probe(), Craft);
                Assert.IsFalse(poor.Success);
                Assert.IsFalse(File.Exists(FileFor(Craft)));
                AgencyEconomyStore.SetBalance(f.Client.AgencyId, 50000, null);
                try
                {
                    AgencyEconomyStore.BlueprintWriteCheckpoint = path => throw new IOException("disk gone");
                    var tooled = AgencyStockTest.Tool(f.Execute, Probe(), Craft);
                    Assert.IsTrue(tooled.Success, tooled.Reason);
                    Assert.AreEqual("Tooled; craft not saved (disk error)", tooled.Reason);
                    Assert.AreEqual(1, f.Snapshot.Designs.Length);
                    Assert.IsTrue(f.Snapshot.Funds < 50000);
                    Assert.AreEqual(0, f.Snapshot.DesignBlueprints.Length);
                }
                finally { AgencyEconomyStore.BlueprintWriteCheckpoint = null; }
            }
        }

        [TestMethod]
        public void QuoteAndFetchSendNoBroadcast()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe(), Craft).Success);
                int Snapshots() => f.Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyEconomySnapshotMsgData>().Count();
                EconomyCommand Next(EconomyCommand c) { c.RequestId = Guid.NewGuid(); c.SessionId = f.Session; c.Sequence = 0; return c; }
                var before = Snapshots();
                AgencyEconomyStore.HandleCommand(f.Client, Next(new EconomyCommand { Operation = EconomyOperation.FetchBlueprint, StockFingerprint = Fp, Sequence = 1 }));
                AgencyEconomyStore.HandleCommand(f.Client, Next(new EconomyCommand { Operation = EconomyOperation.Quote, Manifest = Probe(), ManifestHash = ToolingPolicy.ManifestHash(Probe()), Sequence = 1 }));
                Assert.AreEqual(before, Snapshots());
                Assert.AreEqual(2, f.Client.SendMessageQueue.Select(m => m.Data).OfType<AgencyEconomyResultMsgData>().Count(r => r.Result.Operation == EconomyOperation.FetchBlueprint || r.Result.Operation == EconomyOperation.Quote));
                var delta = new EconomyCommand { Operation = EconomyOperation.Delta, FundsDelta = 1 };
                AgencyEconomyStore.HandleCommand(f.Client, Next(delta));
                Assert.IsTrue(Snapshots() > before, "Any other command still broadcasts.");
            }
        }

        [TestMethod]
        public void ValidateRejectsCorruptBlueprintRefs()
        {
            using (var f = new AgencyEconomyTest.Fixture())
            {
                Assert.IsTrue(AgencyStockTest.Tool(f.Execute, Probe(), Craft).Success);
                var good = File.ReadAllText(AgencyEconomyStore.FilePath);
                void Corrupt(Action<EconomyDocument> change)
                {
                    var document = JsonConvert.DeserializeObject<EconomyDocument>(good);
                    change(document);
                    File.WriteAllText(AgencyEconomyStore.FilePath, JsonConvert.SerializeObject(document));
                    AgencyEconomyStore.Load();
                    Assert.IsFalse(AgencyEconomyStore.Ready);
                }
                EconomyAgency Mine(EconomyDocument d) => d.Agencies[f.Client.AgencyId];
                Corrupt(d => Mine(d).Blueprints[Fp].Hash = Sha(Craft).ToUpperInvariant());
                Corrupt(d => Mine(d).Blueprints[Fp].Hash = "abc");
                Corrupt(d => Mine(d).Blueprints["orphan"] = new ToolingBlueprintRef { Fingerprint = "orphan", Editor = "VAB", Hash = FakeHash(1), Size = 10 });
                Corrupt(d => Mine(d).Blueprints[Fp].Editor = "KSC");
                Corrupt(d => Mine(d).Blueprints[Fp].Size = ToolingLimits.MaxToolingBlueprintBytes + 1);
                Corrupt(d =>
                {
                    for (var i = 0; i < 16; i++)
                    {
                        var m = new ToolingManifest { Parts = new[] { new ToolingPart { Name = "filler" + i, UnitCost = 1 } } };
                        var fp = ToolingPolicy.Fingerprint(m);
                        Mine(d).Designs.Add(new ToolingDesign { Fingerprint = fp, Manifest = m });
                        Mine(d).Blueprints[fp] = new ToolingBlueprintRef { Fingerprint = fp, Editor = "VAB", Hash = FakeHash(i + 1), Size = ToolingLimits.MaxToolingBlueprintBytes };
                    }
                });
                File.WriteAllText(AgencyEconomyStore.FilePath, good);
                AgencyEconomyStore.Load();
                Assert.IsTrue(AgencyEconomyStore.Ready);
            }
        }
    }
}
