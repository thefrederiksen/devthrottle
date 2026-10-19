using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.MailDesk;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE DECISION ROW'S RULES VERSION AND ATTEMPT COUNT ON POSTGRESQL (Mail Desk mission, phase 3 contract, section 8; task
/// F). The migration <c>AddMailDeskDecisionAttempts</c> is proven by the Gateway's own start-up path building a database
/// here, and the screening's one write is run against it: a decision recorded with its rules version and attempt 1; a
/// <c>failed</c> one decided again in place with its attempt raised; a decision that is not <c>failed</c> never changed;
/// the spend stop written once and cleared by a change of the limit.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the Gateway's
/// own start-up path and dropped afterwards.
/// </summary>
public sealed class MailDeskDecisionAttemptsPostgresTests
{
    private const string Mailbox = "email@devthrottle.com";
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-maildesk-attempts-a");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");

    [RequiresPostgresFact]
    public void TheDecisionAttemptColumns_AndTheScreeningsWrite_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("maildeskattempts");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var channels = new MailDeskChannelStore(db);
            var decisions = new MailDeskDecisionStore(db);
            var settings = new MailDeskSettingsStore(db);
            var channel = channels.Add(A, Mailbox, "outlook", new[] { "devlinux" }, Owner, "the support mailbox", Now).Channel!;

            // A failed screening, recorded with its rules version and attempt 1.
            var failed = decisions.Record(A, Input(channel.Id, MailDeskWords.Decisions.Failed, "sender-screen: timeout", cost: null, rulesVersion: 3, attempt: 1));
            Assert.Equal(MailDeskDecisionWriteKind.Inserted, failed.Kind);
            Assert.Equal((3, 1), (failed.Decision.RulesVersion, failed.Decision.AttemptCount));

            // Decided again in place: the same row, attempt 2.
            var again = decisions.Record(A, Input(channel.Id, MailDeskWords.Decisions.MailDeskAgent, "route-model: unsure", cost: 200, rulesVersion: 4, attempt: 2));
            Assert.Equal(MailDeskDecisionWriteKind.Updated, again.Kind);
            var row = decisions.Find(A, channel.Id, "m-1")!;
            Assert.Equal(failed.Decision.Id, row.Id);
            Assert.Equal((MailDeskWords.Decisions.MailDeskAgent, 4, 2, 200L), (row.Decision, row.RulesVersion, row.AttemptCount, row.CostMicros!.Value));

            // A decision that is not failed is never changed.
            var kept = decisions.Record(A, Input(channel.Id, MailDeskWords.Decisions.Junk, "block", cost: null, rulesVersion: 5, attempt: 3));
            Assert.Equal(MailDeskDecisionWriteKind.Kept, kept.Kind);
            Assert.Equal(MailDeskWords.Decisions.MailDeskAgent, decisions.Find(A, channel.Id, "m-1")!.Decision);

            // The spend stop: written once, cleared by a change of the limit.
            Assert.True(settings.MarkSpendStopped(A, Now));
            Assert.False(settings.MarkSpendStopped(A, Now.AddMinutes(5)));
            Assert.Equal(Now, settings.SpendStoppedUtc(A));
            settings.Set(A, MailDeskSettingNames.SpendLimit, 10m, Owner, "raised for the test", Now.AddMinutes(10));
            Assert.Null(settings.SpendStoppedUtc(A));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            DropDatabase(connection);
        }
    }

    private static MailDeskDecisionInput Input(Guid channel, string decision, string reason, long? cost, int rulesVersion, int attempt)
        => new(channel, "m-1", Now, "someone@example.net", "A question",
            decision == MailDeskWords.Decisions.Junk ? MailDeskWords.Layers.Block : MailDeskWords.Layers.RouteModel,
            decision, null, null, reason, null, cost, MailDeskWords.Modes.Observe, null, null, Now, rulesVersion, attempt);

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
