using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using CcDirector.Cockpit.Components.Pages;
using CcDirector.Cockpit.Services;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Issue #239: the fleet-level Wingman Pipeline page renders the GET /wingman/queue snapshot into
/// its four sections (in flight, queue, recent, brain health). These bUnit tests render the REAL
/// compiled WingmanQueue component, backed by a REAL GatewayClient whose HttpClient is stubbed to
/// return a known snapshot, and assert the rendered markup shows the real pipeline contents (not
/// placeholder text) - including the brain model and the consecutive-rejection counter. Also emits
/// a standalone HTML proof artifact (real markup + real app.css) when CC239_PROOF_DIR is set, the
/// same screenshot-proof pattern as the #219 rail tests.
/// </summary>
public sealed class WingmanQueuePageTests : TestContext
{
    private const string SidA = "aaaa1111-2222-3333-4444-555566667777";
    private const string SidB = "bbbb1111-2222-3333-4444-555566667777";
    private const string SidC = "cccc1111-2222-3333-4444-555566667777";

    // A stub handler that answers GET /wingman/queue with a fixed snapshot JSON.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly WingmanQueueDto _snapshot;
        public StubHandler(WingmanQueueDto snapshot) => _snapshot = snapshot;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(_snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static WingmanQueueDto PopulatedSnapshot() => new()
    {
        InFlight = new WingmanInFlightItem { SessionId = SidA, Kind = "brief", ElapsedSeconds = 12 },
        Queue = new List<WingmanQueueItem>
        {
            new() { SessionId = SidB, Kind = "explain" },
            new() { SessionId = SidC, Kind = "brief" },
        },
        Recent = new List<WingmanRecentBrief>
        {
            new() { SessionId = SidA, TurnNumber = 7, GeneratedAtUtc = DateTime.UtcNow.AddSeconds(-30), Degraded = false, Model = "gateway-brain/opus" },
            new() { SessionId = SidB, TurnNumber = 3, GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-2), Degraded = true, Model = "stub" },
        },
        Brain = new WingmanBrainHealth
        {
            Pid = 4242, Model = "opus", Alive = true, Status = "Running",
            ConsecutiveRejections = 2, RejectionThreshold = 3, RecoveryInFlight = false,
        },
    };

    private GatewayClient ClientFor(WingmanQueueDto snapshot)
    {
        var http = new HttpClient(new StubHandler(snapshot)) { BaseAddress = new Uri("http://gw.test/") };
        return new GatewayClient(http, NullLogger<GatewayClient>.Instance);
    }

    private IRenderedComponent<WingmanQueue> Render(WingmanQueueDto snapshot)
    {
        Services.AddSingleton(ClientFor(snapshot));
        return RenderComponent<WingmanQueue>();
    }

    [Fact]
    public void Renders_inflight_session_with_kind_and_elapsed()
    {
        var cut = Render(PopulatedSnapshot());

        var inflight = cut.Find(".wq-inflight").TextContent;
        Assert.Contains("BRIEF", inflight);
        Assert.Contains(SidA[..8], inflight);
        Assert.Contains("reading", inflight);
    }

    [Fact]
    public void Renders_ordered_queue_explain_first()
    {
        var cut = Render(PopulatedSnapshot());

        var items = cut.FindAll(".wq-queue li");
        Assert.Equal(2, items.Count);
        // The explain (user-initiated) is first in the queue order.
        Assert.Contains("EXPLAIN", items[0].TextContent);
        Assert.Contains(SidB[..8], items[0].TextContent);
        Assert.Contains("BRIEF", items[1].TextContent);
        Assert.Contains(SidC[..8], items[1].TextContent);
    }

    [Fact]
    public void Renders_recent_briefs_with_degraded_flag_and_model()
    {
        var cut = Render(PopulatedSnapshot());

        var rows = cut.FindAll(".wq-recent tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("gateway-brain/opus", rows[0].TextContent);
        Assert.Contains("ok", rows[0].TextContent);
        Assert.Contains("degraded", rows[1].TextContent);
    }

    [Fact]
    public void Brain_health_block_shows_model_and_consecutive_rejections()
    {
        // The poisoned-brain visibility acceptance criterion: model + the rejection counter render.
        var cut = Render(PopulatedSnapshot());

        var brain = cut.Find(".wq-brain").TextContent;
        Assert.Contains("opus", brain);          // model
        Assert.Contains("2 / 3", brain);          // consecutive rejections / threshold
        Assert.Contains("alive", brain);
    }

    [Fact]
    public void Idle_snapshot_shows_idle_text_not_placeholder_rows()
    {
        var cut = Render(new WingmanQueueDto
        {
            InFlight = null,
            Queue = new List<WingmanQueueItem>(),
            Recent = new List<WingmanRecentBrief>(),
            Brain = new WingmanBrainHealth { Status = "NotStarted", Model = "opus", RejectionThreshold = 3 },
        });

        Assert.Contains("Idle", cut.Find(".wq-inflight, .wq-idle").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll(".wq-queue li"));
        Assert.Empty(cut.FindAll(".wq-recent tbody tr"));
    }

    /// <summary>
    /// Emits a standalone HTML proof artifact (the real rendered page wrapped in the real app.css)
    /// when CC239_PROOF_DIR is set. Not an assertion test - it lets the Developer Agent screenshot
    /// the genuine compiled markup for the issue's visual proof. Skipped in the normal suite.
    /// </summary>
    [Fact]
    public void EmitProofArtifact_WhenProofDirSet()
    {
        var proofDir = Environment.GetEnvironmentVariable("CC239_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(proofDir)) return; // no-op in the normal suite

        var cut = Render(PopulatedSnapshot());
        var pageHtml = cut.Markup;

        var here = AppContext.BaseDirectory;
        var cssPath = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..",
            "CcDirector.Cockpit", "wwwroot", "app.css"));
        var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";

        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>\n" +
            "body{background:#1E1E1E;margin:0;padding:16px;font-family:'Segoe UI',sans-serif;color:#CCCCCC}\n" +
            css +
            "\n</style></head><body>" +
            pageHtml +
            "</body></html>";

        Directory.CreateDirectory(proofDir);
        File.WriteAllText(Path.Combine(proofDir, "wingman-page-rendered.html"), html);
    }
}
