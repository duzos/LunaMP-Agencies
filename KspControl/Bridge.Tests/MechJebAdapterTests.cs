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

        private MechJebAdapter Make(Func<object> source = null, Type atmosphere = null, string version = "2.15.2.0", Type coreType = null)
        {
            return new MechJebAdapter(source ?? (() => core), () => vessel,
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
            var adapter = new MechJebAdapter(() => null, null, () => null, () => null);
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
            Assert.AreEqual("mechjeb_version_unsupported", Throws(() => adapter.EngageAscent(new object())).Code);
            Assert.AreEqual("mechjeb_version_unsupported", Throws(() => adapter.ConfigureAscent(100000, 0, true)).Code);
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
            var view = Make().ConfigureAscent(120000, 28.5, true);
            Assert.AreEqual(120000, core.AscentSettings.DesiredOrbitAltitude.Val); Assert.AreEqual(28.5, core.AscentSettings.DesiredInclination.Val);
            Assert.IsTrue(core.AscentSettings.Autostage); Assert.IsFalse(core.AscentSettings.SkipCircularization, "the bridge never lets the ascent skip its circularization");
            Assert.AreEqual(120000, view.TargetAltitudeMeters); Assert.AreEqual(28.5, view.InclinationDegrees); Assert.AreEqual(true, view.Autostage); Assert.AreEqual("CLASSIC", view.AscentType);
        }

        [TestMethod] public void ASettingMechJebDoesNotKeepFailsTheConfiguration()
        {
            core.AscentSettings.DesiredOrbitAltitude = new FrozenDouble();
            Assert.AreEqual("engage_failed", Throws(() => Make().ConfigureAscent(120000, 0, false)).Code);
        }

        [TestMethod] public void EngagingAddsOurUserAndEnablesTheModuleWhetherOrNotAddDoes()
        {
            var user = new object(); var adapter = Make();
            adapter.EngageAscent(user);
            Assert.IsTrue(core.Ascent.Enabled); CollectionAssert.AreEqual(new[] { user }, core.Ascent.Users.ToArray());
            var reading = adapter.ReadAscent(user);
            Assert.IsTrue(reading.Enabled && reading.OwnUserPresent); Assert.AreEqual(0, reading.OtherUsers); Assert.AreEqual("Pre-launch", reading.Status);
            adapter.DisengageAscent(user);
            Assert.IsFalse(core.Ascent.Enabled); Assert.AreEqual(0, core.Ascent.Users.Count);

            core = new MechJebCore(); UserPool.AddEnablesModule = false; var second = new object(); var other = Make();
            other.EngageAscent(second);
            Assert.IsTrue(core.Ascent.Enabled, "enabled explicitly when the pool did not");
            other.DisengageAscent(second);
            Assert.IsFalse(core.Ascent.Enabled);
        }

        [TestMethod] public void DisengagingLeavesAnotherUsersHoldAlone()
        {
            var ours = new object(); var theirs = new object(); var adapter = Make();
            core.Ascent.Users.Add(theirs); adapter.EngageAscent(ours);
            var reading = adapter.ReadAscent(ours); Assert.AreEqual(1, reading.OtherUsers);
            adapter.DisengageAscent(ours);
            Assert.IsTrue(core.Ascent.Enabled, "someone else still holds it");
            CollectionAssert.AreEqual(new[] { theirs }, core.Ascent.Users.ToArray());
        }

        [TestMethod] public void TheMasterCoreIsUsedWhenAVesselHasSeveral()
        {
            var master = new MechJebCore(); core.MasterMechJeb = master;
            var adapter = Make();
            adapter.ConfigureAscent(150000, 10, false);
            Assert.AreEqual(150000, master.AscentSettings.DesiredOrbitAltitude.Val); Assert.AreEqual(100000, core.AscentSettings.DesiredOrbitAltitude.Val);
        }

        [TestMethod] public void WithNoCoreOnTheVesselEveryCallRefusesAndNothingIsCached()
        {
            MechJebCore current = null; var adapter = Make(() => current);
            Assert.IsFalse(adapter.HasCore());
            Assert.AreEqual("mechjeb_unavailable", Throws(() => adapter.EngageAscent(new object())).Code);
            current = core; Assert.IsTrue(adapter.HasCore());
            adapter.EngageAscent(new object()); Assert.IsTrue(core.Ascent.Enabled);
            current = null; Assert.IsFalse(adapter.HasCore(), "the vessel was destroyed: the stale core is not remembered");
        }

        // ---- node executor ----

        [TestMethod] public void ExecutingANodeForcesAutowarpOffAndAbortOnDisengage()
        {
            var user = new object(); var adapter = Make();
            adapter.EngageNode(user, false);
            Assert.IsFalse(core.Node.Autowarp); Assert.AreEqual(1, core.Node.ExecuteOne); Assert.AreEqual(0, core.Node.ExecuteAll);
            var reading = adapter.ReadNode(user);
            Assert.IsTrue(reading.Enabled && reading.OwnUserPresent); Assert.AreEqual("WARPALIGN", reading.State); Assert.IsFalse(reading.Autowarp);
            adapter.DisengageNode(user);
            Assert.AreEqual(1, core.Node.Aborts); Assert.IsFalse(core.Node.Enabled);
            adapter.EngageNode(user, true);
            Assert.AreEqual(1, core.Node.ExecuteAll);
        }

        [TestMethod] public void ThrustOffReachesTheThrustController()
        {
            Make().ThrustOff();
            Assert.AreEqual(1, core.Thrust.Off);
        }

        // ---- competing controllers ----

        [TestMethod] public void OurOwnHoldIsNotACompetitorButAnyoneElsesIs()
        {
            var ours = new object(); var adapter = Make();
            Assert.AreEqual(0, adapter.FindCompetitors(ours, false).Count);
            adapter.EngageAscent(ours);
            Assert.AreEqual(0, adapter.FindCompetitors(ours, false).Count, "only our user holds the ascent module");
            CollectionAssert.AreEqual(new[] { "mechjeb.ascent" }, adapter.FindCompetitors(null, false).ToArray(), "to a new caller it is engaged");
            core.Node.Users.Add(new object());
            CollectionAssert.AreEqual(new[] { "mechjeb.node" }, adapter.FindCompetitors(ours, false).ToArray());
        }

        [TestMethod] public void AnEnabledAutopilotWithNoUsersIsStillACompetitor()
        {
            core.Landing.Enabled = true;
            CollectionAssert.AreEqual(new[] { "mechjeb.landing" }, Make().FindCompetitors(new object(), false).ToArray());
        }

        [TestMethod] public void SupportModulesCountOnlyAtAdmission()
        {
            core.Attitude.Users.Add(new object());
            var adapter = Make();
            CollectionAssert.AreEqual(new[] { "mechjeb.attitude" }, adapter.FindCompetitors(null, false).ToArray());
            Assert.AreEqual(0, adapter.FindCompetitors(null, true).Count, "MechJeb's own ascent holds the attitude controller while it flies");
        }

        [TestMethod] public void ModulesFoundThroughTheGenericLookupAreScannedToo()
        {
            core.Rendezvous.Users.Add(new object());
            CollectionAssert.AreEqual(new[] { "mechjeb.rendezvous" }, Make().FindCompetitors(null, true).ToArray());
        }

        [TestMethod] public void AnActiveAtmosphereAutopilotModuleIsACompetitor()
        {
            var aa = new AtmosphereAutopilot(); AtmosphereAutopilot.Instance = aa;
            aa.Modules[typeof(string)] = new FakeAtmosphere.AutopilotModule { Active = false, ModuleName = "Cruise" };
            var adapter = Make(atmosphere: typeof(AtmosphereAutopilot));
            Assert.IsTrue(adapter.Capabilities.AtmosphereAutopilotInstalled);
            Assert.AreEqual(0, adapter.FindCompetitors(null, true).Count);
            aa.Modules[typeof(int)] = new FakeAtmosphere.AutopilotModule { Active = true, ModuleName = "Fly By Wire" };
            CollectionAssert.AreEqual(new[] { "atmosphere_autopilot.Fly By Wire" }, adapter.FindCompetitors(null, true).ToArray());
        }

        [TestMethod] public void AnUnreadableAtmosphereAutopilotFailsClosed()
        {
            var aa = new AtmosphereAutopilot { Throw = true }; AtmosphereAutopilot.Instance = aa;
            var found = Make(atmosphere: typeof(AtmosphereAutopilot)).FindCompetitors(null, true);
            CollectionAssert.AreEqual(new[] { "atmosphere_autopilot:unreadable" }, found.ToArray());
        }

        // ---- status ----

        [TestMethod] public void StatusReportsPresenceVersionModulesSettingsAndEngagedStates()
        {
            var adapter = Make();
            adapter.ConfigureAscent(110000, 5, true); adapter.EngageAscent(new object());
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
            var adapter = Make(() => null);
            var status = adapter.ReadStatus();
            Assert.AreEqual("available", (string)status["mechjeb"]); Assert.AreEqual(false, (bool)status["vesselCore"]);
            Assert.AreEqual(true, (bool)status["modules"]["ascent"]["supported"]);
        }
    }
}
