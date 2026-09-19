using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The owner's switch (register redesign, owner ruling 2026-07-18): any workflow - built-ins
/// included - can be turned off, because DevThrottle configures to what the USER wants. The
/// semantics under test are the ruling verbatim: off hides the workflow from agents' briefings,
/// refuses the default conduct read with a clear message, refuses new runs and seats - and deletes
/// NOTHING: the catalog still lists it (so the register can show and flip it), pinned
/// explicit-version reads keep resolving, past runs stay, and the flip is instant both ways.
/// A mission whose workflow is off still gets created; it runs ungoverned until the flip back.
/// </summary>
public sealed class WorkflowEnableSwitchTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private (WorkflowStore Workflows, WorkflowRunStore Runs) NewStores()
    {
        var db = _h.Open();
        return (new WorkflowStore(db), new WorkflowRunStore(db));
    }

    [Fact]
    public void Off_hides_from_nothing_in_the_catalog_but_marks_the_state()
    {
        var (workflows, _) = NewStores();

        Assert.True(workflows.SetEnabled("mission", false, "test:owner"));

        // The catalog still lists it - the register must be able to SHOW an off workflow to flip
        // it back - and the state is reported honestly.
        var listed = workflows.ListPublished();
        var mission = listed.Single(w => w.Id == "mission");
        Assert.False(mission.Enabled);
        Assert.True(listed.Single(w => w.Id == "standalone").Enabled);
        Assert.False(workflows.GetPublished("mission")!.Enabled);
    }

    [Fact]
    public void Off_refuses_the_default_conduct_read_with_a_clear_message_but_pinned_reads_resolve()
    {
        var (workflows, _) = NewStores();
        workflows.SetEnabled("mission", false, "test:owner");

        var refusal = Assert.Throws<WorkflowValidationException>(
            () => workflows.GetInstructions("mission", version: null));
        Assert.Contains("turned OFF", refusal.Message);
        Assert.Contains("workflow enable mission", refusal.Message);

        // Pinned history is untouchable: a seated run's conduct never disappears under it. The
        // property is "this read resolved to the shipped mission conduct", so it is asserted
        // against the shipped source itself - never a phrase copied out of it, which rots the
        // moment the conduct is reworded.
        Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), workflows.GetInstructions("mission", version: 1));
    }

    [Fact]
    public void Off_refuses_new_runs_and_the_flip_back_restores_everything()
    {
        var (workflows, runs) = NewStores();
        var before = runs.Create("mission", "Governed while on");
        workflows.SetEnabled("mission", false, "test:owner");

        Assert.Throws<WorkflowValidationException>(() => runs.Create("mission", "Refused while off"));
        Assert.False(runs.IsWorkflowEnabled("mission"));
        // Past runs stay readable, and they report the workflow's current state.
        Assert.False(runs.Get(before.Id)!.WorkflowEnabled);

        Assert.True(workflows.SetEnabled("mission", true, "test:owner"));
        Assert.True(runs.IsWorkflowEnabled("mission"));
        Assert.Equal("mission", runs.Create("mission", "Governed again").WorkflowId);
        Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), workflows.GetInstructions("mission", version: null));
    }

    [Fact]
    public void The_briefing_index_drops_off_workflows_and_counts_honestly()
    {
        // The Director-side index builder consumes the catalog wire shape; enabled=false is the
        // owner's own switch and removes the workflow from every session's briefing. Absent
        // (an older Gateway) means enabled.
        var text = Core.Sessions.WorkflowIndexStore.BuildIndexText(new[]
        {
            new Core.Sessions.WorkflowIndexStore.CatalogWorkflow("mission", "The conduct.", Enabled: true),
            new Core.Sessions.WorkflowIndexStore.CatalogWorkflow("standalone", "Off now.", Enabled: false),
            new Core.Sessions.WorkflowIndexStore.CatalogWorkflow("legacy", "Old gateway.", Enabled: null),
        });

        Assert.Contains("  - mission:", text);
        Assert.Contains("  - legacy:", text);
        Assert.DoesNotContain("standalone", text);

        Assert.Equal("", Core.Sessions.WorkflowIndexStore.BuildIndexText(new[]
        {
            new Core.Sessions.WorkflowIndexStore.CatalogWorkflow("only", "Everything off.", Enabled: false),
        }));
    }
}

/// <summary>
/// The switch over real HTTP, including the ungoverned-mission ruling: turning the mission workflow
/// off must never break mission creation - the mission is simply created without a governance run,
/// and turning the switch back on governs the next one.
/// </summary>
[Collection("DirectorRoot")]
public sealed class WorkflowEnableSwitchEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-wf-switch-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public WorkflowEnableSwitchEndpointTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-wf-switch-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token-12345");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Disable_off_missions_run_ungoverned_then_enable_governs_again()
    {
        var off = await _http.PostAsync("gateway/workflows/mission/disable?by=test-owner", null);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        // The default conduct read refuses with the clear message, as a 400 - never a 404.
        var read = await _http.GetAsync("gateway/workflows/mission/instructions");
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.Contains("turned OFF", await read.Content.ReadAsStringAsync());

        // The owner ruling: mission creation still works - ungoverned, with no run id.
        var ungoverned = await _http.PostAsJsonAsync("missions", new { missionName = "While off" });
        Assert.Equal(HttpStatusCode.Created, ungoverned.StatusCode);
        var ungovernedDto = await ungoverned.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Null(ungovernedDto!["workflowRunId"]?.GetValue<string?>());

        var on = await _http.PostAsync("gateway/workflows/mission/enable?by=test-owner", null);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        var governed = await _http.PostAsJsonAsync("missions", new { missionName = "After the flip" });
        Assert.Equal(HttpStatusCode.Created, governed.StatusCode);
        var governedDto = await governed.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(string.IsNullOrWhiteSpace((string?)governedDto!["workflowRunId"]));
    }

    [Fact]
    public async Task The_switch_requires_an_actor()
    {
        // A governance change has an actor, always - flipping the fleet's rules anonymously is
        // refused, the same posture as run acceptance.
        var resp = await _http.PostAsync("gateway/workflows/mission/disable", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("requires 'by'", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_workflow_switch_is_a_404()
    {
        var resp = await _http.PostAsync("gateway/workflows/no-such/disable?by=test-owner", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

}
