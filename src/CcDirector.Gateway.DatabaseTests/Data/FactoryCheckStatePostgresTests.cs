using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Triggers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE STATE A FACTORY CHECK KEEPS, AND THE NEW TRIGGER AND SCHEDULE COLUMNS, ON POSTGRESQL (Factory Definition, phase
/// 5b, design 1.1, 1.2 and 1.4). The hosted Gateway runs on PostgreSQL, so the migration that adds them is proven by the
/// Gateway's own start-up path building a database here, and the store's rules are run against it: append-only, the
/// newest row is the state, an unchanged state writes nothing, a reset is an empty row, a 64 KB state is kept whole,
/// each account reads only its own, and the key refuses a second row of the same number.
///
/// It owns a database of its own on the rig (<see cref="PostgresProofDatabase.ConnectionFor"/>), built by the Gateway's
/// own start-up path and dropped afterwards, so it shares nothing with the proof classes.
/// </summary>
public sealed class FactoryCheckStatePostgresTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-state-a");
    private static readonly TenantId B = new("acct-state-b");
    private const string Owner = "trigger:11111111-1111-1111-1111-111111111111";

    [RequiresPostgresFact]
    public void TheCheckState_AndTheDefinitionColumns_OnPostgres()
    {
        var connection = PostgresProofDatabase.ConnectionFor("factorycheckstate");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, connection);
        try
        {
            DropDatabase(connection);
            using var db = new GatewayDatabase(new SingleTenantContext());
            var store = new FactoryCheckStateStore(db);

            // Append-only; an unchanged state writes nothing; the newest is the state.
            Assert.Equal("", store.StateFor(A, "warmforward", Owner));
            Assert.NotNull(store.RecordIfChanged(A, "warmforward", Owner, "one", "dir-1", "run-1", Now));
            Assert.Null(store.RecordIfChanged(A, "warmforward", Owner, "one", "dir-1", "run-2", Now.AddMinutes(1)));
            var whole = new string('s', FactoryCheckStateStore.MaxContentBytes);
            Assert.Equal(2, store.RecordIfChanged(A, "warmforward", Owner, whole, "dir-1", "run-3", Now.AddMinutes(2))!.Sequence);
            Assert.Equal(whole, store.StateFor(A, "warmforward", Owner));

            // A reset is an empty row marked as one, on top of the history.
            var reset = store.Reset(A, "warmforward", Owner, "soren", Now.AddMinutes(3));
            Assert.Equal((3, "", "person soren", true), (reset.Sequence, reset.Content, reset.WrittenBy, reset.Reset));
            Assert.Equal("", store.StateFor(A, "warmforward", Owner));

            // Another account reads nothing of it.
            Assert.Null(store.Newest(B, "warmforward", Owner));

            // The key refuses a second row of the same number: two writers cannot both add it.
            using (var ctx = db.CreateContext(A))
            {
                var duplicate = new FactoryCheckStateEntity
                {
                    Factory = "warmforward", OwnerKey = Owner, Sequence = 3, Content = "x", WrittenUtc = Now, WrittenBy = "dir-2",
                };
                duplicate.TenantId = ctx.ActiveTenant!;
                ctx.FactoryCheckStates.Add(duplicate);
                Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
            }

            // The definition columns of a trigger and of a schedule round-trip.
            using (var ctx = db.CreateContext(A))
            {
                var trigger = new TriggerEntity
                {
                    Name = "warmforward-frontdesk", Factory = "warmforward", FactoryAgent = "frontdesk", Machine = "SOREN_NORTH",
                    RepoPath = "", CheckCommand = "", IntervalSeconds = 600, Prompt = "Go.", CreatedBy = "test", CreatedUtc = Now,
                    Seat = "frontdesk", DefinitionVersion = 4, CheckPath = "checks/new_mail.py", SecretsJson = """["a","b"]""",
                };
                trigger.TenantId = ctx.ActiveTenant!;
                ctx.Triggers.Add(trigger);
                var job = new CronJobEntity
                {
                    Id = "cj_check1", Name = "checked", ScheduleKind = CronSchedule.KindRecurring, CronExpression = "0 9 * * *",
                    TimeZoneId = "Europe/Copenhagen", Factory = "warmforward", Seat = "frontdesk",
                    Target = new CronJobTarget { Machine = "SOREN_NORTH" }, Action = new CronJobAction { RepoPath = "", Seed = "Go." },
                    CheckPath = "checks/new_mail.py", CheckSecretsJson = """["a"]""", DefinitionVersion = 4, CreatedUtc = Now,
                };
                job.TenantId = ctx.ActiveTenant!;
                ctx.CronJobs.Add(job);
                ctx.SaveChanges();
            }
            using (var ctx = db.CreateContext(A))
            {
                var t = ctx.Triggers.AsNoTracking().Single(x => x.Name == "warmforward-frontdesk");
                Assert.Equal(("frontdesk", 4, "checks/new_mail.py", """["a","b"]"""), (t.Seat, t.DefinitionVersion, t.CheckPath, t.SecretsJson));
                var j = ctx.CronJobs.AsNoTracking().Single(x => x.Id == "cj_check1");
                Assert.Equal(("checks/new_mail.py", """["a"]""", 4), (j.CheckPath, j.CheckSecretsJson, j.DefinitionVersion));
            }
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
