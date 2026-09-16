using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// End-to-end proof for the workflow catalog (issue #1617). Boots a real GatewayHost on an ephemeral
/// port with an isolated CC_DIRECTOR_ROOT and reads the routes over real HTTP, so this proves the
/// endpoint is actually mapped on the host - not merely that BuiltInWorkflows returns a list.
///
/// The point of the feature is that the Gateway is the HOME for workflows, so what is worth asserting
/// is that a client asking the Gateway gets the four built-ins back, each with the seats filled in.
/// </summary>
[Collection("DirectorRoot")]
public sealed class WorkflowEndpointsTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-workflows-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public WorkflowEndpointsTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-workflows-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token-12345", authEnabled: true,
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
    public async Task Get_workflows_serves_the_four_built_ins()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows");

        var workflows = body!["workflows"]!.AsArray();
        var ids = workflows.Select(w => (string?)w!["id"]).ToArray();
        Assert.Equal(new[] { "mission", "standalone", "standalone-with-review", "fleet-manager" }, ids);
    }

    [Fact]
    public async Task Every_workflow_states_its_seats_and_what_done_means()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows");

        foreach (var workflow in body!["workflows"]!.AsArray())
        {
            var name = (string?)workflow!["name"];
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace((string?)workflow["summary"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)workflow["whenToUse"]));
            Assert.False(string.IsNullOrWhiteSpace((string?)workflow["humanCheckpoint"]));

            var steps = workflow["steps"]!.AsArray();
            Assert.NotEmpty(steps);
            foreach (var step in steps)
            {
                // A step without a doer or without a definition of done is not a workflow step, it is a
                // wish. These are the two fields the whole feature exists to make explicit.
                Assert.False(string.IsNullOrWhiteSpace((string?)step!["doer"]), $"{name}: step has no doer");
                Assert.False(string.IsNullOrWhiteSpace((string?)step["done"]), $"{name}: step has no done");
            }
        }
    }

    [Fact]
    public async Task Standalone_with_review_puts_the_review_in_a_separate_seat()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows/standalone-with-review");

        var steps = body!["steps"]!.AsArray();
        Assert.Equal(2, steps.Count);
        // The whole point of this workflow versus plain Standalone: the reviewing seat is not the seat
        // that did the work. If these ever collapse to one doer, the workflow is a lie.
        Assert.Equal("Worker", (string?)steps[0]!["doer"]);
        Assert.Equal("Reviewer", (string?)steps[1]!["doer"]);
    }

    [Fact]
    public async Task Standalone_has_no_review_seat()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows/standalone");

        var steps = body!["steps"]!.AsArray();
        var step = Assert.Single(steps);
        Assert.Null((string?)step!["reviewer"]);
    }

    [Fact]
    public async Task Get_workflow_by_id_is_case_insensitive()
    {
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows/MISSION");
        Assert.Equal("Mission", (string?)body!["name"]);
    }

    [Fact]
    public async Task The_persisted_catalog_adds_its_fields_without_touching_the_legacy_shape()
    {
        // Workflows mission, phase 1: the catalog is served from the persisted store. The legacy
        // fields are frozen (the tests above pin them); the store may only ADD fields. These are the
        // additions the Cockpit and CLI will read.
        var body = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows/mission");

        Assert.Equal(1, (int?)body!["version"]);
        Assert.True((bool?)body["isBuiltIn"]);
        Assert.False((bool?)body["hasDraft"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)body["contentHash"]));
        Assert.NotNull(body["updatedUtc"]);
    }

    [Fact]
    public async Task Get_unknown_workflow_reports_not_found()
    {
        // No fallback to "the first workflow" or an empty object: an unknown id is an explicit 404.
        var resp = await _http.GetAsync("gateway/workflows/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Authoring_round_trip_create_draft_publish_read_archive()
    {
        // Workflows mission, phase 2: the full agent authoring loop over real HTTP.
        var create = await _http.PostAsJsonAsync("gateway/workflows", new
        {
            id = "smoke-flow",
            name = "Smoke flow",
            summary = "End-to-end authoring smoke.",
            authoredBy = "endpoint-test",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // A draft is invisible to the catalog and publish refuses a skeletal draft.
        var listed = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows");
        Assert.DoesNotContain(listed!["workflows"]!.AsArray(), w => (string?)w!["id"] == "smoke-flow");
        var earlyPublish = await _http.PostAsync("gateway/workflows/smoke-flow/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, earlyPublish.StatusCode);

        // Fill the draft (full replacement), then publish.
        var put = await _http.PutAsJsonAsync("gateway/workflows/smoke-flow/draft", new
        {
            name = "Smoke flow",
            summary = "End-to-end authoring smoke.",
            steps = new[] { new { name = "Do", description = "d", doer = "Worker", done = "merged" } },
            instructionsMarkdown = "# Smoke flow\n\nDo the thing.",
            authoredBy = "endpoint-test",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var publish = await _http.PostAsync("gateway/workflows/smoke-flow/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        // The agent read path serves the raw markdown.
        var instructions = await _http.GetStringAsync("gateway/workflows/smoke-flow/instructions");
        Assert.Equal("# Smoke flow\n\nDo the thing.", instructions);

        // A stale If-Match is refused with a conflict.
        var stale = new HttpRequestMessage(HttpMethod.Put, "gateway/workflows/smoke-flow/draft")
        {
            Content = JsonContent.Create(new { name = "Smoke flow", summary = "edit" }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"stale-hash\"");
        var conflict = await _http.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // A duplicate id is a conflict; archiving removes it from the catalog; built-ins refuse delete.
        var duplicate = await _http.PostAsJsonAsync("gateway/workflows", new
        {
            id = "smoke-flow", name = "n", summary = "s",
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var delete = await _http.DeleteAsync("gateway/workflows/smoke-flow");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var deleteBuiltIn = await _http.DeleteAsync("gateway/workflows/mission");
        Assert.Equal(HttpStatusCode.BadRequest, deleteBuiltIn.StatusCode);
    }

    [Fact]
    public async Task Creating_a_mission_opens_a_workflow_run_pinned_to_the_mission_conduct()
    {
        // Workflows mission, phase 4 (issue #1771): a mission IS a run of the built-in "mission"
        // workflow. The mission response carries the additive workflowRunId; the run is pinned to
        // the published mission version and anchored back to the mission.
        var created = await _http.PostAsJsonAsync("missions", new { missionName = "Spine proof" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var mission = await created.Content.ReadFromJsonAsync<JsonObject>();
        var runId = (string?)mission!["workflowRunId"];
        Assert.False(string.IsNullOrWhiteSpace(runId));

        var run = await _http.GetFromJsonAsync<JsonObject>($"gateway/workflow-runs/{runId}");
        Assert.Equal("mission", (string?)run!["workflowId"]);
        Assert.Equal("Spine proof", (string?)run["name"]);
        Assert.Equal("created", (string?)run["status"]);
        Assert.Equal("pending", (string?)run["acceptanceStatus"]);
        Assert.Equal((string?)mission["missionId"], (string?)run["missionId"]);

        // The pinned hash is the published mission workflow's hash.
        var workflow = await _http.GetFromJsonAsync<JsonObject>("gateway/workflows/mission");
        Assert.Equal((string?)workflow!["contentHash"], (string?)run["contentHash"]);

        // The run also lists by its mission anchor.
        var listed = await _http.GetFromJsonAsync<JsonObject>(
            $"gateway/workflow-runs?missionId={(string?)mission["missionId"]}");
        Assert.Single(listed!["runs"]!.AsArray());
    }

    [Fact]
    public async Task The_api_does_not_squat_on_the_cockpit_page_path()
    {
        // Regression: the catalog was first mapped at a bare /workflows, which is the path the Cockpit's
        // Workflows PAGE owns. The Gateway serves the single-page app at "/" and falls unknown page paths
        // back to index.html, so an API there WINS the path and a hard navigation to /workflows renders
        // raw JSON instead of the page. Caught by opening the page in a browser, not by a green test.
        var resp = await _http.GetAsync("workflows");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("\"whenToUse\"", body);
        Assert.DoesNotContain("\"humanCheckpoint\"", body);
    }

}
