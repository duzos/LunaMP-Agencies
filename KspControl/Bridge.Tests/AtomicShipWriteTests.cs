using System;
using System.IO;
using System.Linq;
using System.Text;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KspControl.BridgeTests
{
    /// <summary>Create-only and replace-only atomic writes on the real disk: the file lands whole or not at all.</summary>
    [TestClass]
    public class AtomicShipWriteTests
    {
        private string dir;
        private readonly DiskOperationFiles files = new DiskOperationFiles();

        [TestInitialize] public void Setup() { dir = Path.Combine(Path.GetTempPath(), "kc-save-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); }
        [TestCleanup] public void Cleanup() { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } }

        private string Target(string name = "Probe.craft") { return Path.Combine(dir, name); }
        private static byte[] Bytes(string text) { return Encoding.UTF8.GetBytes(text); }

        [TestMethod] public void CreateNewWritesTheWholeFileAndLeavesNoTemporaryFile()
        {
            files.CreateNew(Target(), Bytes("one"));
            Assert.AreEqual("one", File.ReadAllText(Target()));
            CollectionAssert.AreEqual(new[] { "Probe.craft" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }

        [TestMethod] public void CreateNewNeverTouchesAnExistingFile()
        {
            File.WriteAllText(Target(), "human");
            var error = Assert.ThrowsException<IOException>(() => files.CreateNew(Target(), Bytes("ours")));
            StringAssert.Contains(error.Message, "exists");
            Assert.AreEqual("human", File.ReadAllText(Target()));
            CollectionAssert.AreEqual(new[] { "Probe.craft" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray(), "the temporary file is removed on refusal");
        }

        [TestMethod] public void CreateNewMakesTheFolderWhenItIsMissing()
        {
            var nested = Path.Combine(dir, "a", "b", "Probe.craft");
            files.CreateNew(nested, Bytes("x")); Assert.AreEqual("x", File.ReadAllText(nested));
        }

        [TestMethod] public void ReplaceExistingSwapsTheContentAndLeavesNoTemporaryFile()
        {
            File.WriteAllText(Target(), "old");
            files.ReplaceExisting(Target(), Bytes("new"));
            Assert.AreEqual("new", File.ReadAllText(Target()));
            CollectionAssert.AreEqual(new[] { "Probe.craft" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }

        [TestMethod] public void ReplaceExistingRefusesAMissingFileInsteadOfCreatingIt()
        {
            Assert.ThrowsException<FileNotFoundException>(() => files.ReplaceExisting(Target(), Bytes("new")));
            Assert.IsFalse(File.Exists(Target()));
            Assert.AreEqual(0, Directory.GetFiles(dir).Length, "no temporary file left");
        }

        [TestMethod] public void TheTemporaryFileLivesBesideTheTargetAndIsNeverACraft()
        {
            // Observed through a directory that cannot be written to by the final move: a read-only target blocks Replace, and the temp must be cleaned up.
            File.WriteAllText(Target(), "locked");
            File.SetAttributes(Target(), FileAttributes.ReadOnly);
            try
            {
                Assert.ThrowsException<UnauthorizedAccessException>(() => files.ReplaceExisting(Target(), Bytes("new")));
            }
            finally { File.SetAttributes(Target(), FileAttributes.Normal); }
            Assert.AreEqual("locked", File.ReadAllText(Target()));
            CollectionAssert.AreEqual(new[] { "Probe.craft" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }
            // ---- partial File.Replace failures (ReplaceFileW 1176 / 1177) ----

        private DiskOperationFiles With(Action<string, string, string> replace) { return new DiskOperationFiles(replace); }

        [TestMethod] public void APartialReplaceThatTookTheTargetAwayPutsThePreviousBytesBack()
        {
            File.WriteAllText(Target(), "old");
            var partial = With((source, destination, backup) => { File.Move(destination, backup); throw new IOException("1176"); });
            var error = Assert.ThrowsException<FileStateChangedException>(() => partial.ReplaceExisting(Target(), Bytes("new")));
            Assert.AreEqual("restored_previous", error.Outcome);
            Assert.AreEqual("old", File.ReadAllText(Target()));
            CollectionAssert.AreEqual(new[] { "Probe.craft" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray(), "no temporary or backup file left");
        }

        [TestMethod] public void APartialReplaceWithNoBackupFallsBackToTheNewBytes()
        {
            File.WriteAllText(Target(), "old");
            var partial = With((source, destination, backup) => { File.Delete(destination); throw new IOException("1177"); });
            var error = Assert.ThrowsException<FileStateChangedException>(() => partial.ReplaceExisting(Target(), Bytes("new")));
            Assert.AreEqual("restored_new", error.Outcome);
            Assert.AreEqual("new", File.ReadAllText(Target()), "the temporary file was the only copy of the new bytes, so it is moved into place, not deleted");
            CollectionAssert.AreEqual(new[] { "Probe.craft" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }

        [TestMethod] public void AReplaceThatFailsWithTheTargetUntouchedIsAPlainFailure()
        {
            File.WriteAllText(Target(), "old");
            var refusing = With((source, destination, backup) => { throw new IOException("sharing"); });
            var error = Assert.ThrowsException<IOException>(() => refusing.ReplaceExisting(Target(), Bytes("new")));
            Assert.IsNotInstanceOfType(error, typeof(FileStateChangedException));
            Assert.AreEqual("old", File.ReadAllText(Target()));
            CollectionAssert.AreEqual(new[] { "Probe.craft" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }

        [TestMethod] public void AReplaceThatLandedAndThenFailedIsReportedAsChanged()
        {
            File.WriteAllText(Target(), "old");
            var late = With((source, destination, backup) => { File.Replace(source, destination, backup); throw new IOException("after"); });
            var error = Assert.ThrowsException<FileStateChangedException>(() => late.ReplaceExisting(Target(), Bytes("new")));
            Assert.AreEqual("target_changed", error.Outcome);
            Assert.AreEqual("new", File.ReadAllText(Target()));
        }

        [TestMethod] public void TheTemporaryAndBackupFilesAreShortKcNamesBesideTheTarget()
        {
            File.WriteAllText(Target(), "old");
            string temp = null, backup = null;
            var spy = With((source, destination, bak) => { temp = source; backup = bak; File.Replace(source, destination, bak); });
            spy.ReplaceExisting(Target(), Bytes("new"));
            Assert.AreEqual(dir, Path.GetDirectoryName(temp)); Assert.AreEqual(dir, Path.GetDirectoryName(backup));
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(temp), @"^kc-[0-9a-f]{32}\.tmp$"), temp);
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(backup), @"^kc-[0-9a-f]{32}\.bak$"), backup);
            CollectionAssert.AreEqual(new[] { "Probe.craft" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray(), "the backup is deleted after success");
        }
    }

    /// <summary>A backup is only swept when this process left it behind after a successful replace and its target exists.</summary>
    [TestClass]
    public class ShipsSweeperBackupTests
    {
        [TestMethod] public void ATrackedBackupIsSweptOnlyWhenOldEnoughAndItsTargetExists()
        {
            ShipsSweeper.ForgetAll();
            var files = new KspControl.BridgeTests.MemoryFiles();
            var dir = Path.Combine(Path.GetTempPath(), "ships"); var bak = Path.Combine(dir, "kc-" + new string('c', 32) + ".bak"); var target = Path.Combine(dir, "A.craft");
            files.Put(bak, "old"); files.Put(target, "new");
            var now = DateTime.UtcNow;
            ShipsSweeper.RegisterBackup(bak, target, now);
            var declared = new System.Collections.Generic.List<DeclaredOutput>();
            ShipsSweeper.Sweep(files, dir, now.AddMinutes(1), declared);
            Assert.IsTrue(files.Exists(bak), "too young");
            ShipsSweeper.Sweep(files, dir, now.AddMinutes(6), declared);
            Assert.IsFalse(files.Exists(bak)); Assert.IsTrue(files.Exists(target));
            Assert.IsTrue(declared.Any(d => d.Detail == "stale_backup_swept"));
        }

        [TestMethod] public void ATrackedBackupWhoseTargetIsGoneIsLeftAlone()
        {
            ShipsSweeper.ForgetAll();
            var files = new KspControl.BridgeTests.MemoryFiles();
            var dir = Path.Combine(Path.GetTempPath(), "ships"); var bak = Path.Combine(dir, "kc-" + new string('d', 32) + ".bak"); var target = Path.Combine(dir, "A.craft");
            files.Put(bak, "only copy");
            var now = DateTime.UtcNow; ShipsSweeper.RegisterBackup(bak, target, now);
            var declared = new System.Collections.Generic.List<DeclaredOutput>();
            ShipsSweeper.Sweep(files, dir, now.AddHours(1), declared);
            Assert.IsTrue(files.Exists(bak)); Assert.IsTrue(declared.Any(d => d.Detail == "backup_target_missing_left_alone"));
            ShipsSweeper.ForgetAll();
        }
    }
}
