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
/// bUnit tests for the Schedule page (issue #488): render the REAL compiled <see cref="Schedule"/>
/// component, backed by a REAL <see cref="GatewayClient"/> whose HttpClient is stubbed to serve the
/// cron + directors endpoints, and assert the rendered markup shows the jobs table, the run history,
/// the empty state, and the create modal. Also emits a standalone HTML proof artifact (real markup +
/// real app.css) when CC488_PROOF_DIR is set - the same screenshot-proof pattern as the #239 page
/// tests, with no live Gateway and no risk to the fleet.
/// </summary>
public sealed class SchedulePageTests : TestContext
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<CronJobDto> _jobs;
        private readonly IReadOnlyList<CronRunRecord> _runs;
        private readonly IReadOnlyList<DirectorDto> _directors;
        public StubHandler(IReadOnlyList<CronJobDto> jobs, IReadOnlyList<CronRunRecord> runs, IReadOnlyList<DirectorDto> directors)
        { _jobs = jobs; _runs = runs; _directors = directors; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = path switch
            {
                "/cron/jobs" => new { jobs = _jobs },
                "/directors" => (object)_directors,
                _ when path.StartsWith("/cron/jobs/", StringComparison.Ordinal) && path.EndsWith("/runs", StringComparison.Ordinal)
                    => new { jobId = "cj_1", runs = _runs },
                _ => new { },
            };
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static CronJobDto Job(string id, string name, string? workList, string? cron, string? runAt) => new()
    {
        Id = id, Name = name, Enabled = true,
        ScheduleKind = cron is not null ? "recurring" : "oneOff",
        CronExpression = cron, RunAt = runAt, TimeZoneId = "America/Chicago",
        NextRunUtc = new DateTime(2026, 6, 18, 5, 0, 0, DateTimeKind.Utc),
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", WorkListName = workList, Seed = workList is null ? "/help" : "" },
    };

    private static List<CronJobDto> SampleJobs() => new()
    {
        Job("cj_1", "Tonight - drain work list", "Tonight", null, "2026-06-18T00:00:00"),
        Job("cj_2", "Nightly backlog sweep", "Backlog", "0 0 * * *", null),
    };

    private static List<CronRunRecord> SampleRuns() => new()
    {
        new() { ScheduledUtc = new DateTime(2026,6,17,5,0,0,DateTimeKind.Utc), FiredUtc = new DateTime(2026,6,17,5,0,3,DateTimeKind.Utc), TargetDirectorId = "workstation-A", SessionId = "8c1e2f", InfraStatus = "worklist-started", TaskStatus = "unknown" },
    };

    private static List<DirectorDto> SampleDirectors() => new()
    {
        new() { DirectorId = "workstation-A", MachineName = "workstation-A", ControlEndpoint = "http://127.0.0.1:7879" },
    };

    private GatewayClient ClientFor(List<CronJobDto> jobs, List<CronRunRecord> runs, List<DirectorDto> directors)
    {
        var http = new HttpClient(new StubHandler(jobs, runs, directors)) { BaseAddress = new Uri("http://gw.test/") };
        return new GatewayClient(http, NullLogger<GatewayClient>.Instance);
    }

    private IRenderedComponent<Schedule> Render(List<CronJobDto> jobs, List<CronRunRecord>? runs = null, List<DirectorDto>? directors = null)
    {
        Services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        Services.AddSingleton(ClientFor(jobs, runs ?? SampleRuns(), directors ?? SampleDirectors()));
        return RenderComponent<Schedule>();
    }

    [Fact]
    public void Renders_jobs_table_with_name_target_and_schedule()
    {
        var cut = Render(SampleJobs());

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("table.sched-tbl tbody tr");
            Assert.Equal(2, rows.Count);
            Assert.Contains("Tonight - drain work list", rows[0].TextContent);
            Assert.Contains("workstation-A", rows[0].TextContent);
            Assert.Contains("work list Tonight", rows[0].TextContent);
            Assert.Contains("0 0 * * *", rows[1].TextContent); // recurring schedule shown
        });
    }

    [Fact]
    public void Empty_state_when_no_jobs()
    {
        var cut = Render(new List<CronJobDto>());
        cut.WaitForAssertion(() => Assert.Contains("No cron jobs yet", cut.Markup));
    }

    [Fact]
    public void New_button_opens_create_modal_with_director_option()
    {
        var cut = Render(SampleJobs());

        cut.Find("button.btn.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("New cron job", cut.Find(".modal-head").TextContent);
            // The Director is chosen via a separate picker dialog (#495), so the create modal shows a
            // compact field (Choose button), not the Director list inline.
            Assert.NotNull(cut.Find(".dpick-field"));
            Assert.NotNull(cut.Find(".dpick-choose"));
        });
    }

    [Fact]
    public void Selecting_a_job_shows_its_run_history()
    {
        var cut = Render(SampleJobs());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("table.sched-tbl tbody tr")));
        cut.FindAll("table.sched-tbl tbody tr")[0].Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Run history", cut.Markup);
            Assert.Contains("worklist-started", cut.Markup);
        });
    }

    /// <summary>
    /// Regression for the QA #488 defect: editing a DISABLED job (changing only its name) must NOT
    /// re-enable it - the PUT body must carry Enabled=false (and the preserved PreventOverlap=false).
    /// Drives the real component through the edit modal and captures the PUT request body.
    /// </summary>
    [Fact]
    public void Editing_a_disabled_job_preserves_enabled_false_and_preventoverlap_in_the_put_body()
    {
        var disabled = Job("cj_1", "Tonight - drain work list", "Tonight", null, "2026-06-18T00:00:00");
        disabled.Enabled = false;
        disabled.PreventOverlap = false;

        var handler = new CaptureHandler(new List<CronJobDto> { disabled }, SampleDirectors());
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        Services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        Services.AddSingleton(new GatewayClient(http, NullLogger<GatewayClient>.Instance));
        var cut = RenderComponent<Schedule>();

        // Open the edit modal for the disabled job.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.linkbtn")));
        cut.FindAll("button.linkbtn").First(b => b.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.Contains("Edit cron job", cut.Find(".modal-head").TextContent));

        // Change only the name, then Save.
        cut.FindAll(".sched-modal .fld input")[0].Input("Renamed but still disabled");
        cut.Find(".modal-foot .btn.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(handler.LastPutBody);
            var sent = JsonSerializer.Deserialize<CronJobDto>(handler.LastPutBody!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(sent);
            Assert.Equal("Renamed but still disabled", sent.Name);
            Assert.False(sent.Enabled);          // NOT silently re-enabled
            Assert.False(sent.PreventOverlap);    // preserved, not reset to the default true
        });
    }

    /// <summary>Routing handler that also captures the body of a PUT /cron/jobs/{id}.</summary>
    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<CronJobDto> _jobs;
        private readonly IReadOnlyList<DirectorDto> _directors;
        public string? LastPutBody { get; private set; }
        public CaptureHandler(IReadOnlyList<CronJobDto> jobs, IReadOnlyList<DirectorDto> directors)
        { _jobs = jobs; _directors = directors; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path.StartsWith("/cron/jobs/", StringComparison.Ordinal))
            {
                LastPutBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LastPutBody ?? "{}", System.Text.Encoding.UTF8, "application/json"),
                };
            }

            object body = path switch
            {
                "/cron/jobs" => new { jobs = _jobs },
                "/directors" => (object)_directors,
                _ when path.EndsWith("/runs", StringComparison.Ordinal) => new { jobId = "cj_1", runs = Array.Empty<CronRunRecord>() },
                _ => new { },
            };
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// Emits the real rendered Schedule page (real markup + real app.css + the page's scoped CSS)
    /// when CC488_PROOF_DIR is set, so the Developer Agent can screenshot the genuine compiled page
    /// for the issue's visual proof. No-op in the normal suite.
    /// </summary>
    [Fact]
    public void EmitProofArtifact_WhenProofDirSet()
    {
        var proofDir = Environment.GetEnvironmentVariable("CC488_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(proofDir)) return;

        var cut = Render(SampleJobs());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("table.sched-tbl tbody tr")));
        // Open the create modal too, so the proof shows the form.
        var pageHtml = cut.Markup;

        var here = AppContext.BaseDirectory;
        var cssRoot = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "CcDirector.Cockpit"));
        var appCss = Path.Combine(cssRoot, "wwwroot", "app.css");
        var scopedCss = Path.Combine(cssRoot, "Components", "Pages", "Schedule.razor.css");
        var css = (File.Exists(appCss) ? File.ReadAllText(appCss) : "")
            + "\n" + (File.Exists(scopedCss) ? StripScope(File.ReadAllText(scopedCss)) : "");

        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>\n" +
            "body{background:#1E1E1E;margin:0;padding:16px;font-family:'Segoe UI',sans-serif;color:#CCCCCC}\n" +
            css +
            "\n</style></head><body>" + pageHtml + "</body></html>";

        Directory.CreateDirectory(proofDir);
        File.WriteAllText(Path.Combine(proofDir, "schedule-page-rendered.html"), html);
    }

    // The scoped CSS uses plain class selectors (Blazor rewrites them with a [b-xxxx] attribute at
    // build time). For the standalone artifact the bUnit markup has no scope attribute, so use the
    // raw selectors as-is - they already match the rendered classes.
    private static string StripScope(string css) => css;
}
