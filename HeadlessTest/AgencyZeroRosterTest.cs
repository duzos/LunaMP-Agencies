using HeadlessTest.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessTest;

[TestClass]
[DoNotParallelize]
public class AgencyZeroRosterTest
{
    public TestContext TestContext { get; set; }

    // Verifies real-message persistence, not the stock KSP hiring UI or its funding charge.
    [TestMethod]
    public async Task EmptyAgencyRosterAcceptsRecruitAndPreservesItAcrossReconnectAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var bots = new[] { new BotClient("RosterA", "roster-a-" + run), new BotClient("RosterB", "roster-b-" + run) };
        var a = bots[0]; var b = bots[1];
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, settings =>
            {
                settings.AgencyKerbalsPerAgency = true;
                settings.AgencyZeroStartingKerbals = true;
            });
            var agencyA = await a.ConnectAsync(server.Port, token);
            var agencyB = await b.ConnectAsync(server.Port, token);
            Assert.AreNotEqual(agencyA.MyAgencyId, agencyB.MyAgencyId);
            Assert.AreEqual(0, (await RosterAsync(a, token)).Kerbals.Count);
            Assert.AreEqual(0, (await RosterAsync(b, token)).Kerbals.Count);

            const string recruit = "Harness Recruit Kerman";
            const string payload = "{\nname = Harness Recruit Kerman\ntype = Crew\nstate = Available\ntrait = Pilot\nbrave = 0.42\ndumb = 0.17\n}\n";
            // Proto and the next request share production ReliableOrdered channel 7.
            // The reply is an authoritative completion barrier for saving this proto.
            a.SendKerbal(recruit, payload);
            var saved = await RosterAsync(a, token);
            Assert.AreEqual(1, saved.Kerbals.Count);
            Assert.AreEqual(recruit, saved.Kerbals[0].Name);
            Assert.AreEqual(payload, saved.Kerbals[0].ConfigNodeText);
            Assert.AreEqual(0, (await RosterAsync(b, token)).Kerbals.Count, "A's recruit must not enter B's roster");
            // A's saved reply proves the proto handler finished; B's later channel-7 reply
            // advances B beyond any erroneously relayed proto from that handler.
            Assert.IsFalse(b.KerbalProtos.Any(proto => proto.Kerbal.Name == recruit), "A's recruit must not be relayed to B");

            var oldGeneration = a.Generation;
            await a.DisconnectForReconnectAsync(token);
            var reconnected = await a.ConnectAsync(server.Port, token);
            Assert.IsTrue(reconnected.Generation > oldGeneration);
            Assert.AreEqual(agencyA.MyAgencyId, reconnected.MyAgencyId, "Same stable identity must keep its agency");
            var restored = await RosterAsync(a, token);
            Assert.AreEqual(1, restored.Kerbals.Count);
            Assert.AreEqual(recruit, restored.Kerbals[0].Name);
            Assert.AreEqual(payload, restored.Kerbals[0].ConfigNodeText);
            Assert.AreEqual(0, (await RosterAsync(b, token)).Kerbals.Count);
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

    private static async Task<KerbalRosterSnapshot> RosterAsync(BotClient bot, CancellationToken token)
    {
        // Exactly one outstanding request per bot; no previously received reply remains pending.
        bot.RequestKerbals();
        return await bot.WaitForAsync<KerbalRosterSnapshot>(_ => true, token);
    }
}
