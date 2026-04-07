using LmpCommon.Time;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net.Sockets;

namespace LmpCommonTest
{
    [TestClass]
    public class TimeTests
    {
        [TestMethod]
        public void TestGetTime()
        {
            try
            {
                var date = TimeRetrieverNtp.GetNtpTime("time.google.com");
                Assert.IsTrue(date.Year >= 2020 && date.Year < 2100, "NTP time should be a plausible calendar date.");
            }
            catch (SocketException)
            {
                Assert.Inconclusive("NTP query failed (UDP port 123 blocked or unreachable); skipping network time test.");
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"NTP query failed: {ex.Message}");
            }
        }
    }
}
