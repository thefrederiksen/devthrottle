using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission (the cut): the Cockpit-parity Director HTTP routes (GET/DELETE /repos,
/// GET /coaching/categories, GET /claude-sessions, GET /handovers (+content), GET /fs/list, and the
/// ResumeSessionId passthrough on POST /sessions) are DELETED. Each now rides the tunnel as a verb
/// whose shared core lives in <see cref="CatalogReadExecutor"/> / <see cref="SessionWriteExecutor"/> /
/// <see cref="SessionCommandExecutor"/>. The Gateway forwards these director-level verbs over the
/// tunnel (proven end to end in TunnelDirectorReadProofTests); this file pins the DIRECTOR-SIDE
/// catalog/read/parse LOGIC - repo listing, coaching cards, claude-session shape, handover
/// frontmatter parsing + traversal-safe content read, drive-root / subdirectory listing, and the
/// ResumeSessionId-carrying create validation - by driving those cores directly against real
/// on-disk fixtures.
///
/// CC_DIRECTOR_ROOT and CC_VAULT_PATH are redirected to an isolated temp dir so nothing touches (or
/// pollutes) the user's real files or vault. In the "DirectorRoot" collection (serializes
/// root-touching tests).
/// </summary>
[Collection("DirectorRoot")]
public sealed class CockpitParityEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string DirectorId = "dir-parity-test";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _prevVault;
    private string _tempRepos = null!;
    private string _repoA = null!;
    private string _repoB = null!;
    private SessionManager _sm = null!;
    private RepositoryRegistry _registry = null!;

    public CockpitParityEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _prevVault = Environment.GetEnvironmentVariable("CC_VAULT_PATH");
        _root = Path.Combine(Path.GetTempPath(), "ccd-parity-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        // CC_VAULT_PATH is set machine-wide to the real vault; redirect it too so handover
        // scans hit our isolated temp folder and never touch (or pollute) the user's vault.
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", Path.Combine(_root, "vault"));

        // Two real repo folders, repoA with two sub-dirs (for the fs-list test).
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
    }

    public void Dispose()
    {
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable("CC_VAULT_PATH", _prevVault);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- helpers: drive the shared command cores the tunnel verbs dispatch to ----

    private static T Ok<T>(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        var body = JsonSerializer.Deserialize<T>(result.BodyJson!, Web);
        Assert.NotNull(body);
        return body!;
    }

    private static string Payload(object dto) => JsonSerializer.Serialize(dto, Web);

    // ===== /repos =====

    [Fact]
    public void Repos_lists_seeded_repositories_with_lastused()
    {
        var repos = Ok<List<RepositoryDto>>(CatalogReadExecutor.ReposList(_registry));
        Assert.Equal(2, repos.Count);
        Assert.Contains(repos, r => r.Name == "repoA");
        Assert.Contains(repos, r => r.Name == "repoB");
    }

    [Fact]
    public void Delete_repo_requires_path()
    {
        var result = SessionWriteExecutor.RepoDelete(
            new DirectorCommand { Verb = "repo-delete", PayloadJson = "" }, _registry);
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public void Delete_repo_removes_from_registry()
    {
        var result = SessionWriteExecutor.RepoDelete(
            new DirectorCommand { Verb = "repo-delete", PayloadJson = Payload(new RepoDeleteRequest { Path = _repoB }) },
            _registry);
        var response = Ok<RepoDeleteResponse>(result);
        Assert.True(response.Removed);

        var repos = Ok<List<RepositoryDto>>(CatalogReadExecutor.ReposList(_registry));
        Assert.Single(repos);
        Assert.DoesNotContain(repos, r => r.Name == "repoB");
    }

    // ===== /coaching/categories =====

    [Fact]
    public void Coaching_categories_returns_assistant_and_coach_with_paths()
    {
        var cats = Ok<List<CoachingCategoryDto>>(CatalogReadExecutor.CoachingCategories());
        Assert.Equal(2, cats.Count);

        var assistant = cats.Single(c => c.Key == "assistant");
        Assert.Equal("Assistant", assistant.Label);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Path));

        var coach = cats.Single(c => c.Key == "coach");
        Assert.Equal("Coach", coach.Label);
        Assert.False(string.IsNullOrWhiteSpace(coach.Path));
    }

    // ===== /claude-sessions =====

    [Fact]
    public void Claude_sessions_returns_a_list()
    {
        // Reads the real ~/.claude/projects (may be empty); we assert shape + Ok, not contents.
        var sessions = Ok<List<ClaudeSessionDto>>(
            CatalogReadExecutor.ClaudeSessions(new DirectorCommand { Verb = "claude-sessions", PayloadJson = "" }));
        Assert.NotNull(sessions);
    }

    // ===== /handovers =====

    [Fact]
    public void Handovers_lists_seeded_handover_with_parsed_frontmatter()
    {
        var handovers = Ok<List<HandoverDto>>(
            CatalogReadExecutor.HandoversList(new DirectorCommand { Verb = "handovers-list", PayloadJson = "" }));
        var h = Assert.Single(handovers);
        Assert.Equal("Test handover", h.Title);
        Assert.Equal("2026-06-01 09:00", h.DateDisplay);
        Assert.Equal("Test Session", h.SessionName);
        Assert.Equal(_repoA, h.RepoPath);
    }

    [Fact]
    public void Handover_content_returns_full_text()
    {
        var handovers = Ok<List<HandoverDto>>(
            CatalogReadExecutor.HandoversList(new DirectorCommand { Verb = "handovers-list", PayloadJson = "" }));
        var path = handovers.Single().Path;

        var dto = Ok<HandoverContentDto>(CatalogReadExecutor.HandoversContent(
            new DirectorCommand { Verb = "handovers-content", PayloadJson = Payload(new HandoverContentRequest { Path = path }) }));
        Assert.Contains("Test handover body", dto.Content);
    }

    [Fact]
    public void Handover_content_requires_path()
    {
        var result = CatalogReadExecutor.HandoversContent(
            new DirectorCommand { Verb = "handovers-content", PayloadJson = "" });
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    [Fact]
    public void Handover_content_rejects_path_outside_folder()
    {
        var result = CatalogReadExecutor.HandoversContent(
            new DirectorCommand { Verb = "handovers-content", PayloadJson = Payload(new HandoverContentRequest { Path = _repoA }) });
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    // ===== /fs/list =====
    // Tenant-boundary hardening (release 2026-07-31, finding CR-4): the listing is CONTAINED to the working
    // directories of the sessions this Director hosts. It used to list drive roots and any directory on the
    // machine, which on a hosted Gateway handed any active device key a full remote directory browse.

    /// <summary>A live session rooted at <see cref="_repoA"/>, making repoA an allowed fs-list root.</summary>
    private void SeatSessionInRepoA() => _sm.CreateEmbeddedSession(_repoA, null, new ExecuteActionTestBackend());

    [Fact]
    public void Fs_list_without_path_returns_the_session_roots_not_the_drives()
    {
        SeatSessionInRepoA();
        var listing = Ok<DirectoryListingDto>(
            CatalogReadExecutor.FsList(_sm, new DirectorCommand { Verb = "fs-list", PayloadJson = "" }));
        Assert.Null(listing.CurrentPath);
        var entry = Assert.Single(listing.Entries);
        Assert.Equal(Path.GetFullPath(_repoA).TrimEnd('\\', '/'), entry.Path);
        Assert.False(entry.IsDrive);
    }

    [Fact]
    public void Fs_list_without_path_and_without_sessions_lists_nothing()
    {
        var listing = Ok<DirectoryListingDto>(
            CatalogReadExecutor.FsList(_sm, new DirectorCommand { Verb = "fs-list", PayloadJson = "" }));
        Assert.Null(listing.CurrentPath);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void Fs_list_with_a_session_root_returns_subdirectories_and_no_parent_above_the_root()
    {
        SeatSessionInRepoA();
        var listing = Ok<DirectoryListingDto>(CatalogReadExecutor.FsList(_sm,
            new DirectorCommand { Verb = "fs-list", PayloadJson = Payload(new FsListRequest { Path = _repoA }) }));
        Assert.Equal(Path.GetFullPath(_repoA), listing.CurrentPath);
        Assert.Null(listing.ParentPath); // the root's own parent is not browsable
        Assert.Equal(2, listing.Entries.Count);
        Assert.Contains(listing.Entries, e => e.Name == "subdir1");
        Assert.Contains(listing.Entries, e => e.Name == "subdir2");
    }

    [Fact]
    public void Fs_list_inside_a_session_root_keeps_the_parent_within_the_root()
    {
        SeatSessionInRepoA();
        var sub = Path.Combine(_repoA, "subdir1");
        var listing = Ok<DirectoryListingDto>(CatalogReadExecutor.FsList(_sm,
            new DirectorCommand { Verb = "fs-list", PayloadJson = Payload(new FsListRequest { Path = sub }) }));
        Assert.Equal(Path.GetFullPath(sub), listing.CurrentPath);
        Assert.Equal(Path.GetFullPath(_repoA), listing.ParentPath);
    }

    [Fact]
    public void Fs_list_outside_every_session_root_is_refused()
    {
        // repoB exists on disk but no session is rooted there - the reported CR-4 symptom was that any
        // real directory on the machine could be listed. It must now be refused, not listed.
        SeatSessionInRepoA();
        var result = CatalogReadExecutor.FsList(_sm, new DirectorCommand
        {
            Verb = "fs-list",
            PayloadJson = Payload(new FsListRequest { Path = _repoB }),
        });
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("outside", result.Error);
    }

    [Fact]
    public void Fs_list_with_nonexistent_path_inside_the_root_returns_400()
    {
        SeatSessionInRepoA();
        var result = CatalogReadExecutor.FsList(_sm, new DirectorCommand
        {
            Verb = "fs-list",
            PayloadJson = Payload(new FsListRequest { Path = Path.Combine(_repoA, "does-not-exist") }),
        });
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }

    // ===== ResumeSessionId passthrough =====

    [Fact]
    public async Task Create_session_accepts_resume_session_id_field()
    {
        // A bogus repo path must be a BadRequest (proves the body, incl. ResumeSessionId, parsed
        // cleanly and the create core reached validation rather than throwing on the new field).
        var req = new NewSessionRequest
        {
            RepoPath = Path.Combine(_root, "no-such-repo"),
            Agent = "ClaudeCode",
            ResumeSessionId = "abc-123-resume",
        };
        var result = await SessionCommandExecutor.CreateAsync(_sm, DirectorId,
            new DirectorCommand { Verb = "create", PayloadJson = Payload(req) });
        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
    }
}
