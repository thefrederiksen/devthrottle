using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The repo-state push endpoint (issue #2118). The claim that matters most is the CROSS-TENANT NEGATIVE: a
/// device key belonging to account A must not be able to write repo state into account B, whatever the
/// request body says. The tenant comes from the authenticated credential; <c>directorId</c> in the payload
/// is a row key inside the caller's own partition and carries no authority at all.
///
/// Driven through a real request pipeline (auth middleware + the mapped route) rather than by calling the
/// handler, because the property under test is exactly the seam between the credential and the store - and
/// that seam only exists once the middleware has run.
/// </summary>
public sealed class RepoStateEndpointsTests : IAsyncLifetime
{
    private const string SharedToken = "shared-machine-token";

    private readonly GatewayDbTestHarness _h = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"rs-dev-{Guid.NewGuid():N}.json");
    private static readonly DateTime T0 = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private DeviceRegistry _devices = null!;
    private RepoStateStore _store = null!;

    public async Task InitializeAsync()
    {
        var db = _h.Open(new AsyncLocalTenantContext());
        _devices = new DeviceRegistry(_devPath);
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: false);
        _store = new RepoStateStore(db);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");

        // The REAL host-wide gate, so an unauthenticated push meets the production wall rather than a
        // stand-in.
        var cfg = new AuthMiddleware.RequireToken { Hosted = false, Token = SharedToken, Devices = _devices };
        _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, cfg, next));

        // The hosted pipeline's tenant middleware: after auth, enter the scope the authenticated device key
        // resolves to. Present because the property under test is exactly the seam between the credential
        // and the store, and that seam only exists once this has run.
        _app.Use(async (ctx, next) =>
        {
            if (boundary.ResolveRequestTenant(ctx) is { } resolved)
            {
                using (boundary.EnterScope(resolved))
                    await next();
            }
            else
            {
                await next();
            }
        });

        Api.RepoStateEndpoints.Map(_app, _store, boundary, () => T0);
        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

    /// <summary>Enroll a device and bind it to a tenant, exactly as hosted enrollment does.</summary>
    private string KeyFor(string deviceId, string tenantId)
    {
        var key = _devices.Register(deviceId, deviceId.ToUpperInvariant()).DeviceKey;
        _devices.SetAccountBinding(deviceId, $"sub-{tenantId}", tenantId);
        return key;
    }

    private static RepoStatePushRequest Push(string directorId, string repoPath, string branchName) => new()
    {
        DirectorId = directorId,
        MachineName = "SOREN",
        Repositories = new List<RepoStateSnapshotDto>
        {
            new()
            {
                Name = "repo", Path = repoPath, CollectedAtUtc = T0, DefaultBranch = "origin/main",
                CurrentBranch = "main",
                Branches = new List<RepoStateBranchDto>
                {
                    new() { Name = branchName, CommitsAheadOfDefault = 2, MergedIntoDefault = false },
                },
            },
        },
    };

    private async Task<HttpResponseMessage> PostAsync(string? key, RepoStatePushRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api.RepoStateEndpoints.Path)
        {
            Content = JsonContent.Create(body),
        };
        if (key is not null)
            request.Headers.Add("Authorization", $"Bearer {key}");
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task A_device_key_from_one_account_cannot_write_repo_state_into_another()
    {
        var aliceKey = KeyFor("dev-alice", "tenant-alice");
        KeyFor("dev-bob", "tenant-bob");

        // Alice pushes, naming a director id and a repository path that Bob also uses. Nothing in the body
        // names a tenant, and nothing in it could: the tenant comes from the key.
        var response = await PostAsync(aliceKey, Push("dir-shared", "D:/repos/shared", "alice-branch"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var alice = Assert.Single(_store.ReadFresh(new TenantId("tenant-alice"), TimeSpan.FromHours(12), T0));
        Assert.Equal("alice-branch", Assert.Single(alice.Branches).Name);

        // Bob's partition is untouched.
        Assert.Empty(_store.ReadFresh(new TenantId("tenant-bob"), TimeSpan.FromHours(12), T0));
    }

    [Fact]
    public async Task Each_accounts_push_lands_only_in_its_own_partition_even_on_identical_keys()
    {
        var aliceKey = KeyFor("dev-alice", "tenant-alice");
        var bobKey = KeyFor("dev-bob", "tenant-bob");

        Assert.Equal(HttpStatusCode.OK,
            (await PostAsync(aliceKey, Push("dir-shared", "D:/repos/shared", "alice-branch"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await PostAsync(bobKey, Push("dir-shared", "D:/repos/shared", "bob-branch"))).StatusCode);

        var alice = Assert.Single(_store.ReadFresh(new TenantId("tenant-alice"), TimeSpan.FromHours(12), T0));
        var bob = Assert.Single(_store.ReadFresh(new TenantId("tenant-bob"), TimeSpan.FromHours(12), T0));

        Assert.Equal("alice-branch", Assert.Single(alice.Branches).Name);
        Assert.Equal("bob-branch", Assert.Single(bob.Branches).Name);
    }

    [Fact]
    public async Task An_unauthenticated_push_is_refused()
    {
        var response = await PostAsync(key: null, Push("dir-1", "D:/repos/one", "b"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_key_with_no_bound_tenant_is_DENIED_never_served_the_local_partition()
    {
        // A real, valid device key that was never bound to an account. On hosted this must be a deny - it
        // must NOT fall through to the Local partition, which is where a self-host install's rows live.
        var unbound = _devices.Register("dev-unbound", "UNBOUND").DeviceKey;

        var response = await PostAsync(unbound, Push("dir-1", "D:/repos/one", "b"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_store.ReadFresh(TenantId.Local, TimeSpan.FromHours(12), T0));
    }

    [Fact]
    public async Task A_push_with_no_director_id_is_rejected()
    {
        var key = KeyFor("dev-alice", "tenant-alice");
        var body = Push("", "D:/repos/one", "b");

        var response = await PostAsync(key, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_repository_rejects_the_push_and_stores_nothing()
    {
        var key = KeyFor("dev-alice", "tenant-alice");
        var body = Push("dir-1", "D:/repos/one", "b");
        body.Repositories.Add(new RepoStateSnapshotDto { Name = "broken", Path = "", CollectedAtUtc = T0 });

        var response = await PostAsync(key, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_store.ReadFresh(new TenantId("tenant-alice"), TimeSpan.FromHours(12), T0));
    }

    [Fact]
    public async Task There_is_no_read_route_for_repo_state()
    {
        // The only consumer reads the store in-process. A public read would put every repository path and
        // branch name of every account on an HTTP surface for no caller that exists.
        var key = KeyFor("dev-alice", "tenant-alice");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(key, Push("dir-1", "D:/repos/one", "b"))).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, Api.RepoStateEndpoints.Path);
        request.Headers.Add("Authorization", $"Bearer {key}");
        var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
