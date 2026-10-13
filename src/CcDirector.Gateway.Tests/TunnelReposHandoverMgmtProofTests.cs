using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission (CUT RESTORATION, SB-4a): the director-level repository + saved-handover MANAGEMENT
/// routes on the Gateway - POST /directors/{id}/repos (repo-add), PATCH /directors/{id}/repos (repo-rename),
/// GET /directors/{id}/repos/overview (repos-overview), POST /directors/{id}/handovers (handover-create), and
/// DELETE /directors/{id}/handovers (handover-delete). The cut migrated the repo READS and repo-delete but left
/// these asymmetric leftovers un-migrated; they are restored here as tunnel verbs + Gateway routes.
///
/// TUNNEL-BY-CONSTRUCTION: the Director registers with a DELIBERATELY UNREACHABLE control endpoint (via
/// <see cref="FakeTunnelDirector"/>), so an HTTP dial cannot succeed - a working result could ONLY have ridden
/// the tunnel. Each test asserts the exact verb (and, where it matters, the payload) the Gateway sent DOWN the
/// tunnel, and - the Architect's SB-4a guardrail - the FAITHFUL HTTP status the old REST route returned: the
/// repo-add 201-vs-200 added flag, repo-rename's 404 for an unregistered path, handover-create's 201,
/// handover-delete's 404 for a missing file and 400 for a bad path, and 502 when the owner is not connected.
/// The core LOGIC (directory-existence, registry mutation, handover write/delete) is proven separately, over
/// the real executors, in RestApiSelfServiceEndpointsTests.
/// </summary>
public sealed class TunnelReposHandoverMgmtProofTests : IAsyncLifetime
{
    private const string Token = "test-token-repos-handover-mgmt";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-reposmgmt-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    // ===================================================================================== repo-add ====

    [Fact]
    public async Task RepoAdd_ridesTheTunnel_carriesPathAndName_and201WhenNewlyAdded()
    {
        const string dir = "dir-repo-add-new";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd => cmd.Verb switch
        {
            "repo-add" => FakeTunnelDirector.Ok(new RepoAddResponse
            {
                Added = true,
                Repo = new RepositoryDto { Name = "devthrottle", Path = @"D:\ReposFred\devthrottle" },
            }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });

        var resp = await _http.PostAsJsonAsync($"directors/{dir}/repos",
            new RepoAddRequest { Path = @"D:\ReposFred\devthrottle", Name = "devthrottle" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode); // added -> 201, and the unreachable endpoint means it rode the tunnel
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["added"]?.GetValue<bool>());
        Assert.Equal("devthrottle", node?["repo"]?["name"]?.GetValue<string>());

        var sent = Assert.Single(fake.AllOf("repo-add"));
        Assert.Equal("", sent.SessionId); // director-level
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal(@"D:\ReposFred\devthrottle", (string?)payload["path"]);
        Assert.Equal("devthrottle", (string?)payload["name"]);
    }

    [Fact]
    public async Task RepoAdd_returns200WhenAlreadyPresent()
    {
        const string dir = "dir-repo-add-existing";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd => cmd.Verb switch
        {
            "repo-add" => FakeTunnelDirector.Ok(new RepoAddResponse
            {
                Added = false, // already registered
                Repo = new RepositoryDto { Name = "devthrottle", Path = @"D:\ReposFred\devthrottle" },
            }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });

        var resp = await _http.PostAsJsonAsync($"directors/{dir}/repos",
            new RepoAddRequest { Path = @"D:\ReposFred\devthrottle" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // not added -> 200
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(node?["added"]?.GetValue<bool>());
    }

    [Fact]
    public async Task RepoAdd_blankPath_is400AtTheGateway_withoutTouchingTheTunnel()
    {
        const string dir = "dir-repo-add-blank";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir);

        var resp = await _http.PostAsJsonAsync($"directors/{dir}/repos", new RepoAddRequest { Path = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(fake.LastOf("repo-add")); // rejected before any tunnel send
    }

    [Fact]
    public async Task RepoAdd_directorLevelBadRequest_mapsTo400()
    {
        // The Director core rejects a non-existent directory with BadRequest; the route must surface 400, not 502.
        const string dir = "dir-repo-add-nodir";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd =>
            DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "directory not found: D:\\nope"));

        var resp = await _http.PostAsJsonAsync($"directors/{dir}/repos",
            new RepoAddRequest { Path = @"D:\nope" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Single(fake.AllOf("repo-add"));
    }

    // ================================================================================== repo-rename ====

    [Fact]
    public async Task RepoRename_ridesTheTunnel_carriesPathAndName_and200()
    {
        const string dir = "dir-repo-rename-ok";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd => cmd.Verb switch
        {
            "repo-rename" => FakeTunnelDirector.Ok(new RepositoryDto { Name = "new name", Path = @"D:\repo" }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });

        var resp = await _http.PatchAsJsonAsync($"directors/{dir}/repos",
            new RepoRenameRequest { Path = @"D:\repo", Name = "new name" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("new name", node?["name"]?.GetValue<string>());

        var sent = Assert.Single(fake.AllOf("repo-rename"));
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal(@"D:\repo", (string?)payload["path"]);
        Assert.Equal("new name", (string?)payload["name"]);
    }

    [Fact]
    public async Task RepoRename_unregisteredPath_mapsTo404()
    {
        // The Director core returns NotFound for a path not in the registry; the route must surface 404.
        const string dir = "dir-repo-rename-404";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd =>
            DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "repository not registered"));

        var resp = await _http.PatchAsJsonAsync($"directors/{dir}/repos",
            new RepoRenameRequest { Path = @"D:\gone", Name = "x" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Single(fake.AllOf("repo-rename"));
    }

    // ================================================================================ repos-overview ====

    [Fact]
    public async Task ReposOverview_ridesTheTunnel_and200()
    {
        const string dir = "dir-repos-overview";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd => cmd.Verb switch
        {
            "repos-overview" => FakeTunnelDirector.Ok(new List<RepoOverviewDto>
            {
                new() { Name = "devthrottle", Path = @"D:\ReposFred\devthrottle", LiveSessionCount = 2, HandoverCount = 1 },
            }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });

        var resp = await _http.GetAsync($"directors/{dir}/repos/overview");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("devthrottle", node?[0]?["name"]?.GetValue<string>());
        Assert.Equal(2, node?[0]?["liveSessionCount"]?.GetValue<int>());
        var sent = Assert.Single(fake.AllOf("repos-overview"));
        Assert.Equal("", sent.SessionId);
    }

    // ============================================================================== handover-create ====

    [Fact]
    public async Task HandoverCreate_ridesTheTunnel_carriesTheBody_and201()
    {
        const string dir = "dir-handover-create";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd => cmd.Verb switch
        {
            "handover-create" => FakeTunnelDirector.Ok(new HandoverDto
            {
                Path = @"D:\h\20260713_1200_my-handover.md",
                Title = "My handover",
                RepoPaths = new List<string> { @"D:\repo" },
            }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });

        var resp = await _http.PostAsJsonAsync($"directors/{dir}/handovers", new HandoverCreateRequest
        {
            Title = "My handover",
            Content = "# body",
            RepoPaths = new List<string> { @"D:\repo" },
            SessionName = "Some Session",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode); // handover-create success -> 201
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("My handover", node?["title"]?.GetValue<string>());

        var sent = Assert.Single(fake.AllOf("handover-create"));
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal("My handover", (string?)payload["title"]);
        Assert.Equal("# body", (string?)payload["content"]);
        Assert.Equal("Some Session", (string?)payload["sessionName"]);
    }

    [Fact]
    public async Task HandoverCreate_blankTitle_is400AtTheGateway()
    {
        const string dir = "dir-handover-create-blank";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir);

        var resp = await _http.PostAsJsonAsync($"directors/{dir}/handovers",
            new HandoverCreateRequest { Title = "", Content = "# body" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(fake.LastOf("handover-create"));
    }

    // ============================================================================== handover-delete ====

    [Fact]
    public async Task HandoverDelete_ridesTheTunnel_carriesThePath_and200()
    {
        const string dir = "dir-handover-delete";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd => cmd.Verb switch
        {
            "handover-delete" => FakeTunnelDirector.Ok(new HandoverDeleteResponse { Removed = true }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });

        var resp = await _http.DeleteAsync($"directors/{dir}/handovers?path={Uri.EscapeDataString(@"D:\h\a.md")}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["removed"]?.GetValue<bool>());

        var sent = Assert.Single(fake.AllOf("handover-delete"));
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal(@"D:\h\a.md", (string?)payload["path"]);
    }

    [Fact]
    public async Task HandoverDelete_missingFile_mapsTo404()
    {
        const string dir = "dir-handover-delete-404";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd =>
            DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "handover not found"));

        var resp = await _http.DeleteAsync($"directors/{dir}/handovers?path={Uri.EscapeDataString(@"D:\h\gone.md")}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Single(fake.AllOf("handover-delete"));
    }

    [Fact]
    public async Task HandoverDelete_pathOutsideFolder_mapsTo400()
    {
        const string dir = "dir-handover-delete-400";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir, dispatch: cmd =>
            DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "path must live inside the handover folder"));

        var resp = await _http.DeleteAsync($"directors/{dir}/handovers?path={Uri.EscapeDataString(@"C:\Windows\evil.md")}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Single(fake.AllOf("handover-delete"));
    }

    [Fact]
    public async Task HandoverDelete_blankPath_is400AtTheGateway()
    {
        const string dir = "dir-handover-delete-blank";
        await using var fake = await FakeTunnelDirector.StartAsync(_gateway, Token, dir);

        var resp = await _http.DeleteAsync($"directors/{dir}/handovers");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(fake.LastOf("handover-delete"));
    }

    // ========================================================================== owner-not-connected ====

    [Fact]
    public async Task ReposManagement_ownerRegisteredButNotTunnelConnected_is502()
    {
        // A Director present in the registry but WITHOUT a tunnel connection: the send delegate returns null,
        // which the route maps to 502 (the post-cut "not connected" signal - there is no HTTP fallback).
        const string dir = "dir-not-connected";
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = dir,
            TailnetEndpoint = "http://127.0.0.1:59919/",
            MachineName = "no-tunnel-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        var resp = await _http.PostAsJsonAsync($"directors/{dir}/repos",
            new RepoAddRequest { Path = @"D:\ReposFred\devthrottle" });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task ReposManagement_unknownDirector_is404()
    {
        var resp = await _http.GetAsync($"directors/{Guid.NewGuid()}/repos/overview");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

}
