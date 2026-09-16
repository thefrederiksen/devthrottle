using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Fleet;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The Fleet Manager routes (the Fleet Manager mission, step 3), driven through their handlers with real
/// stores, a real pushed-session store, a real Director registry and the real roster fold - and no booted
/// host. The account is resolved by the delegate the host passes in; here it is read off a test header, so two
/// accounts can be exercised side by side.
///
/// What they cannot prove: that the host maps these handlers on these paths, and that the middleware admits a
/// session key to them. The second is <see cref="SessionKeyGuardTests"/>.
/// </summary>
public sealed class FleetManagerEndpointsTests : IDisposable
{
    private const string TenantHeader = "X-Test-Account";
    private static readonly TenantId TenantA = new("acct-fm-a");
    private static readonly TenantId TenantB = new("acct-fm-b");

    private const string FleetManager = "10000000-0000-4000-8000-000000000001";
    private static readonly string OwnedWorking = "10000000-0000-4000-8000-000000000002";
    private static readonly string OwnedStopped = "10000000-0000-4000-8000-000000000003";
    private static readonly string NotOwned = "10000000-0000-4000-8000-000000000004";
    private static readonly string OwnedExited = "10000000-0000-4000-8000-000000000005";
    private static readonly string OtherAccountSession = "20000000-0000-4000-8000-000000000001";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-fm-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new();
    private readonly FleetOutcomeStore _outcomes;
    private readonly FleetPreferenceStore _preferences;
    private readonly TurnVerdictStore _verdicts;
    private bool _colourOn = true;

    public FleetManagerEndpointsTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        _outcomes = new FleetOutcomeStore(_harness.Open());
        _preferences = new FleetPreferenceStore(_harness.Open());
        _verdicts = new TurnVerdictStore(_harness.Open());
    }

    public void Dispose()
    {
        _registry.Dispose();
        _harness.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private static TenantId? ResolveTenant(HttpContext ctx)
        => ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && v.ToString().Length > 0
            ? new TenantId(v.ToString())
            : null;

    private static DefaultHttpContext Request(TenantId? tenant, object? body = null, string query = "", string? sessionKey = null)
    {
        var ctx = new DefaultHttpContext();
        if (tenant is { } t) ctx.Request.Headers[TenantHeader] = t.Value;
        if (body is not null)
        {
            ctx.Request.ContentType = "application/json";
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                body as string ?? JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
        if (query.Length > 0) ctx.Request.QueryString = new QueryString("?" + query);
        if (sessionKey is not null)
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                new SessionCredentialIdentity(Guid.Parse(sessionKey), tenant ?? TenantId.Local, "director-a");
        return ctx;
    }

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static T Body<T>(IResult result) => Assert.IsType<JsonHttpResult<T>>(result).Value!;

    /// <summary>A field of an anonymous answer body, read by reflection.</summary>
    private static object? Field(IResult result, string name)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return value.GetType().GetProperty(name)!.GetValue(value);
    }

    private FleetDigestSources Sources() => new(
        FoldedRoster: tenant => GatewayEndpoints.FoldedAccountRoster(_registry, _pushed, tenant, null, null, null, null),
        SessionInAccount: (tenant, sid) => _pushed.TryLocateIgnoringFreshness(tenant, sid) is not null,
        LatestVerdict: (tenant, sid) => _verdicts.Latest(tenant, sid),
        VerdictColourOn: _ => _colourOn);

    private async Task<FleetOutcomeDto> FileAsync(TenantId tenant, FleetOutcomeFileRequest request, string? sessionKey = FleetManager)
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(Request(tenant, request, sessionKey: sessionKey), ResolveTenant, _outcomes);
        Assert.Equal(StatusCodes.Status201Created, Status(result));
        return Body<FleetOutcomeDto>(result);
    }

    private void SeedFleet()
    {
        var now = DateTime.UtcNow;
        _registry.RegisterFromStream("director-a", "MACHINE_A", "someone", "1.0", pid: 11, startedAt: now, tenant: TenantA);
        _registry.RegisterFromStream("director-b", "MACHINE_B", "someone", "1.0", pid: 12, startedAt: now, tenant: TenantB);

        _pushed.RegisterConnection(TenantA, "director-a", "conn-a");
        Assert.True(_pushed.ApplySnapshot(TenantA, "director-a", "conn-a", 1, new List<SessionDto>
        {
            new() { SessionId = FleetManager, Name = "Fleet Manager", ActivityState = "WaitingForInput",
                    WorkflowId = FleetManagerSessions.WorkflowId, CreatedAt = now.AddHours(-2), LastActivityAt = now },
            new() { SessionId = OwnedWorking, Name = "Product repository - fix the flaky roster test, second attempt",
                    ActivityState = "Working", IsControlled = true, ControllerSessionId = FleetManager,
                    MissionName = "Roster", UncommittedCount = 3, CreatedAt = now.AddHours(-1), LastActivityAt = now },
            new() { SessionId = OwnedStopped, Name = "Docs, one change", ActivityState = "WaitingForInput",
                    IsControlled = true, ControllerSessionId = FleetManager, UncommittedCount = 0,
                    CreatedAt = now.AddMinutes(-30), LastActivityAt = now },
            new() { SessionId = OwnedExited, Name = "Gone", ActivityState = "Exited",
                    IsControlled = true, ControllerSessionId = FleetManager, CreatedAt = now.AddMinutes(-20) },
            new() { SessionId = NotOwned, Name = "The owner's own session", ActivityState = "WaitingForInput",
                    CreatedAt = now.AddMinutes(-10), LastActivityAt = now },
        }));

        // Another account holds a session that names the same controlling id. It must never be counted.
        _pushed.RegisterConnection(TenantB, "director-b", "conn-b");
        Assert.True(_pushed.ApplySnapshot(TenantB, "director-b", "conn-b", 1, new List<SessionDto>
        {
            new() { SessionId = OtherAccountSession, Name = "Other account", ActivityState = "Working",
                    IsControlled = true, ControllerSessionId = FleetManager, CreatedAt = now },
        }));
    }

    private static TurnVerdictDto Verdict(string verdictId) => new()
    {
        VerdictId = verdictId,
        JudgedAtUtc = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc),
        TurnEndObservedAtUtc = new DateTime(2026, 9, 16, 10, 59, 50, DateTimeKind.Utc),
        ScreenHash = "screen-hash-digest",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.NeededYou,
        Confidence = "high",
        Evidence = "Shall I publish the change now?",
        Label = "Asks whether to publish",
        Summary = "The docs change is ready and it asks before publishing.",
        AnswerVia = "reply",
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "The docs session asks whether to publish.",
    };

    // ---- outcomes ----------------------------------------------------------------------------------------

    [Fact]
    public async Task FileOutcome_SessionKey_Returns201_FiledByTheCallingSession()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Ready());

        Assert.Equal(FleetManager, filed.FiledBy);
        Assert.Equal("open", filed.Status);
        Assert.Equal("ready", filed.Kind);
    }

    [Fact]
    public async Task FileOutcome_DeviceKey_IsFiledByTheOwner()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding(), sessionKey: null);

        Assert.Equal("owner", filed.FiledBy);
    }

    [Fact]
    public async Task FileOutcome_BadKind_Is400NamingTheValidKinds()
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, new { kind = "update", title = "Something" }), ResolveTenant, _outcomes);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("kind 'update' is not valid; use one of: ready, finding, decision", Field(result, "error"));
        Assert.Empty(_outcomes.List(TenantA, "all", null, 50));
    }

    [Fact]
    public async Task FileOutcome_MissingRequiredField_Is400()
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, new { kind = "decision", title = "Pick one", decision = new { question = "Which?", options = new[] { "A" } } }),
            ResolveTenant, _outcomes);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("decision.options needs at least two options, got 1", Field(result, "error"));
    }

    [Fact]
    public async Task FileOutcome_BrokenJson_Is400()
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(Request(TenantA, "{ not json"), ResolveTenant, _outcomes);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
    }

    [Fact]
    public async Task AnyRoute_NoAccount_Is403()
    {
        Assert.Equal(StatusCodes.Status403Forbidden,
            Status(await FleetManagerEndpoints.FileOutcomeAsync(Request(null, FleetOutcomeStoreTests.Ready()), ResolveTenant, _outcomes)));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Status(FleetManagerEndpoints.ListOutcomes(Request(null), ResolveTenant, _outcomes)));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Status(FleetManagerEndpoints.Digest(Request(null, query: "session=" + FleetManager), ResolveTenant, _outcomes, _preferences, Sources())));
    }

    [Fact]
    public async Task Answer_ClosesTheRecord_ThenASecondAnswerIs409AndChangesNothing()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Decision());

        var first = await FleetManagerEndpoints.AnswerOutcomeAsync(
            Request(TenantA, new { answer = "Stable" }, sessionKey: FleetManager), filed.Id, ResolveTenant, _outcomes);
        var second = await FleetManagerEndpoints.AnswerOutcomeAsync(
            Request(TenantA, new { answer = "Beta" }), filed.Id, ResolveTenant, _outcomes);

        Assert.Equal(StatusCodes.Status200OK, Status(first));
        var answered = Body<FleetOutcomeDto>(first);
        Assert.Equal("answered", answered.Status);
        Assert.Equal(FleetManager, answered.AnsweredBy);
        Assert.True(answered.AnswerMatchedOption);

        Assert.Equal(StatusCodes.Status409Conflict, Status(second));
        Assert.Equal("Stable", _outcomes.Get(TenantA, Guid.Parse(filed.Id))!.Answer);

        // It left the open list, which is what the list route serves by default.
        var open = FleetManagerEndpoints.ListOutcomes(Request(TenantA), ResolveTenant, _outcomes);
        Assert.Equal(0, Field(open, "count"));
        var all = FleetManagerEndpoints.ListOutcomes(Request(TenantA, query: "status=all"), ResolveTenant, _outcomes);
        Assert.Equal(1, Field(all, "count"));
    }

    [Fact]
    public async Task AnotherAccount_ReadAndAnswer_Answer404ExactlyLikeAnUnknownId()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Ready());
        var unknown = Guid.NewGuid().ToString();

        var foreignRead = FleetManagerEndpoints.GetOutcome(Request(TenantB), filed.Id, ResolveTenant, _outcomes);
        var unknownRead = FleetManagerEndpoints.GetOutcome(Request(TenantB), unknown, ResolveTenant, _outcomes);
        var foreignAnswer = await FleetManagerEndpoints.AnswerOutcomeAsync(
            Request(TenantB, new { answer = "Merge" }), filed.Id, ResolveTenant, _outcomes);
        var foreignList = FleetManagerEndpoints.ListOutcomes(Request(TenantB, query: "status=all"), ResolveTenant, _outcomes);

        Assert.Equal(StatusCodes.Status404NotFound, Status(foreignRead));
        Assert.Equal(StatusCodes.Status404NotFound, Status(unknownRead));
        // The same sentence shape, with only the id asked for in it.
        Assert.Equal(((string)Field(unknownRead, "error")!).Replace(unknown, "ID"),
            ((string)Field(foreignRead, "error")!).Replace(filed.Id, "ID"));
        Assert.Equal(StatusCodes.Status404NotFound, Status(foreignAnswer));
        Assert.Equal(0, Field(foreignList, "count"));
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(filed.Id))!.Status);
    }

    [Theory]
    [InlineData("status=closed", "status 'closed' is not valid; use one of: open, answered, all")]
    [InlineData("kind=news", "kind 'news' is not valid; use one of: ready, finding, decision")]
    [InlineData("count=lots", "count 'lots' is not a whole number between 1 and 200")]
    public void ListOutcomes_BadFilter_Is400(string query, string expected)
    {
        var result = FleetManagerEndpoints.ListOutcomes(Request(TenantA, query: query), ResolveTenant, _outcomes);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal(expected, Field(result, "error"));
    }

    // ---- preferences -------------------------------------------------------------------------------------

    [Fact]
    public async Task Preferences_AddListDelete_KeepTheWordsAndStayInTheAccount()
    {
        const string words = "stop asking me about draft posts, just stage them";
        var added = await FleetManagerEndpoints.AddPreferenceAsync(
            Request(TenantA, new { text = words }, sessionKey: FleetManager), ResolveTenant, _preferences);
        Assert.Equal(StatusCodes.Status201Created, Status(added));
        var pref = Body<FleetPreferenceDto>(added);
        Assert.Equal(words, pref.Text);
        Assert.Equal(FleetManager, pref.CreatedBy);

        Assert.Equal(0, Field(FleetManagerEndpoints.ListPreferences(Request(TenantB), ResolveTenant, _preferences), "count"));
        Assert.Equal(StatusCodes.Status404NotFound,
            Status(FleetManagerEndpoints.DeletePreference(Request(TenantB), pref.Id, ResolveTenant, _preferences)));
        Assert.Equal(1, Field(FleetManagerEndpoints.ListPreferences(Request(TenantA), ResolveTenant, _preferences), "count"));

        Assert.Equal(StatusCodes.Status200OK,
            Status(FleetManagerEndpoints.DeletePreference(Request(TenantA), pref.Id, ResolveTenant, _preferences)));
        Assert.Equal(0, Field(FleetManagerEndpoints.ListPreferences(Request(TenantA), ResolveTenant, _preferences), "count"));
    }

    [Fact]
    public async Task Preferences_Blank_Is400()
    {
        var result = await FleetManagerEndpoints.AddPreferenceAsync(Request(TenantA, new { text = "  " }), ResolveTenant, _preferences);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
    }

    // ---- digest ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Digest_CarriesOwnedSessionsOnly_WithTheirFoldedStateAndStoredVerdict()
    {
        SeedFleet();
        _verdicts.Store(TenantA, OwnedStopped, Verdict("verdict-docs"));

        var result = FleetManagerEndpoints.Digest(
            Request(TenantA, query: "session=" + FleetManager, sessionKey: FleetManager),
            ResolveTenant, _outcomes, _preferences, Sources());

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var digest = Body<FleetDigestDto>(result);
        Assert.Equal(FleetManager, digest.SessionId);
        Assert.True(digest.IsFleetManager);
        Assert.False(digest.VerdictsWithheld);

        // Owned only: not the owner's own session, not the exited one, not another account's that names the
        // same controlling id. Oldest first.
        Assert.Equal(new[] { OwnedWorking, OwnedStopped }, digest.OwnedSessions.Select(s => s.SessionId));

        var working = digest.OwnedSessions[0];
        Assert.Equal("Product repository - fix the flaky roster test, second attempt", working.Name);
        Assert.Equal("working", working.State);
        Assert.Equal("Working", working.StateLabel);
        Assert.Equal("Roster", working.MissionName);
        Assert.Equal(3, working.UncommittedCount);
        Assert.Null(working.TurnVerdict);

        var stopped = digest.OwnedSessions[1];
        Assert.Equal("stopped", stopped.State);
        Assert.NotNull(stopped.TurnVerdict);
        Assert.Equal("verdict-docs", stopped.TurnVerdict!.VerdictId);
        Assert.Equal("Shall I publish the change now?", stopped.TurnVerdict.Evidence);

        Assert.Equal(2, digest.OwnedSessionCounts.Total);
        Assert.Equal(1, digest.OwnedSessionCounts.Working);
        Assert.Equal(1, digest.OwnedSessionCounts.Stopped);
        Assert.Equal(0, digest.OwnedSessionCounts.NeedsYou);
    }

    [Fact]
    public async Task Digest_CarriesOpenRecordsOnly_AndThePreferences_AndCounts()
    {
        SeedFleet();
        var ready = await FileAsync(TenantA, FleetOutcomeStoreTests.Ready());
        var decision = await FileAsync(TenantA, FleetOutcomeStoreTests.Decision());
        var answered = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());
        _outcomes.Answer(TenantA, Guid.Parse(answered.Id), "Thanks.", "owner", DateTime.UtcNow);
        await FileAsync(TenantB, FleetOutcomeStoreTests.Finding("another account's"));
        _preferences.Add(TenantA, "merge docs changes on green", "owner", DateTime.UtcNow);

        var digest = Body<FleetDigestDto>(FleetManagerEndpoints.Digest(
            Request(TenantA, query: "session=" + FleetManager), ResolveTenant, _outcomes, _preferences, Sources()));

        Assert.Equal(new[] { decision.Id, ready.Id }.OrderBy(x => x), digest.Outcomes.Select(o => o.Id).OrderBy(x => x));
        Assert.All(digest.Outcomes, o => Assert.Equal("open", o.Status));
        Assert.Equal(2, digest.OutcomeCounts.Total);
        Assert.Equal(1, digest.OutcomeCounts.Ready);
        Assert.Equal(1, digest.OutcomeCounts.Decision);
        Assert.Equal(0, digest.OutcomeCounts.Finding);
        Assert.Equal("merge docs changes on green", Assert.Single(digest.Preferences).Text);
    }

    [Fact]
    public void Digest_ForAPlainSession_SaysItIsNotTheFleetManager()
    {
        SeedFleet();

        var digest = Body<FleetDigestDto>(FleetManagerEndpoints.Digest(
            Request(TenantA, query: "session=" + NotOwned), ResolveTenant, _outcomes, _preferences, Sources()));

        Assert.False(digest.IsFleetManager);
        Assert.Empty(digest.OwnedSessions);
        Assert.Equal(0, digest.OwnedSessionCounts.Total);
    }

    [Fact]
    public void Digest_SessionOfAnotherAccountOrUnknown_Is404()
    {
        SeedFleet();

        var foreign = FleetManagerEndpoints.Digest(
            Request(TenantA, query: "session=" + OtherAccountSession), ResolveTenant, _outcomes, _preferences, Sources());
        var unknown = FleetManagerEndpoints.Digest(
            Request(TenantA, query: "session=" + Guid.NewGuid()), ResolveTenant, _outcomes, _preferences, Sources());
        // And the other account asking about THIS account's Fleet Manager.
        var reverse = FleetManagerEndpoints.Digest(
            Request(TenantB, query: "session=" + FleetManager), ResolveTenant, _outcomes, _preferences, Sources());

        Assert.Equal(StatusCodes.Status404NotFound, Status(foreign));
        Assert.Equal(StatusCodes.Status404NotFound, Status(unknown));
        Assert.Equal(StatusCodes.Status404NotFound, Status(reverse));
    }

    [Theory]
    [InlineData("", "session is required")]
    [InlineData("session=abc", "session 'abc' is not a session id")]
    public void Digest_MissingOrMalformedSession_Is400(string query, string expected)
    {
        var result = FleetManagerEndpoints.Digest(Request(TenantA, query: query), ResolveTenant, _outcomes, _preferences, Sources());

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.StartsWith(expected, (string)Field(result, "error")!);
    }

    [Fact]
    public void Digest_ShadowAccount_WithholdsVerdictsFromASessionKeyButNotFromADevice()
    {
        SeedFleet();
        _verdicts.Store(TenantA, OwnedStopped, Verdict("verdict-shadow"));
        _colourOn = false;

        var asSession = Body<FleetDigestDto>(FleetManagerEndpoints.Digest(
            Request(TenantA, query: "session=" + FleetManager, sessionKey: FleetManager),
            ResolveTenant, _outcomes, _preferences, Sources()));
        var asDevice = Body<FleetDigestDto>(FleetManagerEndpoints.Digest(
            Request(TenantA, query: "session=" + FleetManager), ResolveTenant, _outcomes, _preferences, Sources()));

        Assert.True(asSession.VerdictsWithheld);
        Assert.NotNull(asSession.VerdictsWithheldReason);
        Assert.All(asSession.OwnedSessions, s => Assert.Null(s.TurnVerdict));
        Assert.False(asDevice.VerdictsWithheld);
        Assert.Equal("verdict-shadow", asDevice.OwnedSessions.Single(s => s.SessionId == OwnedStopped).TurnVerdict!.VerdictId);
    }
}
