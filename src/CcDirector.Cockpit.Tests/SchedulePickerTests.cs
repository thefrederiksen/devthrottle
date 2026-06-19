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
/// bUnit tests for the machine picker on the Schedule page (issue #503, superseding the #495 Director
/// picker): the create modal shows a card picker (not a &lt;select&gt;) keyed on MACHINE name - one
/// card per machine, collapsing the machine's Directors - previews each machine's running sessions,
/// flags NEEDS YOU, and persists the SELECTED machine name in the created job. A machine with no
/// Director running still appears (one is launched on demand). Backed by a real GatewayClient with a
/// stubbed HttpClient - no Gateway.
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
                        new SessionDto { SessionId = "s1", DirectorId = North7882, MachineName = "SOREN_NORTH", Name = "implement #482 - cron store", ActivityState = "Working", SortOrder = 0 },
                        new SessionDto { SessionId = "s2", DirectorId = North7882, MachineName = "SOREN_NORTH", Name = "qa #488 - schedule page", ActivityState = "WaitingForInput", NeedsYouSince = DateTime.UtcNow, SortOrder = 1 },
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

    // Two machines: SOREN_NORTH (two Directors) and SOREN (one Director). The picker collapses each
    // machine to a single card regardless of how many Directors it has.
    private static List<DirectorDto> Directors() => new()
    {
        new() { DirectorId = North7882, MachineName = "SOREN_NORTH", ControlEndpoint = "http://127.0.0.1:7882", Version = "0.9.10", StartedAt = DateTime.UtcNow.AddHours(-3) },
        new() { DirectorId = North7885, MachineName = "SOREN_NORTH", ControlEndpoint = "http://127.0.0.1:7885", Version = "0.9.10", StartedAt = DateTime.UtcNow.AddMinutes(-20) },
        new() { DirectorId = "soren-7879-id", MachineName = "SOREN", ControlEndpoint = "http://127.0.0.1:7879", Version = "0.9.10", StartedAt = DateTime.UtcNow.AddDays(-1) },
    };

    // Opens the New cron job modal (compact machine field, no inline list).
    private (IRenderedComponent<Schedule> cut, PickerHandler handler) RenderAndOpenCreate()
    {
        var handler = new PickerHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        Services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        Services.AddSingleton(new GatewayClient(http, NullLogger<GatewayClient>.Instance));
        var cut = RenderComponent<Schedule>();
        cut.Find("button.btn.primary").Click(); // New cron job
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".dpick-field")));
        return (cut, handler);
    }

    // Opens the separate machine-picker dialog from the compact field's Choose button.
    private void OpenPicker(IRenderedComponent<Schedule> cut)
    {
        cut.Find(".dpick-choose").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".dpick .dcard")));
    }

    /// <summary>Emits the real compiled picker (modal open, multi-machine) for a visual screenshot when CC503_PROOF_DIR is set. No-op otherwise.</summary>
    [Fact]
    public void EmitProofArtifact_WhenProofDirSet()
    {
        var proofDir = Environment.GetEnvironmentVariable("CC503_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(proofDir)) return;

        var (cut, _) = RenderAndOpenCreate();
        OpenPicker(cut);
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
    public void Create_modal_machine_field_is_compact_with_no_inline_list()
    {
        // QA #495: the form modal must NOT cram the rich picker inline.
        var (cut, _) = RenderAndOpenCreate();

        Assert.NotNull(cut.Find(".dpick-field"));                 // compact chosen-value + Choose button
        Assert.NotNull(cut.Find(".dpick-choose"));
        Assert.Empty(cut.FindAll(".dpick"));                      // no inline card list in the form
        Assert.Empty(cut.FindAll(".dcard"));
        Assert.Contains("No machine selected", cut.Find(".dpick-field").TextContent);
    }

    [Fact]
    public void Picker_is_cards_not_a_select_and_shows_one_card_per_machine()
    {
        var (cut, _) = RenderAndOpenCreate();
        OpenPicker(cut);

        // AC1: the picker dialog is a card list (one .dcard per MACHINE), not a <select>.
        Assert.NotNull(cut.Find(".dpicker-modal .dpick"));
        // AC2: the two SOREN_NORTH Directors collapse to ONE machine card -> 2 machines, 2 cards.
        Assert.Equal(2, cut.FindAll(".dcard").Count);
        var names = cut.FindAll(".dcard .dname").Select(e => e.TextContent.Trim()).ToList();
        Assert.Contains("SOREN_NORTH", names);
        Assert.Contains("SOREN", names);
    }

    [Fact]
    public void Picker_previews_running_sessions_and_needs_you()
    {
        var (cut, _) = RenderAndOpenCreate();
        OpenPicker(cut);

        // AC3: the SOREN_NORTH card previews its sessions and flags NEEDS YOU.
        var card = cut.FindAll(".dcard").First(c => c.QuerySelector(".dname")!.TextContent.Trim() == "SOREN_NORTH");
        Assert.Contains("implement #482", card.TextContent);
        Assert.Contains("qa #488", card.TextContent);
        Assert.NotNull(card.QuerySelector(".dneeds"));        // NEEDS YOU flag present
        Assert.Contains("2 directors running", card.TextContent);
        // SOREN has a Director but no sessions -> the idle preview.
        var idle = cut.FindAll(".dcard").First(c => c.QuerySelector(".dname")!.TextContent.Trim() == "SOREN");
        Assert.Contains("0 sessions", idle.TextContent);
    }

    [Fact]
    public void Selecting_a_card_persists_that_machine_name_in_the_created_job()
    {
        var (cut, handler) = RenderAndOpenCreate();
        OpenPicker(cut);

        // Pick the SOREN_NORTH card -> the dialog closes and the compact field shows the chosen machine.
        cut.FindAll("button.dcard").First(c => c.QuerySelector(".dname")!.TextContent.Trim() == "SOREN_NORTH").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".dpicker-modal"));          // picker dialog closed
            Assert.Contains("SOREN_NORTH", cut.Find(".dpick-chosen").TextContent);
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
            // AC4: the persisted target is the SELECTED machine name (not a Director id or port).
            Assert.Equal("SOREN_NORTH", sent.Target.Machine);
        });
    }
}
