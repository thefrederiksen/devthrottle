using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// A DELETED TRIGGER'S KEPT RUNS ARE READABLE (Factory Definition, phase 5b, carried from phase 4's Finding 1). A publish
/// that removes a trigger says "its run history is kept", and the rows are kept in <c>trigger_runs</c>; before this,
/// <c>GET /triggers/{id}/runs</c> answered 404 once the trigger was gone, so only the database showed them. Now the
/// route serves the kept rows by the id the trigger had, marked <c>deleted</c>, and answers 404 only when there is
/// neither a trigger nor kept rows IN THE CALLING ACCOUNT.
///
/// Through the real <see cref="TriggerEndpoints.Map"/>, in memory (the endpoint is found by matching its own template
/// and run; no port is opened), on a real <see cref="TriggerStore"/> over one SQLite database holding two accounts.
/// The calling account is named by a header, the way the host resolves it from the credential.
/// </summary>
public sealed class TriggerRunsOfADeletedTriggerTests : IDisposable
{
    private static readonly TenantId Mine = new("acct-runs-mine");
    private static readonly TenantId Theirs = new("acct-runs-theirs");
    private static readonly DateTime Now = new(2026, 10, 15, 14, 52, 0, DateTimeKind.Utc);
    private const string AccountHeader = "X-Test-Account";
    private const string Director = "dir-qa-standin";

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly ServiceProvider _services;
    private readonly Routes _routes;
    private readonly TriggerStore _store;

    public TriggerRunsOfADeletedTriggerTests()
    {
        _db = _h.Open();
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = new Routes(_services);
        _store = new TriggerStore(_db);
        var service = new TriggerService(_store, new FactoryActivityRecord(_db),
            (_, _, _) => Task.FromResult(TriggerStartAttempt.Failed("no machine in this test")),
            findSession: (_, _) => null, findSessionByName: (_, _) => null,
            timeZone: _ => TimeZoneInfo.Utc, nowUtc: () => Now, startLifetime: CancellationToken.None,
            directorRunsDefinitionChecks: (_, _) => true, machineRunsDefinitionChecks: (_, _) => true);
        TriggerEndpoints.Map(_routes,
            ctx => ctx.Request.Headers.TryGetValue(AccountHeader, out var v) ? new TenantId(v.ToString()) : null,
            service, directorMachine: (_, _) => "SOREN_NORTH", nowUtc: () => Now,
            sessionFactoryOf: _ => SessionFactoryLookup.NotKnown,
            isDefined: (_, _) => false,
            dueChecks: DueCheckTestParts.NoDueChecks(_db, _h.LegacyPath("due.runs.json")));
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    // ---------- the in-memory pipeline ----------

    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private async Task<(int Status, JsonElement Body)> GetRuns(TenantId account, string id)
    {
        var path = $"/triggers/{id}/runs";
        var endpoint = _routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/triggers/{id}/runs");
        var values = new RouteValueDictionary();
        Assert.True(new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary())
            .TryMatch(path, values));
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        ctx.Request.RouteValues = values;
        ctx.Request.Headers[AccountHeader] = account.Value;
        ctx.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, document.RootElement.Clone());
    }

    // ---------- the world each test starts from ----------

    /// <summary>A trigger in <paramref name="account"/> with <paramref name="checks"/> recorded checks.</summary>
    private Guid TriggerWithChecks(TenantId account, string name, int checks)
    {
        var (created, error) = _store.Create(account, new TriggerDefinitionRequest
        {
            Name = name, Factory = "qa-outreach", FactoryAgent = "outreach", Machine = "SOREN_NORTH",
            RepoPath = @"D:\factories\qa-outreach", CheckCommand = "python check.py", IntervalSeconds = 300,
            Prompt = "There are {count} new.",
        }, "the owner (test)", Now.AddDays(-1));
        Assert.True(created is not null, error);
        for (var i = 0; i < checks; i++)
            Assert.NotNull(_store.RecordCheck(account, created!.Id, Director, Now.AddMinutes(i), TriggerRunOutcome.NothingToDo,
                count: 0, sessionId: null, reason: null, nowUtc: Now.AddMinutes(i)));
        return created!.Id;
    }

    private static void AssertNoSuchTrigger((int Status, JsonElement Body) answer)
    {
        Assert.Equal(StatusCodes.Status404NotFound, answer.Status);
        Assert.Equal("trigger_not_found", answer.Body.GetProperty("code").GetString());
    }

    // ---------- the tests ----------

    [Fact]
    public async Task Runs_DeletedTriggerWithKeptRows_ServesThemMarkedDeleted()
    {
        var id = TriggerWithChecks(Mine, "qa-inbox", checks: 2);
        Assert.True(_store.Delete(Mine, id.ToString("D")));

        var answer = await GetRuns(Mine, id.ToString("D"));

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        Assert.True(answer.Body.GetProperty("deleted").GetBoolean());
        Assert.Equal(id.ToString("D"), answer.Body.GetProperty("triggerId").GetString());
        var runs = answer.Body.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(TriggerRunOutcome.NothingToDo, r.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task Runs_DeletedTriggerIdInUpperCase_ServesTheSameKeptRows()
    {
        var id = TriggerWithChecks(Mine, "qa-inbox", checks: 1);
        Assert.True(_store.Delete(Mine, id.ToString("D")));

        // The QA rig read the row's id upper-case out of the database; the id is the same id however it is spelled.
        var answer = await GetRuns(Mine, id.ToString("D").ToUpperInvariant());

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        Assert.True(answer.Body.GetProperty("deleted").GetBoolean());
        Assert.Single(answer.Body.GetProperty("runs").EnumerateArray());
    }

    [Fact]
    public async Task Runs_AnotherAccountsDeletedTrigger_Is404AndShowsNothing()
    {
        var theirs = TriggerWithChecks(Theirs, "their-inbox", checks: 3);
        Assert.True(_store.Delete(Theirs, theirs.ToString("D")));

        var answer = await GetRuns(Mine, theirs.ToString("D"));

        AssertNoSuchTrigger(answer);
        // And the rows are still theirs to read, so the 404 is the account boundary, not missing rows.
        Assert.Equal(3, (await GetRuns(Theirs, theirs.ToString("D"))).Body.GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public async Task Runs_UnknownId_Is404()
        => AssertNoSuchTrigger(await GetRuns(Mine, Guid.NewGuid().ToString("D")));

    [Fact]
    public async Task Runs_UnknownName_Is404()
        => AssertNoSuchTrigger(await GetRuns(Mine, "no-such-trigger"));

    [Fact]
    public async Task Runs_DeletedTriggerThatNeverRan_Is404()
    {
        var id = TriggerWithChecks(Mine, "qa-inbox", checks: 0);
        Assert.True(_store.Delete(Mine, id.ToString("D")));

        AssertNoSuchTrigger(await GetRuns(Mine, id.ToString("D")));
    }

    [Fact]
    public async Task Runs_LiveTrigger_ByNameAndById_AreServedAsOnMain_NotMarkedDeleted()
    {
        var id = TriggerWithChecks(Mine, "qa-inbox", checks: 2);

        foreach (var key in new[] { "qa-inbox", id.ToString("D") })
        {
            var answer = await GetRuns(Mine, key);
            Assert.Equal(StatusCodes.Status200OK, answer.Status);
            Assert.False(answer.Body.GetProperty("deleted").GetBoolean());
            Assert.Equal(id.ToString("D"), answer.Body.GetProperty("triggerId").GetString());
            Assert.Equal(2, answer.Body.GetProperty("runs").GetArrayLength());
        }
    }

    [Fact]
    public async Task Runs_LiveTriggerThatNeverRan_AnswersAnEmptyListAsOnMain()
    {
        TriggerWithChecks(Mine, "qa-inbox", checks: 0);

        var answer = await GetRuns(Mine, "qa-inbox");

        Assert.Equal(StatusCodes.Status200OK, answer.Status);
        Assert.False(answer.Body.GetProperty("deleted").GetBoolean());
        Assert.Equal(0, answer.Body.GetProperty("runs").GetArrayLength());
    }
}
