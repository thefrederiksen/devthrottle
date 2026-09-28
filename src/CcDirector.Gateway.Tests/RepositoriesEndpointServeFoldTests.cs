using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// REGRESSION (inspection round 3, ruling R3-7): the old-Director compatibility proof runs through
/// the ACTUAL GET /repositories endpoint - a real HTTP request into the mapped Gateway route, the
/// folded JSON out - not the fold helper alone. The shape a pre-fix Director pushes (Provisional
/// set, a stale positive safe count, stale "safe-to-reap" worktree states) must serve folded:
/// zero safe count, every worktree "verifying". A verified repository must pass through unchanged
/// on the same route. Proven to have teeth by removing the endpoint's fold call and watching the
/// old-Director assertion go red.
/// </summary>
public sealed class RepositoriesEndpointServeFoldTests
{
    [Fact]
    public async Task GetRepositories_OldDirectorProvisionalShape_ServesFoldedJson()
    {
        var store = new PushedRepositoryStore();
        store.RegisterConnection(TenantId.Local, "d-old", "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, "d-old", "conn-1", 1, new[]
        {
            new RepoStatusDto
            {
                Name = "widget",
                Path = @"D:\repos\widget",
                MachineName = "M1",
                DirectorId = "d-old",
                Provisional = true,
                WorktreeCount = 2,
                WorktreesSafeToReap = 2, // the stale safe count a pre-fix Director pushes
                WorktreeBytes = 8192,
                Worktrees = new List<WorktreeDto>
                {
                    new() { Path = @"D:\repos\widget-wt1", Branch = "done", State = "safe-to-reap", Reason = "merged", SizeBytes = 4096 },
                    new() { Path = @"D:\repos\widget-wt2", Branch = "old", State = "safe-to-reap", Reason = "merged", SizeBytes = 4096 },
                },
            },
        }));

        await WithGateway(store, async http =>
        {
            using var response = await http.GetAsync("repositories");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var repo = Assert.Single(body.RootElement.EnumerateArray());
            Assert.Equal("widget", repo.GetProperty("name").GetString());
            Assert.True(repo.GetProperty("provisional").GetBoolean());
            Assert.Equal(0, repo.GetProperty("worktreesSafeToReap").GetInt32()); // stale count never serves
            var worktrees = repo.GetProperty("worktrees").EnumerateArray().ToList();
            Assert.Equal(2, worktrees.Count);
            Assert.All(worktrees, w => Assert.Equal("verifying", w.GetProperty("state").GetString()));
        });
    }

    [Fact]
    public async Task GetRepositories_VerifiedRepository_ServesAsPushed()
    {
        var store = new PushedRepositoryStore();
        store.RegisterConnection(TenantId.Local, "d-new", "conn-1");
        Assert.True(store.ApplySnapshot(TenantId.Local, "d-new", "conn-1", 1, new[]
        {
            new RepoStatusDto
            {
                Name = "widget",
                Path = @"D:\repos\widget",
                MachineName = "M1",
                DirectorId = "d-new",
                Provisional = false,
                WorktreeCount = 1,
                WorktreesSafeToReap = 1,
                Worktrees = new List<WorktreeDto>
                {
                    new() { Path = @"D:\repos\widget-wt", Branch = "done", State = "safe-to-reap", Reason = "merged", SizeBytes = 4096 },
                },
            },
        }));

        await WithGateway(store, async http =>
        {
            using var response = await http.GetAsync("repositories");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var repo = Assert.Single(body.RootElement.EnumerateArray());
            Assert.False(repo.GetProperty("provisional").GetBoolean());
            Assert.Equal(1, repo.GetProperty("worktreesSafeToReap").GetInt32());
            var worktree = Assert.Single(repo.GetProperty("worktrees").EnumerateArray());
            Assert.Equal("safe-to-reap", worktree.GetProperty("state").GetString());
        });
    }

    /// <summary>Hosts the real Gateway routes over HTTP with the given push cache - the same
    /// production <see cref="GatewayEndpoints.Map"/> the shipped Gateway runs.</summary>
    private static async Task WithGateway(PushedRepositoryStore store, Func<HttpClient, Task> assertion)
    {
        var instancesDirectory = Path.Combine(Path.GetTempPath(), "cc-repofold-" + Guid.NewGuid().ToString("N"));
        WebApplication? app = null;
        DirectorRegistry? registry = null;
        var started = false;
        try
        {
            var builder = WebApplication.CreateBuilder();
            // Issue #2161: bind an operating-system-assigned port; the number is read back after start.
            builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
            app = builder.Build();
            registry = new DirectorRegistry(instancesDirectory);
            GatewayEndpoints.Map(
                app,
                registry,
                version: "test",
                token: "test-token",
                // Self-host-only harness. The boundary is required and non-nullable now (finding I1-01), so
                // it gets the REAL self-host boundary: built over the SingleTenantContext, it always
                // resolves Local - behaviour identical to the null it used to state.
                tenantBoundary: new CcDirector.Gateway.Tenancy.HostedTenantBoundary(
                    new CcDirector.Core.Tenancy.SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry()),
                pushedRepositories: store, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);
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
                if (started)
                    await app.StopAsync();
                await app.DisposeAsync();
            }
            registry?.Dispose();
        }
    }
}
