using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE RULE ROUTES, CALLED AS AN AGENT SESSION (Session Rules part 2, phase 2). The real route table over a real
/// store, behind a stand-in for the one thing the host-wide middleware does that these routes read: it stamps
/// the verified session's identity on the request. So what is tested is exactly what a session key reaches -
/// who the history says made each change is read from the request, never from the body.
/// </summary>
public sealed class SessionRuleEndpointsAgentTests : IAsyncDisposable
{
    private static readonly Guid CallingSession = Guid.Parse("9a6f1510-57a2-4cde-8571-8bbdbd9938a0");

    private readonly GatewayDbTestHarness _h = new();
    private WebApplication? _app;
    private HttpClient? _client;
    private string? _rosterAgent = "Codex";

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        _h.Dispose();
    }

    private async Task<HttpClient> StartAsync()
    {
        // An EMPTY builder: no settings files, so no file watchers to run out of.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                new SessionCredentialIdentity(CallingSession, TenantId.Local, "director-test");
            await next();
        });
        SessionRuleEndpoints.Map(_app, new SessionRuleStore(_h.Open()), _ => _rosterAgent);
        await _app.StartAsync();
        _client = _app.GetTestClient();
        return _client;
    }

    private static object ANewRule() => new
    {
        instruction = "When a session runs echo RULE-PROBE, tell me.",
        screenDescription = "A shell command that echoes RULE-PROBE.",
        triggerWords = new[] { "RULE-PROBE" },
        checks = Array.Empty<object>(),
        scope = "all-sessions",
        cooldownSeconds = 300,
        dailyCap = 5,
    };

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<string> CreateAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/gateway/rules", ANewRule());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return (await JsonOf(created)).GetProperty("rule").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task CreatePromoteChangeDelete_ByASessionKey_EachIsInTheHistoryWithThatSessionAndAgent()
    {
        // Arrange
        var client = await StartAsync();

        // Act
        var id = await CreateAsync(client);
        var promoted = await client.PostAsJsonAsync($"/gateway/rules/{id}/promote",
            new { acknowledgement = "The dry run is right; making it live." });
        var changed = await client.PatchAsJsonAsync($"/gateway/rules/{id}", new { dailyCap = 2 });
        var deleted = await client.DeleteAsync($"/gateway/rules/{id}");
        var history = await JsonOf(await client.GetAsync($"/gateway/rules/history?rule={id}"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal("session:" + CallingSession,
            (await JsonOf(promoted)).GetProperty("rule").GetProperty("promotedBy").GetString());
        var change = await JsonOf(changed);
        Assert.Equal("dry_run", change.GetProperty("rule").GetProperty("state").GetString());
        Assert.True(change.GetProperty("wasLive").GetBoolean());
        Assert.True((await JsonOf(deleted)).GetProperty("deleted").GetBoolean());

        Assert.Equal(4, history.GetProperty("count").GetInt32());
        var rows = history.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(new[] { "delete", "change", "promote", "create" }, rows.Select(r => r.GetProperty("action").GetString()));
        Assert.All(rows, r =>
        {
            Assert.Equal(CallingSession.ToString(), r.GetProperty("sessionId").GetString());
            Assert.Equal("Codex", r.GetProperty("agent").GetString());
            Assert.Equal("session:" + CallingSession, r.GetProperty("changedBy").GetString());
        });
        Assert.Equal(2, rows[1].GetProperty("after").GetProperty("dailyCap").GetInt32());
        Assert.Equal(5, rows[1].GetProperty("before").GetProperty("dailyCap").GetInt32());
    }

    [Fact]
    public async Task Change_ARuleThatDoesNotExist_Is404NamingTheId()
    {
        // Arrange
        var client = await StartAsync();
        var missing = Guid.NewGuid();

        // Act
        var response = await client.PatchAsJsonAsync($"/gateway/rules/{missing}", new { dailyCap = 2 });

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(missing.ToString(), (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Change_AFieldARuleDoesNotHave_Is400AndTheRuleIsUnchanged()
    {
        // Arrange
        var client = await StartAsync();
        var id = await CreateAsync(client);

        // Act
        var response = await client.PatchAsJsonAsync($"/gateway/rules/{id}", new { colour = "red" });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'colour'", (await JsonOf(response)).GetProperty("error").GetString());
        var history = await JsonOf(await client.GetAsync($"/gateway/rules/history?rule={id}"));
        Assert.Equal(1, history.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Create_ByASessionNotOnTheLiveRoster_RecordsThatItsAgentIsNotKnown()
    {
        // Arrange
        _rosterAgent = null;
        var client = await StartAsync();

        // Act
        var id = await CreateAsync(client);

        // Assert
        var history = await JsonOf(await client.GetAsync($"/gateway/rules/history?session={CallingSession}"));
        var row = Assert.Single(history.GetProperty("changes").EnumerateArray());
        Assert.Equal(id, row.GetProperty("ruleId").GetString());
        Assert.Equal(SessionRuleEndpoints.AgentNotOnRoster, row.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task History_ARuleFilterThatIsNotAnId_Is400RatherThanIgnored()
    {
        // Arrange
        var client = await StartAsync();

        // Act
        var response = await client.GetAsync("/gateway/rules/history?rule=not-an-id");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task History_WithNothingChanged_SaysCountZero()
    {
        // Arrange
        var client = await StartAsync();

        // Act
        var history = await JsonOf(await client.GetAsync("/gateway/rules/history"));

        // Assert
        Assert.Equal(0, history.GetProperty("count").GetInt32());
        Assert.Empty(history.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task History_WithNoLimit_AnswersTheDefaultNumberNewestFirst()
    {
        // Arrange - one more change than the default answers.
        var client = await StartAsync();
        for (var i = 0; i <= SessionRuleEndpoints.DefaultHistoryLimit; i++) await CreateAsync(client);

        // Act
        var history = await JsonOf(await client.GetAsync("/gateway/rules/history"));

        // Assert
        Assert.Equal(50, SessionRuleEndpoints.DefaultHistoryLimit);
        Assert.Equal(50, history.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task History_AboveTheCeiling_Is400AndAtTheCeiling_IsServed()
    {
        // Arrange
        var client = await StartAsync();

        // Act
        var above = await client.GetAsync("/gateway/rules/history?limit=501");
        var at = await client.GetAsync("/gateway/rules/history?limit=500");

        // Assert
        Assert.Equal(500, SessionRuleEndpoints.MaxHistoryLimit);
        Assert.Equal(HttpStatusCode.BadRequest, above.StatusCode);
        Assert.Equal(HttpStatusCode.OK, at.StatusCode);
    }

    [Fact]
    public async Task Promote_ARuleThatDoesNotExist_Is404LikeEveryOtherRuleRoute()
    {
        // Arrange
        var client = await StartAsync();
        var missing = Guid.NewGuid();

        // Act
        var response = await client.PostAsJsonAsync($"/gateway/rules/{missing}/promote", new { acknowledgement = "yes" });

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(missing.ToString(), (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public void ActorOf_TheSharedMachineToken_IsNamedAsTheMachineTokenNotAsUnchecked()
    {
        // Arrange - authenticated, but by the shared token: no device, no session, no person.
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "the-shared-token";

        // Act
        var actor = SessionRuleEndpoints.ActorOf(http, _ => "never asked");

        // Assert
        Assert.Equal(SessionRuleEndpoints.MachineTokenCaller, actor.ChangedBy);
        Assert.Null(actor.SessionId);
    }

    [Fact]
    public void ActorOf_ARequestWithNoCheckedCredential_IsUnnamed()
    {
        // Act
        var actor = SessionRuleEndpoints.ActorOf(new Microsoft.AspNetCore.Http.DefaultHttpContext(), _ => "never asked");

        // Assert
        Assert.Equal(SessionRuleEndpoints.UnnamedCaller, actor.ChangedBy);
    }
}
