using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// A session key may SEND errors (the Error Logging mission, the owner's ruling of 9 October): a command line tool an
/// agent runs reports with the session's own key. Proved through the real authentication middleware, the real session
/// key registry and the real route: the key can post; every report is filed under the key's OWN account whatever the
/// body claims; it still reads only its own account; and nothing else the guard protects moved.
/// </summary>
public sealed class SessionKeyErrorReportTests : IAsyncDisposable
{
    private static readonly TenantId AccountA = new("tenant-a");
    private static readonly TenantId AccountB = new("tenant-b");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "session-key-errors-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient? _http;
    private ErrorReportStore? _store;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<(string KeyA, string KeyB)> StartAsync()
    {
        var sessions = new SessionKeyRegistry(_harness.Open());
        var keyA = GatewaySessionKey.Mint();
        var keyB = GatewaySessionKey.Mint();
        Assert.True(sessions.Register(AccountA, "director-a", Guid.NewGuid().ToString(), GatewaySessionKey.Hash(keyA), DateTime.UtcNow.AddHours(1)));
        Assert.True(sessions.Register(AccountB, "director-b", Guid.NewGuid().ToString(), GatewaySessionKey.Hash(keyB), DateTime.UtcNow.AddHours(1)));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var config = new AuthMiddleware.RequireToken { Token = "shared-machine-token", Devices = null, Sessions = sessions };
        app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, config, next));

        _store = new ErrorReportStore(_root);
        DirectorErrorEndpoints.Map(app, _store, new HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry()), tenants: null);
        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/") };
        return (keyA, keyB);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string key, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http!.SendAsync(request);
    }

    /// <summary>A tool's report, with an account named in the body at both levels - which no route reads.</summary>
    private static string ToolReport(string message, string claimedAccount) => $$"""
        {"account":"{{claimedAccount}}","reports":[{"component":"tool","source":"cc-devthrottle","kind":"logged",
        "message":"{{message}}","repeat_count":1,"first_seen_utc":"2026-10-09T10:00:00Z","last_seen_utc":"2026-10-09T10:00:00Z",
        "product_version":"2.18.0","os":"windows","os_version":"10","arch":"x64","machine_id":"0123456789abcdef",
        "account":"{{claimedAccount}}","tenant":"{{claimedAccount}}"}]}
        """;

    private IReadOnlyList<ErrorReportRecord> Everything()
        => _store!.Query(new ErrorReportQuery(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), Limit: 500)).Records;

    [Fact]
    public async Task A_session_key_can_post_and_the_report_is_filed_under_its_own_account_whatever_the_body_claims()
    {
        var (keyA, _) = await StartAsync();

        var post = await SendAsync(HttpMethod.Post, "gateway/director-errors", keyA,
            ToolReport("session list FAILED: the Gateway answered 503", claimedAccount: AccountB.Value));

        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var row = Assert.Single(Everything());
        Assert.Equal(AccountA.Value, row.Account);
        Assert.Equal(ErrorReportLimits.Tool, row.Component);
        Assert.Equal("session list FAILED: the Gateway answered 503", row.Message);
    }

    [Fact]
    public async Task A_session_key_cannot_file_under_another_account()
    {
        var (keyA, keyB) = await StartAsync();

        await SendAsync(HttpMethod.Post, "gateway/director-errors", keyA, ToolReport("from A FAILED", AccountB.Value));
        await SendAsync(HttpMethod.Post, "gateway/director-errors", keyB, ToolReport("from B FAILED", AccountA.Value));

        var rows = Everything();
        Assert.Equal(2, rows.Count);
        Assert.Equal(AccountA.Value, rows.Single(r => r.Message == "from A FAILED").Account);
        Assert.Equal(AccountB.Value, rows.Single(r => r.Message == "from B FAILED").Account);
    }

    [Fact]
    public async Task A_session_key_still_reads_only_its_own_accounts_errors()
    {
        var (keyA, keyB) = await StartAsync();
        await SendAsync(HttpMethod.Post, "gateway/director-errors", keyB, ToolReport("B only FAILED", AccountA.Value));
        await SendAsync(HttpMethod.Post, "gateway/director-errors", keyA, ToolReport("A only FAILED", AccountB.Value));

        var read = await SendAsync(HttpMethod.Get, "gateway/director-errors", keyA);
        var groups = await SendAsync(HttpMethod.Get, "gateway/director-errors/groups", keyA);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var errors = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("A only FAILED", Assert.Single(errors.EnumerateArray()).GetProperty("message").GetString());
        var grouped = (await groups.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groups");
        Assert.Equal("A only FAILED", Assert.Single(grouped.EnumerateArray()).GetProperty("sample_message").GetString());
    }

    [Fact]
    public async Task A_session_key_still_reaches_no_administrator_error_route()
    {
        var (keyA, _) = await StartAsync();

        var admin = await SendAsync(HttpMethod.Get, "gateway/admin/director-errors", keyA);

        Assert.NotEqual(HttpStatusCode.OK, admin.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/gateway/director-errors", true)]
    [InlineData("GET", "/gateway/director-errors", true)]
    [InlineData("GET", "/gateway/director-errors/groups", true)]
    // Only the one literal was widened: the grouped read is no write, the other verbs stay refused, and every
    // administrator route stays refused.
    [InlineData("POST", "/gateway/director-errors/groups", false)]
    [InlineData("PUT", "/gateway/director-errors", false)]
    [InlineData("DELETE", "/gateway/director-errors", false)]
    [InlineData("POST", "/gateway/director-errors/x", false)]
    [InlineData("POST", "/gateway/admin/director-errors", false)]
    [InlineData("GET", "/gateway/admin/director-errors", false)]
    [InlineData("GET", "/gateway/admin/director-errors/groups", false)]
    [InlineData("GET", "/gateway/admin/director-errors/summaries", false)]
    [InlineData("PUT", "/gateway/admin/director-errors/linked-issue", false)]
    [InlineData("GET", "/gateway/admin/install-reports", false)]
    [InlineData("POST", "/client-errors", false)]
    public void The_guard_widened_by_exactly_one_route(string method, string path, bool allowed)
    {
        Assert.Equal(allowed, SessionKeyGuard.Check(method, path).Allowed);
        Assert.Equal(allowed, SessionKeyGuard.Check(method, path, raised: true).Allowed);
    }
}
