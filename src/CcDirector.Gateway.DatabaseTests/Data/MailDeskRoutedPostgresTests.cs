using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.MailDesk;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// AN EMAIL ROUTED TO A SEAT ON POSTGRESQL (Mail Desk mission, phase 4 contract, section 3.1; task B). The migration
/// <c>AddMailDeskRoutedLiveColumns</c> is proven by the Gateway's own start-up path building a database here, and every
/// write of <see cref="MailDeskRoutedStore"/> is run against it: create (once per decision, the unique index refusing a
/// second row), the start and its session with the decision's outcome in the same save, a failed start, the re-route,
/// the redirect, a return to the Mail Desk seat, done, and the hand to the owner; then the routed read with its filters.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the Gateway's
/// own start-up path and dropped afterwards.
/// </summary>
public sealed class MailDeskRoutedPostgresTests
{
    private const string Mailbox = "email@devthrottle.com";
    private const string Session = "aaaaaaaa-0000-4000-8000-000000000001";
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-maildesk-routed-a");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");

    [RequiresPostgresFact]
    public void TheRoutedColumns_AndEveryRoutedWrite_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("maildeskrouted");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var channels = new MailDeskChannelStore(db);
            var decisions = new MailDeskDecisionStore(db);
            var routed = new MailDeskRoutedStore(db);
            channels.Add(A, Mailbox, "outlook", new[] { "devlinux" }, Owner, "the support mailbox", Now);
            var channel = channels.SetMode(A, Mailbox, MailDeskWords.Modes.Live, Owner, "live for the proof", Now).Channel!;

            var route = decisions.Insert(A, Input(channel.Id, "m-1", MailDeskWords.Decisions.Route, "qa-books", "clerk", -30)).Decision;
            var agent = decisions.Insert(A, Input(channel.Id, "m-2", MailDeskWords.Decisions.MailDeskAgent, null, null, -20)).Decision;
            decisions.Insert(A, Input(channel.Id, "m-3", MailDeskWords.Decisions.Archive, null, null, -10));

            // Create: one queued row per live route and mail-desk-agent decision, once.
            var created = routed.CreateMissing(A, Now);
            Assert.Equal(new[] { route.Id, agent.Id }, created.Select(r => r.DecisionId).ToArray());
            Assert.Empty(routed.CreateMissing(A, Now));
            var routeRow = routed.ForDecision(A, route.Id)!;
            Assert.Equal((channel.Id, "qa-books", "clerk", MailDeskWords.RoutedStates.Open), (routeRow.ChannelId, routeRow.Factory, routeRow.Seat, routeRow.State));
            var agentRow = routed.ForDecision(A, agent.Id)!;
            Assert.Equal((MailDeskWords.DefaultFactory, MailDeskWords.DefaultSeat), (agentRow.Factory, agentRow.Seat));

            // The unique index: a second row for one decision cannot exist.
            using (var ctx = db.CreateContext(A))
            {
                ctx.MailDeskRouted.Add(new MailDeskRoutedEntity
                {
                    TenantId = A.Value, DecisionId = route.Id, ChannelId = channel.Id, Factory = "qa-books", Seat = "clerk",
                    State = MailDeskWords.RoutedStates.Open, CreatedUtc = Now, ChangedUtc = Now,
                });
                Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
            }

            // A failed start is counted and the row queued again; the next start gives the session and the decision's outcome.
            Assert.Single(routed.BeginStart(A, new[] { routeRow.Id }, "start-1", Now, Now));
            Assert.Equal(1, routed.RecordStartFailure(A, routeRow.Id, "refused", Now)!.StartFailures);
            Assert.Single(routed.BeginStart(A, new[] { routeRow.Id }, "start-2", Now, Now));
            Assert.Single(routed.Take(A, "start-2", Session, "devlinux", Now));
            Assert.Equal((MailDeskWords.Outcomes.SeatStarted, $"session {Session} on devlinux (qa-books/clerk)"),
                (decisions.Get(A, route.Id)!.Outcome, decisions.Get(A, route.Id)!.OutcomeDetail));

            // Only the holding session may answer; a return goes to the Mail Desk seat with the note.
            Assert.Equal(MailDeskRoutedRefusal.NotYourEmail, routed.Done(A, routeRow.Id, "someone-else", null, Now).Refusal);
            Assert.True(routed.Return(A, routeRow.Id, Session, "a support question", Now).Succeeded);
            var returned = routed.Get(A, routeRow.Id)!;
            Assert.Equal((MailDeskWords.DefaultFactory, MailDeskWords.DefaultSeat, (string?)null, 1, "a support question"),
                (returned.Factory, returned.Seat, returned.SessionId, returned.ReturnedCount, returned.ReturnNote));

            // The Mail Desk agent's row: started, orphaned once, started again, done.
            routed.BeginStart(A, new[] { agentRow.Id }, "start-3", Now, Now);
            routed.Take(A, "start-3", Session, "devlinux", Now);
            Assert.Equal(1, routed.Reroute(A, agentRow.Id, Now)!.RerouteCount);
            routed.BeginStart(A, new[] { agentRow.Id }, "start-4", Now, Now);
            routed.Take(A, "start-4", Session, "devlinux", Now);
            Assert.True(routed.Done(A, agentRow.Id, Session, "answered", Now).Succeeded);
            Assert.Equal((MailDeskWords.RoutedStates.Done, "answered"), (routed.Get(A, agentRow.Id)!.State, routed.Get(A, agentRow.Id)!.DoneNote));

            // The returned row cannot be taken by its seat: handed to the owner. The clerk had it, so the decision keeps
            // its seat-started outcome (review of 4278, finding 1); only an email no seat ever had is marked failed.
            var handed = routed.Redirect(A, routeRow.Id, "'mail-desk' has no published definition", Now)!;
            Assert.Equal((MailDeskWords.RoutedStates.Returned, "'mail-desk' has no published definition"), (handed.State, handed.OwnerReason));
            Assert.Equal(MailDeskWords.Outcomes.SeatStarted, decisions.Get(A, route.Id)!.Outcome);

            // The routed read, with its filters.
            Assert.Equal(2, routed.List(A, new MailDeskRoutedFilter()).Total);
            Assert.Equal(new[] { agentRow.Id }, routed.List(A, new MailDeskRoutedFilter(State: MailDeskWords.RoutedStates.Done)).Routed.Select(r => r.Id));
            Assert.Equal(new[] { routeRow.Id }, routed.List(A, new MailDeskRoutedFilter(State: MailDeskWords.RoutedStates.Returned, Factory: MailDeskWords.DefaultFactory)).Routed.Select(r => r.Id));
            Assert.Empty(routed.List(A, new MailDeskRoutedFilter(Queued: true)).Routed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            DropDatabase(connection);
        }
    }

    private static MailDeskDecisionInput Input(Guid channel, string key, string decision, string? factory, string? seat, int minutes)
        => new(channel, key, Now.AddMinutes(minutes), "someone@example.net", "A question",
            decision == MailDeskWords.Decisions.Route ? MailDeskWords.Layers.Rule : MailDeskWords.Layers.RouteModel,
            decision, factory, seat, "reason", null, null, MailDeskWords.Modes.Live, null, null, Now.AddMinutes(minutes + 1));

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
