using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using CcDirector.Launcher;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Launcher.Tests;

/// <summary>
/// Integration tests for <see cref="LauncherAuth"/> middleware.
/// Spins up a minimal in-process Kestrel and verifies auth behavior.
/// </summary>
public sealed class LauncherAuthTests : IAsyncDisposable
{
    private const string ValidToken = "test-valid-token-abc123";
    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly int _port;

    public LauncherAuthTests()
    {
        _port = FindFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, _port));
        builder.Logging.ClearProviders();
        builder.Services.AddRoutingCore();

        _app = builder.Build();

        // Wire the auth middleware with our test token.
        _app.Use((ctx, next) => LauncherAuth.Run(ctx, ValidToken, next));
        _app.UseRouting();

        _app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
        _app.MapGet("/protected", () => Results.Ok(new { secret = "yes" }));
        _app.MapPost("/action", () => Results.Ok(new { done = true }));

        _app.StartAsync().GetAwaiter().GetResult();

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
    }

    // -------------------------------------------------------------------------
    // AC3a: /healthz is always public - no token required.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Healthz_NoToken_Returns200()
    {
        var resp = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // AC3b: Protected endpoints with correct Bearer token -> 200.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Protected_ValidBearer_Returns200()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/protected");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task PostAction_ValidBearer_Returns200()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/action");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // AC3c: Missing token -> 401.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Protected_NoToken_Returns401()
    {
        var resp = await _client.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PostAction_NoToken_Returns401()
    {
        var resp = await _client.PostAsync("/action", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // AC3d: Wrong token -> 401.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Protected_WrongToken_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/protected");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Protected_EmptyBearerValue_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/protected");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer ");

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // -------------------------------------------------------------------------
    // AC3e: 401 body is JSON with "error" field.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Unauthorized_ResponseBody_IsJson()
    {
        var resp = await _client.GetAsync("/protected");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("error", body, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
