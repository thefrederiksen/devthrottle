using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection; // AddMessagePackProtocol (client)
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR D): the DIRECTOR-LEVEL routes on the Gateway that used to HTTP-dial the
/// target Director (the six reads repos-list / facts / coaching-categories / claude-sessions / interrupted-list /
/// fs-list, plus the three director-level mutations repo-delete / interrupted-dismiss / interrupted-remove) now
/// ride the tunnel under stream mode. This boots a REAL streamMode <see cref="GatewayHost"/>, dials the REAL
/// DirectorHub with a REAL MessagePack SignalR client, and drives each route end to end.
///
/// TUNNEL-BY-CONSTRUCTION: the Director is registered with a DELIBERATELY UNREACHABLE control endpoint, so an
/// HTTP dial cannot succeed - a 200 with the expected body can ONLY have ridden the tunnel. Each test also
/// asserts the exact verb (and, where it matters, the payload) the Gateway sent DOWN the tunnel, so a route that
/// silently mapped to the wrong verb or dropped its query parameters fails loudly.
///
/// These are DIRECTOR-LEVEL commands (SessionId is ""); the Gateway targets the Director by its id, so no live
/// session is needed - the connected Director IS the addressable unit.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TunnelDirectorReadProofTests : IAsyncLifetime
{
    private const string Token = "test-token-director-read-proof";
    private const string DirectorId = "dir-director-read";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-dirread-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private HubConnection _conn = null!;

    // Every command the Gateway sent over the tunnel. A test asks for the command of ITS OWN verb, never for "the last
    // command": the Gateway also sends commands of its own accord (a pushed waiting session has its screen read on a
    // background task), so which command arrived last depends on how busy the machine is.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DirectorCommand> _commands = new();

    private DirectorCommand Sent(string verb) => Assert.Single(_commands, c => c.Verb == verb);

    public TunnelDirectorReadProofTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-dirread-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // The Director registers UNREACHABLE, so any working route proves it rode the tunnel, never an HTTP dial.
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = DirectorId,
            TailnetEndpoint = "http://127.0.0.1:59919/", // nothing listens here
            MachineName = "dirread-machine",
            Pid = 1,
            Version = "test",
            StartedAt = DateTime.UtcNow,
        });

        _conn = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_gateway.Port}/director-stream", o => o.AccessTokenProvider = () => Task.FromResult<string?>(Token))
            .AddMessagePackProtocol()
            .Build();
        _conn.On<DirectorCommand, DirectorCommandResult>("Command", Dispatch);
        await _conn.StartAsync();
        await _conn.InvokeAsync("Hello", new DirectorStreamHello { DirectorId = DirectorId, Version = "test" });
    }

    public async Task DisposeAsync()
    {
        try { await _conn.DisposeAsync(); } catch { /* best effort */ }
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    // The Director-side Command handler: records the command, then returns a canned body per verb so the test
    // can assert the Gateway route surfaced exactly that body as its HTTP response. Bodies are serialized from
    // the SAME contract DTOs the real cores emit, so a shape mismatch would fail the Gateway-side deserialize.
    private DirectorCommandResult Dispatch(DirectorCommand cmd)
    {
        _commands.Enqueue(cmd);
        return cmd.Verb switch
        {
            "repos-list" => Ok(new List<RepositoryDto> { new() { Name = "devthrottle", Path = @"D:\ReposFred\devthrottle" } }),
            "facts" => Ok(new DirectorFactsDto { DirectorId = DirectorId, MachineName = "dirread-machine", Version = "9.9.9" }),
            "coaching-categories" => Ok(new List<CoachingCategoryDto> { new() { Key = "assistant", Label = "Assistant" } }),
            "claude-sessions" => Ok(new List<ClaudeSessionDto> { new() { ClaudeSessionId = "cs-1", RepoPath = @"D:\repo" } }),
            "fs-list" => Ok(new DirectoryListingDto { CurrentPath = @"D:\some\dir", Entries = new() }),
            "handovers-list" => Ok(new List<HandoverDto> { new() { Path = @"D:\h\a.md", Title = "a handover" } }),
            "handovers-content" => Ok(new HandoverContentDto { Path = @"D:\h\a.md", Content = "the handover body" }),
            "interrupted-list" => Ok(new List<CrashJournalDto>
            {
                new()
                {
                    DirectorId = "dead-dir",
                    Pid = 4321,
                    MachineName = "dirread-machine",
                    Sessions = new List<CrashJournalSessionDto>
                    {
                        new() { SessionId = "sess-abc", Name = "a dead session", RepoPath = @"D:\repo" },
                    },
                },
            }),
            "repo-delete" => Ok(new RepoDeleteResponse { Removed = true }),
            "interrupted-dismiss" => Ok(new InterruptedDismissResponse { Dismissed = true }),
            "interrupted-remove" => Ok(new InterruptedRemoveResponse { Removed = true }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        };
    }

    private static DirectorCommandResult Ok(object body) => DirectorCommandResult.Success(JsonSerializer.Serialize(body, WebJson));
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------------------------- reads ----

    [Fact]
    public async Task Repos_ridesTheTunnel_asReposListVerb()
    {
        var resp = await _http.GetAsync($"directors/{DirectorId}/repos");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // an HTTP dial to the unreachable Director would have failed
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("devthrottle", node?[0]?["name"]?.GetValue<string>());
        var sent = Sent("repos-list");
        Assert.Equal("", sent.SessionId); // director-level: no session
    }

    [Fact]
    public async Task Facts_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"directors/{DirectorId}/facts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("9.9.9", node?["version"]?.GetValue<string>());
        Assert.Equal(DirectorId, node?["directorId"]?.GetValue<string>());
        Sent("facts");
    }

    [Fact]
    public async Task CoachingCategories_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"directors/{DirectorId}/coaching/categories");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("assistant", node?[0]?["key"]?.GetValue<string>());
        Sent("coaching-categories");
    }

    [Fact]
    public async Task ClaudeSessions_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"directors/{DirectorId}/claude-sessions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("cs-1", node?[0]?["claudeSessionId"]?.GetValue<string>());
        Sent("claude-sessions");
    }

    [Fact]
    public async Task FsList_ridesTheTunnel_andCarriesThePathInThePayload()
    {
        var resp = await _http.GetAsync($"directors/{DirectorId}/fs/list?path={Uri.EscapeDataString(@"D:\some\dir")}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(@"D:\some\dir", node?["currentPath"]?.GetValue<string>());

        var sent = Sent("fs-list");
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal(@"D:\some\dir", (string?)payload["path"]); // the ?path query rode the payload
    }

    [Fact]
    public async Task InterruptedList_fanOut_ridesTheTunnel_andFlattensToRows()
    {
        var resp = await _http.GetAsync("interrupted");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        // The fan-out flattened the one journal's one session into an InterruptedSessionDto row.
        Assert.Equal("sess-abc", node?[0]?["sessionId"]?.GetValue<string>());
        Assert.Equal("dead-dir", node?[0]?["deadDirectorId"]?.GetValue<string>());
        Assert.Equal(DirectorId, node?[0]?["reportedByDirectorId"]?.GetValue<string>());
        Sent("interrupted-list");
    }

    [Fact]
    public async Task HandoversList_ridesTheTunnel()
    {
        var resp = await _http.GetAsync($"directors/{DirectorId}/handovers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("a handover", node?[0]?["title"]?.GetValue<string>());
        Sent("handovers-list");
    }

    [Fact]
    public async Task HandoversContent_ridesTheTunnel_andCarriesThePathInThePayload()
    {
        var resp = await _http.GetAsync($"directors/{DirectorId}/handovers/content?path={Uri.EscapeDataString(@"D:\h\a.md")}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("the handover body", node?["content"]?.GetValue<string>());

        var sent = Sent("handovers-content");
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal(@"D:\h\a.md", (string?)payload["path"]);
    }

    // ------------------------------------------------------------------------------------ writes ----

    [Fact]
    public async Task RepoDelete_ridesTheTunnel_andCarriesThePathInThePayload()
    {
        var resp = await _http.DeleteAsync($"directors/{DirectorId}/repos?path={Uri.EscapeDataString(@"D:\gone")}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["removed"]?.GetValue<bool>());

        var sent = Sent("repo-delete");
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal(@"D:\gone", (string?)payload["path"]);
    }

    [Fact]
    public async Task InterruptedDismiss_ridesTheTunnel_andCarriesTheJournalKey()
    {
        var resp = await _http.DeleteAsync($"interrupted/dead-dir/4321?via={DirectorId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["dismissed"]?.GetValue<bool>());

        var sent = Sent("interrupted-dismiss");
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal("dead-dir", (string?)payload["deadDirectorId"]);
        Assert.Equal(4321, (int?)payload["deadPid"]);
    }

    [Fact]
    public async Task InterruptedRemove_ridesTheTunnel_andCarriesTheSessionKey()
    {
        var resp = await _http.DeleteAsync($"interrupted/dead-dir/4321/sessions/sess-abc?via={DirectorId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = await resp.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["removed"]?.GetValue<bool>());

        var sent = Sent("interrupted-remove");
        var payload = JsonNode.Parse(sent.PayloadJson)!.AsObject();
        Assert.Equal("dead-dir", (string?)payload["deadDirectorId"]);
        Assert.Equal(4321, (int?)payload["deadPid"]);
        Assert.Equal("sess-abc", (string?)payload["sessionId"]);
    }

    // -------------------------------------------------------------------------------------- helpers ----

}
