using System.Net;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A HOSTED GATEWAY WITH THE STRANDED-MACHINE ALARM STARTS, SERVES, AND RUNS ITS LICENSE MINUTE CHECK ON POSTGRESQL
/// (mission "Licenses - Loud When Stranded", the reland of step 1).
///
/// WHY IT EXISTS. Step 1 (#4493) was reverted because production did not answer after its deploy. The outage was later
/// seen on the rolled-back image too (Npgsql read timeouts, then an exhausted pool of 10), so step 1 was not its cause -
/// but nothing had ever started a real host with step 1 in it AND license data for the minute check to work on. The
/// Tech Lead's local boot answered /healthz with no tenant, so the check did nothing; the service tests do the work but
/// start no host. This is both at once.
///
/// What it starts is the host production runs: <see cref="GatewayHostOptions.Hosted"/>, the Gateway database on
/// PostgreSQL through <see cref="GatewayDatabase.PostgresConnectionEnvVar"/> - the variable a hosted deployment sets -
/// opened and migrated by the host's own startup path, on a database of this run's that is dropped afterwards.
///
/// The license data is the 26 October 2026 picture in small. machine-a is on account A, which is at 100% of its weekly
/// limit, and holds no sign-in for account B, which has room; no machine can approve sign-ins for B, so the Gateway
/// cannot sign it in by itself. machine-b is on B. The check is <see cref="GatewayHost.RunLicenseSwitchCheckAsync"/>,
/// what the host's timer calls once a minute, inside the per-tenant pass.
///
/// Facts, each stated as something that must be TRUE:
///   1. the host answers /healthz with 200 before the check;
///   2. the minute check finishes, and writes no FAILED line from any license check;
///   3. it did real work on the data: the stranded alarm is OPEN for machine-a on account A, and its one owner email
///      went out - so a check that read nothing could not pass;
///   4. a second check keeps it open quietly (no second email) and still fails nothing;
///   5. the host still answers /healthz with 200 after both checks;
///   6. no step hangs: the start, each /healthz, each minute check and the stop must each finish within
///      <see cref="HungAfter"/>, or the test fails with a sentence naming the step that hung.
///
/// Gated by [RequiresPostgresFact]: it runs under scripts\test-database.ps1 and reports SKIPPED anywhere else.
/// </summary>
[Collection("DirectorRoot")]
public sealed class LicenseHostStartPostgresTests : IAsyncLifetime
{
    private const string A = "account-a@example.com";
    private const string B = "account-b@example.com";
    private const string MachineA = "machine-a";
    private const string MachineB = "machine-b";

    /// <summary>The one moment the test happens at: the host's license clock is held here.</summary>
    private static readonly DateTime Now = new(2026, 10, 26, 20, 50, 0, DateTimeKind.Utc);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-license-host-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-license-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<(TenantId Tenant, LicenseOwnerEmail Email)> _emails = new();
    private string? _priorRoot;
    private string? _priorConnection;
    private string _connection = "";
    private GatewayHost? _gateway;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        if (!PostgresRigGate.IsRequired)
            return; // the test reports SKIPPED: there is no database to start a host on
        // Set HERE, not in the constructor, so a host that fails to start is answered by the restore below: xunit 2
        // does not call DisposeAsync when InitializeAsync throws.
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _priorConnection = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        try
        {
            _connection = PostgresProofDatabase.ConnectionFor("licensehost");
            DropDatabase(_connection);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, _connection);

            _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-license-host", authEnabled: true,
                instancesDirectory: _instancesDir,
                workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
                snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
                streamMode: true,
                options: new GatewayHostOptions { Hosted = true });
            _gateway.LicenseClock = new FixedClock();
            // Every owner email of this host is taken here: nothing reaches the account service.
            _gateway.LicenseOwnerMail = (tenant, email, _) =>
            {
                lock (_emails) _emails.Add((tenant, email));
                return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
            };
            await Bounded(_gateway.StartAsync(), "the hosted host's StartAsync");
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        }
        catch
        {
            await StopAndRestoreAsync();
            throw;
        }
    }

    public Task DisposeAsync() => StopAndRestoreAsync();

    private async Task StopAndRestoreAsync()
    {
        _http?.Dispose();
        try
        {
            if (_gateway is not null)
                await Bounded(_gateway.StopAsync(), "the hosted host's StopAsync");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, _priorConnection);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
            if (_connection.Length > 0)
            {
                // The stopped host's pooled connections would keep the database in use and refuse the drop.
                Npgsql.NpgsqlConnection.ClearAllPools();
                DropDatabase(_connection);
            }
            try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
        }
    }

    private static LicenseHolding Holding(string email, bool active, double weekly) => new()
    {
        AccountEmail = email,
        IsActive = active,
        Plan = "max",
        RateLimitTier = "default_claude_max_20x",
        FiveHour = new LicenseWindow { Utilization = 30, ResetsAtUtc = Now.AddHours(2) },
        SevenDay = new LicenseWindow { Utilization = weekly, ResetsAtUtc = Now.AddDays(4) },
        ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
        ReadAtUtc = Now,
        SignInExpiresAtUtc = Now.AddDays(20),
    };

    private static LicenseUsageReport Report(string machine, params LicenseHolding[] holdings) => new()
    {
        MachineName = machine,
        OsUser = "owner",
        SentAtUtc = Now,
        Trigger = LicenseReportTriggers.Interval,
        Holdings = holdings.ToList(),
    };

    private async Task<HttpStatusCode> Healthz(string when)
        => (await Bounded(_http.GetAsync("healthz"), $"GET /healthz {when}")).StatusCode;

    /// <summary>
    /// The longest any one step may take before the test calls it hung: an UPPER BOUND, never what decides a normal
    /// pass (the recorded runs take 1 to 3 seconds for the whole test). A hang is exactly what was suspected of step 1
    /// in production - a Gateway that stopped answering - so a step that never returns must FAIL, naming itself, rather
    /// than stall the run with no verdict (the review of pull request 4525, finding F1).
    /// </summary>
    private static readonly TimeSpan HungAfter = TimeSpan.FromMinutes(2);

    private static async Task Bounded(Task step, string name)
    {
        try
        {
            await step.WaitAsync(HungAfter);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{name} did not finish within {HungAfter.TotalMinutes:0} minutes: it hung.");
        }
    }

    private static async Task<T> Bounded<T>(Task<T> step, string name)
    {
        await Bounded((Task)step, name);
        return await step;
    }

    [RequiresPostgresFact]
    public async Task AHostedHostOnPostgres_ServesHealthz_AndItsLicenseMinuteCheckRaisesTheStrandedAlarm_WithoutAFailure()
    {
        var gateway = _gateway!;
        Assert.Equal(HttpStatusCode.OK, await Healthz("before the check"));

        // One account, with two Directors whose stream connections are registered, so the tenant is in the host's
        // per-tenant pass and its machines count as connected - the shape of a real account on the hosted Gateway.
        var tenant = gateway.TenantRegistry.MintOrLookupBySubject("sub-license-host", "owner@example.com");
        gateway.PushedSessions.RegisterConnection(tenant, "dir-machine-a", "conn-machine-a");
        gateway.PushedSessions.RegisterConnection(tenant, "dir-machine-b", "conn-machine-b");
        gateway.LicenseStore.ApplyReport(tenant, "dir-machine-a", 1, Report(MachineA, Holding(A, true, 100)), Now, TimeSpan.Zero);
        gateway.LicenseStore.ApplyReport(tenant, "dir-machine-b", 2, Report(MachineB, Holding(B, true, 2)), Now, TimeSpan.Zero);

        using var log = new CapturedLog();
        await Bounded(gateway.RunLicenseSwitchCheckAsync(), "the first license minute check (RunLicenseSwitchCheckAsync)");

        var first = log.Lines();
        Assert.DoesNotContain(first, l => l.Contains("[License", StringComparison.Ordinal) && l.Contains("FAILED", StringComparison.Ordinal));
        Assert.DoesNotContain(first, l => l.Contains("[GatewayHost] license", StringComparison.Ordinal) && l.Contains("FAILED", StringComparison.Ordinal));
        var alarm = Assert.Single(gateway.LicenseAlarms.ListOpen(tenant), a => a.Kind == LicenseStrandedAlarm.Kind.Key);
        Assert.Equal((MachineA, A), (alarm.MachineName, alarm.AccountEmail));
        var (emailedTenant, _) = Assert.Single(_emails);
        Assert.Equal(tenant, emailedTenant);
        // And the row is in PostgreSQL, read by raw SQL over a connection of the test's own: no object under test
        // answers this, so a host that quietly kept its data anywhere else fails here.
        Assert.Equal(1L, OpenStrandedRowsInPostgres(tenant));

        await Bounded(gateway.RunLicenseSwitchCheckAsync(), "the second license minute check (RunLicenseSwitchCheckAsync)");

        Assert.DoesNotContain(log.Lines(), l => l.Contains("FAILED", StringComparison.Ordinal)
                                                && (l.Contains("[License", StringComparison.Ordinal) || l.Contains("[GatewayHost] license", StringComparison.Ordinal)));
        Assert.Equal(alarm.Id, Assert.Single(gateway.LicenseAlarms.ListOpen(tenant), a => a.Kind == LicenseStrandedAlarm.Kind.Key).Id);
        Assert.Single(_emails);
        Assert.Equal(HttpStatusCode.OK, await Healthz("after both checks"));
    }

    private long OpenStrandedRowsInPostgres(TenantId tenant)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connection);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM gateway.license_alarms WHERE tenant_id = @t AND \"Kind\" = @k AND \"ClosedAtUtc\" IS NULL";
        cmd.Parameters.AddWithValue("t", tenant.Value);
        cmd.Parameters.AddWithValue("k", LicenseStrandedAlarm.Kind.Key);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Drop this test's own database. Its name carries the throwaway prefix by construction; the guard is
    /// repeated here because this is the line that destroys something.</summary>
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
