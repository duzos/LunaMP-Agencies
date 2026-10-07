using System;
using System.Collections.Generic;
using System.Linq;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class FlightEffectClassifierTests
    {
        private const string Vessel = "11111111-1111-1111-1111-111111111111";

        [TestMethod]
        public void TheVesselEffectIsAlwaysFirstAndBenignModulesAddNoConsequence()
        {
            var plan = FlightEffectClassifier.Plan(Vessel, new[] { FakeFlight.Act("ModuleLight", "5"), FakeFlight.Act("ModuleLandingGear", "6") }, false);
            Assert.IsTrue(plan.Allowed); Assert.AreEqual(0, plan.Consequential.Count);
            Assert.AreEqual(FlightEffects.Family, plan.Effects[0].Operation); Assert.AreEqual("vessel:" + Vessel, plan.Effects[0].Recipient);
            Assert.AreEqual(3, plan.Effects.Length);
        }

        [TestMethod]
        public void EveryKnownConsequentialKindIsNamed()
        {
            var modules = new Dictionary<string, string>
            {
                { "ModuleEngines", "engine" }, { "ModuleRCSFX", "thruster" }, { "ModuleAnchoredDecoupler", "decouple" }, { "ModuleDockingNode", "dock" },
                { "ModuleParachute", "chute" }, { "LaunchClamp", "release" }, { "ModuleScienceExperiment", "science" }, { "ModuleProceduralFairing", "decouple" }
            };
            foreach (var pair in modules)
            {
                var plan = FlightEffectClassifier.Plan(Vessel, new[] { FakeFlight.Act(pair.Key, "9", "Go") }, false);
                Assert.IsTrue(plan.Allowed, pair.Key);
                Assert.AreEqual(pair.Value + ":9/" + pair.Key + ".Go", plan.Consequential.Single());
                Assert.AreEqual(FlightEffects.Family, plan.Effects.Last().Operation);
            }
        }

        [TestMethod]
        public void AnUnknownModuleIsUnclassifiedAndItsEffectIsNotAKnownEffect()
        {
            var plan = FlightEffectClassifier.Plan(Vessel, new[] { FakeFlight.Act("ModuleLight", "1"), FakeFlight.Act("ModMissileLauncher", "2", "Fire") }, false);
            Assert.IsFalse(plan.Allowed);
            Assert.AreEqual("2/ModMissileLauncher.Fire", plan.Unclassified.Single());
            Assert.AreEqual(FlightEffects.Unclassified, plan.Effects.Last().Operation);
            Assert.AreEqual(-1, Array.IndexOf(GrantMapping.KnownEffects, FlightEffects.Unclassified), "the authority must refuse it too");
        }

        [TestMethod]
        public void StagingOnlyCountsModulesThatActOnActivationOrAreNamedConsequential()
        {
            var plan = FlightEffectClassifier.Plan(Vessel, new[]
            {
                FakeFlight.Act("ModuleSomethingInert", "1", null, false), FakeFlight.Act("ModuleDecouple", "2", null, false), FakeFlight.Act("ModuleModThing", "3", null, true)
            }, true);
            Assert.IsFalse(plan.Allowed);
            Assert.AreEqual("3/ModuleModThing", plan.Unclassified.Single());
            Assert.AreEqual("decouple:2/ModuleDecouple", plan.Consequential.Single());
        }

        [TestMethod]
        public void AnActionGroupCountsEveryBoundActionRegardlessOfTheStagingFlag()
        {
            var plan = FlightEffectClassifier.Plan(Vessel, new[] { FakeFlight.Act("ModuleSomethingInert", "1", "Do", false) }, false);
            Assert.IsFalse(plan.Allowed);
        }

        [TestMethod]
        public void ARunawayEffectCountIsRefusedNotTruncated()
        {
            var many = Enumerable.Range(0, 1200).Select(i => FakeFlight.Act("ModuleLight", i.ToString())).ToArray();
            var plan = FlightEffectClassifier.Plan(Vessel, many, false);
            Assert.IsFalse(plan.Allowed); CollectionAssert.Contains(plan.Unclassified, "too_many_effects");
        }

        [TestMethod]
        public void ThePlanIsDeterministicSoRevalidationCanCompareIt()
        {
            var actions = new[] { FakeFlight.Act("ModuleEngines", "1"), FakeFlight.Act("ModuleLight", "2") };
            var a = FlightEffectClassifier.Plan(Vessel, actions, false); var b = FlightEffectClassifier.Plan(Vessel, actions, false);
            CollectionAssert.AreEqual(a.Effects.Select(e => e.Operation + e.Recipient).ToArray(), b.Effects.Select(e => e.Operation + e.Recipient).ToArray());
        }
    }

    [TestClass]
    public class FlightStateServiceTests
    {
        private FakeFlight flight;
        private FlightStateService service;

        [TestInitialize]
        public void Setup() { flight = new FakeFlight(); service = new FlightStateService(flight, () => "epoch1"); }

        [TestMethod]
        public void AnOwnedVesselReportsEveryRequestedFieldWithUnitsInTheNames()
        {
            flight.Snap.Controls = new FlightControlStates { Throttle = 0.25, Sas = true, Rcs = true, Gear = false, Lights = true, Brakes = true, CurrentStage = 2, StageCount = 4 };
            flight.Groups["SAS"] = true; flight.Groups["RCS"] = true; flight.Groups["Light"] = true; flight.Groups["Brakes"] = true;
            flight.Snap.DeltaVReady = true; flight.Snap.TotalDeltaVActual = 3200;
            flight.Snap.Stages.Add(new FlightStageDeltaV { Stage = 1, DeltaVActual = 1200, DeltaVVacuum = 1500, DeltaVSeaLevel = 1100, BurnTimeSeconds = 80, ThrustToWeightActual = 1.4, MassKilograms = 5000 });
            flight.Snap.Resources.Add(new FlightResource { Name = "LiquidFuel", Amount = 90, Capacity = 180 });
            flight.Snap.CrewCount = 2; flight.Snap.MechJebPresent = true;
            flight.Snap.Warp.CurrentIndex = 3;
            var result = service.State();
            Assert.IsNull(result.Reason);
            var d = result.Data;
            Assert.AreEqual("Probe", (string)d["vessel"]["name"]); Assert.AreEqual("vessel:" + flight.Snap.VesselId, (string)d["vessel"]["entity"]);
            Assert.AreEqual("ORBITING", (string)d["situation"]); Assert.AreEqual("Kerbin", (string)d["body"]);
            Assert.AreEqual(1234.5, (double)d["universalTimeSeconds"], 1e-9);
            foreach (var key in new[] { "altitudeAboveSeaLevelMetres", "verticalSpeedMetresPerSecond", "surfaceSpeedMetresPerSecond", "orbitalSpeedMetresPerSecond" }) Assert.IsNotNull(d[key], key);
            var orbit = d["orbit"];
            Assert.AreEqual("Kerbin", (string)orbit["referenceBody"]);
            foreach (var key in new[] { "apoapsisAltitudeMetres", "periapsisAltitudeMetres", "inclinationDegrees", "eccentricity", "periodSeconds", "timeToApoapsisSeconds", "timeToPeriapsisSeconds" }) Assert.IsNotNull(orbit[key], key);
            Assert.AreEqual(2, (int)d["stage"]["current"]); Assert.IsTrue((bool)d["stage"]["deltaVReady"]);
            Assert.AreEqual(1200.0, (double)d["stage"]["perStage"][0]["deltaVActualMetresPerSecond"], 1e-9);
            Assert.AreEqual("LiquidFuel", (string)d["resources"]["totals"][0]["name"]);
            Assert.AreEqual(0.25, (double)d["controls"]["throttleFraction"], 1e-9);
            Assert.IsTrue((bool)d["controls"]["sas"]); Assert.IsTrue((bool)d["controls"]["rcs"]); Assert.IsFalse((bool)d["controls"]["gear"]); Assert.IsTrue((bool)d["controls"]["lights"]); Assert.IsTrue((bool)d["controls"]["brakes"]);
            Assert.AreEqual(3, (int)d["warp"]["rateIndex"]); Assert.AreEqual(1000.0, (double)d["warp"]["capEffectiveRate"], 1e-9);
            Assert.AreEqual(2, (int)d["crew"]["count"]); Assert.IsTrue((bool)d["controlState"]["mechJebPresent"]);
            Assert.AreEqual("epoch1", (string)d["epoch"]);
        }

        [TestMethod]
        public void TheCurrentOrbitReferenceBodyIsSeparateFromAForecast()
        {
            flight.Snap.Body = "Kerbin"; flight.Snap.Orbit.ReferenceBody = "Kerbin"; flight.Snap.Orbit.PredictedNextBody = "Mun"; flight.Snap.Orbit.PatchEndTransition = "ENCOUNTER";
            var orbit = service.State().Data["orbit"];
            Assert.AreEqual("Kerbin", (string)orbit["referenceBody"]); Assert.AreEqual("Mun", (string)orbit["predictedNextBody"]);
            StringAssert.Contains((string)orbit["predictedNote"], "never proof");
            flight.Snap.Orbit.ReferenceBody = "Mun"; flight.Snap.Body = "Mun";
            Assert.AreEqual("Mun", (string)service.State().Data["orbit"]["referenceBody"]);
        }

        [TestMethod]
        public void AVesselTheAgencyDoesNotOwnDisclosesNothing()
        {
            flight.Snap.Owned = false;
            var result = service.State();
            Assert.AreEqual(FlightReasons.VesselNotOwned, result.Reason); Assert.IsNull(result.Data);
        }

        [TestMethod]
        public void OutsideFlightOrWithoutAVesselThereIsNoState()
        {
            flight.InFlightValue = false; Assert.AreEqual(FlightReasons.FlightUnavailable, service.State().Reason);
            flight.InFlightValue = true; flight.Snap = null; Assert.AreEqual(FlightReasons.FlightUnavailable, service.State().Reason);
        }

        [TestMethod]
        public void StagesAndResourcesAreBounded()
        {
            for (var i = 0; i < 50; i++) { flight.Snap.Stages.Add(new FlightStageDeltaV { Stage = i }); flight.Snap.Resources.Add(new FlightResource { Name = "R" + i }); }
            var d = service.State().Data;
            Assert.AreEqual(FlightStateService.MaxStages, d["stage"]["perStage"].Count()); Assert.IsTrue((bool)d["stage"]["truncated"]);
            Assert.AreEqual(FlightStateService.MaxResources, d["resources"]["totals"].Count()); Assert.IsTrue((bool)d["resources"]["truncated"]);
        }

        [TestMethod]
        public void NonFiniteNumbersBecomeNullAndDeltaVIsNullUntilReady()
        {
            flight.Snap.Orbit.PeriodSeconds = double.NaN; flight.Snap.Orbit.TimeToApoapsis = double.PositiveInfinity; flight.Snap.DeltaVReady = false; flight.Snap.TotalDeltaVActual = 99;
            var d = service.State().Data;
            Assert.AreEqual(JTokenType.Null, d["orbit"]["periodSeconds"].Type); Assert.AreEqual(JTokenType.Null, d["orbit"]["timeToApoapsisSeconds"].Type);
            Assert.AreEqual(JTokenType.Null, d["stage"]["totalDeltaVActualMetresPerSecond"].Type);
        }

        [TestMethod]
        public void TheReportNeverContainsGrantOrLeaseMaterial()
        {
            var text = service.State().Data.ToString().ToLowerInvariant();
            foreach (var word in new[] { "leaseid", "grant", "token", "secret" }) Assert.IsFalse(text.Contains(word), word);
        }
    }

    [TestClass]
    public class FlightGrantAndAuthorityTests
    {
        private static GrantPayload Payload(string[] operations, string[] facilities)
        {
            return new GrantPayload
            {
                GrantId = "g1", Generation = 1, IssuedUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0), ExpiresUtc = GrantPayload.FormatUtc(AuthorityHelpers.Utc0.AddHours(8)),
                Binding = new GrantBindingInfo { InstallId = "install", SaveFolder = "save", Agency = "agency" }, Operations = operations, Facilities = facilities,
                UnsavedCraftPolicy = "refuse", MaxParts = 250
            };
        }

        [TestMethod]
        public void FlightControlIsInTheAuthoritysEffectMap()
        {
            CollectionAssert.Contains(GrantMapping.KnownEffects, FlightEffects.Family);
        }

        [TestMethod]
        public void TheFlightFacilityBindsTheFamilyToAnyVesselAndNothingElse()
        {
            var payload = Payload(new[] { "flight.control", "editor.replace_craft" }, new[] { "VAB", "FLIGHT" });
            Assert.IsNull(GrantCodec.Validate(payload));
            var grant = GrantMapping.ToGrant(payload);
            Assert.IsTrue(grant.AllowsEntity("vessel:aaaa")); Assert.IsTrue(grant.AllowsEntity("editor:VAB")); Assert.IsFalse(grant.AllowsEntity("editor:SPH"));
            Assert.IsTrue(grant.Allows(new ClassifiedEffect("flight.control", "vessel:aaaa/engine:5/ModuleEngines", 0)));
            Assert.IsTrue(grant.Allows(new ClassifiedEffect("editor.replace_craft", "editor:VAB", 0)));
            Assert.IsFalse(grant.Allows(new ClassifiedEffect("editor.replace_craft", "vessel:aaaa", 0)), "an editor family never reaches a vessel");
            Assert.IsFalse(grant.Allows(new ClassifiedEffect("flight.control", "editor:VAB", 0)), "the flight family never reaches an editor");
        }

        [TestMethod]
        public void AGrantWithoutTheFlightFacilityOrWithoutTheFamilyAllowsNoFlightEffect()
        {
            var noFacility = GrantMapping.ToGrant(Payload(new[] { "flight.control" }, new[] { "VAB" }));
            Assert.IsFalse(noFacility.AllowsEntity("vessel:a")); Assert.IsFalse(noFacility.Allows(new ClassifiedEffect("flight.control", "vessel:a", 0)));
            var noFamily = GrantMapping.ToGrant(Payload(new[] { "editor.replace_craft" }, new[] { "FLIGHT" }));
            Assert.IsTrue(noFamily.AllowsEntity("vessel:a")); Assert.IsFalse(noFamily.Allows(new ClassifiedEffect("flight.control", "vessel:a", 0)));
        }

        [TestMethod]
        public void AllThreeFacilitiesValidateAndFourDoNot()
        {
            Assert.IsNull(GrantCodec.Validate(Payload(new[] { "flight.control" }, new[] { "VAB", "SPH", "FLIGHT" })));
            Assert.AreEqual("facilities", GrantCodec.Validate(Payload(new[] { "flight.control" }, new[] { "VAB", "SPH", "FLIGHT", "FLIGHT" })));
            Assert.AreEqual("facilities", GrantCodec.Validate(Payload(new[] { "flight.control" }, new[] { "LAUNCHPAD" })));
        }

        [TestMethod]
        public void AnUnclassifiedEffectIsDeniedByTheAuthorityItself()
        {
            var rig = new FlightRig();
            var effects = new[] { new ClassifiedEffect(FlightEffects.Family, "vessel:" + rig.Flight.Snap.VesselId, 0), new ClassifiedEffect(FlightEffects.Unclassified, "vessel:x/part:1/Mod", 0) };
            var error = Assert.ThrowsException<InvalidOperationException>(() => rig.Authority.Admit(rig.Lease, 0, effects));
            Assert.AreEqual("effect_denied_or_unknown", error.Message);
        }

        [TestMethod]
        public void LeaseEndedFiresOncePerEndedLeaseWithItsReason()
        {
            var rig = new FlightRig(); var reasons = new List<string>();
            rig.Authority.LeaseEnded += reasons.Add;
            rig.Authority.HumanTakeover();
            rig.Authority.HumanTakeover(); // nothing held any more
            CollectionAssert.AreEqual(new[] { "human_takeover" }, reasons);
        }

        [TestMethod]
        public void AFlightSceneThatIsNotReadyRefusesALeaseAsFlightUnavailableAndAnEditorKeepsItsOwnCode()
        {
            var rig = new FlightRig(lease: false);
            rig.Context.Ready = false; rig.Frame();
            var error = Assert.ThrowsException<InvalidOperationException>(() => rig.Authority.AcquireLease(300000, "p"));
            Assert.AreEqual(FlightReasons.FlightUnavailable, error.Message);
            var editor = new OperationRig(lease: false);
            editor.Port.SceneReadyValue = false; editor.Frame();
            var editorError = Assert.ThrowsException<InvalidOperationException>(() => editor.Authority.AcquireLease(300000, "p"));
            Assert.AreEqual(ControlReasons.EditorUnavailable, editorError.Message);
        }

        [TestMethod]
        public void ExactEditorMatchingIsUnchangedByTheWildcardRule()
        {
            var grant = AuthorityHelpers.Grant();
            Assert.IsTrue(grant.AllowsEntity("editor:VAB")); Assert.IsFalse(grant.AllowsEntity("editor:VABX")); Assert.IsFalse(grant.AllowsEntity("editor:*"));
        }
    }
}
