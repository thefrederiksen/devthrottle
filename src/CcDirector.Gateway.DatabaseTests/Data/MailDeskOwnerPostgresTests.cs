using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.MailDesk;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// DONE, RETURN AND THE OWNER ON POSTGRESQL (Mail Desk mission, phase 4 contract, section 4; task C). The migration
/// <c>AddMailDeskRoutedOwnerColumns</c> is proven by the Gateway's own start-up path building a database here, and the
/// reads and writes task C added to <see cref="MailDeskRoutedStore"/> are run against it: a return records when and
/// from which seat, the hand to the owner records when, the owner's rows and the window's rows are read, each row is
/// read with its decision and mailbox, and the owner's problem is resolved once.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the Gateway's
/// own start-up path and dropped afterwards.
/// </summary>
public sealed class MailDeskOwnerPostgresTests
{
    private const string Mailbox = "email@devthrottle.com";
    private const string Session = "aaaaaaaa-0000-4000-8000-000000000002";
    private static readonly DateTime Now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-maildesk-owner-a");
    private static readonly MailDeskWho Owner = MailDeskWho.Person("owner-device-1");

    [RequiresPostgresFact]
    public void TheOwnerColumns_ReturnHandResolveAndTheReads_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("maildeskowner");
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
            var decision = decisions.Insert(A, new MailDeskDecisionInput(channel.Id, "m-1", Now.AddMinutes(-30), "someone@example.net",
                "An invoice", MailDeskWords.Layers.Rule, MailDeskWords.Decisions.Route, "qa-books", "clerk", "invoices", null, null,
                MailDeskWords.Modes.Live, null, null, Now.AddMinutes(-29))).Decision;
            var row = Assert.Single(routed.CreateMissing(A, Now));

            // The clerk returns it: when and from which seat are kept.
            routed.BeginStart(A, new[] { row.Id }, "start-1", Now, Now);
            routed.Take(A, "start-1", Session, "devlinux", Now);
            Assert.True(routed.Return(A, row.Id, Session, "not an invoice", Now.AddMinutes(1)).Succeeded);
            var returned = routed.Get(A, row.Id)!;
            Assert.Equal(("qa-books/clerk", Now.AddMinutes(1)), (returned.ReturnedFrom, returned.ReturnedUtc));

            // The Mail Desk agent returns it: handed to the owner, with when.
            routed.BeginStart(A, new[] { row.Id }, "start-2", Now.AddMinutes(2), Now.AddMinutes(2));
            routed.Take(A, "start-2", Session, "devlinux", Now.AddMinutes(2));
            Assert.True(routed.Return(A, row.Id, Session, "no seat fits", Now.AddMinutes(3)).Succeeded);
            var handed = routed.Get(A, row.Id)!;
            Assert.Equal((MailDeskWords.RoutedStates.Returned, Now.AddMinutes(3), "mail-desk/mail-desk", "no seat fits"),
                (handed.State, handed.OwnerUtc, handed.ReturnedFrom, handed.ReturnNote));
            // ...and the clerk's earlier return is kept beside it.
            Assert.Equal((Now.AddMinutes(1), "qa-books/clerk", "not an invoice"),
                (handed.EarlierReturnedUtc, handed.EarlierReturnedFrom, handed.EarlierReturnNote));

            // The reads: the owner's rows, the window's rows, and each with its decision and mailbox.
            Assert.Equal(new[] { row.Id }, routed.WithOwner(A).Select(r => r.Id));
            Assert.Equal(new[] { row.Id }, routed.TouchedBetween(A, Now, Now.AddHours(1)).Select(r => r.Id));
            Assert.Empty(routed.TouchedBetween(A, Now.AddHours(1), Now.AddHours(2)));
            var email = Assert.Single(routed.Emails(A, routed.WithOwner(A)));
            Assert.Equal((decision.Id, Mailbox, MailDeskWords.DefaultFactory), (email.Decision.Id, email.Mailbox, email.Factory));

            // Resolved once, by hand; the email stays with the owner.
            Assert.True(routed.Resolve(A, row.Id, "you", "answered it myself", Now.AddMinutes(4)));
            Assert.False(routed.Resolve(A, row.Id, "you", "again", Now.AddMinutes(5)));
            var resolved = routed.Get(A, row.Id)!;
            Assert.Equal((MailDeskWords.RoutedStates.Returned, Now.AddMinutes(4), "you", "answered it myself"),
                (resolved.State, resolved.ResolvedUtc, resolved.ResolvedBy, resolved.ResolvedReason));
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
