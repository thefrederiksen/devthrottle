using System.Net;
using System.Net.Http.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Teams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A dark Gateway answers 404 on every team route, whatever else would answer (devthrottle_internal#2300, #2311). On a
/// Gateway with the Cockpit built in, an unmapped GET falls to the Cockpit's single-page fallback and answers 200 with
/// its page. Whether a test Gateway HAS the Cockpit depends on what ran before it (one test deletes the built shell when
/// it finishes), so the host-level dark tests could pass or fail by run order. This pins the rule without that chance:
/// a real web host whose fallback answers 200 for every request, exactly as the Cockpit's does.
/// </summary>
public sealed class TeamsDarkRoutesTests
{
    private const string ShellBody = "<!doctype html><title>shell</title>";

    private static async Task<WebApplication> Host()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        TeamsDarkRoutes.Use(app);
        app.MapGet(HostedEnrollmentEndpoint.Path, () => Results.Text("personal enrollment route"));
        app.MapFallback("{*path}", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(ShellBody);
        });
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.First() + "/") };

    [Theory]
    [InlineData("GET", "teams")]
    [InlineData("POST", "teams")]
    [InlineData("GET", "teams/6f0c0000-0000-4000-8000-000000000001/members")]
    [InlineData("GET", "teams/6f0c0000-0000-4000-8000-000000000001/fleet-map")]
    [InlineData("GET", "Teams")]
    [InlineData("GET", "devices/enroll-hosted/teams")]
    [InlineData("POST", "devices/enroll-hosted/move")]
    public async Task ATeamRoute_IsNotFound_EvenWhereAFallbackWouldAnswer200(string method, string path)
    {
        await using var app = await Host();
        using var http = Client(app);
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") req.Content = JsonContent.Create(new { deviceId = "director-1" });

        using var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain("<!doctype", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APathThatOnlyStartsWithTheSameLetters_AndThePersonalEnrollmentRoute_AreUntouched()
    {
        await using var app = await Host();
        using var http = Client(app);

        using var lookalike = await http.GetAsync("teamsx");
        Assert.Equal(HttpStatusCode.OK, lookalike.StatusCode);
        Assert.Equal(ShellBody, await lookalike.Content.ReadAsStringAsync());

        using var personal = await http.GetAsync("devices/enroll-hosted");
        Assert.Equal("personal enrollment route", await personal.Content.ReadAsStringAsync());
    }

    [Fact]
    public void IsTeamPath_MatchesWholeSegmentsOnly()
    {
        Assert.True(TeamsDarkRoutes.IsTeamPath("/teams"));
        Assert.True(TeamsDarkRoutes.IsTeamPath("/teams/x"));
        Assert.True(TeamsDarkRoutes.IsTeamPath("/TEAMS/x"));
        Assert.True(TeamsDarkRoutes.IsTeamPath(HostedEnrollmentEndpoint.TeamsPath));
        Assert.True(TeamsDarkRoutes.IsTeamPath(HostedEnrollmentEndpoint.MovePath));
        Assert.False(TeamsDarkRoutes.IsTeamPath("/teamsx"));
        Assert.False(TeamsDarkRoutes.IsTeamPath("/"));
        Assert.False(TeamsDarkRoutes.IsTeamPath(HostedEnrollmentEndpoint.Path));
        Assert.False(TeamsDarkRoutes.IsTeamPath("/devices/enroll-hosted/teamsx"));
    }
}
