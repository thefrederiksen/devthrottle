using System.Net;
using System.Text.Json;
using Bunit;
using CcDirector.Cockpit.Components.Pages;
using CcDirector.Cockpit.Services;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// bUnit tests for the Director picker on the Schedule page (issue #495): the create modal shows a
/// card picker (not a &lt;select&gt;) keyed on machine name + :port, disambiguates same-machine
/// Directors by port, previews each Director's running sessions, and persists the SELECTED Director's
/// real id in the created job. Backed by a real GatewayClient with a stubbed HttpClient - no Gateway.
/// </summary>
public sealed class SchedulePickerTests : TestContext
{
    private const string North7882 = "north-7882-id";
    private const string North7885 = "north-7885-id";

    private sealed class PickerHandler : HttpMessageHandler
    {
        public string? LastPostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/cron/jobs")
            {
                LastPostBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
                return Ok(new CronJobDto { Id = "cj_new", Name = "n" });
            }

            object body = path switch
            {
                "/cron/jobs" => new { jobs = Array.Empty<CronJobDto>() },
                "/directors" => (object)Directors(),
                "/sessions" => new
                {
                    sessions = new[]
                    {
                        new SessionDto { SessionId = "s1", DirectorId = North7882, Name = "implement #482 - cron store", ActivityState = "Working", SortOrder = 0 },
                        new SessionDto { SessionId = "s2", DirectorId = North7882, Name = "qa #488 - schedule page", ActivityState = "WaitingForInput", NeedsYouSince = DateTime.UtcNow, SortOrder = 1 },
                    },
                    machineErrors = Array.Empty<MachineErrorDto>(),
                },
                _ when path.EndsWith("/runs", StringComparison.Ordinal) => new { jobId = "x", runs = Array.Empty<CronRunRecord>() },
                _ => new { },
            };
            return Ok(body);
        }

        private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private static List<DirectorDto> Directors() => new()
    {
        new() { DirectorId = North7882, MachineName = "SOREN_NORTH", ControlEndpoint = "http://127.0.0.1:7882", Version = "0.9.10", StartedAt = DateTime.UtcNow.AddHours(-3) },
        new() { DirectorId = North7885, MachineName = "SOREN_NORTH", ControlEndpoint = "http://127.0.0.1:7885", Version = "0.9.10", StartedAt = DateTime.UtcNow.AddMinutes(-20) },
        new() { DirectorId = "soren-7879-id", MachineName = "SOREN", ControlEndpoint = "http://127.0.0.1:7879", Version = "0.9.10", StartedAt = DateTime.UtcNow.AddDays(-1) },
    };

    private (IRenderedComponent<Schedule> cut, PickerHandler handler) RenderAndOpenCreate()
    {
        var handler = new PickerHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        Services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        Services.AddSingleton(new GatewayClient(http, NullLogger<GatewayClient>.Instance));
        var cut = RenderComponent<Schedule>();
        cut.Find("button.btn.primary").Click(); // New cron job
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".dpick .dcard")));
        return (cut, handler);
    }

    /// <summary>Emits the real compiled picker (modal open, multi-Director) for a visual screenshot when CC495_PROOF_DIR is set. No-op otherwise.</summary>
    [Fact]
    public void EmitProofArtifact_WhenProofDirSet()
    {
        var proofDir = Environment.GetEnvironmentVariable("CC495_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(proofDir)) return;

        var (cut, _) = RenderAndOpenCreate();
        var pageHtml = cut.Markup;

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CcDirector.Cockpit"));
        var css = (File.Exists(Path.Combine(root, "wwwroot", "app.css")) ? File.ReadAllText(Path.Combine(root, "wwwroot", "app.css")) : "")
            + "\n" + (File.Exists(Path.Combine(root, "Components", "Pages", "Schedule.razor.css")) ? File.ReadAllText(Path.Combine(root, "Components", "Pages", "Schedule.razor.css")) : "");
        var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>\n" +
            "body{background:#1E1E1E;margin:0;padding:16px;font-family:'Segoe UI',sans-serif;color:#CCCCCC}\n" +
            css + "\n</style></head><body>" + pageHtml + "</body></html>";
        Directory.CreateDirectory(proofDir);
        File.WriteAllText(Path.Combine(proofDir, "picker-rendered.html"), html);
    }

    [Fact]
    public void Picker_is_cards_not_a_select_and_shows_ports()
    {
        var (cut, _) = RenderAndOpenCreate();

        // AC1: it is a card picker (one .dcard per Director), not a <select> of Directors.
        Assert.NotNull(cut.Find(".dpick"));
        Assert.Equal(3, cut.FindAll(".dcard").Count);
        var ports = cut.FindAll(".dcard .dport").Select(e => e.TextContent.Trim()).ToList();
        // AC2: same-machine Directors are told apart by port, not GUID.
        Assert.Contains(":7882", ports);
        Assert.Contains(":7885", ports);
        Assert.Contains(":7879", ports);
    }

    [Fact]
    public void Picker_previews_running_sessions_and_needs_you()
    {
        var (cut, _) = RenderAndOpenCreate();

        // AC3: the :7882 card previews its sessions and flags NEEDS YOU.
        var card = cut.FindAll(".dcard").First(c => c.QuerySelector(".dport")!.TextContent.Trim() == ":7882");
        Assert.Contains("implement #482", card.TextContent);
        Assert.Contains("qa #488", card.TextContent);
        Assert.NotNull(card.QuerySelector(".dneeds"));        // NEEDS YOU flag present
        // a same-machine idle Director shows the empty preview
        var idle = cut.FindAll(".dcard").First(c => c.QuerySelector(".dport")!.TextContent.Trim() == ":7885");
        Assert.Contains("0 sessions", idle.TextContent);
    }

    [Fact]
    public void Selecting_a_card_persists_that_directors_real_id_in_the_created_job()
    {
        var (cut, handler) = RenderAndOpenCreate();

        // Select the :7882 card.
        cut.FindAll("button.dcard").First(c => c.QuerySelector(".dport")!.TextContent.Trim() == ":7882").Click();
        cut.WaitForAssertion(() =>
        {
            var sel = cut.FindAll(".dcard.sel");
            Assert.Single(sel);
            Assert.Equal(":7882", sel[0].QuerySelector(".dport")!.TextContent.Trim());
        });

        // Fill the minimum and create.
        cut.FindAll(".sched-modal .fld input")[0].Input("Tonight drain");
        cut.Find(".modal-foot .btn.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(handler.LastPostBody);
            var sent = JsonSerializer.Deserialize<CronJobDto>(handler.LastPostBody!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(sent);
            // AC4: the persisted target is the SELECTED Director's real id (not its machine name or port).
            Assert.Equal(North7882, sent.Target.DirectorId);
        });
    }
}
