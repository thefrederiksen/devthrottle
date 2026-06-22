// Local stub backend for issue #628 proof. Receives the forwarded POST /api/v1/telemetry/login
// from the Gateway relay and appends one JSON line per request to STUB_LOG, recording the method,
// path, the Authorization header (so we can prove the SAME Bearer arrived), and the body (so we can
// prove source:"app" arrived). The status it returns is controlled by STUB_STATUS (default 200);
// set STUB_STATUS=500 to exercise the backend-5xx best-effort case.
using System.Text.Json;

var port = Environment.GetEnvironmentVariable("STUB_PORT") ?? "9628";
var logPath = Environment.GetEnvironmentVariable("STUB_LOG") ?? "stub-requests.log";
var status = int.TryParse(Environment.GetEnvironmentVariable("STUB_STATUS"), out var s) ? s : 200;

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
var app = builder.Build();
app.Urls.Add($"http://127.0.0.1:{port}");

app.MapPost("/api/v1/telemetry/login", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var record = new
    {
        receivedAtUtc = DateTime.UtcNow,
        method = ctx.Request.Method,
        path = ctx.Request.Path.Value,
        authorization = ctx.Request.Headers.Authorization.ToString(),
        body,
    };
    File.AppendAllText(logPath, JsonSerializer.Serialize(record) + Environment.NewLine);
    return Results.StatusCode(status);
});

app.MapGet("/healthz", () => Results.Ok("stub-ok"));

app.Run();
