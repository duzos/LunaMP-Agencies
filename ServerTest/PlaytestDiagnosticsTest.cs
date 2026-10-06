using LmpCommon.Diagnostics;
using LmpCommon.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Diagnostics;
using Server.Agency;
using ServerTest.Agency;
using System.Threading.Tasks;
using Server.Settings.Definition;
using Server.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;

namespace ServerTest
{
    [TestClass]
    [DoNotParallelize]
    public class PlaytestDiagnosticsTest
    {
        [TestMethod]
        public async Task AgencyRawUpdate_ReportsAppliedOrFailedAfterTaskCompletion()
        {
            using (var scope = new AgencyTestScope())
            {
                var field = typeof(PlaytestDiagnostics).GetField("_trace", BindingFlags.NonPublic | BindingFlags.Static);
                var previous = field.GetValue(null);
                var lines = new List<string>();
                field.SetValue(null, new DiagnosticTrace(lines.Add) { Enabled = true });
                var agency = Guid.NewGuid();
                try
                {
                    await AgencyScenarioUpdater.RawConfigNodeInsertOrUpdate(agency, "Funding", "funds = 42\n");
                    Assert.AreEqual("42", AgencyScenarioStore.GetOrNull(agency, "Funding").GetValue("funds").Value);
                    Assert.AreEqual(1, lines.Count(line => line.Contains("event=scenario.apply") && line.Contains("result=applied-in-memory")));
                    Assert.IsFalse(lines.Any(line => line.Contains("result=failed")));

                    // A missing payload deterministically exercises parser failure rather than
                    // relying on LunaConfigNode's tolerance for malformed text.
                    await AgencyScenarioUpdater.RawConfigNodeInsertOrUpdate(agency, "Funding", null);
                    Assert.AreEqual("42", AgencyScenarioStore.GetOrNull(agency, "Funding").GetValue("funds").Value);
                    Assert.AreEqual(1, lines.Count(line => line.Contains("event=scenario.apply") && line.Contains("result=applied-in-memory")));
                    Assert.AreEqual(1, lines.Count(line => line.Contains("event=scenario.apply") && line.Contains("result=failed") && line.Contains("errorType=NullReferenceException")));
                    Assert.IsFalse(lines.Any(line => line.Contains("persisted")));
                }
                finally
                {
                    field.SetValue(null, previous);
                }
            }
        }

        [TestMethod]
        public void DebugSettings_DefaultAndOldXmlKeepDiagnosticsOff_OptInRoundTrips()
        {
            Assert.IsFalse(new DebugSettingsDefinition().VerboseDiagnostics);
            var serializer = new XmlSerializer(typeof(DebugSettingsDefinition));
            using (var reader = new StringReader("<DebugSettingsDefinition><SimulatedLossChance>0</SimulatedLossChance></DebugSettingsDefinition>"))
                Assert.IsFalse(((DebugSettingsDefinition)serializer.Deserialize(reader)).VerboseDiagnostics);
            var settings = new DebugSettingsDefinition { VerboseDiagnostics = true };
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, settings);
                using (var reader = new StringReader(writer.ToString()))
                    Assert.IsTrue(((DebugSettingsDefinition)serializer.Deserialize(reader)).VerboseDiagnostics);
            }
        }

        [TestMethod]
        public void RealLockDecisions_EmitAcceptedAndDeniedWithoutPlayerOrPayloadDump()
        {
            var field = typeof(PlaytestDiagnostics).GetField("_trace", BindingFlags.NonPublic | BindingFlags.Static);
            var previous = field.GetValue(null);
            var lines = new List<string>();
            field.SetValue(null, new DiagnosticTrace(lines.Add) { Enabled = true });
            var vessel = Guid.NewGuid();
            var owner = new LockDefinition(LockType.Control, "diagnostic-owner-" + Guid.NewGuid(), vessel);
            var contender = new LockDefinition(LockType.Control, "diagnostic-contender-" + Guid.NewGuid(), vessel);
            try
            {
                Assert.IsTrue(LockSystem.AcquireLock(owner, false, out var repeated));
                Assert.IsFalse(repeated);
                Assert.IsFalse(LockSystem.AcquireLock(contender, false, out _));
                Assert.IsTrue(lines.Any(line => line.Contains("event=lock.acquire") && line.Contains("result=accepted") && line.Contains(vessel.ToString())));
                Assert.IsTrue(lines.Any(line => line.Contains("event=lock.acquire") && line.Contains("result=denied") && line.Contains("reason=held")));
                Assert.IsFalse(lines.Any(line => line.Contains(owner.PlayerName) || line.Contains(contender.PlayerName)));
            }
            finally
            {
                LockSystem.ReleaseLock(owner);
                field.SetValue(null, previous);
            }
        }
    }
}
