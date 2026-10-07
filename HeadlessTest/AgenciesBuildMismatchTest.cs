using HeadlessTest.Infrastructure;
using LmpCommon.Agency;
using LmpCommon.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessTest;

[TestClass]
[DoNotParallelize]
public class AgenciesBuildMismatchTest
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task ClientSendingNoBuildGetsReplyFourWithTheReasonThenIsDisconnectedAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var bots = new[] { new BotClient("OldBuild", "old-build-" + run), new BotClient("CurrentBuild", "current-build-" + run) };
        var old = bots[0]; var current = bots[1];
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token);
            var reply = await old.ConnectRejectedAsync(server.Port, 0, token);
            Assert.AreEqual(HandshakeReply.AgenciesBuildMismatch, reply.Response);
            StringAssert.Contains(reply.Reason, "agencies.1 or older");
            StringAssert.Contains(reply.Reason, AgenciesBuild.ReleasesPage);
            StringAssert.Contains(reply.Reason, "agencies." + AgenciesBuild.Number);

            // The server waits about 2 s before closing, so the reply above has time to arrive.
            var sinceReply = Stopwatch.StartNew();
            var closed = await old.WaitForDisconnectAsync(token);
            sinceReply.Stop();
            Assert.AreEqual(reply.Reason, closed.Reason);
            Assert.IsTrue(sinceReply.Elapsed < TimeSpan.FromSeconds(3), $"disconnected {sinceReply.Elapsed.TotalSeconds:F1}s after the reply");
            Assert.IsTrue(sinceReply.Elapsed >= TimeSpan.FromSeconds(1), $"disconnected {sinceReply.Elapsed.TotalSeconds:F1}s after the reply, too soon for it to be a delayed close");

            // A client on the server's build is unaffected and the server survived the rejection.
            var sync = await current.ConnectAsync(server.Port, token);
            Assert.AreNotEqual(Guid.Empty, sync.MyAgencyId);
            server.ThrowIfExited();
            succeeded = true;
        }
        finally
        {
            var cleanupErrors = new List<Exception>();
            foreach (var bot in bots)
            {
                try { await bot.DisposeAsync(); }
                catch (Exception error) { cleanupErrors.Add(error); TestContext.WriteLine($"Bot cleanup failed: {error}"); }
                try
                {
                    if (server != null) server.AttachTranscript(bot.Name, bot.Transcript);
                    else TestContext.WriteLine(bot.Transcript);
                }
                catch (Exception error) { cleanupErrors.Add(error); TestContext.WriteLine($"Transcript attachment failed: {error}"); }
            }
            if (server != null)
            {
                try { await server.DisposeAsync(); }
                catch (Exception error) { cleanupErrors.Add(error); TestContext.WriteLine($"Server cleanup failed: {error}"); }
            }
            if (succeeded && cleanupErrors.Count != 0)
                Assert.Fail("Scenario passed but fixture cleanup failed: " + string.Join("; ", cleanupErrors.Select(e => e.Message)));
        }
    }
}
