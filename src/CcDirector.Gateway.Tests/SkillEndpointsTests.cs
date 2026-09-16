using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the central skill library (devthrottle_internal issue 995). Boots a real
/// GatewayHost on an ephemeral port with an isolated CC_DIRECTOR_ROOT and drives the routes over real
/// HTTP, so this proves the endpoints are actually MAPPED on the host - not merely that the store
/// works.
///
/// The claim this feature makes to a machine is: "you install nothing; ask the Gateway". These tests
/// are that claim, executed - the register comes back over the wire, the body comes back over the
/// wire, and the body is not in the register.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SkillEndpointsTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-skills-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public SkillEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-skills-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task The_register_serves_the_shipped_skills()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/skills");

        var ids = body!["skills"]!.AsArray().Select(s => (string?)s!["id"]).ToArray();
        Assert.Equal(new[] { "dev-throttle", "fleet-comms", "move-session", "terminology", "fleet-manager" }, ids);
    }

    [Fact]
    public async Task Every_listed_skill_carries_what_an_agent_chooses_from_and_nothing_more()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/skills");

        foreach (var skill in body!["skills"]!.AsArray())
        {
            var row = skill!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace((string?)row["summary"]));
            Assert.NotEmpty(row["triggers"]!.AsArray());
            // The listing is what EVERY session's briefing is rendered from: no body may ride it.
            Assert.False(row.ContainsKey("bodyMarkdown"));
            Assert.False(row.ContainsKey("files"));
        }
    }

    [Fact]
    public async Task A_body_is_fetched_per_skill_as_raw_markdown()
    {
        var response = await _http.GetAsync("gateway/skills/move-session/body");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# Move Session", body);
    }

    [Fact]
    public async Task An_unknown_skill_is_a_clean_not_found()
    {
        var response = await _http.GetAsync("gateway/skills/no-such-skill/body");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_built_in_cannot_be_edited_over_the_wire_and_says_to_clone_instead()
    {
        var response = await _http.PutAsJsonAsync("gateway/skills/move-session/draft", new
        {
            name = "Mine",
            summary = "Rewritten.",
            bodyMarkdown = "# mine",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["error"];
        Assert.Contains("clone", error!);
    }

    [Fact]
    public async Task A_skill_is_authored_published_and_immediately_served()
    {
        var created = await _http.PostAsJsonAsync("gateway/skills", new
        {
            id = "deploy-gateway",
            name = "Deploy the gateway",
            summary = "Release the hosted Gateway and confirm it comes back healthy.",
            triggers = new[] { "deploy the gateway", "release the gateway" },
            bodyMarkdown = "# Deploy the gateway\n\nStart the deploy, watch it, verify health.",
            authoredBy = "test",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // A draft is invisible until published - nothing an agent reads has changed yet.
        var beforeIds = (await _http.GetFromJsonAsync<JsonObject>("gateway/skills"))!["skills"]!
            .AsArray().Select(s => (string?)s!["id"]).ToArray();
        Assert.DoesNotContain("deploy-gateway", beforeIds);

        var published = await _http.PostAsync("gateway/skills/deploy-gateway/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        var afterIds = (await _http.GetFromJsonAsync<JsonObject>("gateway/skills"))!["skills"]!
            .AsArray().Select(s => (string?)s!["id"]).ToArray();
        Assert.Contains("deploy-gateway", afterIds);
        Assert.Contains("Start the deploy",
            await _http.GetStringAsync("gateway/skills/deploy-gateway/body"));
    }

    [Fact]
    public async Task Switching_a_skill_off_refuses_its_fetch_with_a_reason_not_a_not_found()
    {
        var off = await _http.PostAsync("gateway/skills/move-session/disable?by=test", null);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var fetch = await _http.GetAsync("gateway/skills/move-session/body");

        // 400 with a message, never 404: the skill exists, the owner switched it off, and an agent
        // reading "not found" would go looking for a skill that is sitting right there.
        Assert.Equal(HttpStatusCode.BadRequest, fetch.StatusCode);
        var error = (string?)(await fetch.Content.ReadFromJsonAsync<JsonObject>())!["error"];
        Assert.Contains("turned OFF", error!);

        // Still listed, marked off, so the register can switch it back on.
        var row = (await _http.GetFromJsonAsync<JsonObject>("gateway/skills"))!["skills"]!.AsArray()
            .Single(s => (string?)s!["id"] == "move-session")!.AsObject();
        Assert.False((bool)row["enabled"]!);
    }

    [Fact]
    public async Task A_switched_off_skill_refuses_the_PINNED_fetch_the_command_line_actually_makes()
    {
        // The command line resolves the head version and asks for it BY NUMBER, so a switch that
        // only guarded the unpinned route would be bypassed by every real agent fetch. This drives
        // the pinned route over the wire, which is the shape that regressed once already.
        var version = (int)(await _http.GetFromJsonAsync<JsonObject>("gateway/skills/move-session"))!
            ["version"]!;
        await _http.PostAsync("gateway/skills/move-session/disable?by=test", null);

        var pinned = await _http.GetAsync($"gateway/skills/move-session/body?version={version}");

        Assert.Equal(HttpStatusCode.BadRequest, pinned.StatusCode);
        var error = (string?)(await pinned.Content.ReadFromJsonAsync<JsonObject>())!["error"];
        Assert.Contains("turned OFF", error!);
    }

    [Fact]
    public async Task Switching_a_skill_without_naming_who_is_refused()
    {
        var response = await _http.PostAsync("gateway/skills/move-session/disable", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cloning_a_built_in_creates_an_editable_copy()
    {
        var response = await _http.PostAsync(
            "gateway/skills/move-session/clone?newId=move-session-copy&by=test", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var clone = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True((bool)clone["editable"]!);
        Assert.False((bool)clone["isBuiltIn"]!);
        Assert.Equal(await _http.GetStringAsync("gateway/skills/move-session/body"),
            await _http.GetStringAsync("gateway/skills/move-session-copy/body"));
    }

    [Fact]
    public async Task Version_history_is_served_and_a_superseded_version_stays_readable()
    {
        await _http.PostAsJsonAsync("gateway/skills", new
        {
            id = "versioned",
            name = "Versioned",
            summary = "First.",
            bodyMarkdown = "# v1",
            authoredBy = "test",
        });
        await _http.PostAsync("gateway/skills/versioned/publish", null);
        await _http.PutAsJsonAsync("gateway/skills/versioned/draft", new
        {
            name = "Versioned",
            summary = "Second.",
            bodyMarkdown = "# v2",
            authoredBy = "test",
        });
        await _http.PostAsync("gateway/skills/versioned/publish", null);

        var versions = (await _http.GetFromJsonAsync<JsonObject>("gateway/skills/versioned/versions"))!
            ["versions"]!.AsArray();
        Assert.Equal(2, versions.Count);
        Assert.Equal("# v2", await _http.GetStringAsync("gateway/skills/versioned/body"));
        Assert.Equal("# v1", await _http.GetStringAsync("gateway/skills/versioned/body?version=1"));
    }

    [Fact]
    public async Task Supporting_files_are_fetched_one_at_a_time_beside_the_body()
    {
        await _http.PostAsJsonAsync("gateway/skills", new
        {
            id = "with-files",
            name = "With files",
            summary = "Carries a helper.",
            bodyMarkdown = "# With files\n\nRun helper.py.",
            files = new[] { new { fileName = "helper.py", content = "print('hello')" } },
            authoredBy = "test",
        });
        await _http.PostAsync("gateway/skills/with-files/publish", null);

        var listed = (await _http.GetFromJsonAsync<JsonObject>("gateway/skills"))!["skills"]!.AsArray()
            .Single(s => (string?)s!["id"] == "with-files")!.AsObject();
        // The register says a file EXISTS; it never carries its content.
        Assert.Equal(1, (int)listed["fileCount"]!);

        Assert.Equal("print('hello')",
            await _http.GetStringAsync("gateway/skills/with-files/files/helper.py"));
    }

    [Fact]
    public async Task A_file_in_a_subdirectory_is_served_over_the_wire()
    {
        // A skill is a DIRECTORY, so the file being asked for is "references/tracing.md" - a path, not
        // a bare name. A single-segment route parameter would simply not match it, and because this
        // Gateway answers an unknown PAGE path with the single-page app's HTML shell rather than a
        // 404, a route that failed to match would come back as a cheerful 200 full of markup. So the
        // CONTENT TYPE is asserted, not just the status: that is the only thing that tells the two
        // apart, and it is exactly how this feature shipped a bug once already.
        await _http.PostAsJsonAsync("gateway/skills", new
        {
            id = "nested-files",
            name = "Nested files",
            summary = "Carries a reference document in a subdirectory.",
            bodyMarkdown = "# Nested\n\nSee references/tracing.md.",
            files = new[] { new { fileName = "references/tracing.md", content = "# Tracing" } },
            authoredBy = "test",
        });
        await _http.PostAsync("gateway/skills/nested-files/publish", null);

        var response = await _http.GetAsync("gateway/skills/nested-files/files/references/tracing.md");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("# Tracing", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_binary_file_is_served_as_bytes_not_as_text()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE };
        await _http.PostAsJsonAsync("gateway/skills", new
        {
            id = "binary-files",
            name = "Binary files",
            summary = "Carries an image.",
            bodyMarkdown = "# Binary\n\nUse assets/logo.png.",
            files = new[]
            {
                new
                {
                    fileName = "assets/logo.png",
                    content = Convert.ToBase64String(bytes),
                    encoding = "base64",
                },
            },
            authoredBy = "test",
        });
        await _http.PostAsync("gateway/skills/binary-files/publish", null);

        var response = await _http.GetAsync("gateway/skills/binary-files/files/assets/logo.png");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }
}
