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
public class AgencyLaunchSitesTest
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task AdminAssignmentsMoveExclusivelyAndSurviveReconnectAsync()
    {
        const string password = "synthetic-headless-admin";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var run = Guid.NewGuid().ToString("N");
        var bots = new[] { new BotClient("SitesA", "sites-a-" + run), new BotClient("SitesB", "sites-b-" + run) };
        var a = bots[0]; var b = bots[1];
        ServerProcess server = null;
        var succeeded = false;
        try
        {
            server = await ServerProcess.StartAsync(TestContext, token, settings =>
            {
                settings.AdminPassword = password;
                settings.AgencyLaunchSitesPerAgency = true;
            });
            var initialA = await a.ConnectAsync(server.Port, token);
            var initialB = await b.ConnectAsync(server.Port, token);
            Assert.IsTrue(initialA.LaunchSitesReady && initialB.LaunchSitesReady);
            Assert.AreEqual(0, initialA.LaunchSites.Count, "KSC must not receive an implicit assignment");
            Assert.AreNotEqual(initialA.MyAgencyId, initialB.MyAgencyId);

            a.SendAdmin("wrong-password", AgencyAdminOp.AssignLaunchSite, initialA.MyAgencyId, "Forbidden");
            Assert.IsFalse((await a.WaitForAsync<AgencyReplySnapshot>(_ => true, token)).Success);

            a.SendAdmin(password, AgencyAdminOp.AssignLaunchSite, initialA.MyAgencyId, "LaunchPad");
            await ExpectSuccess(a, token);
            var assignedA = await WaitMap(a, initialA.LaunchSitesRevision, "LaunchPad", initialA.MyAgencyId, token);
            await WaitMap(b, initialB.LaunchSitesRevision, "LaunchPad", initialA.MyAgencyId, token);
            Assert.AreEqual(1, assignedA.LaunchSites.Count, "Rejected request must not persist a hidden assignment");

            a.SendAdmin(password, AgencyAdminOp.AssignLaunchSite, initialB.MyAgencyId, "LaunchPad");
            await ExpectSuccess(a, token);
            var moved = await WaitMap(a, assignedA.LaunchSitesRevision, "LaunchPad", initialB.MyAgencyId, token);
            await WaitMap(b, assignedA.LaunchSitesRevision, "LaunchPad", initialB.MyAgencyId, token);
            Assert.AreEqual(1, moved.LaunchSites.Count, "Reassignment must replace, not duplicate ownership");

            a.SendAdmin(password, AgencyAdminOp.UnassignLaunchSite, initialA.MyAgencyId, "LaunchPad");
            Assert.IsFalse((await a.WaitForAsync<AgencyReplySnapshot>(_ => true, token)).Success, "Stale ownership must not remove the new owner's site");
            a.SendAdmin(password, AgencyAdminOp.UnassignLaunchSite, initialB.MyAgencyId, "LaunchPad");
            await ExpectSuccess(a, token);
            var empty = await WaitMap(a, moved.LaunchSitesRevision, "LaunchPad", Guid.Empty, token);
            await WaitMap(b, moved.LaunchSitesRevision, "LaunchPad", Guid.Empty, token);
            Assert.AreEqual(0, empty.LaunchSites.Count);

            const string moddedSite = "Example KK Site With Spaces";
            a.SendAdmin(password, AgencyAdminOp.AssignLaunchSite, initialB.MyAgencyId, moddedSite);
            await ExpectSuccess(a, token);
            var saved = await WaitMap(a, empty.LaunchSitesRevision, moddedSite, initialB.MyAgencyId, token);
            await WaitMap(b, empty.LaunchSitesRevision, moddedSite, initialB.MyAgencyId, token);
            await a.DisconnectForReconnectAsync(token);
            var reconnected = await a.ConnectAsync(server.Port, token);
            Assert.AreEqual(initialA.MyAgencyId, reconnected.MyAgencyId);
            Assert.IsTrue(reconnected.LaunchSitesReady);
            Assert.AreEqual(saved.LaunchSitesRevision, reconnected.LaunchSitesRevision);
            Assert.AreEqual(initialB.MyAgencyId, reconnected.LaunchSites.Single(site => site.SiteId == moddedSite).AgencyId);
            server.ThrowIfExited();
            succeeded = true;
        }
        finally
        {
            var errors = new List<Exception>();
            foreach (var bot in bots)
            {
                try { await bot.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); TestContext.WriteLine("Bot cleanup failed: " + error); }
                try
                {
                    if (server != null) server.AttachTranscript(bot.Name, bot.Transcript);
                    else TestContext.WriteLine(bot.Transcript);
                }
                catch (Exception error) { errors.Add(error); TestContext.WriteLine("Transcript attachment failed: " + error); }
            }
            if (server != null)
            {
                try { await server.DisposeAsync(); }
                catch (Exception error) { errors.Add(error); TestContext.WriteLine("Server cleanup failed: " + error); }
            }
            if (succeeded && errors.Count != 0) Assert.Fail("Scenario passed but cleanup failed: " + string.Join("; ", errors.Select(error => error.Message)));
        }
    }

    private static async Task ExpectSuccess(BotClient bot, CancellationToken token)
    {
        var reply = await bot.WaitForAsync<AgencyReplySnapshot>(_ => true, token);
        Assert.IsTrue(reply.Success, reply.Message);
    }

    private static Task<AgencySyncSnapshot> WaitMap(BotClient bot, long previousRevision, string site, Guid owner, CancellationToken token)
        => bot.WaitForAsync<AgencySyncSnapshot>(sync => sync.LaunchSitesReady && sync.LaunchSitesRevision > previousRevision
            && (owner == Guid.Empty ? sync.LaunchSites.All(entry => entry.SiteId != site)
                : sync.LaunchSites.Any(entry => entry.SiteId == site && entry.AgencyId == owner)), token);
}
