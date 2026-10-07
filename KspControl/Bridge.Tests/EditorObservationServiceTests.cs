using System;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Pure = KspControl.EditorModel;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class EditorObservationServiceTests
    {
        private TrackerRig rig;
        private EditorObservationService service;

        [TestInitialize]
        public void Setup()
        {
            rig = new TrackerRig();
            service = new EditorObservationService(rig.Port, rig.Tracker, () => "epoch1");
        }

        private JObject State()
        { var result = service.State(); Assert.IsNull(result.Reason, result.Reason); return result.Data; }

        // ---- editor_state ----

        [TestMethod] public void StateEnvelopeCarriesTheRevisionTokenAndEditorFacts()
        {
            var data = State();
            Pure.EditorRevisionToken token;
            Assert.IsTrue(Pure.EditorRevisionToken.TryParse((string)data["editorRevision"], out token));
            Assert.AreEqual("epoch1", token.WorldEpoch); Assert.AreEqual(rig.Tracker.Generation, token.Generation); Assert.AreEqual(rig.Tracker.EditRevision, token.EditRevision);
            Assert.AreEqual(3, (int)data["partCount"]); Assert.AreEqual("VAB", (string)data["facility"]); Assert.AreEqual("Probe", (string)data["shipName"]);
            Assert.AreEqual(false, (bool)data["unsaved"]); Assert.AreEqual(true, (bool)data["idle"]); Assert.AreEqual("st_idle", (string)data["fsmState"]);
            Assert.AreEqual(0, ((JArray)data["busy"]).Count); Assert.AreEqual("full", (string)data["fingerprintMode"]); Assert.AreEqual(true, (bool)data["fingerprintAvailable"]);
            Assert.AreEqual("Probe", (string)data["lastSavedName"]); Assert.AreEqual(0, ((JArray)data["recentSnapshots"]).Count); Assert.AreEqual(JTokenType.Null, data["lastOperation"].Type);
            Assert.AreEqual("none", (string)data["operationWindow"]);
        }

        [TestMethod] public void TheStateTokenPassesAdmissionUntilTheCraftChanges()
        {
            var token = (string)State()["editorRevision"];
            Assert.AreEqual(TokenCheck.Fresh, rig.Tracker.CheckToken(token, "epoch1"));
            rig.Edit(EditorFake.CraftText(moduleValue: "99")); rig.Tracker.OnEvent(EditorEventKind.ShipModified); rig.Advance(300);
            Assert.AreEqual(TokenCheck.Stale, rig.Tracker.CheckToken(token, "epoch1"));
            Assert.AreNotEqual(token, (string)State()["editorRevision"]);
        }

        [TestMethod] public void TenStateCallsOnAnIdleEditorReturnTheSameTokenAndNeverTakeOver()
        {
            var first = (string)State()["editorRevision"];
            for (var i = 0; i < 10; i++) { rig.Advance(1000); Assert.AreEqual(first, (string)State()["editorRevision"]); }
        }

        [TestMethod] public void BusyReasonsAreReported()
        {
            rig.Port.Fsm = "st_place"; rig.Port.Selected = true; rig.Port.Locks.Add("Saving"); rig.Port.Locks.Add(EditorIdle.OperationLockId); rig.Port.Locks.Add(EditorIdle.LaunchLockId); rig.Port.Locks.Add("ClickThroughBlocker_x");
            var data = State();
            Assert.AreEqual(false, (bool)data["idle"]);
            CollectionAssert.AreEqual(new[] { "part_held", "fsm_not_idle", "modal_lock:Saving", "operation_running", "launch_pending" }, ((JArray)data["busy"]).Select(t => (string)t).ToArray());
        }

        [TestMethod] public void EmptyEditorInPodSelectIsReportedIdle()
        {
            rig.Port.Parts = 0; rig.Port.Craft = "ship = Empty\n"; rig.Port.Fsm = "st_podSelect";
            var data = State();
            Assert.AreEqual(true, (bool)data["idle"]); Assert.AreEqual(0, (int)data["partCount"]);
        }

        [TestMethod] public void CapabilityFlagsReportUnavailableWhenReflectionIsUnavailable()
        {
            rig.Port.Caps = EditorCapabilities.None(); rig.Port.Fsm = null; rig.Port.UnsavedValue = null; rig.Port.LastSaved = null;
            var data = State(); var caps = (JObject)data["capabilities"];
            Assert.AreEqual("unavailable", (string)caps["fsm"]); Assert.AreEqual("unavailable", (string)caps["unsavedMarker"]); Assert.AreEqual("unavailable", (string)caps["saveOverwriteGuard"]);
            Assert.AreEqual("unavailable", (string)data["fsmState"]); Assert.AreEqual("unknown", (string)data["unsaved"]); Assert.AreEqual("unavailable", (string)data["lastSavedName"]);
            Assert.AreEqual(true, (bool)data["idle"], "fallback: selected part and locks only");
            Assert.AreEqual(false, (bool)((JObject)caps["guardMembers"])["undoLevel"]);
        }

        [TestMethod] public void CapabilityFlagsReportAvailableWhenTheProbeSucceeded()
        {
            var caps = (JObject)State()["capabilities"];
            Assert.AreEqual("available", (string)caps["fsm"]); Assert.AreEqual("available", (string)caps["unsavedMarker"]); Assert.AreEqual("available", (string)caps["saveOverwriteGuard"]);
            Assert.AreEqual(true, (bool)((JObject)caps["guardMembers"])["setLastSanitizedSaveName"]);
        }

        [TestMethod] public void UnsavedIsReportedAsABooleanOrUnknown()
        {
            rig.Port.UnsavedValue = true; Assert.AreEqual(true, (bool)State()["unsaved"]);
            rig.Port.UnsavedValue = null; Assert.AreEqual("unknown", (string)State()["unsaved"]);
        }

        [TestMethod] public void GuardSentinelIsNeverEchoedWithItsControlCharacter()
        {
            rig.Port.LastSaved = EditorObservationService.GuardSentinel;
            var text = (string)State()["lastSavedName"];
            Assert.AreEqual("kspcontrol_unsaved_sentinel", text); Assert.IsFalse(text.Any(char.IsControl));
        }

        [TestMethod] public void ShipNameDropsControlCharactersAndIsBounded()
        {
            rig.Port.Name = "A\u0001B\n" + new string('x', 400); rig.Advance(16);
            var name = (string)State()["shipName"];
            Assert.IsTrue(name.StartsWith("AB")); Assert.AreEqual(256, name.Length);
        }

        [TestMethod] public void HeavyFingerprintReportsDirtyTrackedModeAndCost()
        {
            rig.Port.CaptureCost = 35; var data = State();
            Assert.AreEqual("dirty_tracked", (string)data["fingerprintMode"]); Assert.AreEqual(35.0, (double)data["lastPollCostMs"], 1e-9);
        }

        [TestMethod] public void StateOutsideTheEditorIsEditorUnavailable()
        {
            rig.Port.InEditorValue = false; var result = service.State();
            Assert.AreEqual(ControlReasons.EditorUnavailable, result.Reason); Assert.IsNull(result.Data);
        }

        [TestMethod] public void StateWithoutAFingerprintHasANullTokenAndAnUnavailableFlag()
        {
            rig.Port.Ship = new object(); rig.Port.CaptureThrows = true;
            var data = State();
            Assert.AreEqual(JTokenType.Null, data["editorRevision"].Type); Assert.AreEqual(false, (bool)data["fingerprintAvailable"]);
        }

        // ---- editor_engineering ----

        private static EngineeringData Data()
        {
            var data = new EngineeringData { PartTotal = 3, DryMassTonnes = 1.25, FuelMassTonnes = 0.5, DryCostFunds = 1200, FuelCostFunds = 300, AllPartsConnected = true, ShipPartsUnlocked = true, PartsStockAllowed = true, MaxStackNodeGapMetres = 0.0004, DeltaVReady = true };
            data.Parts.Add(new EngineeringPart { CraftId = 100000, Name = "mk1pod.v2", AttachKind = "root", Stage = -1, MassTonnes = 0.8, ResourceMassTonnes = 0.01 });
            data.Parts.Add(new EngineeringPart { CraftId = 100001, Name = "fuelTankSmall", ParentCraftId = 100000, AttachKind = "stack", Node = "top", ParentNode = "bottom", Stage = 1, MassTonnes = 0.1, SymmetryGroup = 100001 });
            var stage = new EngineeringStage { Stage = 1 }; stage.Parts.Add(new EngineeringStagePart { CraftId = 100002, Name = "liquidEngine" }); data.Stages.Add(stage);
            data.DeltaV.Add(new EngineeringDeltaV { Stage = 1, DeltaVVac = 968, DeltaVAtmosphere = 800, TwrVac = 7.6, TwrAtmosphere = 6.1, StartMassTonnes = 2.2, EndMassTonnes = 1.1, BurnTimeSeconds = 34.5 });
            return data;
        }

        private JObject Engineering(JObject args = null)
        { var result = service.Engineering(args); Assert.IsNull(result.Reason, result.Reason + " " + result.Detail); return result.Data; }

        [TestMethod] public void EngineeringEnvelopeHasTotalsStagesDeltaVPartsAndProvenance()
        {
            rig.Port.Engineering = Data();
            var data = Engineering(new JObject { ["offset"] = 0, ["limit"] = 2, ["includeDeltaV"] = true });
            Assert.AreEqual(3, (int)data["partTotal"]); Assert.AreEqual(2, ((JArray)data["parts"]).Count); Assert.AreEqual(2, (int)data["nextOffset"]);
            var totals = (JObject)data["totals"];
            Assert.AreEqual(1.25, (double)totals["dryMassTonnes"], 1e-9); Assert.AreEqual(300.0, (double)totals["fuelCostFunds"], 1e-9); Assert.AreEqual(true, (bool)totals["allPartsConnected"]); Assert.AreEqual(true, (bool)totals["shipPartsUnlocked"]);
            var stage = (JObject)((JArray)data["stages"])[0]; Assert.AreEqual(1, (int)stage["stage"]); Assert.AreEqual(100002u, (uint)((JArray)stage["parts"])[0]["craftId"]);
            var dv = (JObject)data["deltaV"]; Assert.AreEqual(true, (bool)dv["ready"]); Assert.AreEqual(968.0, (double)((JArray)dv["stages"])[0]["deltaVVacuum"], 1e-9);
            var second = (JObject)((JArray)data["parts"])[1];
            Assert.AreEqual("fuelTankSmall_100001", (string)second["ref"]); Assert.AreEqual(100000u, (uint)second["parentCraftId"]); Assert.AreEqual("stack", (string)second["attach"]["kind"]);
            Assert.AreEqual("top", (string)second["attach"]["node"]); Assert.AreEqual("bottom", (string)second["attach"]["parentNode"]); Assert.AreEqual(100001u, (uint)second["symmetryGroup"]);
            Assert.AreEqual(JTokenType.Null, ((JObject)((JArray)data["parts"])[0])["parentCraftId"].Type);
            Assert.AreEqual(0.0004, (double)data["geometry"]["maxLinkedStackNodeGapMetres"], 1e-12);
            Assert.AreEqual("deferred_to_P3", (string)data["toolingQuote"]); Assert.AreEqual("deferred_to_P3", (string)data["launchResearchAllowance"]);
            Assert.AreEqual(true, (bool)data["partsStockAllowed"]); Assert.AreEqual("PartTechAvailable_and_PartModelPurchased", (string)data["partsStockAllowedBasis"]);
            Assert.AreEqual(true, (bool)data["craftIdentifiersValid"]); Assert.IsNotNull(data["provenance"]["totals"]);
        }

        [TestMethod] public void LastPageHasNoNextOffset()
        {
            rig.Port.Engineering = Data();
            var data = Engineering(new JObject { ["offset"] = 2, ["limit"] = 50 });
            Assert.AreEqual(JTokenType.Null, data["nextOffset"].Type);
        }

        [TestMethod] public void SandboxWithoutResearchReportsNullPartsStockAllowed()
        {
            var sandbox = Data(); sandbox.PartsStockAllowed = null; rig.Port.Engineering = sandbox;
            var data = Engineering();
            Assert.AreEqual(JTokenType.Null, data["partsStockAllowed"].Type); Assert.AreEqual("no_research_instance", (string)data["partsStockAllowedBasis"]);
        }

        [TestMethod] public void DeniedPartsAreListedAndBounded()
        {
            var denied = Data(); denied.PartsStockAllowed = false; for (var i = 0; i < 40; i++) denied.DeniedParts.Add("part" + i); rig.Port.Engineering = denied;
            var data = Engineering();
            Assert.AreEqual(false, (bool)data["partsStockAllowed"]); Assert.AreEqual(16, ((JArray)data["deniedParts"]).Count);
        }

        [TestMethod] public void DeltaVNotReadyOrNotRequestedYieldsNoStages()
        {
            var notReady = Data(); notReady.DeltaVReady = false; rig.Port.Engineering = notReady;
            var data = Engineering(new JObject { ["includeDeltaV"] = true });
            Assert.AreEqual(false, (bool)data["deltaV"]["ready"]); Assert.AreEqual(0, ((JArray)data["deltaV"]["stages"]).Count);
            rig.Port.Engineering = Data();
            var skipped = Engineering(new JObject { ["includeDeltaV"] = false });
            Assert.AreEqual(false, (bool)skipped["deltaV"]["requested"]); Assert.AreEqual(0, ((JArray)skipped["deltaV"]["stages"]).Count);
        }

        [TestMethod] public void NonFiniteNumbersBecomeNullNotInvalidJson()
        {
            var bad = Data(); bad.DryMassTonnes = double.NaN; bad.MaxStackNodeGapMetres = double.PositiveInfinity; rig.Port.Engineering = bad;
            var data = Engineering();
            Assert.AreEqual(JTokenType.Null, data["totals"]["dryMassTonnes"].Type); Assert.AreEqual(JTokenType.Null, data["geometry"]["maxLinkedStackNodeGapMetres"].Type);
            Assert.IsNotNull(data.ToString(Newtonsoft.Json.Formatting.None));
        }

        [DataTestMethod]
        [DataRow("offset", -1, "offset")] [DataRow("offset", 100001, "offset")] [DataRow("limit", 0, "limit")] [DataRow("limit", 51, "limit")] [DataRow("limit", -3, "limit")]
        public void OutOfRangeArgumentsAreInvalidArgumentWithTheFieldNamed(string field, int value, string expected)
        {
            rig.Port.Engineering = Data();
            var result = service.Engineering(new JObject { [field] = value });
            Assert.AreEqual(ControlReasons.InvalidArgument, result.Reason); StringAssert.Contains(result.Detail, expected);
        }

        [TestMethod] public void WrongArgumentTypesAreInvalidArgument()
        {
            foreach (var args in new[] { new JObject { ["offset"] = "1" }, new JObject { ["limit"] = 2.5 }, new JObject { ["includeDeltaV"] = "yes" }, new JObject { ["limit"] = true } })
                Assert.AreEqual(ControlReasons.InvalidArgument, service.Engineering(args).Reason, args.ToString());
        }

        [TestMethod] public void BoundaryArgumentsAreAccepted()
        {
            rig.Port.Engineering = Data();
            foreach (var args in new[] { new JObject { ["offset"] = 0, ["limit"] = 1 }, new JObject { ["offset"] = 100000, ["limit"] = 50 }, null, new JObject() })
                Assert.IsNull(service.Engineering(args).Reason);
        }

        [TestMethod] public void EngineeringOutsideTheEditorIsEditorUnavailable()
        {
            rig.Port.InEditorValue = false;
            Assert.AreEqual(ControlReasons.EditorUnavailable, service.Engineering(new JObject()).Reason);
        }

        [TestMethod] public void EngineeringArgumentsAreCheckedBeforeTheEditorIsTouched()
        {
            rig.Port.InEditorValue = false;
            Assert.AreEqual(ControlReasons.InvalidArgument, service.Engineering(new JObject { ["limit"] = 0 }).Reason);
        }

        [TestMethod] public void EngineeringCaptureIsUnderTheSelfEventGuard()
        {
            rig.Port.Engineering = Data(); rig.Sink.LeaseHeld = true; rig.Advance(16);
            var revision = rig.Revision;
            rig.Port.DuringCapture = () => rig.Tracker.OnEvent(EditorEventKind.PartPicked);
            Assert.IsNull(service.Engineering(new JObject()).Reason);
            Assert.AreEqual(0, rig.Sink.Takeovers); Assert.AreEqual(revision, rig.Revision); Assert.IsTrue(rig.Tracker.SelfEvents >= 1);
        }

        [TestMethod] public void StateReportsUnreadableFsmAsBusy()
        {
            rig.Port.Fsm = EditorIdle.FsmUnreadable;
            var data = State();
            Assert.AreEqual(false, (bool)data["idle"]); Assert.AreEqual("fsm_unreadable", (string)((JArray)data["busy"])[0]);
        }

        [TestMethod] public void StateWithAFailedCaptureReportsFingerprintUnavailable()
        {
            rig.Port.CaptureThrows = true;
            var data = State();
            Assert.AreEqual(false, (bool)data["fingerprintAvailable"]); Assert.AreEqual(JTokenType.Null, data["editorRevision"].Type);
        }

        [TestMethod] public void CraftIdentifiersAreCheckedAgainstTheToolingRule()
        {
            rig.Port.Engineering = Data();
            Assert.AreEqual(true, (bool)Engineering()["craftIdentifiersValid"]);
            rig.Port.Craft = EditorFake.CraftText().Replace("part = fuelTankSmall_100001", "part = fuelTankSmall_100000");
            var duplicate = Engineering();
            Assert.AreEqual(false, (bool)duplicate["craftIdentifiersValid"]); StringAssert.StartsWith((string)duplicate["craftIdentifiersProblem"], "duplicate_identifier");
            rig.Port.Craft = EditorFake.CraftText().Replace("part = liquidEngine_100002", "part = liquidEngine");
            var invalid = Engineering();
            Assert.AreEqual(false, (bool)invalid["craftIdentifiersValid"]); StringAssert.StartsWith((string)invalid["craftIdentifiersProblem"], "invalid_identifier");
            rig.Port.CaptureThrows = true;
            var unavailable = Engineering();
            Assert.AreEqual(false, (bool)unavailable["craftIdentifiersValid"]); Assert.AreEqual("capture_unavailable", (string)unavailable["craftIdentifiersProblem"]);
        }

        [TestMethod] public void IdentifierRuleMirrorsCraftIndices()
        {
            string problem;
            Assert.IsTrue(CraftIdentifierRule.Check(Pure.ConfigText.Parse("PART\n{\n part = a_b_12\n}\nPART\n{\n part = c_13\n}\n"), out problem), "name may contain underscores; the suffix after the last one counts");
            Assert.IsFalse(CraftIdentifierRule.Check(Pure.ConfigText.Parse("PART\n{\n part = a_-5\n}\n"), out problem));
            Assert.IsFalse(CraftIdentifierRule.Check(Pure.ConfigText.Parse("PART\n{\n part = a_1\n}\nPART\n{\n part = b_1\n}\n"), out problem));
            Assert.IsFalse(CraftIdentifierRule.Check(Pure.ConfigText.Parse("PART\n{\n name = x\n}\n"), out problem));
            Assert.IsFalse(CraftIdentifierRule.Check(null, out problem));
            Assert.IsTrue(CraftIdentifierRule.Check(Pure.ConfigText.Parse("ship = empty\n"), out problem), "no parts: nothing to reject");
        }
    }
}
