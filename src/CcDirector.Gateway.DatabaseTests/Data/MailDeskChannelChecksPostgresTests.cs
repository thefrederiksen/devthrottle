using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.MailDesk;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE MAILBOX CHECKS TABLE ON POSTGRESQL (Mail Desk mission, phase 3 contract, section 6.2). The hosted Gateway runs
/// on PostgreSQL, so the migration that adds <c>mail_desk_channel_checks</c> is proven by the Gateway's own start-up
/// path building a database here, and the store is run against it: a first good check kept, a good check after it
/// writing nothing, failures kept with their sentence, the first good check after them kept, a resolve kept once, and
/// each account reading only its own rows - the spend total and the window reads the 08:00 section uses beside it.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the Gateway's
/// own start-up path and dropped afterwards, so it shares nothing with the proof classes.
/// </summary>
public sealed class MailDeskChannelChecksPostgresTests
{
    private const string Mailbox = "email@devthrottle.com";
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-maildesk-checks-a");
    private static readonly TenantId B = new("acct-maildesk-checks-b");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");

    [RequiresPostgresFact]
    public void TheMailboxChecksTable_AndItsStore_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("maildeskchecks");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var channels = new MailDeskChannelStore(db);
            var checks = new MailDeskChannelCheckStore(db);
            var decisions = new MailDeskDecisionStore(db);
            var rules = new MailDeskRulesStore(db);
            var channel = channels.Add(A, Mailbox, "outlook", new[] { "devlinux" }, Owner, "the support mailbox", Now).Channel!;
            var run = Guid.NewGuid();

            // First good check kept; a good check after it is not.
            Assert.True(checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, run, Now, "devlinux")).Written);
            Assert.False(checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, run, Now.AddMinutes(10), "devlinux")).Written);

            // Two failures kept with their words, then the first good check after them.
            var head = checks.Record(A, MailDeskChannelCheckInput.Failed(channel.Id, run, Now.AddMinutes(20), "devlinux",
                "email@devthrottle.com is not signed in on devlinux: run cc-outlook -a support auth", "no account")).Row!;
            checks.Record(A, MailDeskChannelCheckInput.Failed(channel.Id, null, Now.AddMinutes(30), "devlinux", "not signed in", null));
            Assert.True(checks.Record(A, MailDeskChannelCheckInput.Read(channel.Id, null, Now.AddMinutes(40), "devlinux")).Written);

            var kept = checks.ByChannel(A)[channel.Id];
            Assert.Equal(new[] { true, false, false, true }, kept.Select(c => c.Ok));
            Assert.Equal("no account", kept[1].Detail);
            Assert.Equal(run, kept[1].TriggerRunId);

            // The problems fold reads them as one cleared problem of two failures.
            var problem = Assert.Single(MailDeskProblems.MailboxProblems(channel, kept, TimeZoneInfo.Utc, Now.AddHours(1)));
            Assert.Equal(2, problem.Occurrences);
            Assert.Equal(head.Id.ToString("D"), problem.RunId);

            // A resolve kept once.
            Assert.True(checks.Resolve(A, head.Id, "you", "signed in again", Now.AddHours(1)));
            Assert.False(checks.Resolve(A, head.Id, "you", "again", Now.AddHours(2)));
            Assert.Equal("signed in again", checks.Find(A, head.Id)!.ResolvedReason);

            // Beside it, the reads the 08:00 section uses.
            decisions.Insert(A, new MailDeskDecisionInput(channel.Id, "m-1", Now, "news@example.org", "A newsletter", MailDeskWords.Layers.Allow,
                MailDeskWords.Decisions.Archive, null, null, "allow", 0.93, 120, MailDeskWords.Modes.Observe, null, null, Now.AddMinutes(5)));
            decisions.Insert(A, new MailDeskDecisionInput(channel.Id, "m-2", Now, "news@example.org", "Another", MailDeskWords.Layers.Allow,
                MailDeskWords.Decisions.Archive, null, null, "allow", null, null, MailDeskWords.Modes.Observe, null, null, Now.AddDays(2)));
            Assert.Equal(120, decisions.SpentMicros(A));
            Assert.Equal(new[] { "m-1" }, decisions.DecidedBetween(A, Now, Now.AddDays(1)).Select(d => d.MessageKey));
            Assert.Single(rules.ChangesBetween(A, Now, Now.AddDays(1)));

            // Another account reads nothing of it.
            Assert.Empty(checks.ByChannel(B));
            Assert.Null(checks.Find(B, head.Id));
            Assert.Equal(0, decisions.SpentMicros(B));
            Assert.Empty(rules.ChangesBetween(B, Now, Now.AddDays(1)));
            Assert.Throws<ArgumentException>(() => checks.Record(B, MailDeskChannelCheckInput.Read(channel.Id, null, Now, "devlinux")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            DropDatabase(connection);
        }
    }

    private static void DropDatabase(string connection)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>().UseNpgsql(connection).Options;
        using var ctx = new GatewayDbContext(options) { ActiveTenant = TenantId.Local.Value };
        var name = ctx.Database.GetDbConnection().Database;
        if (!name.StartsWith("ccpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to drop '{name}': it is not a throwaway rig database.");
        ctx.Database.EnsureDeleted();
    }
}
