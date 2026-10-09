using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Issue #3311, end to end over real HTTP: the Director's own reporter posts a logged error to the real
/// route, and the account read returns it - then again from a NEW store over the same folder, which is what
/// a redeployed Gateway is. Self-host shape (no hosted boundary, so the account is the Local one).
/// It lives in this unlocked suite because it touches only its own temporary folder and a loopback port.
/// </summary>
public sealed class DirectorErrorEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "director-errors-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<(WebApplication App, string Url)> StartAsync(ErrorReportStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        DirectorErrorEndpoints.Map(app, store, tenantBoundary: null, tenants: null);
        await app.StartAsync();
        return (app, app.Urls.First());
    }

    [Fact]
    public async Task A_logged_Director_error_reaches_the_Gateway_and_survives_a_redeploy()
    {
        var (app, url) = await StartAsync(new ErrorReportStore(_root));
        try
        {
            var config = new GatewayConfig { Url = url, Token = "device-key" };
            var reporter = new ErrorReporter(ErrorReportLimits.Director, () => config, new HttpClient(),
                machineName: "devthrottle-mac-mini", productVersion: "2.9.0-test");

            reporter.OnLogLine("[SessionManager] CreateSession FAILED: System.UnauthorizedAccessException: denied /Users/robert/repo\n   at X.Y()");
            Assert.Equal(1, await reporter.SendPendingAsync(CancellationToken.None));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        // The redeploy: a fresh process, a fresh store object, the same durable folder.
        var (again, url2) = await StartAsync(new ErrorReportStore(_root));
        try
        {
            using var client = new HttpClient();
            var answer = await client.GetFromJsonAsync<JsonElement>($"{url2}/gateway/director-errors?machine=devthrottle-mac-mini");

            Assert.Equal(1, answer.GetProperty("total_matched").GetInt32());
            var error = answer.GetProperty("errors")[0];
            Assert.Equal("director", error.GetProperty("component").GetString());
            Assert.Equal("SessionManager", error.GetProperty("source").GetString());
            Assert.Equal("System.UnauthorizedAccessException", error.GetProperty("exception_type").GetString());
            Assert.Equal("2.9.0-test", error.GetProperty("product_version").GetString());
            Assert.Contains("~/repo", error.GetProperty("message").GetString());
            Assert.DoesNotContain("robert", error.GetRawText());
            Assert.DoesNotContain("devthrottle-mac-mini", error.GetRawText());
        }
        finally
        {
            await again.StopAsync();
            await again.DisposeAsync();
        }
    }

    [Fact]
    public async Task One_failure_on_two_sessions_reads_back_as_one_problem_over_HTTP()
    {
        // Issue #3675, over the real route: two reports that differ only by a session id that carries letters
        // come back from GET /gateway/director-errors/groups as ONE row with a count of two.
        var (app, url) = await StartAsync(new ErrorReportStore(_root));
        try
        {
            using var client = new HttpClient();
            var batch = new ErrorReportBatch(
            [
                new ErrorReportItem("director", "Session", "logged", "WAIT ENDED session=2c3c4215 verb=prompt", null, null, 1,
                    DateTime.UtcNow, DateTime.UtcNow, "2.18.0", "windows", "10", "x64", ErrorReportMachineId.Of("pc"),
                    UserVisible: true, Action: "send a prompt to the session"),
                new ErrorReportItem("director", "Session", "logged", "WAIT ENDED session=9f8eab7d verb=prompt", null, null, 1,
                    DateTime.UtcNow, DateTime.UtcNow, "2.18.0", "windows", "10", "x64", ErrorReportMachineId.Of("pc")),
            ]);
            var post = await client.PostAsJsonAsync($"{url}/gateway/director-errors", batch);
            Assert.Equal(System.Net.HttpStatusCode.Accepted, post.StatusCode);

            var answer = await client.GetFromJsonAsync<JsonElement>($"{url}/gateway/director-errors/groups");

            Assert.Equal("account", answer.GetProperty("scope").GetString());
            Assert.Equal(1, answer.GetProperty("total_groups").GetInt32());
            Assert.Equal(2, answer.GetProperty("total_reports").GetInt32());
            Assert.Equal(90, answer.GetProperty("retention_days").GetInt32());
            var group = answer.GetProperty("groups")[0];
            Assert.Equal(2, group.GetProperty("count").GetInt32());
            Assert.Equal(1, group.GetProperty("user_visible").GetInt32());
            Assert.Equal(16, group.GetProperty("fingerprint").GetString()!.Length);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
    [Fact]
    public async Task A_report_in_the_old_wire_shape_is_stored_over_HTTP()
    {
        // Issue #3675: every new field is optional on the wire. This is the exact body a Director built before the
        // mission sends - none of the new fields - posted as raw text so no serializer of ours fills them in.
        var (app, url) = await StartAsync(new ErrorReportStore(_root));
        try
        {
            using var client = new HttpClient();
            const string oldWire = """
                {"reports":[{"component":"director","source":"SessionManager","kind":"logged","message":"Save FAILED: disk full",
                "exception_type":null,"stack":null,"repeat_count":1,"first_seen_utc":"2026-10-08T11:00:00Z",
                "last_seen_utc":"2026-10-08T11:01:00Z","product_version":"2.17.0","os":"windows","os_version":"10",
                "arch":"x64","machine_id":"0123456789abcdef"}]}
                """;
            var post = await client.PostAsync($"{url}/gateway/director-errors",
                new StringContent(oldWire, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.Accepted, post.StatusCode);

            var answer = await client.GetFromJsonAsync<JsonElement>($"{url}/gateway/director-errors");
            var error = Assert.Single(answer.GetProperty("errors").EnumerateArray());
            Assert.Equal("Save FAILED: disk full", error.GetProperty("message").GetString());
            Assert.Equal("2.17.0", error.GetProperty("product_version").GetString());
            Assert.Equal(16, error.GetProperty("fingerprint").GetString()!.Length);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
