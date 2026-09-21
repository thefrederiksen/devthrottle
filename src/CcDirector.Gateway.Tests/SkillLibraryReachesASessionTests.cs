using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Sessions;
using CcDirector.Gateway;
using CcDirector.Gateway.Skills;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE PROOF THE WHOLE FEATURE RESTS ON (devthrottle_internal issue 995): a machine with NO skills
/// installed learns what skills exist from the text its sessions are launched with, and can fetch one
/// in full. Every other test in this feature proves a part; this one proves the chain.
///
/// It runs the real chain end to end - a real Gateway serving the real register, the real Director-side
/// index store fetching it over real HTTP, and the real preamble builder rendering it - and asserts the
/// two halves that must both hold:
///
///   1. The briefing NAMES the skills and says how to fetch one. Without this, nothing tells an agent
///      the library exists, and the feature silently delivers nothing.
///   2. The briefing does NOT contain any skill's body. This is what makes the design cheaper than the
///      per-machine file copies it replaces: discovery costs the lines, use costs the body.
///
/// What this CANNOT prove, and no test can: that an agent, having read the line, acts on it. That is a
/// prompt-level obligation. The register's fetch counts are how we find out in the real world.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SkillLibraryReachesASessionTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-skills-reach-" + Guid.NewGuid().ToString("N"));
    private readonly string _cachePath =
        Path.Combine(Path.GetTempPath(), "cc-skills-reach-cache-" + Guid.NewGuid().ToString("N"), "index.json");

    private GatewayHost _gateway = null!;

    public SkillLibraryReachesASessionTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-skills-reach-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345",
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        foreach (var dir in new[] { _instancesDir, _root, Path.GetDirectoryName(_cachePath)! })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    private SkillIndexStore StoreAgainstTheGateway() => new(
        _cachePath,
        new HttpClient(),
        gatewayUrl: $"http://127.0.0.1:{_gateway.Port}",
        token: "test-token-12345");

    [Fact]
    public async Task A_session_briefing_names_every_skill_and_how_to_fetch_one()
    {
        var store = StoreAgainstTheGateway();
        await store.RefreshAsync();

        var preamble = FleetPreamble.Build(
            Guid.NewGuid().ToString(), "a session", "TEST-MACHINE", @"C:\repo",
            user: null, workflowIndex: null, skillIndex: store);

        // Named, with the fetch command, exactly as the register serves them.
        Assert.Contains("[Skills]", preamble);
        Assert.Contains("cc-devthrottle skill get <id>", preamble);
        Assert.Contains("- dev-throttle:", preamble);
        Assert.Contains("- fleet-comms:", preamble);
        Assert.Contains("- move-session:", preamble);
    }

    [Fact]
    public async Task The_briefing_carries_no_skill_body()
    {
        var store = StoreAgainstTheGateway();
        await store.RefreshAsync();

        var preamble = FleetPreamble.Build(
            Guid.NewGuid().ToString(), "a session", "TEST-MACHINE", @"C:\repo",
            user: null, workflowIndex: null, skillIndex: store);

        // A phrase from deep inside the move-session body. If it ever appears in a briefing, the
        // bodies are riding every session on every machine and the feature has inverted its own
        // purpose - it would be strictly worse than the file copies it replaced.
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
        var body = await http.GetStringAsync("gateway/skills/move-session/body");

        Assert.True(body.Length > 2000, "the move-session body should be substantial - if it is not, " +
                                        "this test is no longer measuring anything.");
        Assert.DoesNotContain("# Move Session", preamble);

        // And the block costs ONE LINE PER SKILL, not a page. The bound is drawn from the shipped
        // list rather than a fixed ceiling: it used to read "at most 10" and failed when the product
        // shipped its eleventh skill, which is the list growing as intended and not a briefing that
        // has bloated. What would be a real failure is a skill costing more than its line, so the
        // count is held to the number of skills exactly, and each line to a summary's length.
        var skillLines = preamble.Split('\n').Where(l => l.TrimStart().StartsWith("- ")).ToArray();
        Assert.Equal(BuiltInSkills.All().Count, skillLines.Length);
        Assert.All(skillLines, line => Assert.True(line.Length <= 200,
            $"a skill index entry ran to {line.Length} characters - a body may be riding the briefing: {line}"));
    }

    [Fact]
    public async Task A_skill_switched_off_disappears_from_the_next_briefing()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
        await http.PostAsync("gateway/skills/move-session/disable?by=test", null);

        var store = StoreAgainstTheGateway();
        await store.RefreshAsync();

        var preamble = FleetPreamble.Build(
            Guid.NewGuid().ToString(), "a session", "TEST-MACHINE", @"C:\repo",
            user: null, workflowIndex: null, skillIndex: store);

        // The owner's switch reaches every machine's next briefing with nothing to deploy.
        Assert.DoesNotContain("- move-session:", preamble);
        Assert.Contains("- fleet-comms:", preamble);
    }

    [Fact]
    public async Task A_newly_published_skill_is_in_the_next_briefing_with_nothing_installed()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-12345");
        await http.PostAsJsonAsync("gateway/skills", new
        {
            id = "brand-new",
            name = "Brand new",
            summary = "Written a moment ago, on the Gateway, by an agent.",
            bodyMarkdown = "# Brand new\n\nThe instructions.",
            authoredBy = "test",
        });
        await http.PostAsync("gateway/skills/brand-new/publish", null);

        var store = StoreAgainstTheGateway();
        await store.RefreshAsync();

        var preamble = FleetPreamble.Build(
            Guid.NewGuid().ToString(), "a session", "TEST-MACHINE", @"C:\repo",
            user: null, workflowIndex: null, skillIndex: store);

        // No release, no re-install, no file written to this machine - and the next session knows.
        Assert.Contains("- brand-new: Written a moment ago, on the Gateway, by an agent.", preamble);
        Assert.Contains("# Brand new", await http.GetStringAsync("gateway/skills/brand-new/body"));
    }
}
