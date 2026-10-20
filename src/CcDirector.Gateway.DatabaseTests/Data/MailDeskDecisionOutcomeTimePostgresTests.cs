using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.MailDesk;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE DECISION ROW'S OUTCOME TIME ON POSTGRESQL (Mail Desk mission, phase 4 contract, section 2.3; review of 4274,
/// finding 2). The migration <c>AddMailDeskDecisionOutcomeTime</c> is proven by the Gateway's own start-up path building
/// a database here, and filing's writes and reads are run against it: the queue's read, a move written with its time, a
/// second result ignored, and the 08:00 count's read by the time of filing.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the Gateway's
/// own start-up path and dropped afterwards.
/// </summary>
public sealed class MailDeskDecisionOutcomeTimePostgresTests
{
    private const string Mailbox = "email@devthrottle.com";
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-maildesk-outcome-time-a");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");

    [RequiresPostgresFact]
    public void TheOutcomeTimeColumn_AndFilingsWritesAndReads_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("maildeskoutcometime");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var channel = new MailDeskChannelStore(db).Add(A, Mailbox, "outlook", new[] { "devlinux" }, Owner, "the support mailbox", Now).Channel!;
            var decisions = new MailDeskDecisionStore(db);
            decisions.Insert(A, Input(channel.Id, "m-1", MailDeskWords.Decisions.Archive, Now.AddMinutes(1)));
            decisions.Insert(A, Input(channel.Id, "m-2", MailDeskWords.Decisions.Junk, Now.AddMinutes(2)));

            // The queue's read: both, oldest first.
            Assert.Equal(new[] { "m-1", "m-2" }, decisions.AwaitingFiling(A, channel.Id).Select(d => d.MessageKey).ToArray());

            // A move written with its time; a second result ignored.
            Assert.Equal(MailDeskFilingWrite.Written, decisions.RecordFiling(A, channel.Id, "m-1", MailDeskWords.Outcomes.Filed, "moved to Archive", Now.AddHours(1)));
            Assert.Equal(MailDeskFilingWrite.AlreadySet, decisions.RecordFiling(A, channel.Id, "m-1", MailDeskWords.Outcomes.AlreadyGone, "gone", Now.AddHours(2)));
            Assert.Equal(MailDeskFilingWrite.Written, decisions.RecordFiling(A, channel.Id, "m-2", MailDeskWords.Outcomes.AlreadyGone, "gone", Now.AddHours(1)));
            var row = decisions.Find(A, channel.Id, "m-1")!;
            Assert.Equal((MailDeskWords.Outcomes.Filed, (DateTime?)Now.AddHours(1)), (row.Outcome, row.OutcomeUtc));
            Assert.Empty(decisions.AwaitingFiling(A, channel.Id));

            // The 08:00 count's read, by the time of filing.
            Assert.Equal(new[] { "m-1" }, decisions.FiledBetween(A, Now.AddMinutes(30), Now.AddHours(3)).Select(d => d.MessageKey).ToArray());
            Assert.Empty(decisions.FiledBetween(A, Now, Now.AddMinutes(30)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            DropDatabase(connection);
        }
    }

    private static MailDeskDecisionInput Input(Guid channel, string key, string decision, DateTime received)
        => new(channel, key, received, "someone@example.net", "A message",
            decision == MailDeskWords.Decisions.Junk ? MailDeskWords.Layers.Block : MailDeskWords.Layers.Allow,
            decision, null, null, "a list entry", null, null, MailDeskWords.Modes.Live, null, null, Now.AddMinutes(5));

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
