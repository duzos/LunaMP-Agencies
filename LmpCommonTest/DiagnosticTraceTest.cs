using System;
using System.Collections.Generic;
using LmpCommon.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class DiagnosticTraceTest
    {
        [TestMethod]
        public void Disabled_DoesNotReadClockOrEvaluateDetailsOrEmit()
        {
            var calls = 0;
            var trace = new DiagnosticTrace(_ => calls++, () => { calls++; return DateTime.UtcNow; });
            trace.Write("off", () => { calls++; return ""; });
            Assert.AreEqual(0, calls);
        }

        [TestMethod]
        public void Enabled_ProducesBoundedSingleLineWithSessionAndSequence()
        {
            var lines = new List<string>();
            var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var trace = new DiagnosticTrace(lines.Add, () => now) { Enabled = true };
            trace.Write("state\ninjected", () => "value\r\n" + new string('x', 5000));
            trace.Write("next", () => "agency=abc");
            StringAssert.Contains(lines[0], "[LMP-DIAG] utc=2026-10-06T12:00:00.0000000Z session=");
            StringAssert.Contains(lines[0], "seq=1 event=state injected");
            StringAssert.Contains(lines[1], "seq=2 event=next agency=abc");
            Assert.IsFalse(lines[0].Contains("\n"));
            Assert.IsFalse(lines[0].Contains("\r"));
            Assert.IsTrue(lines[0].Length < 2100);
            StringAssert.EndsWith(lines[0], "[truncated]");
        }

        [TestMethod]
        public void TrafficBurst_DoesNotConsumeDecisionBudget_AndReportsSuppression()
        {
            var lines = new List<string>();
            var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var trace = new DiagnosticTrace(lines.Add, () => now, 1, 1) { Enabled = true };
            trace.Write("packet", () => "first", true);
            var suppressedDetailCalls = 0;
            trace.Write("packet", () => { suppressedDetailCalls++; return ""; }, true);
            trace.Write("decision", () => "allowed");
            trace.Write("decision", () => "suppressed");
            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual(0, suppressedDetailCalls);
            now = now.AddSeconds(5);
            trace.Write("next", () => "new window");
            Assert.AreEqual(4, lines.Count);
            StringAssert.Contains(lines[2], "event=suppressed events=1 traffic=1");
            StringAssert.Contains(lines[3], "event=next");
        }

        [TestMethod]
        public void DiagnosticFailures_DoNotEscape_AndNextEventStillWorks()
        {
            var lines = new List<string>();
            var trace = new DiagnosticTrace(lines.Add) { Enabled = true };
            trace.Write("broken", () => throw new InvalidOperationException("secret detail"));
            StringAssert.Contains(lines[0], "diagnostic-detail-failed type=InvalidOperationException");
            Assert.IsFalse(lines[0].Contains("secret detail"));
            var failingSink = new DiagnosticTrace(_ => throw new Exception("sink")) { Enabled = true };
            failingSink.Write("safe", () => "value");
            trace.Write("next", () => "okay");
            Assert.AreEqual(2, lines.Count);
        }
    }
}
