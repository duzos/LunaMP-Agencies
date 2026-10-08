using System;
using System.Linq;
using FakeAtmosphere;
using FakeMechJeb;
using KspControl.Bridge;
using KspControl.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace KspControl.BridgeTests
{
    [TestClass]
    public class MechJebAdapterTests
    {
        private MechJebCore core;
        private int typeLookups;
        private object vessel;

        [TestInitialize] public void Setup() { core = new MechJebCore(); vessel = new object(); AtmosphereAutopilot.Instance = null; UserPool.AddEnablesModule = true; }
        [TestCleanup] public void Cleanup() { AtmosphereAutopilot.Instance = null; UserPool.AddEnablesModule = true; }

        private MechJebAdapter Make(Func<string, object> source = null, Type atmosphere = null, string version = "2.15.2.0", Type coreType = null)
        {
            return new MechJebAdapter(source ?? (id => core), id => vessel,
                () => { typeLookups++; return coreType ?? typeof(MechJebCore); }, () => atmosphere, t => Version.Parse(version));
        }

        // ---- capabilities: resolved once, version-checked ----

        [TestMethod] public void CapabilitiesAreResolvedOnceAndFlagEveryModule()
        {
            var adapter = Make();
            var caps = adapter.Capabilities; var again = adapter.Capabilities;
            Assert.AreSame(caps, again); Assert.AreEqual(1, typeLookups);
            Assert.IsTrue(caps.Installed && caps.VersionSupported && caps.Usable); Assert.AreEqual("available", caps.State); Assert.AreEqual("2.15.2.0", caps.Version);
            foreach (var module in new[] { "ascent", "ascentSettings", "node", "landing", "rendezvous", "docking", "spaceplane", "airplane", "attitude", "rover", "thrust", "warp" })
                Assert.IsTrue(caps.Has(module), module);
            CollectionAssert.AreEquivalent(new[] { "OperationCircularize", "OperationInterplanetaryTransfer" }, caps.PlannerOperations);
        }

        [TestMethod] public void AMissingMechJebIsUnavailable()
        {
            var adapter = new MechJebAdapter(id => null, null, () => null, () => null);
            var caps = adapter.Capabilities;
            Assert.IsFalse(caps.Installed); Assert.AreEqual("unavailable", caps.State); Assert.AreEqual("mechjeb_unavailable", caps.Reason);
            var status = adapter.ReadStatus();
            Assert.AreEqual("unavailable", (string)status["mechjeb"]); Assert.AreEqual(false, (bool)status["vesselCore"]);
        }

        [TestMethod] public void AnUnsupportedVersionIsReportedAndRefusesEveryMutation()
        {
            var adapter = Make(version: "2.14.0.0");
            Assert.IsTrue(adapter.Capabilities.Installed); Assert.IsFalse(adapter.Capabilities.VersionSupported);
            Assert.AreEqual("mechjeb_version_unsupported", adapter.Capabilities.Reason); Assert.AreEqual("unsupported_version", adapter.Capabilities.State);
            Assert.AreEqual("mechjeb_version_unsupported", Throws(() => adapter.EngageAscent(null, new object())).Code);
            Assert.AreEqual("mechjeb_version_unsupported", Throws(() => adapter.ConfigureAscent(null, 100000, 0, true)).Code);
        }

        [TestMethod] public void ALegacyAscentLayoutWithoutTheNeededMembersIsFlaggedPerModule()
        {
            var adapter = Make(coreType: typeof(LegacyCore));
            var caps = adapter.Capabilities;
            Assert.IsTrue(caps.Installed);
            Assert.IsFalse(caps.Has("ascent"), "no MasterMechJeb and no Ascent property");
            Assert.IsFalse(caps.Has("node"));
        }

        private sealed class LegacyCore { }

        [TestMethod] public void TheDefaultVersionSourceReadsTheFileVersionNotTheAssemblyVersion()
        {
            // The installed MechJeb2.dll is AssemblyVersion 2.5.1.0 but FileVersion 2.15.2.0; the attribute is what names the release.
            var expected = Version.Parse(((System.Reflection.AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(typeof(MechJebCore).Assembly, typeof(System.Reflection.AssemblyFileVersionAttribute))).Version);
            Assert.AreEqual(expected, MechJebAdapter.InstalledVersion(typeof(MechJebCore)));
        }

        private static MechJebException Throws(Action action)
        {
            try { action(); } catch (MechJebException error) { return error; }
            Assert.Fail("expected a MechJebException"); return null;
        }

        // ---- ascent configuration and engagement ----

        [TestMethod] public void ConfigureWritesAndReadsBackTheAscentSettings()
        {
            var view = Make().ConfigureAscent(null, 120000, 28.5, true);
            Assert.AreEqual(120000, core.AscentSettings.DesiredOrbitAltitude.Val); Assert.AreEqual(28.5, core.AscentSettings.DesiredInclination.Val);
            Assert.IsTrue(core.AscentSettings.Autostage); Assert.IsFalse(core.AscentSettings.SkipCircularization, "the bridge never lets the ascent skip its circularization");
            Assert.AreEqual(120000, view.TargetAltitudeMeters); Assert.AreEqual(28.5, view.InclinationDegrees); Assert.AreEqual(true, view.Autostage); Assert.AreEqual("CLASSIC", view.AscentType);
        }

        [TestMethod] public void ASettingMechJebDoesNotKeepFailsTheConfiguration()
        {
            core.AscentSettings.DesiredOrbitAltitude = new FrozenDouble();
            Assert.AreEqual("engage_failed", Throws(() => Make().ConfigureAscent(null, 120000, 0, false)).Code);
        }

        [TestMethod] public void EngagingAddsOurUserAndEnablesTheModuleWhetherOrNotAddDoes()
        {
            var user = new object(); var adapter = Make();
            adapter.EngageAscent(null, user);
            Assert.IsTrue(core.Ascent.Enabled); CollectionAssert.AreEqual(new object[] { core.AscentMenu }, core.Ascent.Users.ToArray(), "engaged through the ascent window module");
            var reading = adapter.ReadAscent(null, user);
            Assert.IsTrue(reading.Enabled && reading.OwnUserPresent); Assert.AreEqual(0, reading.OtherUsers); Assert.AreEqual("Pre-launch", reading.Status);
            adapter.DisengageAscent(null, user);
            Assert.IsFalse(core.Ascent.Enabled); Assert.AreEqual(0, core.Ascent.Users.Count);

            core = new MechJebCore(); UserPool.AddEnablesModule = false; var second = new object(); var other = Make();
            other.EngageAscent(null, second);
            Assert.IsTrue(core.Ascent.Enabled, "enabled explicitly when the pool did not");
            other.DisengageAscent(null, second);
            Assert.IsFalse(core.Ascent.Enabled);
        }

        [TestMethod] public void DisengagingLeavesAnotherUsersHoldAlone()
        {
            var ours = new object(); var theirs = new object(); var adapter = Make();
            core.Ascent.Users.Add(theirs); adapter.EngageAscent(null, ours);
            var reading = adapter.ReadAscent(null, ours); Assert.AreEqual(1, reading.OtherUsers);
            adapter.DisengageAscent(null, ours);
            Assert.IsTrue(core.Ascent.Enabled, "someone else still holds it");
            CollectionAssert.AreEqual(new[] { theirs }, core.Ascent.Users.ToArray());
        }

        [TestMethod] public void TheMasterCoreIsUsedWhenAVesselHasSeveral()
        {
            var master = new MechJebCore(); core.MasterMechJeb = master;
            var adapter = Make();
            adapter.ConfigureAscent(null, 150000, 10, false);
            Assert.AreEqual(150000, master.AscentSettings.DesiredOrbitAltitude.Val); Assert.AreEqual(100000, core.AscentSettings.DesiredOrbitAltitude.Val);
        }

        [TestMethod] public void WithNoCoreOnTheVesselEveryCallRefusesAndNothingIsCached()
        {
            MechJebCore current = null; var adapter = Make(id => current);
            Assert.IsFalse(adapter.HasCore(null));
            Assert.AreEqual("mechjeb_unavailable", Throws(() => adapter.EngageAscent(null, new object())).Code);
            current = core; Assert.IsTrue(adapter.HasCore(null));
            adapter.EngageAscent(null, new object()); Assert.IsTrue(core.Ascent.Enabled);
            current = null; Assert.IsFalse(adapter.HasCore(null), "the vessel was destroyed: the stale core is not remembered");
        }

        // ---- node executor ----

        [TestMethod] public void ExecutingANodeForcesAutowarpOffAndAbortOnDisengage()
        {
            var user = new object(); var adapter = Make();
            adapter.EngageNode(null, user, false);
            Assert.IsFalse(core.Node.Autowarp); Assert.AreEqual(1, core.Node.ExecuteOne); Assert.AreEqual(0, core.Node.ExecuteAll);
            var reading = adapter.ReadNode(null, user);
            Assert.IsTrue(reading.Enabled && reading.OwnUserPresent); Assert.AreEqual("WARPALIGN", reading.State); Assert.IsFalse(reading.Autowarp);
            adapter.DisengageNode(null, user, null);
            Assert.AreEqual(1, core.Node.Aborts); Assert.IsFalse(core.Node.Enabled);
            adapter.EngageNode(null, user, true);
            Assert.AreEqual(1, core.Node.ExecuteAll);
        }

        [TestMethod] public void ThrustOffReachesTheThrustController()
        {
            Make().ThrustOff(null);
            Assert.AreEqual(1, core.Thrust.Off);
        }

        // ---- competing controllers ----

        [TestMethod] public void OurOwnHoldIsNotACompetitorButAnyoneElsesIs()
        {
            var ours = new object(); var adapter = Make();
            Assert.AreEqual(0, adapter.FindCompetitors(null, ours, true).Count);
            adapter.EngageAscent(null, ours);
            Assert.AreEqual(0, adapter.FindCompetitors(null, ours, true).Count, "only our user holds the ascent module");
            CollectionAssert.AreEqual(new[] { "mechjeb.ascent" }, adapter.FindCompetitors(null, null, false).ToArray(), "to a new caller it is engaged");
            core.Node.Users.Add(new object());
            CollectionAssert.AreEqual(new[] { "mechjeb.node" }, adapter.FindCompetitors(null, ours, true).ToArray());
        }

        [TestMethod] public void AnEnabledAutopilotWithNoUsersIsStillACompetitor()
        {
            core.Landing.Enabled = true;
            CollectionAssert.AreEqual(new[] { "mechjeb.landing" }, Make().FindCompetitors(null, new object(), true).ToArray());
        }

        [TestMethod] public void SupportModulesCountOnlyAtAdmission()
        {
            core.Attitude.Users.Add(new object()); core.Rover.Users.Add(new object()); core.Thrust.Users.Add(new object());
            var adapter = Make();
            CollectionAssert.AreEquivalent(new[] { "mechjeb.attitude", "mechjeb.rover" }, adapter.FindCompetitors(null, new object(), true).ToArray(), "a foreign user of a scanned support module is a competitor, in the running scan too; the thrust controller is never scanned (MechJeb's limiters hold it)");
        }

        [TestMethod] public void TheAscentHandingOverToTheNodeExecutorAndAttitudeControllerIsNotACompetitor()
        {
            var ours = new object(); var adapter = Make();
            adapter.EngageAscent(null, ours);
            core.HandOffToNode(); // MechJeb's circularization: node executor and attitude controller with the ascent module as the user
            Assert.IsTrue(core.Node.Enabled);
            Assert.AreEqual(0, adapter.FindCompetitors(null, ours, true).Count, "the ascent module acts for us");
            Assert.AreEqual(1, adapter.ReadNode(null, ours).OtherUsers, "to a node job the ascent module is someone else: only ascent jobs own the window");
            core.Node.Users.Add(new object());
            CollectionAssert.AreEqual(new[] { "mechjeb.node" }, adapter.FindCompetitors(null, ours, true).ToArray(), "a foreign user next to the ascent is still caught");
        }

        [TestMethod] public void ForANodeJobTheAscentWindowIsNotOursAndAPersonEngagingItIsACompetitor()
        {
            var ours = new object(); var adapter = Make();
            adapter.EngageNode(null, ours, false);
            Assert.AreEqual(0, adapter.FindCompetitors(null, ours, false).Count);
            core.Ascent.Users.Add(core.AscentMenu); // a person presses Engage in the MechJeb ascent window
            CollectionAssert.AreEqual(new[] { "mechjeb.ascent" }, adapter.FindCompetitors(null, ours, false).ToArray());
            Assert.AreEqual(0, adapter.FindCompetitors(null, ours, true).Count, "the same state is our own engagement for an ascent job");
        }

        [TestMethod] public void ForANodeJobAHandoffFromAWindowEngagedAscentIsForeignAndReleaseLeavesItBurning()
        {
            var ours = new object(); var adapter = Make();
            core.Ascent.Users.Add(core.AscentMenu); core.HandOffToNode(); // a person's ascent is now using the node executor
            adapter.EngageNode(null, ours, false);
            Assert.AreEqual(1, adapter.ReadNode(null, ours).OtherUsers);
            adapter.DisengageNode(null, ours, null);
            Assert.AreEqual(0, core.Node.Aborts, "their burn is left alone"); Assert.IsTrue(core.Node.Enabled);
        }

        [TestMethod] public void AnAscentModuleThatDoesNotActForUsIsAForeignUser()
        {
            var adapter = Make();
            core.Ascent.Users.Add(new object()); // someone else started the ascent
            core.HandOffToNode();
            CollectionAssert.AreEquivalent(new[] { "mechjeb.ascent", "mechjeb.node", "mechjeb.attitude" }, adapter.FindCompetitors(null, new object(), true).ToArray());
        }

        [TestMethod] public void WithoutAWindowModuleTheBridgeUserItselfEngagesTheAscent()
        {
            core.HasMenu = false; var user = new object(); var adapter = Make();
            adapter.EngageAscent(null, user);
            CollectionAssert.AreEqual(new[] { user }, core.Ascent.Users.ToArray());
            var reading = adapter.ReadAscent(null, user); Assert.IsFalse(reading.ViaWindow); Assert.IsTrue(reading.OwnUserPresent);
            adapter.DisengageAscent(null, user); Assert.IsFalse(core.Ascent.Enabled);
        }

        [TestMethod] public void TheWindowDisengageButtonRemovesTheAscentUser()
        {
            var user = new object(); var adapter = Make();
            adapter.EngageAscent(null, user);
            var before = adapter.ReadAscent(null, user); Assert.IsTrue(before.ViaWindow && before.OwnUserPresent);
            core.Ascent.Users.Remove(core.AscentMenu); // what MechJeb's button does
            var after = adapter.ReadAscent(null, user);
            Assert.IsFalse(after.Enabled); Assert.IsFalse(after.OwnUserPresent); Assert.IsTrue(after.ViaWindow);
        }

        [TestMethod] public void ReleasingTheNodeExecutorLeavesAnotherUsersBurnAlone()
        {
            var user = new object(); var theirs = new object(); var adapter = Make();
            core.Node.Users.Add(theirs); adapter.EngageNode(null, user, false);
            adapter.DisengageNode(null, user, null);
            Assert.AreEqual(0, core.Node.Aborts, "no Abort while someone else holds the executor");
            CollectionAssert.AreEqual(new[] { theirs }, core.Node.Users.ToArray()); Assert.IsTrue(core.Node.Enabled);
        }

        [TestMethod] public void AutowarpIsRestoredOnRelease()
        {
            var user = new object(); var adapter = Make(); core.Node.Autowarp = true;
            var saved = adapter.EngageNode(null, user, false);
            Assert.IsTrue(saved); Assert.IsFalse(core.Node.Autowarp);
            adapter.DisengageNode(null, user, saved);
            Assert.IsTrue(core.Node.Autowarp); Assert.AreEqual(1, core.Node.Aborts);
        }

        [TestMethod] public void EveryCallTargetsTheNamedVesselNeverTheActiveOne()
        {
            var other = new MechJebCore(); string asked = null;
            var adapter = new MechJebAdapter(id => { asked = id; return id == "vessel-b" ? other : core; }, id => vessel, () => typeof(MechJebCore), () => null, t => new Version(2, 15, 2, 0));
            adapter.ConfigureAscent("vessel-b", 130000, 0, false);
            Assert.AreEqual("vessel-b", asked); Assert.AreEqual(130000, other.AscentSettings.DesiredOrbitAltitude.Val); Assert.AreEqual(100000, core.AscentSettings.DesiredOrbitAltitude.Val);
            adapter.EngageAscent("vessel-b", new object()); Assert.IsTrue(other.Ascent.Enabled); Assert.IsFalse(core.Ascent.Enabled);
            adapter.ThrustOff("vessel-b"); Assert.AreEqual(1, other.Thrust.Off); Assert.AreEqual(0, core.Thrust.Off);
            adapter.DisengageAscent("vessel-b", new object()); Assert.IsFalse(other.Ascent.Enabled);
        }

        [TestMethod] public void ModulesFoundThroughTheGenericLookupAreScannedToo()
        {
            core.Rendezvous.Users.Add(new object());
            CollectionAssert.AreEqual(new[] { "mechjeb.rendezvous" }, Make().FindCompetitors(null, null, false).ToArray());
        }

        [TestMethod] public void AnActiveAtmosphereAutopilotModuleIsACompetitor()
        {
            var aa = new AtmosphereAutopilot(); AtmosphereAutopilot.Instance = aa;
            aa.Modules[typeof(string)] = new FakeAtmosphere.AutopilotModule { Active = false, ModuleName = "Cruise" };
            var adapter = Make(atmosphere: typeof(AtmosphereAutopilot));
            Assert.IsTrue(adapter.Capabilities.AtmosphereAutopilotInstalled);
            Assert.AreEqual(0, adapter.FindCompetitors(null, null, false).Count);
            aa.Modules[typeof(int)] = new FakeAtmosphere.AutopilotModule { Active = true, ModuleName = "Fly By Wire" };
            CollectionAssert.AreEqual(new[] { "atmosphere_autopilot.Fly By Wire" }, adapter.FindCompetitors(null, null, false).ToArray());
        }

        [TestMethod] public void AnUnreadableAtmosphereAutopilotFailsClosed()
        {
            var aa = new AtmosphereAutopilot { Throw = true }; AtmosphereAutopilot.Instance = aa;
            var found = Make(atmosphere: typeof(AtmosphereAutopilot)).FindCompetitors(null, null, false);
            CollectionAssert.AreEqual(new[] { "atmosphere_autopilot:unreadable" }, found.ToArray());
        }

        // ---- status ----

        [TestMethod] public void StatusReportsPresenceVersionModulesSettingsAndEngagedStates()
        {
            var adapter = Make();
            adapter.ConfigureAscent(null, 110000, 5, true); adapter.EngageAscent(null, new object());
            var status = adapter.ReadStatus();
            Assert.AreEqual("available", (string)status["mechjeb"]); Assert.AreEqual("2.15.2.0", (string)status["version"]); Assert.AreEqual(true, (bool)status["vesselCore"]);
            Assert.AreEqual(true, (bool)status["modules"]["ascent"]["supported"]); Assert.AreEqual(true, (bool)status["modules"]["ascent"]["engaged"]); Assert.AreEqual("Pre-launch", (string)status["modules"]["ascent"]["status"]);
            Assert.AreEqual(false, (bool)status["modules"]["node"]["engaged"]);
            Assert.AreEqual(110000, (double)status["ascentSettings"]["targetAltitudeMeters"]); Assert.AreEqual(5, (double)status["ascentSettings"]["inclinationDegrees"]); Assert.AreEqual(true, (bool)status["ascentSettings"]["autostage"]);
            Assert.AreEqual("IDLE", (string)status["nodeExecutor"]["state"]); Assert.AreEqual(true, (bool)status["nodeExecutor"]["autowarp"]);
            CollectionAssert.AreEqual(new[] { "mechjeb.ascent" }, ((JArray)status["competingControllers"]).Select(t => (string)t).ToArray());
        }

        [TestMethod] public void StatusWithoutACoreStillDescribesTheInstall()
        {
            var adapter = Make(id => null);
            var status = adapter.ReadStatus();
            Assert.AreEqual("available", (string)status["mechjeb"]); Assert.AreEqual(false, (bool)status["vesselCore"]);
            Assert.AreEqual(true, (bool)status["modules"]["ascent"]["supported"]);
        }
    
        // ---- attitude controller (recovery) ----

        [TestMethod] public void HoldingRetrogradeCallsTheVectorOverloadWithBackAndTheOrbitReference()
        {
            var adapter = Make(); var user = new object();
            Assert.IsTrue(adapter.Capabilities.Has("attitudeControl"));
            adapter.HoldAttitude(null, user, AttitudeDirections.OrbitRetrograde);
            Assert.AreEqual(1, core.Attitude.VectorCalls); Assert.AreEqual(0, core.Attitude.OtherOverloadCalls, "never the quaternion or heading overloads");
            Assert.AreEqual(0.0, core.Attitude.LastDirection.x); Assert.AreEqual(0.0, core.Attitude.LastDirection.y); Assert.AreEqual(-1.0, core.Attitude.LastDirection.z);
            Assert.AreEqual(AttitudeReference.ORBIT, core.Attitude.LastReference); Assert.IsFalse(core.Attitude.LastKillRoll);
            Assert.IsTrue(core.Attitude.Enabled); CollectionAssert.Contains(core.Attitude.Users, user);
            adapter.HoldAttitude(null, user, AttitudeDirections.SurfaceRetrograde);
            Assert.AreEqual(AttitudeReference.SURFACE_VELOCITY, core.Attitude.LastReference);
            Assert.AreEqual(1, core.Attitude.Users.Count, "the user is added once");
        }

        [TestMethod] public void TheAttitudeReadingSeparatesOurHoldFromOthersAndReportsTheAngle()
        {
            var adapter = Make(); var user = new object();
            adapter.HoldAttitude(null, user, AttitudeDirections.OrbitRetrograde);
            core.Attitude.Angle = 3.5;
            var reading = adapter.ReadAttitude(null, user);
            Assert.IsTrue(reading.Enabled && reading.OwnUserPresent); Assert.AreEqual(0, reading.OtherUsers); Assert.AreEqual(3.5, reading.AngleFromTargetDegrees);
            core.Attitude.Users.Add(new object());
            Assert.AreEqual(1, adapter.ReadAttitude(null, user).OtherUsers);
            CollectionAssert.AreEquivalent(new[] { "mechjeb.attitude" }, adapter.FindCompetitors(null, user, false).ToArray());
        }

        [TestMethod] public void OurAttitudeHoldIsNotACompetitorOfOurOwnJob()
        {
            var adapter = Make(); var user = new object();
            adapter.HoldAttitude(null, user, AttitudeDirections.OrbitRetrograde);
            Assert.AreEqual(0, adapter.FindCompetitors(null, user, false).Count);
            CollectionAssert.Contains(adapter.FindCompetitors(null, null, false).ToArray(), "mechjeb.attitude", "a new caller sees the hold as someone else's");
        }

        [TestMethod] public void ReleasingTheAttitudeRemovesOnlyOurUser()
        {
            var adapter = Make(); var user = new object(); var person = new object();
            adapter.HoldAttitude(null, user, AttitudeDirections.OrbitRetrograde);
            adapter.ReleaseAttitude(null, user);
            Assert.IsFalse(core.Attitude.Enabled); Assert.AreEqual(0, core.Attitude.Users.Count);
            adapter.HoldAttitude(null, user, AttitudeDirections.OrbitRetrograde); core.Attitude.Users.Add(person);
            adapter.ReleaseAttitude(null, user);
            Assert.IsTrue(core.Attitude.Enabled, "a person's SmartASS hold stays"); CollectionAssert.AreEqual(new[] { person }, core.Attitude.Users);
        }

        [TestMethod] public void AnAttitudeControllerWithoutTheVectorOverloadIsFlagged()
        {
            var adapter = Make(coreType: typeof(LegacyAttitude.MechJebCore));
            Assert.IsTrue(adapter.Capabilities.Has("attitude"), "the scan still works on Enabled and Users");
            Assert.IsFalse(adapter.Capabilities.Has("attitudeControl"));
        }
}
}

namespace LegacyAttitude
{
    public class UserPool : System.Collections.Generic.List<object> { }
    public class MechJebModuleAttitudeController { public UserPool Users = new UserPool(); public bool Enabled { get; set; } }
    public class MechJebCore { public MechJebCore MasterMechJeb { get { return this; } } public MechJebModuleAttitudeController Attitude = new MechJebModuleAttitudeController(); }
}
