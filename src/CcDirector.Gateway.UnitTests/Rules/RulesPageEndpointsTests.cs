using System.Net;
using System.Text.Json;
using CcDirector.Gateway.Api;
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
/// THE THREE RULES PAGE ROUTES (Session Rules phase 3, wire contracts P1-P3), through the real route table over a
/// real store: the shapes arrive in the wire's own names, a missing or unknown time zone and a missing machine are
/// refused with a stated reason, an unknown rule is a 404, and none of the three is open to a session key.
/// </summary>
public sealed class RulesPageEndpointsTests : IAsyncDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private WebApplication? _app;
    private HttpClient? _client;
    private SessionRuleStore? _store;

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
        _store = new SessionRuleStore(_h.Open());
        SessionRuleEndpoints.Map(_app, _store, _ => null);
        await _app.StartAsync();
        _client = _app.GetTestClient();
        return _client;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private SessionRule AHookRule() => _store!.CreateHook(
        "Never run rm -rf.", RuleAppliesTo.Everywhere, HookRuleWords.BeforeCommand, HookRuleWords.Shell,
        HookRuleWords.Match, new[] { "rm -rf" }, HookRuleWords.Deny, DateTime.UtcNow, AnActor.Owner);

    [Fact]
    public async Task GetPage_WithAZone_AnswersTheTabsAndRulesInTheWiresNames()
    {
        // Arrange
        var client = await StartAsync();
        var rule = AHookRule();

        // Act
        var response = await client.GetAsync("/gateway/rules/page?tz=Europe/Copenhagen");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.NotEqual("", body.GetProperty("today").GetString());
        Assert.Equal(
            new[] { "all", "screen", "hook", "session_start", "fired_today", "changed_by_agents" },
            body.GetProperty("tabs").EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        var item = Assert.Single(body.GetProperty("rules").EnumerateArray());
        Assert.Equal(rule.Id.ToString(), item.GetProperty("id").GetString());
        Assert.Equal("Never fired", item.GetProperty("firedLabel").GetString());
        Assert.Equal("neutral", item.GetProperty("modeTone").GetString());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("previewSuggestions").GetProperty("machines").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("previewSuggestions").GetProperty("repos").ValueKind);
    }

    [Theory]
    [InlineData("/gateway/rules/page")]
    [InlineData("/gateway/rules/page?tz=")]
    [InlineData("/gateway/rules/page?tz=Not/AZone")]
    public async Task GetPage_MissingOrUnknownZone_Is400WithAReason(string path)
    {
        // Arrange
        var client = await StartAsync();

        // Act
        var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("zone", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetRulePage_KnownRule_AnswersFieldsFiringsAndChanges()
    {
        // Arrange
        var client = await StartAsync();
        var rule = AHookRule();

        // Act
        var response = await client.GetAsync($"/gateway/rules/{rule.Id}/page?tz=Europe/Copenhagen");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(rule.Id.ToString(), body.GetProperty("rule").GetProperty("id").GetString());
        Assert.Equal(7, body.GetProperty("fields").GetArrayLength());
        Assert.Equal(0, body.GetProperty("firings").GetArrayLength());
        Assert.Equal("This rule has not fired yet.", body.GetProperty("firingsEmptyText").GetString());
        Assert.Equal("Created", body.GetProperty("changes")[0].GetProperty("actionLabel").GetString());
    }

    [Fact]
    public async Task GetRulePage_UnknownRule_Is404WithAReason()
    {
        // Arrange
        var client = await StartAsync();

        // Act
        var response = await client.GetAsync($"/gateway/rules/{Guid.NewGuid()}/page?tz=Europe/Copenhagen");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("no rule", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetRulePage_BadZone_Is400()
    {
        // Arrange
        var client = await StartAsync();
        var rule = AHookRule();

        // Act
        var response = await client.GetAsync($"/gateway/rules/{rule.Id}/page?tz=Nowhere");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetPreview_WithACommand_AnswersTheDecisionAndNormalisesTheRepo()
    {
        // Arrange
        var client = await StartAsync();
        var rule = AHookRule();

        // Act
        var response = await client.GetAsync(
            "/gateway/rules/preview?machine=devlinux&repo=git@github.com:Acme/web.git&command=rm%20-rf%20build");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("github.com/acme/web", body.GetProperty("repo").GetString());
        var decision = body.GetProperty("decision");
        Assert.Equal("Would deny", decision.GetProperty("verdictLabel").GetString());
        Assert.Equal(rule.Id.ToString(), decision.GetProperty("decidingRuleId").GetString());
    }

    [Fact]
    public async Task GetPreview_NoCommand_DecisionIsNull()
    {
        // Arrange
        var client = await StartAsync();

        // Act
        var body = await JsonOf(await client.GetAsync("/gateway/rules/preview?machine=devlinux"));

        // Assert
        Assert.Equal(JsonValueKind.Null, body.GetProperty("decision").ValueKind);
        Assert.Equal("", body.GetProperty("repo").GetString());
    }

    [Theory]
    [InlineData("/gateway/rules/preview")]
    [InlineData("/gateway/rules/preview?machine=%20&repo=github.com/acme/web")]
    public async Task GetPreview_NoMachine_Is400WithAReason(string path)
    {
        // Arrange
        var client = await StartAsync();

        // Act
        var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("machine", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/gateway/rules/page")]
    [InlineData("/gateway/rules/preview")]
    [InlineData("/gateway/rules/6f1c2a7e-0d3b-4e55-9a51-3c2b8d4e7f10/page")]
    public void SessionKeyGuard_TheRulesPageRoutes_AreNotOpenToASessionKey(string path)
    {
        // Act
        var verdict = SessionKeyGuard.Check("GET", path);

        // Assert
        Assert.False(verdict.Allowed, $"GET {path} was allowed to a session key");
    }
}
