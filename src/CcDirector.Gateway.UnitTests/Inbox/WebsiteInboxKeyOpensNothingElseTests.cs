using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Inbox;

/// <summary>
/// THE WEBSITE KEY OPENS NOTHING ELSE (brief E). A live website key is sent, as a bearer, through the REAL authentication
/// gate (<see cref="AuthMiddleware.Run"/>) to the Gateway's other routes, on a self-hosted and a hosted gate, over real
/// device and session key registries. Every one is 401 and never reaches the route. Only the website's own write route is
/// let through the gate, and that route checks the key itself.
/// </summary>
public sealed class WebsiteInboxKeyOpensNothingElseTests : IDisposable
{
    private const string SharedToken = "shared-machine-token";
    private static readonly TenantId A = new("acct-a");

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private (string Key, AuthMiddleware.RequireToken SelfHost, AuthMiddleware.RequireToken Hosted) Setup()
    {
        var db = _harness.Open();
        var key = new WebsiteInboxKeyStore(db).Mint(A, "dev-phone", new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc));
        var devices = _harness.OpenDevices();
        var sessions = new SessionKeyRegistry(db);
        var selfHost = new AuthMiddleware.RequireToken { Hosted = false, Token = SharedToken, Devices = devices, Sessions = sessions };
        var hosted = new AuthMiddleware.RequireToken
        {
            Hosted = true,
            Token = "",
            Devices = devices,
            Sessions = sessions,
            Boundary = new Tenancy.HostedTenantBoundary(new AsyncLocalTenantContext(), devices, hosted: true),
        };
        return (key, selfHost, hosted);
    }

    private static async Task<(bool Continued, int Status)> RunAsync(string method, string path, string key, AuthMiddleware.RequireToken cfg)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Headers.Authorization = $"Bearer {key}";
        ctx.Response.Body = new MemoryStream();
        var continued = false;
        await AuthMiddleware.Run(ctx, cfg, () => { continued = true; return Task.CompletedTask; });
        return (continued, ctx.Response.StatusCode);
    }

    public static IEnumerable<object[]> OtherRoutes() =>
    [
        ["GET", "/sessions"],
        ["POST", "/sessions/11111111-1111-4111-8111-111111111111/inbox"],
        ["GET", "/gateway/inbox"],
        ["POST", "/gateway/inbox/11111111-1111-4111-8111-111111111111/reply"],
        ["GET", WebsiteInboxEndpoints.KeyPath],
        ["POST", WebsiteInboxEndpoints.KeyPath],
        ["DELETE", WebsiteInboxEndpoints.KeyPath],
        ["POST", "/account/email"],
        ["GET", "/account/status"],
        ["GET", "/directors"],
        ["GET", "/gateway/demo-mode"],
        ["GET", "/gateway/factories"],
        ["GET", "/devices"],
    ];

    [Theory]
    [MemberData(nameof(OtherRoutes))]
    public async Task WebsiteKey_OnAnotherRoute_SelfHosted_Is401(string method, string path)
    {
        var (key, selfHost, _) = Setup();

        var (continued, status) = await RunAsync(method, path, key, selfHost);

        Assert.False(continued, $"{method} {path} let the website key through");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Theory]
    [MemberData(nameof(OtherRoutes))]
    public async Task WebsiteKey_OnAnotherRoute_Hosted_Is401(string method, string path)
    {
        var (key, _, hosted) = Setup();

        var (continued, status) = await RunAsync(method, path, key, hosted);

        Assert.False(continued, $"{method} {path} let the website key through");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task WebsiteWriteRoute_IsTheOnlyOneLetThroughTheGate()
    {
        var (key, selfHost, hosted) = Setup();

        Assert.True((await RunAsync("POST", WebsiteInboxEndpoints.WritePath, key, selfHost)).Continued);
        Assert.True((await RunAsync("POST", WebsiteInboxEndpoints.WritePath, key, hosted)).Continued);
    }
}
