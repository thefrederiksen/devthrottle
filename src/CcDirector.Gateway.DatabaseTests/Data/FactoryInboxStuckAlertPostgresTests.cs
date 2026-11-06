using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Inbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE STUCK-INBOX READ ON POSTGRESQL (the deploy slowdown of 6 November 2026). The stuck-inbox alert reads every overdue
/// row - not received, held by a Secretary, a bundle not decided - in ONE grouped UNION ALL, and the hosted Gateway runs
/// that statement on PostgreSQL, where a set operation is translated differently from SQLite. So it is run here on the
/// schema the Gateway's own start-up builds: two stuck factories, every reason in each, the words and counts the owner
/// reads, and another account seeing none of it. Times are chosen here; no clock is read.
/// </summary>
public sealed class FactoryInboxStuckAlertPostgresTests
{
    private const string Secretary1 = "11111111-1111-4111-8111-111111111111";
    private const string Secretary2 = "22222222-2222-4222-8222-222222222222";
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-stuck-a");
    private static readonly TenantId B = new("acct-stuck-b");

    [RequiresPostgresFact]
    public async Task List_EveryReasonInTwoFactories_IsReadInOneStatement_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("factoryinboxstuck");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var store = new FactoryInboxStore(db, _ => CcDirector.Core.Dictation.Models.DictationDictionary.Empty);
            var source = new FactoryInboxStuckAlertSource(db, _ => [],
                (_, sessions) => sessions.Where(s => s == Secretary1).ToHashSet(StringComparer.Ordinal),
                (_, _) => false);

            Assert.Empty(source.List(A, T0.AddDays(1)));

            foreach (var (factory, secretary) in new[] { ("invoices", Secretary1), ("payroll", Secretary2) })
            {
                var held = await Send(store, factory, T0);
                var handed = await Send(store, factory, T0);
                store.Receive(A, factory, secretary, [held, handed], [], [], [], T0.AddMinutes(1));
                store.HandBundle(A, factory, secretary, [handed], T0.AddMinutes(2));
                await Send(store, factory, T0.AddMinutes(3));
                await Send(store, factory, T0.AddMinutes(4));
            }

            var alerts = source.List(A, T0.AddHours(3)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();

            Assert.Equal(["factory-inbox-stuck:invoices", "factory-inbox-stuck:payroll"], alerts.Select(a => a.Id).ToArray());
            // invoices: its Secretary ended holding a message, two messages unreceived, a bundle never read.
            var invoices = Assert.IsType<FactoryInboxStuckDetail>(alerts[0].Detail).Reasons;
            Assert.Equal(
                new[] { FactoryInboxStuckAlertSource.Causes.NoDirector, FactoryInboxStuckAlertSource.Causes.SecretaryEnded,
                    FactoryInboxStuckAlertSource.Causes.BossDidNotDecide }.OrderBy(c => c, StringComparer.Ordinal),
                invoices.Select(r => r.Cause).OrderBy(c => c, StringComparer.Ordinal));
            Assert.Equal(2, invoices.Single(r => r.Cause == FactoryInboxStuckAlertSource.Causes.NoDirector).Count);
            Assert.Equal(T0.AddMinutes(18), invoices.Single(r => r.Cause == FactoryInboxStuckAlertSource.Causes.NoDirector).SinceUtc);
            Assert.Equal(T0.AddMinutes(16), invoices.Single(r => r.Cause == FactoryInboxStuckAlertSource.Causes.SecretaryEnded).SinceUtc);
            Assert.Equal("A bundle was handed to the Boss more than 2 hours ago and not decided: no Boss has read it.",
                invoices.Single(r => r.Cause == FactoryInboxStuckAlertSource.Causes.BossDidNotDecide).Sentence);
            // payroll: its Secretary still lives, so only the unreceived messages and the bundle.
            var payroll = Assert.IsType<FactoryInboxStuckDetail>(alerts[1].Detail).Reasons;
            Assert.DoesNotContain(payroll, r => r.Cause == FactoryInboxStuckAlertSource.Causes.SecretaryEnded);
            Assert.Equal(2, payroll.Count);

            Assert.Empty(source.List(B, T0.AddHours(3)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
            DropDatabase(connection);
        }
    }

    private static async Task<Guid> Send(FactoryInboxStore store, string factory, DateTime at)
        => (await store.SendAsync(A, new FactoryInboxSendInput(factory, "Check the totals.", FactoryInboxWords.Notes.None,
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone", at))).Id;

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
