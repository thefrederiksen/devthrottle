using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE DEFINITION ROUTES AS THEY ARE MAPPED (Factory Definition mission, phase 1). <see cref="FactoryDefinitionEndpointsTests"/>
/// calls the handlers, which proves the rules and nothing about a request REACHING them: the route templates, the
/// reading of the JSON body, and the names of the fields in the answer are all outside a handler. The command line
/// depends on every one of those - it sends <c>expectedVersion</c>, <c>note</c> and <c>content</c> and reads
/// <c>outcome</c>, <c>findings</c>, <c>current</c> and <c>newestVersion</c> by name.
///
/// So these go through <see cref="FactoryDefinitionEndpoints.Map"/> itself, in memory: the mapped endpoint is found by
/// matching the request's verb and path against its own template, and run with a request body and a response body.
/// No port is opened. They also hold the mapped routes and the session key guard to the same list, because adding a
/// route does not add it to the guard: a session key would be refused with 403 while every handler test stayed green.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryDefinitionRoutesThroughThePipelineTests : IDisposable
{
    private const string TheFactory = DefinitionFixture.FactoryId;
    private const string FactorySession = "75000000-0000-4000-8000-000000000001";
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    // The account a request is resolved to when a test names one; every other request is account A's.
    private const string AccountItem = "test-account";

    private readonly GatewayDbTestHarness _h = new();
    private readonly ServiceProvider _services;
    private readonly GatewayDatabase _db;
    private readonly CcDirector.Gateway.Factory.Triggers.TriggerStore _triggers;
    private Routes _routes;

    public FactoryDefinitionRoutesThroughThePipelineTests()
    {
        _db = _h.Open();
        _triggers = new CcDirector.Gateway.Factory.Triggers.TriggerStore(_db);
        _services = new ServiceCollection().AddRouting().AddLogging().BuildServiceProvider();
        _routes = MapThrough(new FactoryPublishApply(_db));
        new CcDirector.Gateway.Factory.Registry.FactoryRegistryStore(_db).Register(A, DefinitionFixture.Registration(), "test", Now);
    }

    /// <summary>The definition routes mapped over this test's database, publishing through <paramref name="apply"/> and
    /// asking the machines through <paramref name="askMachines"/> (by default, no machine has a Director to ask).</summary>
    private Routes MapThrough(FactoryPublishApply apply, FactoryDefinitionEndpoints.AskMachinesAsync? askMachines = null)
    {
        var routes = new Routes(_services);
        FactoryDefinitionEndpoints.Map(routes,
            resolveTenant: ctx => ctx.Items.TryGetValue(AccountItem, out var account) ? (TenantId)account! : A,
            store: new FactoryDefinitionStore(_db),
            sessionFactoryOf: _ => SessionFactoryLookup.In(TheFactory),
            fleetSkillsOf: _ => DefinitionFixture.FleetSkills,
            connectedMachinesOf: _ => DefinitionFixture.Machines,
            liveOf: (_, _) => null,
            apply: apply,
            nowUtc: () => Now,
            askMachines: askMachines ?? DefinitionFixture.AskNobody,
            checkStates: new FactoryDefinitionEndpoints.CheckStateParts(_triggers.CheckStates, _triggers.List));
        return routes;
    }

    public void Dispose()
    {
        _services.Dispose();
        _h.Dispose();
    }

    /// <summary>The thing routes are mapped onto, with no server behind it: it only collects what was mapped.</summary>
    private sealed class Routes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static void AsAPerson(HttpContext ctx) => ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";

    private static void AsAPersonOfAccountB(HttpContext ctx)
    {
        AsAPerson(ctx);
        ctx.Items[AccountItem] = B;
    }

    private static void AsASessionOfTheFactory(HttpContext ctx) =>
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.Parse(FactorySession), A, "dir-a");

    /// <summary>The mapped endpoint whose verb and template match this request, with the route values it matched.</summary>
    private (RouteEndpoint Endpoint, RouteValueDictionary Values)? Match(string method, string path)
    {
        foreach (var endpoint in _routes.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || !methods.Contains(method)) continue;
            var values = new RouteValueDictionary();
            var matcher = new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary());
            if (matcher.TryMatch(path, values)) return (endpoint, values);
        }
        return null;
    }

    private async Task<(int Status, JsonElement Body)> Send(string method, string path, Action<HttpContext> who, string? json = null)
    {
        var query = path.Contains('?') ? path[path.IndexOf('?')..] : "";
        if (query.Length > 0) path = path[..path.IndexOf('?')];
        var matched = Match(method, path);
        Assert.True(matched is not null, $"no mapped route matches {method} {path}");
        var ctx = new DefaultHttpContext { RequestServices = _services };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        ctx.Request.RouteValues = matched!.Value.Values;
        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasABody());
        }
        ctx.Response.Body = new MemoryStream();
        who(ctx);

        await matched.Value.Endpoint.RequestDelegate!(ctx);

        ctx.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, document.RootElement.Clone());
    }

    private sealed class HasABody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private const string FactoryPath = "/factory-definitions/devthrottle";

    /// <summary>A save's body in the exact spelling the command line sends.</summary>
    private static string SaveBody(IReadOnlyDictionary<string, string> content, int expect, string? note = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["expectedVersion"] = expect, ["note"] = note, ["content"] = content });

    [Fact]
    public async Task A_DEFINITION_SENT_AS_JSON_IS_SAVED_PUBLISHED_AND_READ_BACK_UNDER_THE_NAMES_THE_COMMAND_LINE_USES()
    {
        var saved = await Send("PUT", FactoryPath + "/draft", AsASessionOfTheFactory, SaveBody(DefinitionFixture.Publishable(), 0, "first"));

        Assert.Equal(200, saved.Status);
        Assert.Equal(1, saved.Body.GetProperty("version").GetInt32());
        Assert.Equal("draft", saved.Body.GetProperty("state").GetString());
        Assert.Equal("session", saved.Body.GetProperty("authorKind").GetString());
        Assert.Equal(FactorySession, saved.Body.GetProperty("authorId").GetString());
        Assert.Equal("first", saved.Body.GetProperty("changeNote").GetString());
        Assert.Equal(DefinitionFixture.Publishable().Count, saved.Body.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, saved.Body.GetProperty("newestVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, saved.Body.GetProperty("publishedVersion").ValueKind);
        var notice = saved.Body.GetProperty("findings")[0];
        Assert.Equal("notice", notice.GetProperty("severity").GetString());
        Assert.True(notice.TryGetProperty("file", out _));
        Assert.True(notice.TryGetProperty("line", out _));
        Assert.True(notice.TryGetProperty("message", out _));

        var notYet = await Send("GET", FactoryPath, AsAPerson);
        Assert.Equal(404, notYet.Status);
        Assert.Equal(1, notYet.Body.GetProperty("newestVersion").GetInt32());

        var published = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        Assert.Equal(200, published.Status);
        Assert.Equal("published", published.Body.GetProperty("state").GetString());
        Assert.Equal(1, published.Body.GetProperty("publishedVersion").GetInt32());

        var read = await Send("GET", FactoryPath, AsASessionOfTheFactory);
        Assert.Equal(200, read.Status);
        Assert.Equal(TheFactory, read.Body.GetProperty("factory").GetString());
        Assert.Equal(DefinitionFixture.Publishable()["seats/boss.yaml"], read.Body.GetProperty("files").GetProperty("seats/boss.yaml").GetString());

        var newest = await Send("GET", FactoryPath + "/draft", AsAPerson);
        Assert.Equal(1, newest.Body.GetProperty("version").GetInt32());

        var one = await Send("GET", FactoryPath + "/versions/1", AsAPerson);
        Assert.Equal(DefinitionFixture.Publishable().Count, one.Body.GetProperty("files").EnumerateObject().Count());

        var history = await Send("GET", FactoryPath + "/history?limit=5", AsAPerson);
        Assert.Equal(1, history.Body.GetProperty("publishedVersion").GetInt32());
        Assert.Equal(1, history.Body.GetProperty("total").GetInt32());
        var row = history.Body.GetProperty("versions")[0];
        Assert.Equal("first", row.GetProperty("changeNote").GetString());
        Assert.False(row.TryGetProperty("files", out _));
        Assert.DoesNotContain("Run the factory.", history.Body.GetRawText());
    }

    [Fact]
    public async Task A_PUBLISH_ASKS_THE_SEATS_MACHINE_AND_ITS_ANSWER_COMES_BACK_AS_A_NOTICE_IN_THE_RESPONSE()
    {
        // Phase 5, design section 6: the machine answers that it does not hold the seat's secret and has its repository
        // registered. The publish goes through, and the response carries the notice on the line that names the secret.
        IReadOnlyList<MachineQuestion>? asked = null;
        _routes = MapThrough(new FactoryPublishApply(_db), (_, questions, _) =>
        {
            asked = questions;
            IReadOnlyDictionary<string, MachineReply> replies = questions.ToDictionary(q => q.Machine, q => MachineReply.Answered(new FactoryMachineAnswer
            {
                Machine = q.Machine,
                Secrets = q.Secrets.Select(n => new FactorySecretAnswer { Name = n, Held = false }).ToList(),
                Repositories = q.Repositories.Select(n => new FactoryRepositoryAnswer { Name = n, Registered = true, Path = "repos/" + n }).ToList(),
            }), StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(replies);
        });
        await Send("PUT", FactoryPath + "/draft", AsASessionOfTheFactory, SaveBody(DefinitionFixture.Publishable(), 0));

        var published = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");

        Assert.Equal(200, published.Status);
        var question = Assert.Single(asked!);
        Assert.Equal("SOREN_SOUTH", question.Machine);
        var findings = published.Body.GetProperty("findings").EnumerateArray().ToList();
        var notice = Assert.Single(findings, f => f.GetProperty("file").GetString() == "seats/outreach.yaml" && f.GetProperty("line").GetInt32() == 18);
        Assert.Equal("notice", notice.GetProperty("severity").GetString());
        Assert.Equal("SOREN_SOUTH does not hold the secret 'outreach-smtp-password'. Add it there with cc-secrets; the seat's start will refuse without it.",
            notice.GetProperty("message").GetString());
        Assert.DoesNotContain(findings, f => f.GetProperty("file").GetString() == "seats/outreach.yaml" && f.GetProperty("line").GetInt32() == 8);
    }

    [Fact]
    public async Task THE_CHECKS_REFUSAL_COMES_BACK_AS_422_WITH_EVERY_FINDING_BY_FILE_AND_LINE()
    {
        var broken = DefinitionFixture.PublishableWith("seats/writer.yaml", DefinitionFixture.Writer + "\ncolour: red");

        var refused = await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(broken, 0));

        Assert.Equal(422, refused.Status);
        Assert.Equal("CheckFailed", refused.Body.GetProperty("outcome").GetString());
        Assert.StartsWith("The definition was not saved: the check found 1 error", refused.Body.GetProperty("error").GetString());
        var error = refused.Body.GetProperty("findings").EnumerateArray().First(f => f.GetProperty("severity").GetString() == "error");
        Assert.Equal("seats/writer.yaml", error.GetProperty("file").GetString());
        Assert.Equal(5, error.GetProperty("line").GetInt32());
        Assert.Contains("'colour' is not a key", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_SESSIONS_PUBLISH_AND_RESTORE_COME_BACK_AS_403_WITH_THE_SENTENCE_AND_THE_OUTCOME_BY_NAME()
    {
        await Send("PUT", FactoryPath + "/draft", AsASessionOfTheFactory, SaveBody(DefinitionFixture.Publishable(), 0));

        var refused = await Send("POST", FactoryPath + "/publish", AsASessionOfTheFactory, """{"version":1}""");
        Assert.Equal(403, refused.Status);
        Assert.Equal(FactoryDefinitionStore.OnlyAPersonPublishes, refused.Body.GetProperty("error").GetString());
        Assert.Equal("OnlyAPersonPublishes", refused.Body.GetProperty("outcome").GetString());

        var restore = await Send("POST", FactoryPath + "/restore", AsASessionOfTheFactory, """{"version":1,"expectedVersion":1}""");
        Assert.Equal(403, restore.Status);
        Assert.Equal(FactoryDefinitionStore.OnlyAPersonRestores, restore.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_STALE_SAVE_COMES_BACK_AS_409_WITH_THE_CURRENT_VERSION_UNDER_current()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 1));

        var refused = await Send("PUT", FactoryPath + "/draft", AsASessionOfTheFactory, SaveBody(DefinitionFixture.Publishable(), 1));

        Assert.Equal(409, refused.Status);
        Assert.Equal("Stale", refused.Body.GetProperty("outcome").GetString());
        Assert.Equal(2, refused.Body.GetProperty("current").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_RESTORE_SENT_AS_JSON_PUBLISHES_A_COPY_AS_THE_NEWEST_VERSION()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));
        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        await Send("PUT", FactoryPath + "/draft", AsAPerson,
            SaveBody(DefinitionFixture.PublishableEdit("seats/writer.yaml", "Write the posts.", "v2"), 1));
        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":2}""");

        var back = await Send("POST", FactoryPath + "/restore", AsAPerson, """{"version":1,"expectedVersion":2}""");

        Assert.Equal(200, back.Status);
        Assert.Equal(3, back.Body.GetProperty("version").GetInt32());
        Assert.Equal(1, back.Body.GetProperty("restoredFrom").GetInt32());
        Assert.Equal("published", back.Body.GetProperty("state").GetString());
        Assert.Equal(3, back.Body.GetProperty("publishedVersion").GetInt32());
    }

    [Fact]
    public async Task A_PUBLISH_ANSWERS_WITH_WHAT_CHANGES_and_the_diff_route_answers_it_between_any_two_versions()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));
        var first = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        Assert.True(first.Body.GetProperty("changes").GetProperty("firstPublish").GetBoolean());

        await Send("PUT", FactoryPath + "/draft", AsAPerson,
            SaveBody(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"), 1));
        var diff = await Send("GET", FactoryPath + "/diff", AsASessionOfTheFactory);

        Assert.Equal(200, diff.Status);
        Assert.Equal(1, diff.Body.GetProperty("from").GetInt32());
        Assert.Equal(2, diff.Body.GetProperty("to").GetInt32());
        var changes = diff.Body.GetProperty("changes");
        Assert.False(changes.GetProperty("nothingChanges").GetBoolean());
        var seat = changes.GetProperty("seats")[0];
        Assert.Equal("writer", seat.GetProperty("seat").GetString());
        Assert.Equal("changed", seat.GetProperty("change").GetString());

        var second = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":2}""");
        Assert.Equal(changes.GetRawText(), second.Body.GetProperty("changes").GetRawText());

        var none = await Send("GET", FactoryPath + "/diff?from=2&to=2", AsAPerson);
        Assert.True(none.Body.GetProperty("changes").GetProperty("nothingChanges").GetBoolean());
    }

    // ---------- phase 4: the publish drives the factory ----------

    [Fact]
    public async Task A_PUBLISH_AND_A_RESTORE_SENT_AS_JSON_ANSWER_WITH_applied_ONE_SENTENCE_PER_STEP()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));

        var published = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1,"replaces":0}""");

        Assert.Equal(200, published.Status);
        var applied = published.Body.GetProperty("applied").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal("Definition: version 1 published.", applied[0]);
        Assert.Contains("Registry: seat writer brief file briefs/writer.md -> seats/writer.yaml.", applied);
        Assert.Contains(applied, a => a.StartsWith("Schedule cj_", StringComparison.Ordinal)
                                      && a.EndsWith(" (DevThrottle - Boss) created for seat boss: Weekdays 08:00 Europe/Copenhagen.", StringComparison.Ordinal));
        Assert.True(published.Body.GetProperty("changes").GetProperty("firstPublish").GetBoolean());

        await Send("PUT", FactoryPath + "/draft", AsAPerson,
            SaveBody(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"), 1));
        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":2,"replaces":1}""");

        var restored = await Send("POST", FactoryPath + "/restore", AsAPerson, """{"version":1,"expectedVersion":2,"replaces":2}""");

        Assert.Equal(200, restored.Status);
        var steps = restored.Body.GetProperty("applied").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal("Definition: version 3 written, a copy of version 1, and published.", steps[0]);
        Assert.Contains("Registry: seat writer renamed Author -> Writer.", steps);
        Assert.Contains(restored.Body.GetProperty("changes").GetProperty("seats").EnumerateArray(),
            c => c.GetProperty("seat").GetString() == "writer");
    }

    [Fact]
    public async Task THE_DIFF_CARRIES_plan_AND_THE_PUBLISHED_VERSION_IT_READ()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));

        var before = await Send("GET", FactoryPath + "/diff", AsAPerson);

        Assert.Equal(200, before.Status);
        Assert.Equal(JsonValueKind.Null, before.Body.GetProperty("publishedVersion").ValueKind);
        var plan = before.Body.GetProperty("plan");
        Assert.Equal(1, plan.GetProperty("version").GetInt32());
        Assert.Equal("What publishing version 1 would do to the live factory now:", plan.GetProperty("heading").GetString());
        Assert.Equal("publish", plan.GetProperty("act").GetString());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("refused").ValueKind);
        var steps = plan.GetProperty("steps").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Contains("A new schedule (DevThrottle - Boss) for seat boss: Weekdays 08:00 Europe/Copenhagen.", steps);
        Assert.DoesNotContain(steps, s => s.StartsWith("Definition:", StringComparison.Ordinal));

        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        await Send("PUT", FactoryPath + "/draft", AsAPerson,
            SaveBody(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"), 1));
        var after = await Send("GET", FactoryPath + "/diff", AsAPerson);

        Assert.Equal(1, after.Body.GetProperty("publishedVersion").GetInt32());
        Assert.Contains("Registry: seat writer renamed Writer -> Author.",
            after.Body.GetProperty("plan").GetProperty("steps").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task THE_DIFF_PLAN_OF_A_VERSION_OLDER_THAN_THE_PUBLISHED_ONE_IS_WORDED_AS_A_RESTORE()
    {
        // Arrange: version 1 published, then version 2 (the writer renamed Author) published over it.
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));
        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        await Send("PUT", FactoryPath + "/draft", AsAPerson,
            SaveBody(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"), 1));
        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":2,"replaces":1}""");

        // Act
        var diff = await Send("GET", FactoryPath + "/diff?to=1", AsAPerson);

        // Assert
        Assert.Equal(200, diff.Status);
        var plan = diff.Body.GetProperty("plan");
        Assert.Equal("restore", plan.GetProperty("act").GetString());
        Assert.Equal("Version 1 is older than the published version 2, so it can only be restored. " +
                     "What restoring version 1 would do to the live factory now:", plan.GetProperty("heading").GetString());
        Assert.Contains("Registry: seat writer renamed Author -> Writer.",
            plan.GetProperty("steps").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task THE_DIFF_PLAN_SAYS_WHY_A_PUBLISH_WOULD_BE_REFUSED()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.TimedChecked(), 0));

        var diff = await Send("GET", FactoryPath + "/diff", AsAPerson);

        var plan = diff.Body.GetProperty("plan");
        Assert.Empty(plan.GetProperty("steps").EnumerateArray());
        Assert.StartsWith("The live factory cannot take this definition", plan.GetProperty("refused").GetString());
        var finding = plan.GetProperty("findings")[0];
        Assert.Equal("seats/outreach.yaml", finding.GetProperty("file").GetString());
        Assert.Equal(FactoryDefinitionCheck.TimedCheckStartNotYet, finding.GetProperty("message").GetString());
    }

    // ---------- phase 5b: an interval check publishes, and the state its check keeps ----------

    /// <summary>Publish the valid definition, whose outreach seat checks mail every 10 minutes, and return its trigger.</summary>
    private async Task<CcDirector.Gateway.Data.Entities.TriggerEntity> PublishTheIntervalCheck()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Valid(), 0));
        var published = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        Assert.Equal(200, published.Status);
        return Assert.Single(_triggers.List(A), t => t.Factory == TheFactory);
    }

    [Fact]
    public async Task A_PUBLISH_WITH_AN_INTERVAL_CHECK_WRITES_ITS_TRIGGER_AND_SAYS_SO()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Valid(), 0));

        var published = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");

        Assert.Equal(200, published.Status);
        Assert.Contains("Trigger devthrottle-outreach-start-1 created for seat outreach: every 10 minutes, check checks/new_mail.py with secret outreach-imap-password, on SOREN_SOUTH.",
            published.Body.GetProperty("applied").EnumerateArray().Select(a => a.GetString()));
        var t = Assert.Single(_triggers.List(A), t => t.Factory == TheFactory);
        Assert.Equal(("checks/new_mail.py", 1), (t.CheckPath, t.DefinitionVersion));
    }

    [Fact]
    public async Task THE_CHECK_STATE_LISTS_EACH_CHECKED_START_BY_SEAT_AND_NUMBER_WITH_ITS_NEWEST_STATE()
    {
        var t = await PublishTheIntervalCheck();
        _triggers.CheckStates.RecordIfChanged(A, TheFactory, CcDirector.Gateway.Factory.Triggers.FactoryCheckStateStore.TriggerOwner(t.Id),
            """{"last":"m-41"}""", "dir-1", "run-1", Now);

        var listed = await Send("GET", FactoryPath + "/check-state", AsASessionOfTheFactory);

        Assert.Equal(200, listed.Status);
        Assert.Equal(1, listed.Body.GetProperty("count").GetInt32());
        var s = listed.Body.GetProperty("states")[0];
        Assert.Equal($"trigger:{t.Id:D}", s.GetProperty("ownerKey").GetString());
        Assert.Equal(("outreach", 1, "devthrottle-outreach-start-1"), (s.GetProperty("seat").GetString(), s.GetProperty("start").GetInt32(), s.GetProperty("name").GetString()));
        Assert.Equal("""{"last":"m-41"}""", s.GetProperty("content").GetString());
        Assert.Equal("dir-1", s.GetProperty("writtenBy").GetString());
    }

    [Fact]
    public async Task THE_CHECK_STATE_OF_A_FACTORY_WITH_NO_PUBLISHED_DEFINITION_IS_COUNT_ZERO()
    {
        var listed = await Send("GET", FactoryPath + "/check-state", AsAPerson);

        Assert.Equal(200, listed.Status);
        Assert.Equal(0, listed.Body.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_PERSONS_RESET_APPENDS_AN_EMPTY_STATE_AND_THE_CHECK_IS_HANDED_IT()
    {
        var t = await PublishTheIntervalCheck();
        var owner = CcDirector.Gateway.Factory.Triggers.FactoryCheckStateStore.TriggerOwner(t.Id);
        _triggers.CheckStates.RecordIfChanged(A, TheFactory, owner, "stuck", "dir-1", "run-1", Now);

        var reset = await Send("POST", FactoryPath + "/check-state/reset", AsAPerson, $$"""{"ownerKey":"{{owner}}"}""");

        Assert.Equal(200, reset.Status);
        Assert.True(reset.Body.GetProperty("reset").GetBoolean());
        Assert.Equal("", reset.Body.GetProperty("content").GetString());
        Assert.Equal("", _triggers.CheckStates.StateFor(A, TheFactory, owner));
        var newest = _triggers.CheckStates.Newest(A, TheFactory, owner)!;
        Assert.Equal((2, true), (newest.Sequence, newest.Reset));
        Assert.StartsWith("person", newest.WrittenBy);
    }

    [Fact]
    public async Task A_SESSIONS_RESET_IS_403_IN_WORDS_AND_THE_STATE_IS_UNTOUCHED()
    {
        var t = await PublishTheIntervalCheck();
        var owner = CcDirector.Gateway.Factory.Triggers.FactoryCheckStateStore.TriggerOwner(t.Id);
        _triggers.CheckStates.RecordIfChanged(A, TheFactory, owner, "stuck", "dir-1", "run-1", Now);

        var refused = await Send("POST", FactoryPath + "/check-state/reset", AsASessionOfTheFactory, $$"""{"ownerKey":"{{owner}}"}""");

        Assert.Equal(403, refused.Status);
        Assert.Equal(FactoryDefinitionEndpoints.OnlyAPersonResets, refused.Body.GetProperty("error").GetString());
        Assert.Equal("OnlyAPersonResets", refused.Body.GetProperty("outcome").GetString());
        Assert.Equal("stuck", _triggers.CheckStates.StateFor(A, TheFactory, owner));
    }

    [Fact]
    public async Task A_SECOND_ACCOUNT_READS_NONE_OF_THE_FIRSTS_CHECK_STATE_AND_RESETS_NONE_OF_IT()
    {
        // Arrange: account A's checked start holds a state.
        var t = await PublishTheIntervalCheck();
        var owner = CcDirector.Gateway.Factory.Triggers.FactoryCheckStateStore.TriggerOwner(t.Id);
        _triggers.CheckStates.RecordIfChanged(A, TheFactory, owner, "a's state", "dir-1", "run-1", Now);

        // Act: a person of account B reads the same factory, and resets A's start by its owner key.
        var listed = await Send("GET", FactoryPath + "/check-state", AsAPersonOfAccountB);
        var reset = await Send("POST", FactoryPath + "/check-state/reset", AsAPersonOfAccountB, $$"""{"ownerKey":"{{owner}}"}""");

        // Assert: B sees no checked start, its reset names nothing it has, and A's state and history are untouched.
        Assert.Equal((200, 0), (listed.Status, listed.Body.GetProperty("count").GetInt32()));
        Assert.Equal(404, reset.Status);
        Assert.Equal("a's state", _triggers.CheckStates.StateFor(A, TheFactory, owner));
        Assert.Equal(1, _triggers.CheckStates.Newest(A, TheFactory, owner)!.Sequence);
        Assert.Null(_triggers.CheckStates.Newest(B, TheFactory, owner));
    }

    [Fact]
    public async Task A_RESET_OF_A_START_THE_DEFINITION_DOES_NOT_HAVE_IS_404_AND_WRITES_NOTHING()
    {
        await PublishTheIntervalCheck();
        const string stranger = "trigger:99999999-9999-9999-9999-999999999999";

        var refused = await Send("POST", FactoryPath + "/check-state/reset", AsAPerson, $$"""{"ownerKey":"{{stranger}}"}""");
        var malformed = await Send("POST", FactoryPath + "/check-state/reset", AsAPerson, """{"ownerKey":"session:1"}""");

        Assert.Equal(404, refused.Status);
        Assert.StartsWith($"{stranger} is not a checked start of devthrottle", refused.Body.GetProperty("error").GetString());
        Assert.Equal(400, malformed.Status);
        Assert.Null(_triggers.CheckStates.Newest(A, TheFactory, stranger));
    }

    [Fact]
    public async Task A_PUBLISH_WITH_PREVENT_OVERLAP_OFF_ON_A_CHECKED_START_IS_REFUSED_ON_ITS_LINE()
    {
        // The check refuses it at every save, the publish included; the save is where a person meets it first.
        var off = new Dictionary<string, string>(DefinitionFixture.Valid())
            .Also("seats/outreach.yaml", "    secrets: [outreach-imap-password]\n", "    preventOverlap: false\n");

        var refused = await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(off, 0));

        Assert.Equal(422, refused.Status);
        var error = refused.Body.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("severity").GetString() == "error");
        Assert.Equal(("seats/outreach.yaml", 14), (error.GetProperty("file").GetString(), error.GetProperty("line").GetInt32()));
        Assert.Equal("A start with a check always prevents overlap, so preventOverlap cannot be false here. Remove it.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_PUBLISH_WITH_A_CHECK_THAT_IS_NOT_IN_CHECKS_IS_REFUSED_ON_ITS_LINE()
    {
        var elsewhere = new Dictionary<string, string>(DefinitionFixture.Valid())
            .Also("seats/outreach.yaml", "check: checks/new_mail.py", "check: files/outreach_send.py");

        var refused = await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(elsewhere, 0));

        Assert.Equal(422, refused.Status);
        var errors = refused.Body.GetProperty("findings").EnumerateArray()
            .Where(f => f.GetProperty("severity").GetString() == "error").ToList();
        var error = Assert.Single(errors);
        Assert.Equal(("seats/outreach.yaml", 13), (error.GetProperty("file").GetString(), error.GetProperty("line").GetInt32()));
        Assert.StartsWith("'check' is 'files/outreach_send.py', which is not right.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_PUBLISH_STATING_A_VERSION_IT_NO_LONGER_REPLACES_IS_409_IN_WORDS()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));
        await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");
        await Send("PUT", FactoryPath + "/draft", AsAPerson,
            SaveBody(DefinitionFixture.PublishableEdit("seats/writer.yaml", "name: Writer", "name: Author"), 1));

        var refused = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":2,"replaces":0}""");

        Assert.Equal(409, refused.Status);
        Assert.Equal("Stale", refused.Body.GetProperty("outcome").GetString());
        Assert.Equal("Not published: version 1 is the published definition now, not 0. Read what changes again and publish once more.",
            refused.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_PUBLISH_WITH_A_TIMED_START_THAT_HAS_A_CHECK_IS_422_ON_THE_CHECK_LINE()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.TimedChecked(), 0));

        var refused = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");

        Assert.Equal(422, refused.Status);
        Assert.Equal("CheckFailed", refused.Body.GetProperty("outcome").GetString());
        var error = refused.Body.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("severity").GetString() == "error");
        Assert.Equal("seats/outreach.yaml", error.GetProperty("file").GetString());
        Assert.Equal(13, error.GetProperty("line").GetInt32());
        Assert.Equal(FactoryDefinitionCheck.TimedCheckStartNotYet, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_PUBLISH_OF_A_FACTORY_THE_REGISTRY_DOES_NOT_HOLD_IS_409_IN_WORDS()
    {
        using (var ctx = _db.CreateContext(A))
        {
            ctx.FactoryRegistry.Remove(ctx.FactoryRegistry.Single(f => f.Factory == TheFactory));
            ctx.SaveChanges();
        }
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));

        var refused = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");

        Assert.Equal(409, refused.Status);
        Assert.Equal("NotRegistered", refused.Body.GetProperty("outcome").GetString());
        Assert.Equal("devthrottle is not registered, and until phase 5 a defined factory runs in its registered folder. Register it first, then publish.",
            refused.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_PUBLISH_WHOSE_SCHEDULE_WRITE_FAILS_IS_500_NAMING_THE_STEP_AND_THE_ROW()
    {
        _routes = MapThrough(FactoryPublishScenario.Failing(b => b.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = _h.DbPath,
            ForeignKeys = true,
        }.ToString())));
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));

        var failed = await Send("POST", FactoryPath + "/publish", AsAPerson, """{"version":1}""");

        Assert.Equal(500, failed.Status);
        Assert.Equal("WriteFailed", failed.Body.GetProperty("outcome").GetString());
        var sentence = failed.Body.GetProperty("error").GetString()!;
        Assert.StartsWith("The publish changed nothing: writing schedule cj_", sentence);
        Assert.EndsWith($" for seat 'boss' failed: {FailTheScheduleWrite.Message}", sentence);
        var read = await Send("GET", FactoryPath, AsAPerson);
        Assert.Equal(404, read.Status);
    }

    [Fact]
    public async Task The_factory_in_the_path_is_folded_to_its_one_spelling()
    {
        await Send("PUT", FactoryPath + "/draft", AsAPerson, SaveBody(DefinitionFixture.Publishable(), 0));

        var read = await Send("GET", "/factory-definitions/DevThrottle/draft", AsAPerson);

        Assert.Equal(200, read.Status);
        Assert.Equal(TheFactory, read.Body.GetProperty("factory").GetString());
    }

    [Fact]
    public async Task A_write_that_carries_no_body_is_a_400_in_words()
    {
        var refused = await Send("PUT", FactoryPath + "/draft", AsAPerson);

        Assert.Equal(400, refused.Status);
        Assert.Equal("a body is required", refused.Body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_DRAFT_THAT_MAPS_A_PATH_TO_NULL_IS_REFUSED_WITH_400_NAMING_THE_PATH_NOT_A_500()
    {
        // The exact body review 2 of 4004 sent: JSON null for one file. It used to reach the check, which threw.
        var body = """{"expectedVersion":0,"content":{"factory.yaml":null,"seats/boss.yaml":"seat: boss"}}""";

        var refused = await Send("PUT", FactoryPath + "/draft", AsAPerson, body);

        Assert.Equal(400, refused.Status);
        Assert.Equal("NotText", refused.Body.GetProperty("outcome").GetString());
        Assert.Equal("'factory.yaml' has no text; every file in a definition is text, even if it is empty",
            refused.Body.GetProperty("error").GetString());
        Assert.Equal(404, (await Send("GET", FactoryPath + "/draft", AsAPerson)).Status);
    }

    [Fact]
    public async Task AN_IMPORT_SENT_AS_JSON_IS_STORED_AS_A_DRAFT_AND_EXPORTS_BACK_AS_THE_SAME_FILES()
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?> { ["content"] = DefinitionFixture.Valid() });

        var imported = await Send("POST", "/factory-definitions/import", AsAPerson, body);

        Assert.Equal(200, imported.Status);
        Assert.Equal(TheFactory, imported.Body.GetProperty("factory").GetString());
        Assert.Equal(1, imported.Body.GetProperty("version").GetInt32());
        Assert.Equal("draft", imported.Body.GetProperty("state").GetString());
        Assert.True(imported.Body.GetProperty("created").GetBoolean());
        Assert.Equal(JsonValueKind.Null, imported.Body.GetProperty("publishedVersion").ValueKind);

        var exported = await Send("GET", FactoryPath + "/export", AsASessionOfTheFactory);
        Assert.Equal(200, exported.Status);
        Assert.Equal("definition", exported.Body.GetProperty("source").GetString());
        Assert.Equal(1, exported.Body.GetProperty("version").GetInt32());
        var files = exported.Body.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(DefinitionFixture.Valid(), files);
        Assert.Empty(exported.Body.GetProperty("reported").EnumerateArray());
    }

    [Fact]
    public async Task THE_SCHEMAS_ARE_SERVED_AS_JSON_SCHEMA_DOCUMENTS_ONE_PER_FILE_TYPE()
    {
        var served = await Send("GET", "/factory-definition-schemas", AsASessionOfTheFactory);

        Assert.Equal(200, served.Status);
        var schemas = served.Body.GetProperty("schemas").EnumerateArray().ToList();
        Assert.Equal(new[] { "factory.schema.json", "seat.schema.json", "grants.schema.json", "rules.schema.json" },
            schemas.Select(x => x.GetProperty("name").GetString()));
        var seat = schemas.Single(x => x.GetProperty("applies").GetString() == "seats/<seat>.yaml").GetProperty("schema");
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", seat.GetProperty("$schema").GetString());
        Assert.False(seat.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("GET", "/factory-definitions/devthrottle")]
    [InlineData("GET", "/factory-definitions/devthrottle/draft")]
    [InlineData("GET", "/factory-definitions/devthrottle/history")]
    [InlineData("GET", "/factory-definitions/devthrottle/versions/3")]
    [InlineData("GET", "/factory-definitions/devthrottle/diff")]
    [InlineData("PUT", "/factory-definitions/devthrottle/draft")]
    [InlineData("POST", "/factory-definitions/devthrottle/publish")]
    [InlineData("POST", "/factory-definitions/devthrottle/restore")]
    [InlineData("GET", "/factory-definitions/devthrottle/export")]
    [InlineData("POST", "/factory-definitions/import")]
    [InlineData("GET", "/factory-definition-schemas")]
    public void EACH_ROUTE_IS_MAPPED_and_the_session_key_guard_lets_a_session_reach_it(string method, string path)
    {
        Assert.NotNull(Match(method, path));
        // The two lists must agree: a route the guard does not know is one no session can reach in production.
        Assert.True(SessionKeyGuard.Check(method, path).Allowed);
    }

    [Theory]
    [InlineData("DELETE", "/factory-definitions/devthrottle")]
    [InlineData("PUT", "/factory-definitions/devthrottle")]
    [InlineData("POST", "/factory-definitions/devthrottle/draft")]
    [InlineData("DELETE", "/factory-definitions/devthrottle/draft")]
    [InlineData("GET", "/factory-definitions/devthrottle/publish")]
    [InlineData("GET", "/factory-definitions")]
    [InlineData("GET", "/factory-definitions/devthrottle/versions")]
    [InlineData("PUT", "/factory-definitions/devthrottle/versions/3")]
    [InlineData("GET", "/factory-definitions/devthrottle/versions/3/files")]
    [InlineData("POST", "/factory-definitions/devthrottle/anything-else")]
    [InlineData("POST", "/factory-definitions/devthrottle/diff")]
    [InlineData("POST", "/factory-definitions/devthrottle/export")]
    [InlineData("PUT", "/factory-definitions/import")]
    [InlineData("POST", "/factory-definitions/devthrottle/import")]
    [InlineData("POST", "/factory-definition-schemas")]
    [InlineData("GET", "/factory-definition-schemas/seat")]
    public void NOTHING_ELSE_IS_MAPPED_UNDER_THE_PREFIX_and_the_guard_agrees(string method, string path)
    {
        Assert.Null(Match(method, path));
        Assert.False(SessionKeyGuard.Check(method, path).Allowed);
    }
}
