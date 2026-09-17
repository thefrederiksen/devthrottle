using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>
/// The chrome-less dev reports page loads before any credential exists (issue #3019), and nothing else
/// under it does.
///
/// WHY THE PAGE HAS TO BE PUBLIC. The browser that loads it is a host application's embedded web view. It
/// holds no Gateway credential of its own and never obtains one: the host hands it a key in memory over
/// the host's own message bridge, AFTER the page has loaded. A credential gate on the page address would
/// therefore redirect the only caller the page exists for to the sign-in screen, every time - which is
/// exactly what happened before this rule existed.
///
/// WHY THAT IS SAFE. The page carries no data and no secret. Every dev report route it then calls is an
/// OWNER route that still requires a device key or the machine token and is still scoped to that caller's
/// account, so opening this address in an ordinary browser gets a page that says it was not handed a key
/// and can read nothing.
/// </summary>
public sealed class DevReportPanePageIsPublicTests
{
    private const string Session = "11111111-2222-3333-4444-555555555555";
    private const string PagePath = "/embed/reports/" + Session;

    private static readonly AuthMiddleware.RequireToken Gate = new() { Token = "a-machine-token" };

    /// <summary>Runs one anonymous request through the gate; true when it reached the application.</summary>
    private static async Task<(bool ReachedTheApplication, int Status, string? Location)> PassesGateAsync(
        string method, string path, string? accept = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (accept is not null) ctx.Request.Headers.Accept = accept;

        var reached = false;
        await AuthMiddleware.Run(ctx, Gate, () => { reached = true; return Task.CompletedTask; });
        return (reached, ctx.Response.StatusCode, ctx.Response.Headers.Location.ToString() is { Length: > 0 } l ? l : null);
    }

    [Fact]
    public async Task Run_AnonymousGetOfThePage_ReachesTheApplication()
    {
        var (reached, _, location) = await PassesGateAsync("GET", PagePath);

        Assert.True(reached, "the reports page must load without a credential; the host hands it one afterwards");
        Assert.Null(location);
    }

    [Fact]
    public async Task Run_AnonymousBrowserNavigationToThePage_IsNotRedirectedToSignIn()
    {
        // The web view navigates with Accept: text/html, which is the exact shape the gate otherwise
        // redirects to /signin. That redirect is what made the pane show a sign-in screen.
        var (reached, _, location) = await PassesGateAsync("GET", PagePath, "text/html,application/xhtml+xml");

        Assert.True(reached);
        Assert.Null(location);
    }

    [Fact]
    public async Task Run_AnonymousHeadOfThePage_ReachesTheApplication()
        => Assert.True((await PassesGateAsync("HEAD", PagePath)).ReachedTheApplication);

    [Fact]
    public async Task Run_AnonymousPostToThePageAddress_IsRefused()
    {
        // Read-only on purpose: the public arm is GET and HEAD, so a write route added under /embed later
        // is credential-gated by default rather than inheriting this opening.
        var (reached, status, _) = await PassesGateAsync("POST", PagePath);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Run_AnonymousGetOfAPathUnderThePage_IsRefused()
    {
        var (reached, status, _) = await PassesGateAsync("GET", PagePath + "/html");

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Run_AnonymousGetOfThePrefixWithNoSession_IsRefused()
    {
        var (reached, status, _) = await PassesGateAsync("GET", DevReportPaneUrl.PagePathPrefix);

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Run_AnonymousGetOfTheOwnerReportRoutes_IsStillRefused()
    {
        // The whole safety of opening the page rests on this: the data behind it did not open with it.
        foreach (var path in new[] { "/dev-reports", "/dev-reports/pane-url", "/dev-reports/" + Session })
        {
            var (reached, status, _) = await PassesGateAsync("GET", path);
            Assert.False(reached, $"{path} must stay credential-gated");
            Assert.Equal(StatusCodes.Status401Unauthorized, status);
        }
    }
}
