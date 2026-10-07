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
    }
}
