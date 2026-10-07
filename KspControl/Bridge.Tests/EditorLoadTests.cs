using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    /// <summary>editor_load_craft end to end on fakes (plan R3-section 4, R4-section 6.4): staging, the upgrade pipeline, pre-validation, comparison and the human file left alone.</summary>
    [TestClass]
    public class EditorLoadTests
    {
        private OperationRig rig;
        private const string FileName = "Orbiter Probe.craft";

        [TestInitialize] public void Setup() { rig = new OperationRig(); }

        private string ShipsDir { get { return rig.Paths.ShipsDirectory("VAB"); } }
        private string SourcePath { get { return Path.Combine(ShipsDir, FileName); } }
        private string Staging(string id = "load-0001") { return rig.Paths.StagingPath(id).FullPath; }
        private string Upgraded(string id = "load-0001") { return rig.Paths.UpgradedStagingPath(id).FullPath; }

        private string PutSource(string text = null)
        {
            text = text ?? EditorFake.CraftText();
            rig.Files.Put(SourcePath, text);
            return OperationHash.Sha256Hex(text);
        }

        private BridgeRequest Request(string sha, string requestId = "load-0001", bool? allowUpgrade = null, string fileName = FileName, string facility = "VAB", string token = null)
        {
            var args = new JObject { ["requestId"] = requestId, ["facility"] = facility, ["fileName"] = fileName, ["expectedSha256"] = sha, ["expectedRevision"] = token ?? rig.Token() };
            if (allowUpgrade.HasValue) args["allowUpgrade"] = allowUpgrade.Value;
            return new BridgeRequest { RequestId = "wire-" + requestId, Operation = EditorOperations.LoadCraft, LeaseId = rig.Lease, Arguments = args };
        }

        private OperationJob Start(string sha, bool? allowUpgrade = null)
        {
            var response = rig.Service.Handle(Request(sha, "load-0001", allowUpgrade));
            Assert.AreEqual("running", response.Status, response.ReasonCode + " " + response.Data);
            return rig.Runner.Current;
        }

        private OperationJob Run(string sha, bool? allowUpgrade = null) { var job = Start(sha, allowUpgrade); rig.RunToEnd(); return job; }

        private static string ModifyValue(string text, string from, string to) { return text.Replace(from, to); }

        // ---- the main path ----

        [TestMethod] public void ALoadStagesACopyLoadsTheCopyAndLeavesTheHumanFileAlone()
        {
            var sha = PutSource(); var before = rig.Files.Data[SourcePath];
            var job = Run(sha);
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.AreEqual(1, rig.Port.LoadCalls);
            Assert.AreEqual(Staging(), rig.Port.LoadedPaths[0], "the staged copy is what KSP loads, never the human file");
            CollectionAssert.AreEqual(before, rig.Files.Data[SourcePath]);
            Assert.AreEqual(sha, job.Load.SourceSha256);
            Assert.IsTrue(rig.Files.Copies.Contains(SourcePath + " -> " + Staging()));
            Assert.IsFalse(rig.Files.Writes.Any(p => p.StartsWith(ShipsDir, StringComparison.OrdinalIgnoreCase)), "nothing is written into Ships");
            Assert.IsFalse(rig.Files.Exists(SourcePath + ".original"), "no sidecar beside the human file");
            Assert.IsFalse(rig.Files.Exists(Staging()), "the staged copy goes at the terminal state");
            Assert.AreEqual(1, rig.Port.PipelineCalls); Assert.AreEqual(Staging(), rig.Port.PipelinePaths[0]);
            Assert.IsFalse(job.Terminal && !job.Dispatched);
            Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
        }

        [TestMethod] public void TheEnvelopeReportsTheLoadFields()
        {
            var job = Run(PutSource());
            var envelope = job.ToEnvelope();
            Assert.AreEqual("load_craft", (string)envelope["operation"]);
            Assert.AreEqual(false, (bool)envelope["upgradedOnLoad"]);
            Assert.AreEqual(0, ((JArray)envelope["pipelineDifferences"]).Count);
            Assert.AreEqual("unavailable", (string)envelope["scriptsApplied"]);
            Assert.AreEqual(true, (bool)envelope["comparison"]["equal"]);
            Assert.AreEqual("source", (string)envelope["load"]["loadedNode"]);
            Assert.AreEqual(true, (bool)envelope["load"]["sourceUnchanged"]);
            Assert.AreEqual(false, (bool)envelope["load"]["humanFileModified"]);
            Assert.AreEqual(false, (bool)envelope["notDispatched"]);
            CollectionAssert.IsSubsetOf(new[] { "snapshot_taken", "staging_copy_verified", "upgrade_pipeline_run", "craft_load_dispatched", "craft_loaded" }, ((JArray)envelope["effects"]).Select(t => (string)t).ToList());
        }

        [TestMethod] public void ScriptsAppliedAreReportedWhenTheGameTellsUs()
        {
            rig.Port.ScriptsApplied = new[] { "Upgrade_1_12" };
            var job = Run(PutSource());
            CollectionAssert.AreEqual(new[] { "Upgrade_1_12" }, ((JArray)job.ToEnvelope()["scriptsApplied"]).Select(t => (string)t).ToList());
        }

        [TestMethod] public void ThePhasesOfALoadAreTheApplyPhases()
        {
            var job = Start(PutSource()); var phases = new List<string>();
            for (var i = 0; i < 4000 && !job.Terminal; i++) { rig.Frame(50); var name = OperationJob.PhaseName(job.Phase); if (phases.Count == 0 || phases[phases.Count - 1] != name) phases.Add(name); }
            CollectionAssert.AreEqual(new[] { "snapshot", "staging", "dispatch", "settle", "verify", "post_unlock_grace", "thumbnail_settle", "finalizing", "done" }, phases.Where(p => p != "locking").ToList());
        }

        [TestMethod] public void ALoadIntoAnEmptyEditorTakesNoSnapshot()
        {
            rig = new OperationRig(lease: false);
            rig.Port.Parts = 0; rig.Port.Craft = ""; rig.Port.Ship = new object(); rig.Port.Fsm = "st_podSelect"; rig.Run(20); rig.AcquireLease();
            var job = Run(PutSource());
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.IsNull(job.Snapshot);
        }

        [TestMethod] public void APlainLoadWritesTheOverwriteGuard()
        {
            var job = Run(PutSource());
            Assert.AreEqual("completed", job.Status);
            Assert.AreEqual("sentinel_and_unsaved_marker", (string)job.ToEnvelope()["observed"]["overwriteGuard"]);
            Assert.IsTrue(rig.Port.GuardWrites > 0, "KSP prompts before a human Save overwrites the source");
        }

        [TestMethod] public void AnUpgradedLoadWritesTheOverwriteGuard()
        {
            rig.Port.PipelineTransform = node => node.Children("PART").First().Entries.Add(new Pure.ConfigEntry("upgradedKey", "1"));
            var job = Run(PutSource(), true);
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.AreEqual("sentinel_and_unsaved_marker", (string)job.ToEnvelope()["observed"]["overwriteGuard"]);
        }

        [TestMethod] public void AnOverwriteGuardThatCannotBeWrittenFailsTheLoadAndRestores()
        {
            rig.Port.GuardWritesFail = true;
            var job = Run(PutSource());
            Assert.AreEqual("failed", job.Status); Assert.AreEqual(OperationReasons.SaveOverwriteGuardUnavailable, job.ReasonCode);
            Assert.IsTrue(job.Restore.Attempted);
        }

        // ---- admission refusals ----

        [TestMethod] public void ArgumentsAreValidatedAndNothingRuns()
        {
            var sha = PutSource();
            var cases = new Dictionary<string, BridgeRequest>
            {
                { "facility", Request(sha, facility: "LAUNCHPAD") }, { "sha", Request("ABC") }, { "name", Request(sha, fileName: "no-extension") }, { "bad-name", Request(sha, fileName: "a*b.craft") }
            };
            foreach (var pair in cases)
            {
                var response = rig.Service.Handle(pair.Value);
                Assert.AreEqual("failed", response.Status, pair.Key);
                Assert.AreEqual(ControlReasons.InvalidArgument, response.ReasonCode, pair.Key + " " + response.Data);
                Assert.AreEqual(true, (bool)response.Data["notDispatched"]);
            }
            var badFlag = Request(sha); badFlag.Arguments["allowUpgrade"] = "yes";
            Assert.AreEqual(ControlReasons.InvalidArgument, rig.Service.Handle(badFlag).ReasonCode);
            Assert.AreEqual(0, rig.Port.LoadCalls); Assert.IsFalse(rig.Runner.Busy);
        }

        [TestMethod] public void APathOutsideTheShipsFolderIsRefused()
        {
            var sha = PutSource();
            foreach (var name in new[] { "..\\x.craft", "../x.craft", "sub/x.craft", "C:\\x.craft" })
            {
                var response = rig.Service.Handle(Request(sha, fileName: name));
                Assert.AreEqual(OperationReasons.PathOutsideSave, response.ReasonCode, name);
            }
            Assert.AreEqual(0, rig.Files.Copies.Count);
        }

        [TestMethod] public void AFacilityOtherThanTheEditorsIsRefused()
        {
            var sha = PutSource();
            var response = rig.Service.Handle(Request(sha, facility: "SPH"));
            Assert.AreEqual(ControlReasons.FacilityMismatch, response.ReasonCode);
        }

        [TestMethod] public void AMissingFileIsRefusedAtAdmission()
        {
            var response = rig.Service.Handle(Request(new string('a', 64), fileName: "Ghost.craft"));
            Assert.AreEqual(LoadReasons.CraftNotFound, response.ReasonCode); Assert.AreEqual(true, (bool)response.Data["notDispatched"]);
        }

        [TestMethod] public void AReparsePointFileIsRefused()
        {
            var sha = PutSource(); rig.Files.Reparse.Add(SourcePath);
            Assert.AreEqual(OperationReasons.PathOutsideSave, rig.Service.Handle(Request(sha)).ReasonCode);
        }

        [TestMethod] public void UnsavedHumanWorkIsNeverReplacedUnderTheRefusePolicy()
        {
            rig = new OperationRig(policy: "refuse");
            rig.Port.UnsavedValue = true;
            var response = rig.Service.Handle(Request(PutSource()));
            Assert.AreEqual(OperationReasons.UnsavedHumanCraft, response.ReasonCode);
            Assert.AreEqual(0, rig.Port.LoadCalls);
        }

        [TestMethod] public void ARepeatedRequestIsTheSameJobAndADifferentOneWithTheSameIdConflicts()
        {
            var sha = PutSource();
            var first = rig.Service.Handle(Request(sha, "load-0001", false));
            Assert.AreEqual("running", first.Status);
            var again = rig.Service.Handle(Request(sha, "load-0001", false));
            Assert.AreEqual("running", again.Status);
            Assert.AreEqual((string)first.Data["requestId"], (string)again.Data["requestId"]);
            rig.RunToEnd();
            var conflict = rig.Service.Handle(Request(sha, "load-0001", true));
            Assert.AreEqual(OperationReasons.RequestIdConflict, conflict.ReasonCode);
            Assert.AreEqual(1, rig.Port.LoadCalls);
        }

        // ---- staging and hashes ----

        [TestMethod] public void AHashThatIsNotExpectedIsRefusedBeforeAnyPipelineOrLoad()
        {
            PutSource();
            var job = Run(new string('b', 64));
            Assert.AreEqual("failed", job.Status); Assert.AreEqual(LoadReasons.FileChanged, job.ReasonCode);
            Assert.IsFalse(job.Dispatched); Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
            Assert.AreEqual(0, rig.Port.PipelineCalls); Assert.AreEqual(0, rig.Port.LoadCalls);
            Assert.IsFalse(rig.Files.Exists(Staging()), "the copy is removed");
        }

        [TestMethod] public void AnOversizedFileIsRefused()
        {
            var bytes = new byte[Pure.ConfigText.MaxChars + 1]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)'a';
            rig.Files.Put(SourcePath, bytes);
            var job = Run(OperationHash.Sha256Hex(bytes));
            Assert.AreEqual(LoadReasons.CraftTooLarge, job.ReasonCode); Assert.AreEqual(0, rig.Port.LoadCalls);
        }

        [TestMethod] public void AFileKspCannotParseIsRefusedAsUnreadable()
        {
            var text = "ship = X\nPART\n{\n\tpart = a_1\n";
            var job = Run(PutSource(text));
            Assert.AreEqual(LoadReasons.CraftUnreadable, job.ReasonCode); Assert.AreEqual(0, rig.Port.PipelineCalls);
        }

        // ---- pre-validation ----

        [TestMethod] public void ABrokenLinkIsRefusedBeforeTheEditorOrThePipelineIsTouched()
        {
            var text = EditorFake.CraftText().Replace("link = liquidEngine_100002", "link = liquidEngine_999999");
            var sha = PutSource(text);
            var token = rig.Token(); var craft = rig.Port.Craft;
            var job = Run(sha);
            Assert.AreEqual("failed", job.Status); Assert.AreEqual(LoadReasons.CraftInvalidLinks, job.ReasonCode);
            StringAssert.Contains(job.Detail, "dangling_link");
            Assert.IsFalse(job.Dispatched); Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
            Assert.AreEqual(0, rig.Port.PipelineCalls, "KSP's own code never sees a broken file");
            Assert.AreEqual(0, rig.Port.LoadCalls);
            Assert.AreEqual(craft, rig.Port.Craft, "the editor craft is unchanged");
            Assert.AreEqual(TokenCheck.Fresh, rig.Tracker.CheckToken(token, "epoch1"), "and so is its revision");
            Assert.IsFalse(rig.Files.Exists(Staging()));
        }

        [TestMethod] public void AMissingPartIsRefusedBeforeDispatch()
        {
            rig.Port.KnownParts = new HashSet<string> { "mk1pod.v2", "fuelTankSmall", "liquidEngine" };
            var job = Run(PutSource(EditorFake.CraftText().Replace("liquidEngine", "ghostEngine")));
            Assert.AreEqual(OperationReasons.CraftPartsMissing, job.ReasonCode);
            Assert.IsFalse(job.Dispatched); Assert.AreEqual(0, rig.Port.LoadCalls);
        }

        [TestMethod] public void AModuleThePrefabLacksIsRefusedBeforeDispatch()
        {
            var text = EditorFake.CraftText().Replace("name = ModuleProbe", "name = KspControlProbeMissing");
            var job = Run(PutSource(text));
            Assert.AreEqual(LoadReasons.ModuleNotInstalled, job.ReasonCode);
            StringAssert.Contains(job.Detail, "KspControlProbeMissing");
            Assert.IsFalse(job.Dispatched); Assert.AreEqual(0, rig.Port.LoadCalls);
            Assert.IsFalse(rig.Files.Exists(Staging()));
        }

        // ---- the upgrade pipeline ----

        [TestMethod] public void ASuccessfulPipelineThatChangesNothingLoadsTheVerifiedCopy()
        {
            rig.Port.PipelineTransform = node => { };
            var job = Run(PutSource());
            Assert.AreEqual("completed", job.Status);
            Assert.IsFalse(job.Load.UpgradedOnLoad); Assert.AreEqual(Staging(), rig.Port.LoadedPaths[0]);
            Assert.IsFalse(rig.Files.Exists(Upgraded()), "no upgraded file when nothing changed");
        }

        [TestMethod] public void APipelineThatChangesTheCraftIsRefusedUnlessUpgradeIsAllowed()
        {
            rig.Port.PipelineTransform = node => node.Children("PART").First().Entries.Add(new Pure.ConfigEntry("upgradedKey", "1"));
            var sha = PutSource();
            var job = Run(sha, false);
            Assert.AreEqual("failed", job.Status); Assert.AreEqual(LoadReasons.CraftRequiresUpgrade, job.ReasonCode);
            Assert.IsFalse(job.Dispatched); Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
            Assert.AreEqual(0, rig.Port.LoadCalls, "no load call");
            var envelope = job.ToEnvelope();
            Assert.AreEqual(true, (bool)envelope["upgradedOnLoad"]);
            Assert.IsTrue(((JArray)envelope["pipelineDifferences"]).Count > 0, "the reference node was not mutated with the work node");
            Assert.IsFalse(rig.Files.Exists(Staging())); Assert.IsFalse(rig.Files.Exists(Upgraded()));
            Assert.AreEqual(sha, OperationHash.Sha256Hex(rig.Files.Data[SourcePath]));
        }

        [TestMethod] public void AnUpgradeIsRefusedByDefault()
        {
            rig.Port.PipelineTransform = node => node.Children("PART").First().Entries.Add(new Pure.ConfigEntry("upgradedKey", "1"));
            var job = Run(PutSource());
            Assert.AreEqual(LoadReasons.CraftRequiresUpgrade, job.ReasonCode);
        }

        [TestMethod] public void WhenUpgradeIsAllowedTheStagedPipelineOutputIsLoadedAndTheSourceStaysUntouched()
        {
            rig.Port.PipelineTransform = node => node.Children("PART").First().Entries.Add(new Pure.ConfigEntry("upgradedKey", "1"));
            var sha = PutSource(); var before = rig.Files.Data[SourcePath];
            string loadedText = null;
            rig.Port.OnLoad = (fake, path) => { loadedText = rig.Files.Text(path); };
            var job = Run(sha, true);
            Assert.AreEqual("completed", job.Status, job.ReasonCode + " " + job.Detail);
            Assert.AreEqual(Upgraded(), rig.Port.LoadedPaths[0], "the *.upgraded.craft is what is loaded");
            StringAssert.Contains(loadedText, "upgradedKey = 1");
            Assert.IsTrue(job.Load.UpgradedOnLoad);
            var envelope = job.ToEnvelope();
            Assert.AreEqual(true, (bool)envelope["upgradedOnLoad"]);
            Assert.AreEqual("pipeline_output", (string)envelope["load"]["loadedNode"]);
            var differences = (JArray)envelope["pipelineDifferences"];
            Assert.IsTrue(differences.Any(d => ((string)d["path"]).Contains("upgradedKey")), differences.ToString());
            Assert.AreEqual(true, (bool)envelope["comparison"]["equal"], "pipeline output vs the loaded SaveShip");
            CollectionAssert.AreEqual(before, rig.Files.Data[SourcePath]);
            Assert.IsFalse(rig.Files.Exists(SourcePath + ".original"));
            Assert.IsFalse(rig.Files.Exists(Staging())); Assert.IsFalse(rig.Files.Exists(Upgraded()), "both staged files are removed");
            Assert.IsFalse(rig.Files.Writes.Any(p => p.StartsWith(ShipsDir, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(false, (bool)envelope["load"]["humanFileModified"]);
        }

        [TestMethod] public void AHeaderVersionBumpAloneCountsAsAnUpgrade()
        {
            rig.Port.PipelineTransform = node =>
            {
                var index = node.Entries.FindIndex(e => e.IsValue && e.Key == "version");
                node.Entries[index] = new Pure.ConfigEntry("version", "1.12.99");
            };
            var job = Run(PutSource(), false);
            Assert.AreEqual(LoadReasons.CraftRequiresUpgrade, job.ReasonCode);
            Assert.IsTrue(((JArray)job.ToEnvelope()["pipelineDifferences"]).Any(d => (string)d["path"] == "version"));
        }

        [TestMethod] public void AFailedPipelineDismissesThePopupAndRefusesWithNoLoad()
        {
            rig.Port.PipelineFails = true;
            var job = Run(PutSource());
            Assert.AreEqual("failed", job.Status); Assert.AreEqual(LoadReasons.CraftUpgradeFailed, job.ReasonCode);
            Assert.IsFalse(job.Dispatched); Assert.AreEqual(true, (bool)job.ToEnvelope()["notDispatched"]);
            Assert.IsTrue(job.Load.PopupDismissed); Assert.IsTrue(job.Load.LockRemoved);
            Assert.AreEqual(0, rig.Port.LoadCalls);
            Assert.IsFalse(rig.Files.Exists(Staging())); Assert.IsFalse(rig.Port.Locks.Contains(EditorIdle.OperationLockId));
            // A human click on the stale popup later reaches a closed gate and does nothing.
            rig.Port.LastGate.OnSuccess(new Pure.ConfigNode("")); rig.Port.LastGate.OnFail();
            Assert.AreEqual(2, rig.Port.LastGate.LateCalls);
        }

        [TestMethod] public void APipelineThatThrowsIsAnUpgradeFailure()
        {
            rig.Port.PipelineThrows = "NullReferenceException";
            var job = Run(PutSource());
            Assert.AreEqual(LoadReasons.CraftUpgradeFailed, job.ReasonCode); StringAssert.Contains(job.Detail, "NullReferenceException");
            Assert.AreEqual(0, rig.Port.LoadCalls);
        }

        // ---- after the load ----

        [TestMethod] public void ASourceThatChangesDuringTheLoadFailsTheJobButKeepsTheLoadedCopy()
        {
            var sha = PutSource();
            rig.Port.OnLoad = (fake, path) => rig.Files.Put(SourcePath, EditorFake.CraftText() + "// edited by a human\n");
            var job = Run(sha);
            Assert.AreEqual("failed", job.Status); Assert.AreEqual(LoadReasons.SourceChangedDuringLoad, job.ReasonCode);
            Assert.IsTrue(job.Dispatched);
            Assert.AreEqual(false, (bool)job.ToEnvelope()["load"]["sourceUnchanged"]);
            Assert.AreEqual(1, rig.Port.LoadCalls, "no automatic restore: the editor keeps the loaded copy");
            Assert.IsNotNull(job.Snapshot); Assert.IsTrue(rig.Files.Exists(job.Snapshot.CraftPath), "and the snapshot stays available");
            Assert.IsFalse(job.Restore.Attempted);
        }

        [TestMethod] public void ASidecarThatAppearsDuringTheLoadFailsTheJob()
        {
            var sha = PutSource();
            rig.Port.OnLoad = (fake, path) => rig.Files.Put(SourcePath + ".original", "x");
            var job = Run(sha);
            Assert.AreEqual(LoadReasons.SourceChangedDuringLoad, job.ReasonCode); StringAssert.Contains(job.Detail, ".original");
        }

        [TestMethod] public void AChangedLoadmetaIsReportedAndNeverAFailure()
        {
            var sha = PutSource(); var meta = Path.ChangeExtension(SourcePath, ".loadmeta");
            rig.Files.Put(meta, "before");
            rig.Port.OnLoad = (fake, path) => rig.Files.Put(meta, "after");
            var job = Run(sha);
            Assert.AreEqual("completed", job.Status, job.ReasonCode);
            Assert.IsTrue(job.Declared.Any(d => d.Class == "ships-sidecar" && d.Action == "reported"));
            Assert.AreEqual(true, (bool)job.ToEnvelope()["load"]["sourceUnchanged"]);
        }

        [TestMethod] public void ALoadThatLeavesTheEditorEmptyRestoresTheSnapshot()
        {
            var sha = PutSource(); var before = rig.Port.Craft;
            rig.Port.Mode = EditorFake.LoadMode.EmptyAfterLoad;
            var job = Run(sha);
            Assert.AreEqual("failed", job.Status); Assert.IsTrue(job.Dispatched);
            Assert.IsTrue(job.Restore.Attempted); Assert.IsNotNull(job.Snapshot);
            Assert.AreEqual(false, (bool)job.ToEnvelope()["notDispatched"]);
        }

        [TestMethod] public void AGenuineDifferenceAfterTheLoadIsReportedInTheComparison()
        {
            var sha = PutSource();
            rig.Port.OnLoad = (fake, path) => fake.Craft = fake.Craft.Replace("value = 10", "value = 11");
            var job = Run(sha);
            Assert.AreEqual("completed", job.Status);
            var comparison = job.ToEnvelope()["comparison"];
            Assert.AreEqual(false, (bool)comparison["equal"]);
            Assert.IsTrue(((JArray)comparison["differences"]).Count > 0);
        }

        [TestMethod] public void ThumbnailsAreHousekeptForTheShipNameAndBothStagedFiles()
        {
            var human = rig.Thumb("Probe"); rig.Files.Put(human, "human original");
            rig.Port.OnLoad = (f, p) => { rig.Files.Put(rig.Thumb("kc-load-0001"), "staged"); rig.Files.Put(rig.Thumb("Probe"), "generated"); };
            var job = Run(PutSource());
            Assert.AreEqual("completed", job.Status);
            Assert.AreEqual("human original", rig.Files.Text(human));
            Assert.IsFalse(rig.Files.Exists(rig.Thumb("kc-load-0001")));
        }

        [TestMethod] public void ALoadNeverRunsTheOperationBeforeTheLeaseIsHeld()
        {
            var sha = PutSource();
            var request = Request(sha); request.LeaseId = null;
            Assert.AreEqual(ControlReasons.LeaseRequired, rig.Service.Handle(request).ReasonCode);
        }
    }

    [TestClass]
    public class LoadValidatorTests
    {
        private static Pure.ConfigNode Parse(string text) { return Pure.ConfigText.Parse(text); }
        private static string Good() { return EditorFake.CraftText().Replace("\r\n", "\n"); }

        [TestMethod] public void ASoundCraftPasses() { Assert.IsNull(LoadValidator.Links(Parse(Good()))); }

        private static void AssertLinks(string text, string expected)
        {
            var check = LoadValidator.Links(Parse(text));
            Assert.IsNotNull(check, expected); Assert.AreEqual(LoadReasons.CraftInvalidLinks, check.Reason); StringAssert.Contains(check.Detail, expected);
        }

        [TestMethod] public void ADanglingLinkIsInvalid() { AssertLinks(Good().Replace("link = fuelTankSmall_100001", "link = fuelTankSmall_424242"), "dangling_link"); }
        [TestMethod] public void ALinkWhoseNameDoesNotMatchTheIdIsInvalid() { AssertLinks(Good().Replace("link = fuelTankSmall_100001", "link = wrongName_100001"), "dangling_link"); }
        [TestMethod] public void ADanglingStackPartnerIsInvalid() { AssertLinks(Good().Replace("attN = top,mk1pod.v2_100000_0|0.4|0", "attN = top,mk1pod.v2_777_0|0.4|0"), "dangling_attN"); }
        [TestMethod] public void ADanglingSymmetryReferenceIsInvalid() { AssertLinks(Good().Replace("\tattN = bottom,fuelTankSmall_100001_0|-0.405|0", "\tattN = bottom,fuelTankSmall_100001_0|-0.405|0\n\tsym = ghost_5"), "dangling_sym"); }
        [TestMethod] public void ADanglingSurfacePartnerIsInvalid() { AssertLinks(Good().Replace("\tattN = bottom,fuelTankSmall_100001_0|-0.405|0", "\tsrfN = srfAttach, ghost_9\n\tattN = bottom,fuelTankSmall_100001_0|-0.405|0"), "dangling_srfN"); }
        [TestMethod] public void ADuplicateCraftIdIsInvalid() { AssertLinks(Good().Replace("liquidEngine_100002", "fuelTankSmall_100001"), "duplicate_craft_id"); }
        [TestMethod] public void APartWithoutACraftIdIsInvalid() { AssertLinks(Good().Replace("part = liquidEngine_100002", "part = liquidEngine"), "invalid_part_id"); }
        [TestMethod] public void ASelfLinkIsInvalid() { AssertLinks(Good().Replace("link = liquidEngine_100002", "link = fuelTankSmall_100001"), "self_link"); }
        [TestMethod] public void ATwoParentCraftIsInvalid()
        {
            // The engine is linked from the tank and from the pod.
            AssertLinks(Good().Replace("\tattN = bottom,fuelTankSmall_100001_0|-0.405|0\n", "\tattN = bottom,fuelTankSmall_100001_0|-0.405|0\n\tlink = liquidEngine_100002\n"), "multiple_parents");
        }
        [TestMethod] public void ATwoRootCraftIsInvalid() { AssertLinks(Good().Replace("\tlink = fuelTankSmall_100001\r\n", "").Replace("\tlink = fuelTankSmall_100001\n", ""), "roots=2"); }
        [TestMethod] public void ACraftWithNoPartsIsInvalid() { AssertLinks("ship = x\nversion = 1\n", "no_parts"); }

        [TestMethod] public void ALinkCycleLeavesNoRoot()
        {
            var text = "ship = x\nPART\n{\n\tpart = a_1\n\tlink = b_2\n}\nPART\n{\n\tpart = b_2\n\tlink = a_1\n}\n";
            AssertLinks(text, "roots=0");
        }

        private static Func<string, IReadOnlyCollection<string>> Prefabs(Dictionary<string, string[]> table)
        {
            return name => { string[] modules; return table.TryGetValue(name, out modules) ? modules : null; };
        }

        [TestMethod] public void ModulesPresentOnThePrefabPass()
        {
            var table = new Dictionary<string, string[]> { { "mk1pod.v2", new[] { "ModuleProbe", "ModuleCryoTank" } }, { "fuelTankSmall", new[] { "ModuleProbe", "ModuleCryoTank" } }, { "liquidEngine", new[] { "ModuleProbe", "ModuleCryoTank" } } };
            Assert.IsNull(LoadValidator.Modules(Parse(Good()), Prefabs(table)));
        }

        [TestMethod] public void AModuleMissingFromThePrefabIsNotInstalled()
        {
            var table = new Dictionary<string, string[]> { { "mk1pod.v2", new[] { "ModuleCryoTank" } }, { "fuelTankSmall", new[] { "ModuleProbe", "ModuleCryoTank" } }, { "liquidEngine", new[] { "ModuleProbe", "ModuleCryoTank" } } };
            var check = LoadValidator.Modules(Parse(Good()), Prefabs(table));
            Assert.AreEqual(LoadReasons.ModuleNotInstalled, check.Reason); Assert.AreEqual("mk1pod.v2:ModuleProbe", check.Detail);
        }

        [TestMethod] public void AnUninstalledPartIsReportedAsMissingBeforeModules()
        {
            var table = new Dictionary<string, string[]> { { "mk1pod.v2", new string[0] } };
            var check = LoadValidator.Modules(Parse(Good()), Prefabs(table));
            Assert.AreEqual(OperationReasons.CraftPartsMissing, check.Reason); StringAssert.Contains(check.Detail, "fuelTankSmall");
        }

        [TestMethod] public void TheGateRecordsSuccessAndIgnoresEverythingAfterClose()
        {
            var gate = new UpgradeGate<string>();
            gate.OnSuccess("node");
            Assert.IsTrue(gate.Succeeded); Assert.AreEqual("node", gate.Output);
            gate.Close(); gate.OnSuccess("later"); gate.OnFail();
            Assert.AreEqual("node", gate.Output); Assert.AreEqual(2, gate.LateCalls); Assert.AreEqual(0, gate.FailCalls);
        }

        [TestMethod] public void AFailureCallbackIsNotASuccess()
        {
            var gate = new UpgradeGate<string>();
            gate.OnFail(); gate.Close(); gate.OnSuccess("late");
            Assert.IsFalse(gate.Succeeded); Assert.IsNull(gate.Output); Assert.AreEqual(1, gate.FailCalls); Assert.AreEqual(1, gate.LateCalls);
        }
    }
}
