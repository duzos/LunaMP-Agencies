using System;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class CraftListServiceTests
    {
        private OperationRig rig;
        private CraftListService list;

        [TestInitialize] public void Setup()
        {
            rig = new OperationRig(lease: false);
            list = new CraftListService(() => rig.Paths, rig.Files);
        }

        private static JObject Args(string facility = "VAB", int? offset = null, int? limit = null, string filter = null)
        {
            var a = new JObject { ["facility"] = facility };
            if (offset.HasValue) a["offset"] = offset.Value; if (limit.HasValue) a["limit"] = limit.Value; if (filter != null) a["filter"] = filter;
            return a;
        }

        private JObject Ok(JObject args)
        {
            var result = list.List(args);
            Assert.IsNull(result.Reason, result.Detail);
            return result.Data;
        }

        private void Put(string name, string text = "ship = X") { rig.Files.Put(rig.ShipFile(name.Substring(0, name.Length - ".craft".Length)), text); }

        [TestMethod] public void ListsCraftFilesOrderedByNameWithSizeHashAndTime()
        {
            Put("b Rocket.craft", "bbb"); Put("A Rocket.craft", "aaaa");
            var data = Ok(Args());
            var names = data["crafts"].Select(c => (string)c["fileName"]).ToArray();
            CollectionAssert.AreEqual(new[] { "A Rocket.craft", "b Rocket.craft" }, names);
            Assert.AreEqual(4, (int)data["crafts"][0]["sizeBytes"]);
            Assert.AreEqual(OperationHash.Sha256Hex("aaaa"), (string)data["crafts"][0]["sha256"]);
            StringAssert.EndsWith((string)data["crafts"][0]["modifiedUtc"], "Z");
            Assert.AreEqual(2, (int)data["total"]); Assert.AreEqual(JTokenType.Null, data["nextOffset"].Type);
            Assert.AreEqual("VAB", (string)data["facility"]);
        }

        [TestMethod] public void SidecarsAndOtherFilesAreNotListed()
        {
            Put("Real.craft"); rig.Files.Put(rig.ShipFile("Real") + ".original", "x"); rig.Files.Put(rig.ShipFile("Real") + ".loadmeta", "x");
            rig.Files.Put(System.IO.Path.Combine(rig.Paths.ShipsDirectory("VAB"), "notes.txt"), "x");
            CollectionAssert.AreEqual(new[] { "Real.craft" }, Ok(Args())["crafts"].Select(c => (string)c["fileName"]).ToArray());
        }

        [TestMethod] public void OnlyTheRequestedFacilityIsListed()
        {
            Put("Hangar.craft");
            rig.Files.Put(rig.Paths.ResolveNewShip("SPH", "Plane").FullPath, "x");
            Assert.AreEqual("Hangar.craft", (string)Ok(Args("VAB"))["crafts"][0]["fileName"]);
            Assert.AreEqual("Plane.craft", (string)Ok(Args("SPH"))["crafts"][0]["fileName"]);
        }

        [TestMethod] public void PagingAndFilteringWorkOnTheFilteredList()
        {
            foreach (var n in new[] { "Alpha", "Beta", "Gamma", "Alpine" }) Put(n + ".craft");
            var first = Ok(Args(limit: 2));
            Assert.AreEqual(2, first["crafts"].Count()); Assert.AreEqual(2, (int)first["nextOffset"]); Assert.AreEqual(4, (int)first["total"]);
            var second = Ok(Args(offset: 2, limit: 2));
            Assert.AreEqual(2, second["crafts"].Count()); Assert.AreEqual(JTokenType.Null, second["nextOffset"].Type);
            var filtered = Ok(Args(filter: "alp"));
            CollectionAssert.AreEqual(new[] { "Alpha.craft", "Alpine.craft" }, filtered["crafts"].Select(c => (string)c["fileName"]).ToArray());
            Assert.AreEqual(2, (int)filtered["total"]);
        }

        [TestMethod] public void OwnershipNeedsTheLedgerEntryAndAMatchingHash()
        {
            Put("Ours.craft", "ours"); Put("Theirs.craft", "theirs"); Put("Edited.craft", "edited");
            var ledger = CraftLedger.Load(rig.Files, rig.Paths.LedgerFile);
            ledger.Record(new LedgerEntry { Facility = "VAB", FileName = "Ours.craft", Sha256 = OperationHash.Sha256Hex("ours"), RequestId = "request-0001", SavedUtc = "x" });
            ledger.Record(new LedgerEntry { Facility = "VAB", FileName = "Edited.craft", Sha256 = OperationHash.Sha256Hex("original"), RequestId = "request-0002", SavedUtc = "x" });
            ledger.Save(rig.Files, rig.Paths.LedgerFile);
            var owned = Ok(Args())["crafts"].ToDictionary(c => (string)c["fileName"], c => (bool)c["kspControlOwned"]);
            Assert.IsTrue(owned["Ours.craft"]); Assert.IsFalse(owned["Theirs.craft"]); Assert.IsFalse(owned["Edited.craft"]);
        }

        [TestMethod] public void ACorruptLedgerOwnsNothingAndSaysSo()
        {
            Put("Ours.craft", "ours"); rig.Files.Put(rig.Paths.LedgerFile, "garbage");
            var data = Ok(Args());
            Assert.IsFalse((bool)data["crafts"][0]["kspControlOwned"]); Assert.AreEqual("corrupt", (string)data["ledger"]);
        }

        [TestMethod] public void NamesTheToolsCannotAddressAreCountedNotListed()
        {
            Put("Fine.craft");
            rig.Files.Put(System.IO.Path.Combine(rig.Paths.ShipsDirectory("VAB"), "Odd (1).craft"), "x");
            rig.Files.Put(System.IO.Path.Combine(rig.Paths.ShipsDirectory("VAB"), "dot..dot.craft"), "x");
            var data = Ok(Args());
            CollectionAssert.AreEqual(new[] { "Fine.craft" }, data["crafts"].Select(c => (string)c["fileName"]).ToArray());
            Assert.AreEqual(2, (int)data["skippedUnaddressableNames"]);
        }

        [TestMethod] public void LinkedFilesAreSkipped()
        {
            Put("Real.craft"); Put("Linked.craft"); rig.Files.Reparse.Add(rig.ShipFile("Linked"));
            var data = Ok(Args());
            CollectionAssert.AreEqual(new[] { "Real.craft" }, data["crafts"].Select(c => (string)c["fileName"]).ToArray());
            Assert.AreEqual(1, (int)data["skippedLinkedFiles"]);
        }

        [TestMethod] public void ALinkedShipsFolderListsNothing()
        {
            Put("Real.craft");
            rig.Files.Reparse.Add(System.IO.Path.GetFullPath(rig.Paths.ShipsDirectory("VAB")));
            Assert.AreEqual(0, Ok(Args())["crafts"].Count());
        }

        [TestMethod] public void AMissingFolderIsAnEmptyList()
        {
            var data = Ok(Args());
            Assert.AreEqual(0, (int)data["total"]); Assert.AreEqual(0, data["crafts"].Count());
        }

        [TestMethod] public void ListingWritesNothing()
        {
            Put("Real.craft"); var writes = rig.Files.Writes.Count;
            Ok(Args());
            Assert.AreEqual(writes, rig.Files.Writes.Count); Assert.AreEqual(0, rig.Files.Deletes.Count);
        }

        [DataTestMethod]
        [DataRow("{}")] [DataRow("{\"facility\":\"LAUNCHPAD\"}")] [DataRow("{\"facility\":5}")] [DataRow("{\"facility\":\"VAB\",\"offset\":-1}")] [DataRow("{\"facility\":\"VAB\",\"offset\":100001}")]
        [DataRow("{\"facility\":\"VAB\",\"offset\":\"1\"}")] [DataRow("{\"facility\":\"VAB\",\"limit\":0}")] [DataRow("{\"facility\":\"VAB\",\"limit\":51}")] [DataRow("{\"facility\":\"VAB\",\"limit\":1.5}")]
        [DataRow("{\"facility\":\"VAB\",\"filter\":5}")]
        public void BadArgumentsAreInvalid(string json)
        {
            var result = list.List(JObject.Parse(json));
            Assert.AreEqual("invalid_argument", result.Reason);
        }

        [TestMethod] public void AFilterOver128CharactersIsInvalid()
        {
            Assert.AreEqual("invalid_argument", list.List(Args(filter: new string('a', 129))).Reason);
            Assert.IsNull(list.List(Args(filter: new string('a', 128))).Reason);
        }

        [TestMethod] public void ASaveFolderNamedControlStillListsItsOwnShipsAndReportsNoLedger()
        {
            rig = new OperationRig(lease: false, saveFolder: "control");
            list = new CraftListService(() => rig.Paths, rig.Files);
            rig.Files.Put(rig.ShipFile("Mine"), "x");
            var data = Ok(Args());
            Assert.AreEqual("Mine.craft", (string)data["crafts"][0]["fileName"]); Assert.IsFalse((bool)data["crafts"][0]["kspControlOwned"]); Assert.AreEqual("unavailable", (string)data["ledger"]);
        }
    }
}
