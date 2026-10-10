using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The Error Logging mission, step 2 (issue #3675), over real HTTP: an unhandled endpoint exception is one stored row
/// and a 500 that carries the same correlation id; every error answer carries an id; a failure logged while a request
/// is served is stored under that request's id; and a refused prompt is stored with its session - without its words.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class GatewayRequestErrorsTests : IAsyncDisposable
{
    private const string PromptWords = "please deploy the quarterly payroll for Ingrid Halvorsen";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gateway-request-errors-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient? _http;
    private ErrorReportStore? _store;
    private GatewayErrorSink? _sink;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The Gateway's own order: the scope first, the exception boundary inside it, routing, the route note.</summary>
    private async Task StartAsync(Action<WebApplication> map, Action<WebApplication>? beforeRouting = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        _store = new ErrorReportStore(_root);
        _sink = new GatewayErrorSink(_store, new ErrorIntakeFloods());
        GatewayRequestErrors.Use(app, _sink);
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex) { await GatewayRequestErrors.AnswerUnhandledAsync(ctx, ex); }
        });
        beforeRouting?.Invoke(app);
        app.UseRouting();
        GatewayRequestErrors.UseRouteNote(app);
        map(app);

        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/") };
    }

    private IReadOnlyList<ErrorReportRecord> Stored()
    {
        _sink!.Flush();
        return _store!.Query(new ErrorReportQuery(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), Limit: 500)).Records;
    }

    private static string HeaderOf(HttpResponseMessage response)
        => Assert.Single(response.Headers.GetValues(GatewayRequestErrors.HeaderName));

    [Fact]
    public async Task A_thrown_endpoint_exception_is_one_stored_row_and_a_500_with_the_same_id()
    {
        await StartAsync(app => app.MapGet("/boom/{sid}", (string sid) =>
        {
            throw new InvalidOperationException("the cron table is locked");
#pragma warning disable CS0162
            return Results.Ok();
#pragma warning restore CS0162
        }));

        var response = await _http!.GetAsync("boom/2c3c4215");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("correlation_id").GetString()!;
        Assert.Equal(32, id.Length);
        Assert.Equal(id, HeaderOf(response));
        // Nothing of the exception reaches the client.
        Assert.Equal("internal error", body.GetProperty("error").GetString());
        Assert.DoesNotContain("cron", body.GetRawText());

        var row = Assert.Single(Stored());
        Assert.Equal(id, row.CorrelationId);
        Assert.Equal(ErrorReportLimits.Gateway, row.Component);
        Assert.Equal(500, row.HttpStatus);
        Assert.Equal("internal_error", row.ErrorCode);
        Assert.Equal("GET /boom/{sid}", row.Surface);
        Assert.Equal("System.InvalidOperationException", row.ExceptionType);
        Assert.Equal("unhandled", row.Kind);
        Assert.Equal("2c3c4215", row.SessionId);
        Assert.StartsWith("GET /boom/{sid} answered 500: System.InvalidOperationException: the cron table is locked", row.Message);
        Assert.Contains("InvalidOperationException", row.Stack);
    }

    [Fact]
    public async Task A_request_the_client_abandoned_is_logged_not_stored()
    {
        // Step 2 review, observation 3: the phone leaving mid-request is not a fault of ours.
        var (sink, store) = DirectSink();
        using var gone = new CancellationTokenSource();
        gone.Cancel();
        var ctx = new DefaultHttpContext { RequestAborted = gone.Token };
        ctx.Response.Body = new MemoryStream();

        await GatewayRequestErrors.RunAsync(ctx,
            () => GatewayRequestErrors.AnswerUnhandledAsync(ctx, new OperationCanceledException(gone.Token)), sink);

        sink.Flush();
        Assert.Empty(store.Query(new ErrorReportQuery(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1))).Records);
        Assert.Equal(0, ctx.Response.Body.Length);
    }

    [Fact]
    public async Task A_cancellation_the_client_did_not_cause_is_still_a_stored_500()
    {
        // The contrast: the same exception with the request still open is ours - a timeout of our own, say.
        var (sink, store) = DirectSink();
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await GatewayRequestErrors.RunAsync(ctx,
            () => GatewayRequestErrors.AnswerUnhandledAsync(ctx, new OperationCanceledException()), sink);

        sink.Flush();
        var row = Assert.Single(store.Query(new ErrorReportQuery(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1))).Records);
        Assert.Equal(500, row.HttpStatus);
        Assert.Equal(StatusCodes.Status500InternalServerError, ctx.Response.StatusCode);
    }

    private (GatewayErrorSink Sink, ErrorReportStore Store) DirectSink()
    {
        var store = new ErrorReportStore(_root);
        return (new GatewayErrorSink(store, new ErrorIntakeFloods()), store);
    }

    [Fact]
    public async Task Every_error_answer_carries_an_id_and_a_success_carries_none()
    {
        await StartAsync(app =>
        {
            app.MapGet("/fine", () => Results.Ok(new { ok = true }));
            app.MapGet("/refused", () => Results.Json(new { error = "no" }, statusCode: StatusCodes.Status403Forbidden));
        });

        var fine = await _http!.GetAsync("fine");
        var refused = await _http.GetAsync("refused");
        var unmapped = await _http.GetAsync("no-such-route");

        Assert.False(fine.Headers.Contains(GatewayRequestErrors.HeaderName));
        Assert.Equal(32, HeaderOf(refused).Length);
        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
        Assert.Equal(32, HeaderOf(unmapped).Length);
        Assert.NotEqual(HeaderOf(refused), HeaderOf(unmapped));
    }

    [Fact]
    public async Task A_FAILED_line_logged_while_serving_is_stored_under_the_id_the_answer_carries()
    {
        GatewayErrorSink? attached = null;
        using var log = FileLog.RedirectForTests();
        try
        {
            await StartAsync(app => app.MapGet("/gateway/skills", () =>
            {
                FileLog.Write("[SkillEndpoints] GET /gateway/skills FAILED (IOException): the skill store is locked");
                return Results.Json(new { error = "the skills could not be read" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }));
            attached = _sink;
            FileLog.ErrorObserver += attached!.OnLogLine;

            var response = await _http!.GetAsync("gateway/skills");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var row = Assert.Single(Stored());
            Assert.Equal(HeaderOf(response), row.CorrelationId);
            Assert.Equal("SkillEndpoints", row.Source);
            Assert.Equal("GET /gateway/skills", row.Surface);
            Assert.Equal("GET /gateway/skills FAILED (IOException): the skill store is locked", row.Message);
        }
        finally
        {
            if (attached is not null) FileLog.ErrorObserver -= attached.OnLogLine;
        }
    }

    [Fact]
    public async Task A_prompt_refused_because_the_session_cannot_be_reached_is_stored_with_its_session_and_without_its_words()
    {
        // The real prompt route, called by a session whose target no Director in the account has pushed: the answer
        // the phone shows as a red box. Its row carries the session and the id, and not one word of the prompt.
        var dir = Path.Combine(_root, "instances");
        var registry = new DirectorRegistry(dir);
        var caller = Guid.NewGuid();
        var target = "7d2c0e4e-0000-4000-8000-00000000c0de";
        await StartAsync(
            app => GatewayEndpoints.Map(app, registry, version: "test", token: "test-token",
                tenantBoundary: new HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry()),
                sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown),
            beforeRouting: app => app.Use(async (ctx, next) =>
            {
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(caller, new TenantId("tenant-a"), "director-1");
                await next();
            }));

        // The sink is attached to the process log too (step 2 review, observation 9), so a FAILED line anywhere on the
        // prompt path that carried the prompt's words would land in the store and fail the sweep below.
        HttpResponseMessage response;
        using (var log = FileLog.RedirectForTests())
        {
            FileLog.ErrorObserver += _sink!.OnLogLine;
            try
            {
                response = await _http!.PostAsJsonAsync($"sessions/{target}/prompt", new { text = PromptWords });
            }
            finally
            {
                FileLog.ErrorObserver -= _sink.OnLogLine;
            }
            // Presence, not absence: the prompt path did log while the sink was listening, so the sweep saw its lines.
            Assert.Contains(log.DrainAndReadLines(), l => l.Contains("[GatewayEndpoints]") && l.Contains(target));
        }

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var row = Assert.Single(Stored(), r => r.Kind == "refused");
        Assert.Equal(HeaderOf(response), row.CorrelationId);
        Assert.Equal(target, row.SessionId);
        Assert.Equal("tenant-a", row.Account);
        Assert.Equal(404, row.HttpStatus);
        Assert.Equal("session_not_found", row.ErrorCode);
        Assert.Equal("send a prompt to the session", row.Action);
        Assert.Equal("POST /sessions/{sid}/prompt answered 404 (session_not_found)", row.Message);
        AssertNoPromptWordsAnywhereInTheStore();
        registry.Dispose();
    }

    [Fact]
    public async Task A_refusal_whose_answer_quotes_the_prompt_still_stores_none_of_it()
    {
        // A Director's refusal sentence is free text and could echo what it refused. The row keeps the route, the status
        // and the code only, so even an answer that quotes the prompt word for word puts none of it in the store.
        await StartAsync(app => app.MapPost("/sessions/{sid}/prompt", (HttpContext ctx, string sid) =>
        {
            var answer = Results.Json(new { error = $"the Director refused \"{PromptWords}\"", code = "not_delivered" },
                statusCode: StatusCodes.Status502BadGateway);
            GatewayRequestErrors.RecordRefusal(ctx, answer, "tenant-a", sid, "send a prompt to the session");
            return answer;
        }));

        var response = await _http!.PostAsJsonAsync("sessions/s-1/prompt", new { text = PromptWords });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var row = Assert.Single(Stored());
        Assert.Equal("POST /sessions/{sid}/prompt answered 502 (not_delivered)", row.Message);
        AssertNoPromptWordsAnywhereInTheStore();
    }

    [Fact]
    public async Task A_refusal_that_is_not_of_the_offline_kind_is_not_stored()
    {
        await StartAsync(app => app.MapPost("/sessions/{sid}/prompt", (HttpContext ctx, string sid) =>
        {
            var answer = Results.Json(new { error = "busy" }, statusCode: StatusCodes.Status409Conflict);
            GatewayRequestErrors.RecordRefusal(ctx, answer, "tenant-a", sid, "send a prompt to the session");
            return answer;
        }));

        await _http!.PostAsJsonAsync("sessions/s-1/prompt", new { text = "hello" });

        Assert.Empty(Stored());
    }

    private void AssertNoPromptWordsAnywhereInTheStore()
    {
        var files = Directory.EnumerateFiles(Path.Combine(_root), "*.json*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine(_root, "instances"), StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var word in new[] { "quarterly", "payroll", "Ingrid", "Halvorsen" })
                Assert.DoesNotContain(word, text);
        }
    }
}
