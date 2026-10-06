using HeadlessTest.Infrastructure;
using LmpCommon.Message.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessTest;

[TestClass]
[DoNotParallelize]
public class AgencyChatIsolationTest
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task AgencyChatReachesMembersButRejectsForeignRecipientsAndSpoofingAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var bots = new[] { new BotClient("BotA", "headless-a-" + run), new BotClient("BotB", "headless-b-" + run), new BotClient("BotC", "headless-c-" + run) };
        var a = bots[0]; var b = bots[1]; var c = bots[2];
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token);
            var initial = new List<AgencySyncSnapshot>();
            foreach (var bot in bots)
            {
                server.ThrowIfExited();
                var sync = await bot.ConnectAsync(server.Port, token);
                initial.Add(sync);
                Assert.IsTrue(sync.Agencies.Single(x => x.Id == sync.MyAgencyId).IsSolo, "New player should have an implicit solo agency");
            }
            Assert.AreEqual(3, initial.Select(s => s.MyAgencyId).Distinct().Count(), "Initial bot agencies must be independent");

            var alpha = await CreateAndReconnectAsync(a, server.Port, "Headless Alpha", initial[0].MyAgencyId, token);
            var gamma = await CreateAndReconnectAsync(c, server.Port, "Headless Gamma", initial[2].MyAgencyId, token);
            Assert.AreNotEqual(alpha.MyAgencyId, gamma.MyAgencyId);

            b.RequestJoin(alpha.MyAgencyId);
            var requested = await b.WaitForAsync<AgencyReplySnapshot>(_ => true, token);
            Assert.IsTrue(requested.Success, requested.Message);
            await a.WaitForAsync<JoinRequestSnapshot>(request => request.AgencyId == alpha.MyAgencyId && request.PlayerIdentity == b.Identity, token);
            b.ArmExpectedDisconnect(reason => reason == "You joined 'Headless Alpha'. Reconnecting to load its career state.");
            a.ApproveJoin(alpha.MyAgencyId, b.Identity);
            var approved = await a.WaitForAsync<AgencyReplySnapshot>(_ => true, token);
            Assert.IsTrue(approved.Success, approved.Message);
            await b.WaitForDisconnectAsync(token);
            var membership = await b.ConnectAsync(server.Port, token);
            Assert.AreEqual(alpha.MyAgencyId, membership.MyAgencyId, "Reconnect must confirm authoritative approved membership");
            CollectionAssert.IsSubsetOf(new[] { a.Identity, b.Identity }, membership.Agencies.Single(x => x.Id == alpha.MyAgencyId).Members.ToArray());

            var privateText = "private-" + run;
            a.SendChat(privateText, ChatChannel.Agency, alpha.MyAgencyId);
            foreach (var recipient in new[] { a, b })
                AssertChat(await recipient.WaitForAsync<ChatSnapshot>(chat => chat.Text == privateText, token), a.Name, privateText, ChatChannel.Agency, alpha.MyAgencyId);

            // All messages use production ReliableOrdered chat channel 3 in both directions.
            // Advancing every recipient past this barrier makes the absence check meaningful.
            await BarrierAsync(a, bots, "barrier-private-" + run, token);
            Assert.IsFalse(c.ChatSnapshots.Any(chat => chat.Text == privateText), "Foreign agency received private chat");

            var spoofText = "spoof-" + run;
            a.SendChat(spoofText, ChatChannel.Agency, gamma.MyAgencyId);
            await BarrierAsync(a, bots, "barrier-spoof-" + run, token);
            foreach (var bot in bots)
                Assert.IsFalse(bot.ChatSnapshots.Any(chat => chat.Text == spoofText), $"{bot.Name} received spoofed agency chat");
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

    private static async Task<AgencySyncSnapshot> CreateAndReconnectAsync(BotClient bot, int port, string name, Guid priorAgency, CancellationToken token)
    {
        var oldGeneration = bot.Generation;
        bot.ArmExpectedDisconnect(reason => reason == $"Created agency '{name}'. Reconnecting to load its career state.");
        bot.CreateAgency(name);
        await bot.WaitForDisconnectAsync(token);
        var sync = await bot.ConnectAsync(port, token);
        Assert.IsTrue(sync.Generation > oldGeneration, "Agency sync must belong to the new connection generation");
        Assert.AreNotEqual(priorAgency, sync.MyAgencyId);
        var agency = sync.Agencies.Single(x => x.Id == sync.MyAgencyId);
        Assert.AreEqual(name, agency.Name);
        Assert.IsFalse(agency.IsSolo);
        CollectionAssert.Contains(agency.Members.ToArray(), bot.Identity);
        return sync;
    }

    private static async Task BarrierAsync(BotClient sender, IEnumerable<BotClient> recipients, string text, CancellationToken token)
    {
        sender.SendChat(text, ChatChannel.Global, Guid.Empty);
        foreach (var recipient in recipients)
            AssertChat(await recipient.WaitForAsync<ChatSnapshot>(chat => chat.Text == text, token), sender.Name, text, ChatChannel.Global, Guid.Empty);
    }

    private static void AssertChat(ChatSnapshot chat, string sender, string text, ChatChannel channel, Guid agency)
    {
        Assert.AreEqual(sender, chat.From);
        Assert.AreEqual(text, chat.Text);
        Assert.AreEqual(channel, chat.Channel);
        Assert.AreEqual(agency, chat.AgencyId);
    }
}
