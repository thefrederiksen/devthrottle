using System.Net;
using System.Net.Http.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end tests for the session-self-service REST slice: handover CRUD
/// (POST /handovers, DELETE /handovers, GET /handovers?repo=), explicit repo management
/// (POST /repos, PATCH /repos, GET /repos/overview), the repo filter on
/// GET /claude-sessions, and the Control API info handoff to the SessionManager
/// (CC_DIRECTOR_API / CC_DIRECTOR_ID injection source). Runs a real ControlApiHost on an
/// ephemeral port with CC_DIRECTOR_ROOT + CC_VAULT_PATH redirected to a temp dir.
/// In the "DirectorRoot" collection (serializes root-touching tests).
/// </summary>
[Collection("DirectorRoot")]
public sealed class RestApiSelfServiceEndpointsTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevVault;
    private string _repoA = null!;            // Initialized in InitializeAsync
    private string _repoB = null!;            // Initialized in InitializeAsync
    private ControlApiHost _host = null!;     // Initialized in InitializeAsync
    private SessionManager _sm = null!;       // Initialized in InitializeAsync
    private RepositoryRegistry _registry = null!; // Initialized in InitializeAsync
    private HttpClient _client = null!;       // Initialized in InitializeAsync

    public RestApiSelfServiceEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _prevVault = Environment.GetEnvironmentVariable("CC_VAULT_PATH");
        _root = Path.Combine(Path.GetTempPath(), "ccd-selfsvc-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", Path.Combine(_root, "vault"));
    }

    public async Task InitializeAsync()
    {
        _repoA = Path.Combine(_root, "repos", "repoA");
        _repoB = Path.Combine(_root, "repos", "repoB");
        Directory.CreateDirectory(_repoA);
        Directory.CreateDirectory(_repoB);

        _registry = new RepositoryRegistry();
        _registry.Load();
        _registry.TryAdd(_repoA);

        // One pre-existing handover referencing repoA (for the ?repo= filter + overview count).
        var handoverDir = CcStorage.VaultHandovers();
        Directory.CreateDirectory(handoverDir);
        File.WriteAllText(Path.Combine(handoverDir, "20260601_0900_seeded-handover.md"),
            "---\n" +
            "session_name: Seeded Session\n" +
            "repositories:\n" +
            $"  - path: {_repoA}\n" +
            "---\n\n" +
            "# Seeded handover body\n");

        // One workspace-history entry linking a (fake) Claude session to repoA, so
        // /claude-sessions and /repos/overview have repo-keyed history to aggregate.
        new SessionHistoryStore().Save(new SessionHistoryEntry
        {
            Id = Guid.NewGuid(),
            RepoPath = _repoA,
            ClaudeSessionId = "selfsvc-test-claude-session",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            LastUsedAt = DateTimeOffset.UtcNow.AddHours(-1),
            FirstPromptSnippet = "selfsvc seeded prompt",
        });

        _sm = new SessionManager(new AgentOptions());
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, repositoryRegistry: _registry);
        var port = await _host.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", _prevVault);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ===== Control API info handoff (CC_DIRECTOR_API / CC_DIRECTOR_ID source) =====

    [Fact]
    public void Host_start_publishes_control_api_info_to_session_manager()
    {
        Assert.Equal($"http://127.0.0.1:{_host.Port}", _sm.ControlApiBaseUrl);
        Assert.Equal(_host.DirectorId, _sm.DirectorId);
    }

    // ===== POST /handovers =====

    [Fact]
    public async Task Post_handovers_creates_listed_readable_document()
    {
        var resp = await _client.PostAsJsonAsync("handovers", new HandoverCreateRequest
        {
            Title = "API Created Handover",
            Content = "## What happened\n\nEverything worked.",
            RepoPaths = new List<string> { _repoB },
            SessionName = "Self Service Test",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<HandoverDto>();
        Assert.NotNull(created);
        Assert.Equal("Api created handover", created.Title); // slug round-trip: first letter capitalized
        Assert.Equal("Self Service Test", created.SessionName);
        Assert.Equal(_repoB, created.RepoPath);

        var content = await _client.GetFromJsonAsync<HandoverContentDto>(
            $"handovers/content?path={Uri.EscapeDataString(created.Path)}");
        Assert.NotNull(content);
        Assert.Contains("Everything worked.", content.Content);

        var all = await _client.GetFromJsonAsync<List<HandoverDto>>("handovers");
        Assert.NotNull(all);
        Assert.Contains(all, h => h.Path == created.Path);
    }

    [Theory]
    [InlineData("", "some content")]
    [InlineData("some title", "")]
    public async Task Post_handovers_requires_title_and_content(string title, string content)
    {
        var resp = await _client.PostAsJsonAsync("handovers",
            new HandoverCreateRequest { Title = title, Content = content });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== DELETE /handovers =====

    [Fact]
    public async Task Delete_handovers_removes_document()
    {
        var create = await _client.PostAsJsonAsync("handovers", new HandoverCreateRequest
        {
            Title = "Doomed Handover",
            Content = "Delete me.",
        });
        var created = await create.Content.ReadFromJsonAsync<HandoverDto>();
        Assert.NotNull(created);

        var del = await _client.DeleteAsync($"handovers?path={Uri.EscapeDataString(created.Path)}");
        del.EnsureSuccessStatusCode();
        Assert.False(File.Exists(created.Path));

        var again = await _client.DeleteAsync($"handovers?path={Uri.EscapeDataString(created.Path)}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Delete_handovers_rejects_path_outside_folder()
    {
        var resp = await _client.DeleteAsync($"handovers?path={Uri.EscapeDataString(_repoA)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_handovers_requires_path()
    {
        var resp = await _client.DeleteAsync("handovers");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== GET /handovers?repo= =====

    [Fact]
    public async Task Handovers_repo_filter_matches_frontmatter_repositories()
    {
        var forA = await _client.GetFromJsonAsync<List<HandoverDto>>(
            $"handovers?repo={Uri.EscapeDataString(_repoA)}");
        Assert.NotNull(forA);
        Assert.Contains(forA, h => h.Title == "Seeded handover");

        // Trailing slash + different casing must still match.
        var forASlash = await _client.GetFromJsonAsync<List<HandoverDto>>(
            $"handovers?repo={Uri.EscapeDataString(_repoA.ToUpperInvariant() + "\\")}");
        Assert.NotNull(forASlash);
        Assert.Contains(forASlash, h => h.Title == "Seeded handover");

        var forB = await _client.GetFromJsonAsync<List<HandoverDto>>(
            $"handovers?repo={Uri.EscapeDataString(_repoB)}");
        Assert.NotNull(forB);
        Assert.DoesNotContain(forB, h => h.Title == "Seeded handover");
    }

    // ===== POST /repos =====

    [Fact]
    public async Task Post_repos_registers_new_repo_201_then_200_on_duplicate()
    {
        var resp = await _client.PostAsJsonAsync("repos", new RepoAddRequest { Path = _repoB });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var repos = await _client.GetFromJsonAsync<List<RepositoryDto>>("repos");
        Assert.NotNull(repos);
        Assert.Contains(repos, r => r.Name == "repoB");

        var dup = await _client.PostAsJsonAsync("repos", new RepoAddRequest { Path = _repoB });
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
    }

    [Fact]
    public async Task Post_repos_with_custom_name_applies_name()
    {
        var resp = await _client.PostAsJsonAsync("repos",
            new RepoAddRequest { Path = _repoB, Name = "Nice Display Name" });
        resp.EnsureSuccessStatusCode();

        var repos = await _client.GetFromJsonAsync<List<RepositoryDto>>("repos");
        Assert.NotNull(repos);
        Assert.Contains(repos, r => r.Name == "Nice Display Name" && r.Path.EndsWith("repoB"));
    }

    [Fact]
    public async Task Post_repos_rejects_missing_directory()
    {
        var resp = await _client.PostAsJsonAsync("repos",
            new RepoAddRequest { Path = Path.Combine(_root, "no-such-dir") });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_repos_requires_path()
    {
        var resp = await _client.PostAsJsonAsync("repos", new RepoAddRequest { Path = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== PATCH /repos =====

    [Fact]
    public async Task Patch_repos_renames_registered_repo()
    {
        var resp = await _client.PatchAsJsonAsync("repos",
            new RepoRenameRequest { Path = _repoA, Name = "Renamed Via API" });
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<RepositoryDto>();
        Assert.NotNull(dto);
        Assert.Equal("Renamed Via API", dto.Name);

        var repos = await _client.GetFromJsonAsync<List<RepositoryDto>>("repos");
        Assert.NotNull(repos);
        Assert.Contains(repos, r => r.Name == "Renamed Via API");
    }

    [Fact]
    public async Task Patch_repos_unknown_path_returns_404()
    {
        var resp = await _client.PatchAsJsonAsync("repos",
            new RepoRenameRequest { Path = Path.Combine(_root, "not-registered"), Name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_repos_requires_name()
    {
        var resp = await _client.PatchAsJsonAsync("repos",
            new RepoRenameRequest { Path = _repoA, Name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== GET /repos/overview =====

    [Fact]
    public async Task Repos_overview_aggregates_handovers_and_history()
    {
        var overview = await _client.GetFromJsonAsync<List<RepoOverviewDto>>("repos/overview");
        Assert.NotNull(overview);

        var a = Assert.Single(overview, r => r.Path == Path.GetFullPath(_repoA).TrimEnd('\\'));
        Assert.True(a.PathExists);
        Assert.Equal(1, a.HandoverCount);
        Assert.NotNull(a.LastHandoverUtc);
        Assert.Equal(1, a.HistorySessionCount);
        Assert.Equal("selfsvc seeded prompt", a.LastSessionSummary);
        Assert.NotNull(a.LastSessionAtUtc);
        Assert.Equal(0, a.LiveSessionCount);
        Assert.Empty(a.LiveSessionNames);
    }

    // ===== GET /claude-sessions?repo= =====

    [Fact]
    public async Task Claude_sessions_repo_filter_includes_only_matching_repo()
    {
        var forA = await _client.GetFromJsonAsync<List<ClaudeSessionDto>>(
            $"claude-sessions?repo={Uri.EscapeDataString(_repoA)}");
        Assert.NotNull(forA);
        var entry = Assert.Single(forA);
        Assert.Equal("selfsvc-test-claude-session", entry.ClaudeSessionId);
        Assert.Equal("selfsvc seeded prompt", entry.Summary);

        var forB = await _client.GetFromJsonAsync<List<ClaudeSessionDto>>(
            $"claude-sessions?repo={Uri.EscapeDataString(_repoB)}");
        Assert.NotNull(forB);
        Assert.Empty(forB);
    }
}
