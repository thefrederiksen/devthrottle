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
/// End-to-end smoke tests for the Cockpit-parity Director endpoints: DELETE /repos,
/// GET /coaching/categories, GET /claude-sessions, GET /handovers (+content), GET /fs/list,
/// and ResumeSessionId passthrough on POST /sessions. Runs a real ControlApiHost on an
/// ephemeral port with CC_DIRECTOR_ROOT redirected to a temp dir so nothing touches the
/// user's real files. In the "DirectorRoot" collection (serializes root-touching tests).
/// </summary>
[Collection("DirectorRoot")]
public sealed class CockpitParityEndpointsTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevVault;
    private string _tempRepos = null!;
    private string _repoA = null!;
    private string _repoB = null!;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private RepositoryRegistry _registry = null!;
    private HttpClient _client = null!;

    public CockpitParityEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _prevVault = Environment.GetEnvironmentVariable("CC_VAULT_PATH");
        _root = Path.Combine(Path.GetTempPath(), "ccd-parity-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // CC_VAULT_PATH is set machine-wide to the real vault; redirect it too so handover
        // scans hit our isolated temp folder and never touch (or pollute) the user's vault.
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", Path.Combine(_root, "vault"));
    }

    public async Task InitializeAsync()
    {
        // Two real repo folders, repoA with two sub-dirs (for the fs/list test).
        _tempRepos = Path.Combine(_root, "repos");
        _repoA = Path.Combine(_tempRepos, "repoA");
        _repoB = Path.Combine(_tempRepos, "repoB");
        Directory.CreateDirectory(Path.Combine(_repoA, "subdir1"));
        Directory.CreateDirectory(Path.Combine(_repoA, "subdir2"));
        Directory.CreateDirectory(_repoB);

        _registry = new RepositoryRegistry();
        _registry.Load();
        _registry.TryAdd(_repoA);
        _registry.TryAdd(_repoB);

        // Seed one handover document referencing repoA.
        var handoverDir = CcStorage.VaultHandovers();
        Directory.CreateDirectory(handoverDir);
        File.WriteAllText(Path.Combine(handoverDir, "20260601_0900_test-handover.md"),
            "---\n" +
            "session_name: Test Session\n" +
            "repositories:\n" +
            $"  - path: {_repoA}\n" +
            "---\n\n" +
            "# Test handover body\n");

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

    // ===== /repos =====

    [Fact]
    public async Task Repos_lists_seeded_repositories_with_lastused()
    {
        var repos = await _client.GetFromJsonAsync<List<RepositoryDto>>("repos");
        Assert.NotNull(repos);
        Assert.Equal(2, repos!.Count);
        Assert.Contains(repos, r => r.Name == "repoA");
        Assert.Contains(repos, r => r.Name == "repoB");
    }

    [Fact]
    public async Task Delete_repo_requires_path()
    {
        var resp = await _client.DeleteAsync("repos");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_repo_removes_from_registry()
    {
        var resp = await _client.DeleteAsync($"repos?path={Uri.EscapeDataString(_repoB)}");
        resp.EnsureSuccessStatusCode();

        var repos = await _client.GetFromJsonAsync<List<RepositoryDto>>("repos");
        Assert.NotNull(repos);
        Assert.Single(repos!);
        Assert.DoesNotContain(repos!, r => r.Name == "repoB");
    }

    // ===== /coaching/categories =====

    [Fact]
    public async Task Coaching_categories_returns_assistant_and_coach_with_paths()
    {
        var cats = await _client.GetFromJsonAsync<List<CoachingCategoryDto>>("coaching/categories");
        Assert.NotNull(cats);
        Assert.Equal(2, cats!.Count);

        var assistant = cats.Single(c => c.Key == "assistant");
        Assert.Equal("Assistant", assistant.Label);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Path));

        var coach = cats.Single(c => c.Key == "coach");
        Assert.Equal("Coach", coach.Label);
        Assert.False(string.IsNullOrWhiteSpace(coach.Path));
    }

    // ===== /claude-sessions =====

    [Fact]
    public async Task Claude_sessions_returns_a_list()
    {
        // Reads the real ~/.claude/projects (may be empty); we assert shape + 200, not contents.
        var resp = await _client.GetAsync("claude-sessions");
        resp.EnsureSuccessStatusCode();
        var sessions = await resp.Content.ReadFromJsonAsync<List<ClaudeSessionDto>>();
        Assert.NotNull(sessions);
    }

    // ===== /handovers =====

    [Fact]
    public async Task Handovers_lists_seeded_handover_with_parsed_frontmatter()
    {
        var handovers = await _client.GetFromJsonAsync<List<HandoverDto>>("handovers");
        Assert.NotNull(handovers);
        var h = Assert.Single(handovers!);
        Assert.Equal("Test handover", h.Title);
        Assert.Equal("2026-06-01 09:00", h.DateDisplay);
        Assert.Equal("Test Session", h.SessionName);
        Assert.Equal(_repoA, h.RepoPath);
    }

    [Fact]
    public async Task Handover_content_returns_full_text()
    {
        var handovers = await _client.GetFromJsonAsync<List<HandoverDto>>("handovers");
        var path = handovers!.Single().Path;

        var dto = await _client.GetFromJsonAsync<HandoverContentDto>(
            $"handovers/content?path={Uri.EscapeDataString(path)}");
        Assert.NotNull(dto);
        Assert.Contains("Test handover body", dto!.Content);
    }

    [Fact]
    public async Task Handover_content_requires_path()
    {
        var resp = await _client.GetAsync("handovers/content");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Handover_content_rejects_path_outside_folder()
    {
        var resp = await _client.GetAsync($"handovers/content?path={Uri.EscapeDataString(_repoA)}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== /fs/list =====

    [Fact]
    public async Task Fs_list_without_path_returns_drive_roots()
    {
        var listing = await _client.GetFromJsonAsync<DirectoryListingDto>("fs/list");
        Assert.NotNull(listing);
        Assert.Null(listing!.CurrentPath);
        Assert.NotEmpty(listing.Entries);
        Assert.All(listing.Entries, e => Assert.True(e.IsDrive));
    }

    [Fact]
    public async Task Fs_list_with_path_returns_subdirectories_and_parent()
    {
        var listing = await _client.GetFromJsonAsync<DirectoryListingDto>(
            $"fs/list?path={Uri.EscapeDataString(_repoA)}");
        Assert.NotNull(listing);
        Assert.Equal(Path.GetFullPath(_repoA), listing!.CurrentPath);
        Assert.Equal(Path.GetFullPath(_tempRepos), listing.ParentPath);
        Assert.Equal(2, listing.Entries.Count);
        Assert.Contains(listing.Entries, e => e.Name == "subdir1");
        Assert.Contains(listing.Entries, e => e.Name == "subdir2");
    }

    [Fact]
    public async Task Fs_list_with_nonexistent_path_returns_400()
    {
        var resp = await _client.GetAsync(
            $"fs/list?path={Uri.EscapeDataString(Path.Combine(_root, "does-not-exist"))}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== ResumeSessionId passthrough =====

    [Fact]
    public async Task Create_session_accepts_resume_session_id_field()
    {
        // A bogus repo path must 400 (proves the body, incl. ResumeSessionId, parsed cleanly
        // and the endpoint reached validation rather than throwing on the new field).
        var req = new NewSessionRequest
        {
            RepoPath = Path.Combine(_root, "no-such-repo"),
            Agent = "ClaudeCode",
            ResumeSessionId = "abc-123-resume",
        };
        var resp = await _client.PostAsJsonAsync("sessions", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
