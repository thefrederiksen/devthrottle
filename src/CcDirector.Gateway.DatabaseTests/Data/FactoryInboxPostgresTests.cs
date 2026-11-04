using CcDirector.Core.Dictation;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Inbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE FACTORY INBOX'S FOUR TABLES ON POSTGRESQL (Factory Inbox mission, phase 1). The hosted Gateway runs on PostgreSQL,
/// so the migration that adds them is proven by the Gateway's own start-up path building a database here, and the store
/// is run against it end to end: a typed and a spoken message sent; a claim taken by one Secretary, refused to a second
/// while the first lives, and taken by the second once the first is named ended (the conditional update that is the
/// exclusivity boundary across replicas); a bundle handed; a to-do decision writing exactly one to-do row; a second
/// decision on one message refused by the unique index; the bundle closing; and another account reading none of it.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the Gateway's
/// own start-up path and dropped afterwards, so it shares nothing with the proof classes.
/// </summary>
public sealed class FactoryInboxPostgresTests
{
    private const string Factory = "invoices";
    private const string Secretary1 = "11111111-1111-4111-8111-111111111111";
    private const string Secretary2 = "22222222-2222-4222-8222-222222222222";
    private const string Boss = "33333333-3333-4333-8333-333333333333";
    private static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-inbox-a");
    private static readonly TenantId B = new("acct-inbox-b");

    [RequiresPostgresFact]
    public void TheFactoryInboxTables_AndTheirStore_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("factoryinbox");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var store = new FactoryInboxStore(db);

            // A typed and a spoken message; the spoken one keeps its raw transcript beside its text.
            var typed = store.Send(A, new FactoryInboxSendInput(Factory, "Add an export button", FactoryInboxWords.Notes.Good,
                FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Cockpit, "owner-browser", Now));
            var spoken = store.Send(A, new FactoryInboxSendInput(Factory, "Ask DevThrottle", FactoryInboxWords.Notes.Bad,
                FactoryInboxWords.Sources.Spoken, FactoryInboxWords.Surfaces.Phone, "owner-phone", Now.AddSeconds(1),
                RawText: "Ask dev throttle", Corrections: [new TranscriptEdit("dev throttle", "DevThrottle")]));
            Assert.Equal("Ask dev throttle", store.Get(A, spoken.Id)!.RawText);

            // One Secretary claims both; a second may take nothing while the first lives, and everything once it has ended.
            Assert.Equal(2, store.Receive(A, Factory, Secretary1, null, [], [], [], Now.AddMinutes(1)).Count);
            Assert.Empty(store.Receive(A, Factory, Secretary2, null, [], [], [], Now.AddMinutes(2)));
            Assert.Equal([Secretary1], store.OtherClaimants(A, Factory, Secretary2));
            var taken = store.Receive(A, Factory, Secretary2, null, [Secretary1], [],
                [new FactoryInboxLink(typed.Id, "report", "r-1")], Now.AddMinutes(20));
            Assert.Equal(2, taken.Count);
            Assert.All(taken, m => Assert.Equal(Secretary2, m.ClaimSessionId));

            // The bundle, and the Boss's decisions on it.
            var (bundle, messages) = store.HandBundle(A, Factory, Secretary2, null, Now.AddMinutes(21));
            Assert.Equal(2, messages.Count);
            Assert.Single(store.OpenBundles(A, Factory, Boss, Now.AddMinutes(22)));
            var todo = store.Decide(A, Factory, Boss, typed.Id, FactoryInboxWords.Decisions.Todo, null, null, Now.AddMinutes(23));
            Assert.Equal(todo.Todo!.Id, Assert.Single(store.Todos(A, Factory)).Id);
            Assert.Equal("already_decided", Assert.Throws<FactoryInboxRefusedException>(() =>
                store.Decide(A, Factory, Boss, typed.Id, FactoryInboxWords.Decisions.Noted, null, null, Now.AddMinutes(24))).Code);
            store.Decide(A, Factory, Boss, spoken.Id, FactoryInboxWords.Decisions.Declined, null, "Not now", Now.AddMinutes(25));
            Assert.Empty(store.OpenBundles(A, Factory, Boss, Now.AddMinutes(26)));

            // The unique index refuses a second decision row for one message, written past the store.
            using (var ctx = db.CreateContext(A))
            {
                ctx.FactoryInboxDecisions.Add(new FactoryInboxDecisionEntity
                {
                    TenantId = A.Value, MessageId = typed.Id, Factory = Factory, Kind = FactoryInboxWords.Decisions.Noted,
                    DecidedBySessionId = Boss, DecidedAtUtc = Now,
                });
                Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
            }

            // The summary's reads, and the counts.
            var (sent, decided) = store.Since(A, Factory, Now.AddHours(-1), Now.AddHours(1));
            Assert.Equal(2, sent.Count);
            Assert.Equal(2, decided.Count);
            Assert.Equal(0, store.CountsByFactory(A)[Factory].Waiting);
            Assert.Equal(bundle.Id, store.Get(A, typed.Id)!.BundleId);

            // Another account reads nothing of it.
            Assert.Equal(0, store.Thread(B, Factory, 10).Total);
            Assert.Empty(store.Todos(B, Factory));
            Assert.Empty(store.CountsByFactory(B));
            Assert.Empty(store.OpenBundles(B, Factory, Boss, Now));
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
