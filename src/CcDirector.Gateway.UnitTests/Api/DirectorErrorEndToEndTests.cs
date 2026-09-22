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
}
