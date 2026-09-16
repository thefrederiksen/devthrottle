using System.Net.Http;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A FILTERED ROSTER READ NEVER PRUNES THE SNOOZE MEMORY - driven through the REAL
/// <see cref="GatewayEndpoints.Map"/> routes over HTTP, so the handler's own filter policy is what decides.
///
/// WHY IT IS HERE AND NOT BESIDE THE OTHER SLICE F TESTS. The fold-level test for this passed the roster as
/// null BY HAND, which asserts what the fold does when told it is looking at part of the account - and says
/// nothing whatever about whether the handler tells it that. The production call could be reverted to prune
/// its filtered fleet and that test would stay green. This one issues GET /sessions?machine=... against the
/// real routes, so the only thing that can make it pass is the handler deciding correctly.
///
/// THE RULE IT PINS: a view of part of the account cannot tell "this session is gone" from "this session is
/// not in the part I am looking at", and that distinction is the whole licence to drop an entry. So a read
/// narrowed by machine or by Director observes, and drops nothing.
///
/// PARKED SUITE. Gateway.Tests is host-bound and runs under -Parked.
/// </summary>
public sealed class AFilteredRosterReadNeverPrunesTests : IDisposable
{
    private const string MachineNorth = "SOREN_NORTH";
    private const string MachineSouth = "SOREN_SOUTH";
    private static readonly DateTime Armed = DateTime.UtcNow;

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _instances =
        Path.Combine(Path.GetTempPath(), "cc-filtered-prune-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        try { if (Directory.Exists(_instances)) Directory.Delete(_instances, true); }
        catch { /* best effort */ }
    }

    private static SessionDto Session(string id) => new()
    {
        SessionId = id,
        Agent = "TestAgent",
        RepoPath = "repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        CreatedAt = Armed.AddDays(-1),
        LastActivityAt = Armed.AddMinutes(-1),
    };

    /// <summary>The account's verdicts are on the wire, which is the gate slice F's stamp runs behind. No
    /// verdicts are stored, so every row folds as it would with none - the memory is what is under test.</summary>
    private sealed class ColourOn : ITurnVerdictRowSource
    {
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) =>
            new Dictionary<string, TurnVerdictDto>(StringComparer.Ordinal);
        public bool IsReading(TenantId tenant, string sessionId) => false;
    }

    [Fact]
    public async Task AReadNarrowedByMachine_LeavesTheOtherMachinesWatchAlone()
    {
        var store = new PushedSessionStore(() => DateTime.UtcNow);
        store.RegisterConnection(TenantId.Local, "dir-north", "conn-north");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-north", "conn-north", 1, new[] { Session("s-north") }));
        store.RegisterConnection(TenantId.Local, "dir-south", "conn-south");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-south", "conn-south", 1, new[] { Session("s-south") }));

        var db = _harness.Open();
        var snoozes = new SnoozeRegistry(db, _harness.LegacyPath("snoozes.json"));
        snoozes.Snooze("s-north", DateTime.UtcNow.AddMinutes(30), "dir-north");
        snoozes.Snooze("s-south", DateTime.UtcNow.AddMinutes(30), "dir-south");
        var watch = new SnoozeExpiryReJudge();

        await WithGateway(store, snoozes, watch, async http =>
        {
            // An UNFILTERED read: it can name the account, so it observes both and may prune.
            (await http.GetAsync("sessions")).EnsureSuccessStatusCode();
            Assert.Equal(2, watch.Watching);

            // A read narrowed to ONE MACHINE. The other machine's session is simply not in front of it, and
            // that is not the same as gone - so nothing is dropped.
            (await http.GetAsync($"sessions?machine={MachineNorth}")).EnsureSuccessStatusCode();
            Assert.Equal(2, watch.Watching);

            // Narrowed to one DIRECTOR is the same kind of partial view, and answers the same way.
            (await http.GetAsync("sessions?director=dir-north")).EnsureSuccessStatusCode();
            Assert.Equal(2, watch.Watching);
        });
    }

    [Fact]
    public async Task AnUnfilteredReadStillPrunes_WhenASessionHasReallyGone()
    {
        // The other half, through the same routes: the unfiltered read CAN tell the difference, so it still
        // does its job. Without this, "never prunes" could be satisfied by never pruning at all.
        var store = new PushedSessionStore(() => DateTime.UtcNow);
        store.RegisterConnection(TenantId.Local, "dir-north", "conn-north");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-north", "conn-north", 1, new[] { Session("s-north") }));
        store.RegisterConnection(TenantId.Local, "dir-south", "conn-south");
        Assert.True(store.ApplySnapshot(TenantId.Local, "dir-south", "conn-south", 1, new[] { Session("s-south") }));

        var db = _harness.Open();
        var snoozes = new SnoozeRegistry(db, _harness.LegacyPath("snoozes.json"));
        snoozes.Snooze("s-north", DateTime.UtcNow.AddMinutes(30), "dir-north");
        snoozes.Snooze("s-south", DateTime.UtcNow.AddMinutes(30), "dir-south");
        var watch = new SnoozeExpiryReJudge();

        await WithGateway(store, snoozes, watch, async http =>
        {
            (await http.GetAsync("sessions")).EnsureSuccessStatusCode();
            Assert.Equal(2, watch.Watching);

            // The southern Director's session really has ended - it pushes an empty list.
            Assert.True(store.ApplySnapshot(TenantId.Local, "dir-south", "conn-south", 2, Array.Empty<SessionDto>()));

            (await http.GetAsync("sessions")).EnsureSuccessStatusCode();
            Assert.Equal(1, watch.Watching);
        });
    }

    /// <summary>
    /// Hosts the REAL Gateway routes with the real push store, snooze registry and snooze memory, and two
    /// Directors registered on two different machines so the machine filter has something to narrow.
    /// </summary>
    private async Task WithGateway(
        PushedSessionStore store, SnoozeRegistry snoozes, SnoozeExpiryReJudge watch, Func<HttpClient, Task> assertion)
    {
        WebApplication? app = null;
        DirectorRegistry? registry = null;
        var started = false;
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
            app = builder.Build();
            registry = new DirectorRegistry(_instances);
            registry.RegisterFromStream("dir-north", MachineNorth, "soren", "1.0", 4242, DateTime.UtcNow, TenantId.Local);
            registry.RegisterFromStream("dir-south", MachineSouth, "soren", "1.0", 4243, DateTime.UtcNow, TenantId.Local);

            GatewayEndpoints.Map(
                app,
                registry,
                version: "test",
                token: "test-token",
                tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                    new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry()),
                pushedSessions: store,
                streamStaleAfter: TimeSpan.FromSeconds(20),
                snoozeRegistry: snoozes,
                turnVerdictRows: new ColourOn(),
                snoozeExpiry: watch);

            await app.StartAsync();
            var port = BoundPort.Of(app);
            started = true;
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            await assertion(http);
        }
        finally
        {
            if (app is not null)
            {
                if (started) await app.StopAsync();
                await app.DisposeAsync();
            }
            registry?.Dispose();
        }
    }
}
