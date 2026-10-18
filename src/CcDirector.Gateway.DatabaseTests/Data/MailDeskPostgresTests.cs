using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.MailDesk;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE MAIL DESK'S FIVE TABLES ON POSTGRESQL (Mail Desk mission, phase 1, contract section 2). The hosted Gateway runs on
/// PostgreSQL, so the migration that adds them is proven by the Gateway's own start-up path building a database here,
/// and the three stores are run against it: a channel added, its mode set, removed and added again as a new row; the
/// unique index over ACTIVE channels refusing a second active row (a filtered index, which is the part most likely to
/// differ between the providers); rules versions numbered from 1 with their change rows, the unique (tenant, version)
/// index refusing a second row of one version; one decision per message; the routed table; and each account reading
/// only its own rows.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the Gateway's
/// own start-up path and dropped afterwards, so it shares nothing with the proof classes.
/// </summary>
public sealed class MailDeskPostgresTests
{
    private const string Mailbox = "email@devthrottle.com";
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-maildesk-a");
    private static readonly TenantId B = new("acct-maildesk-b");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");

    [RequiresPostgresFact]
    public void TheMailDeskTables_AndTheirStores_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("maildesk");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var channels = new MailDeskChannelStore(db);
            var rules = new MailDeskRulesStore(db);
            var decisions = new MailDeskDecisionStore(db);

            // A channel: added in observe, its mode set, a second add of the same address refused.
            var first = channels.Add(A, Mailbox, "outlook", new[] { "devlinux", "BACKUP-PC" }, Owner, "the support mailbox", Now).Channel!;
            Assert.Equal(MailDeskWords.Modes.Observe, first.Mode);
            Assert.Equal(MailDeskChannelOutcome.ModeSet, channels.SetMode(A, Mailbox, "live", Owner, "ready", Now.AddMinutes(1)).Outcome);
            Assert.Equal(MailDeskChannelOutcome.AlreadyActive, channels.Add(A, Mailbox, "outlook", new[] { "devlinux" }, Owner, "again", Now).Outcome);

            // The filtered unique index refuses a second ACTIVE row for one address, written past the store.
            using (var ctx = db.CreateContext(A))
            {
                ctx.MailDeskChannels.Add(Channel(Mailbox, removed: false));
                Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
            }

            // A decision on it; one per message.
            var decision = decisions.Insert(A, new MailDeskDecisionInput(first.Id, "m-1", Now, "sales@spammy.example", "Grow", MailDeskWords.Layers.Block,
                MailDeskWords.Decisions.Junk, null, null, "block entry", null, 12, MailDeskWords.Modes.Live, MailDeskWords.Outcomes.Filed, null, Now));
            Assert.True(decision.Inserted);
            Assert.False(decisions.Insert(A, new MailDeskDecisionInput(first.Id, "m-1", Now, "x@example.com", "", MailDeskWords.Layers.Allow,
                MailDeskWords.Decisions.Archive, null, null, "", null, null, MailDeskWords.Modes.Live, null, null, Now)).Inserted);

            // Removed, kept with its decision, and added again as a new row beside it.
            channels.Remove(A, Mailbox, Owner, "paused", Now.AddMinutes(2));
            var second = channels.Add(A, Mailbox, "outlook", new[] { "devlinux" }, Owner, "back", Now.AddMinutes(3)).Channel!;
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, channels.List(A, includeRemoved: true).Count);
            Assert.Equal(1, decisions.List(A, new MailDeskDecisionFilter(ChannelAddress: Mailbox)).Total);
            Assert.Equal(1, decisions.CountsByChannel(A, Now.AddHours(-1), Now.AddHours(1))[first.Id].ByDecision[MailDeskWords.Decisions.Junk]);

            // Rules: version 0 is empty, the first save is version 1 with its change row, the old version reads back.
            Assert.Equal(0, rules.Newest(A).Version);
            var allow = new MailDeskAllowEntry { From = "*@notifications.example.com", What = "build notifications", Do = MailDeskAllowDo.Archive };
            var v1 = rules.Save(A, 0, MailDeskRulesDocument.Empty.WithAllow(allow), new MailDeskChange(MailDeskWords.ChangeKinds.Allow,
                MailDeskWords.ChangeActions.Add, "*@notifications.example.com", "{}", MailDeskWho.Session("sess-1", "Mail Desk - Seat"), "notifications"), Now);
            Assert.Equal(1, v1.Version!.Version);
            var v2 = rules.Save(A, 1, MailDeskRulesDocument.Empty, new MailDeskChange(MailDeskWords.ChangeKinds.Allow,
                MailDeskWords.ChangeActions.Remove, "*@notifications.example.com", "{}", Owner, "too noisy"), Now.AddMinutes(1));
            Assert.Equal(2, v2.Version!.Version);
            Assert.Single(rules.Read(A, 1)!.Document.Allow);
            Assert.Equal(MailDeskRulesOutcome.Stale, rules.Save(A, 1, MailDeskRulesDocument.Empty, new MailDeskChange(MailDeskWords.ChangeKinds.Block,
                MailDeskWords.ChangeActions.Add, "x@example.com", "{}", Owner, "late"), Now).Outcome);
            var history = rules.History(A);
            Assert.Equal(6, history.Total); // channel add, mode, channel remove, channel add, allow add, allow remove
            Assert.Equal("Mail Desk - Seat", rules.History(A, kind: MailDeskWords.ChangeKinds.Allow).Changes.Last().WhoName);

            // The unique (tenant, version) index refuses a second row of one version.
            using (var ctx = db.CreateContext(A))
            {
                ctx.MailDeskRules.Add(new MailDeskRulesVersionEntity
                {
                    TenantId = A.Value, Version = 2, Document = "{}", SavedUtc = Now, SavedByKind = "person", SavedBy = "x",
                });
                Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
            }

            // The routed table keeps a row.
            using (var ctx = db.CreateContext(A))
            {
                ctx.MailDeskRouted.Add(new MailDeskRoutedEntity
                {
                    TenantId = A.Value, DecisionId = decision.Decision.Id, Factory = "devthrottle", Seat = "mail-desk",
                    State = MailDeskWords.RoutedStates.Open, CreatedUtc = Now, ChangedUtc = Now,
                });
                ctx.SaveChanges();
            }

            // Another account reads nothing of it, and may hold the same address and the same version.
            Assert.Empty(channels.List(B, includeRemoved: true));
            Assert.Equal(0, rules.Newest(B).Version);
            Assert.Equal(0, rules.History(B).Total);
            Assert.Equal(0, decisions.List(B, new MailDeskDecisionFilter()).Total);
            using (var ctx = db.CreateContext(B)) Assert.Empty(ctx.MailDeskRouted.ToList());
            Assert.Equal(MailDeskChannelOutcome.Added, channels.Add(B, Mailbox, "outlook", new[] { "devlinux" }, Owner, "theirs", Now).Outcome);
            Assert.Equal(MailDeskRulesOutcome.Saved, rules.Save(B, 0, MailDeskRulesDocument.Empty.WithAllow(allow), new MailDeskChange(
                MailDeskWords.ChangeKinds.Allow, MailDeskWords.ChangeActions.Add, "*@notifications.example.com", "{}", Owner, "theirs"), Now).Outcome);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            DropDatabase(connection);
        }
    }

    private static MailDeskChannelEntity Channel(string address, bool removed) => new()
    {
        TenantId = A.Value, Kind = "email", Provider = "outlook", Address = address, MachinesJson = "[\"x\"]",
        Factory = "mail-desk", Seat = "mail-desk", Mode = "observe", CreatedUtc = Now, CreatedBy = "x", ChangedUtc = Now, ChangedBy = "x",
        RemovedUtc = removed ? Now : null,
    };

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
