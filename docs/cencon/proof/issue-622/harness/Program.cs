// Issue #622 proof host: boots the REAL Gateway cron notification path over real HTTP.
//
// It wires the production CronEngine + GatewayCronNotifier + DirectorEventLog + CronJobStore +
// CronRunHistoryStore exactly as GatewayHost does, and maps the same /cron/jobs REST routes plus
// GET /directors/{id}/events (the existing fleet event ring this feature reuses). The ONLY test
// seam is the ICronSessionStarter: a header on the run-now call forces success or failure so the
// proof can show both a clean fire AND a failed fire deterministically, without a live Director.
//
// Run:  dotnet run --project ProofHost.csproj -- <port>
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Events;
using CcDirector.Gateway.Running;
using Microsoft.AspNetCore.Http;

var port = args.Length > 0 ? int.Parse(args[0]) : 8622;
var dir = Path.Combine(Path.GetTempPath(), "issue-622-proof-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);

var jobStore = new CronJobStore(Path.Combine(dir, "cronjobs.json"));
var history = new CronRunHistoryStore(Path.Combine(dir, "cronruns.json"));
var events = new DirectorEventLog();

// The session starter is the seam: per-job override via the job name suffix so a "FAIL" job fails.
var starter = new ProofStarter();
var workList = new ProofWorkListRunner();

// Production notifier: rides the per-Director event ring (events) and POSTs the optional webhook.
// The deep-link resolver returns a fixed tailnet endpoint for the proof director.
var notifier = new GatewayCronNotifier(
    events,
    directorId => directorId == "director-1" ? "https://proofhost.ts.net" : null,
    $"http://127.0.0.1:{port}",
    new HttpClient { Timeout = TimeSpan.FromSeconds(10) });

var engine = new CronEngine(jobStore, history, starter, workList, notifier, new SystemClock());

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
var app = builder.Build();
app.Urls.Add($"http://127.0.0.1:{port}");

var jsonOpts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// ---- the same /cron/jobs CRUD surface (mirrors CronJobEndpoints) ----
app.MapPost("/cron/jobs", async (HttpContext ctx) =>
{
    var job = await System.Text.Json.JsonSerializer.DeserializeAsync<CronJobDto>(ctx.Request.Body, jsonOpts);
    if (job is null) return Results.BadRequest(new { error = "body required" });
    var (ok, error) = CronSchedule.Validate(job);
    if (!ok) return Results.BadRequest(new { error });
    return Results.Json(jobStore.Create(job), statusCode: StatusCodes.Status201Created);
});
app.MapGet("/cron/jobs", () => Results.Json(new { jobs = jobStore.ListAll() }));
app.MapGet("/cron/jobs/{id}", (string id) =>
    jobStore.Get(id) is { } j ? Results.Json(j) : Results.NotFound(new { error = "no such cron job" }));

// ---- the firing surface (mirrors CronRunEndpoints) ----
app.MapPost("/cron/jobs/{id}/run", async (string id, HttpContext ctx) =>
{
    var result = await engine.RunNowAsync(id, ctx.RequestAborted);
    return result.Outcome switch
    {
        CronFireOutcome.Fired => Results.Json(result.Record),
        CronFireOutcome.SkippedOverlap => Results.Conflict(new { error = "overlap" }),
        CronFireOutcome.NoSuchJob => Results.NotFound(new { error = "no such cron job" }),
        _ => Results.StatusCode(500),
    };
});
app.MapGet("/cron/jobs/{id}/runs", (string id) => Results.Json(new { jobId = id, runs = history.List(id) }));

// ---- the EXISTING fleet event ring this feature reuses (mirrors GatewayEndpoints) ----
app.MapGet("/directors/{id}/events", (string id) =>
    Results.Json(new { directorId = id, events = events.For(id) }));

Console.WriteLine($"[ProofHost] listening on http://127.0.0.1:{port} (data: {dir})");
app.Run();

// ---- seams ----

// A job whose NAME contains the exact marker "[FAILSTART]" fails to start (simulating a
// launcher/Director that cannot start), otherwise it starts cleanly on director-1. This is the only
// injected behavior - the engine, notifier, store, history, and event ring are all production code.
sealed class ProofStarter : ICronSessionStarter
{
    private int _n;
    public Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
    {
        if (job.Name.Contains("[FAILSTART]", StringComparison.Ordinal))
            return Task.FromResult<(string?, string?, string?)>((null, null, "launcher could not start a Director on the target machine"));
        _n++;
        return Task.FromResult<(string?, string?, string?)>(($"sess-{_n:D4}", "director-1", null));
    }
}

sealed class ProofWorkListRunner : ICronWorkListRunner
{
    public Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct) =>
        Task.FromResult(CronWorkListOutcome.Started);
}
