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
/// stores, a real pushed-session store, a real Director registry and the real roster fold. The account is
/// resolved by the delegate the host passes in; here it is read off a test header, so two accounts can be
/// exercised side by side, and the caller's credential is stamped on the request the way the middleware stamps
/// it. <c>FleetManagerRoutesHostTests</c> (Gateway.Tests) proves the same rules through the booted host.
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
    private static readonly string FormerFleetManager = "10000000-0000-4000-8000-000000000006";
    private static readonly string OwnedByFormer = "10000000-0000-4000-8000-000000000007";
    private static readonly string OtherAccountSession = "20000000-0000-4000-8000-000000000001";

    /// <summary>The caller shapes the middleware can admit.</summary>
    private const string Owner = "owner";
    private const string DirectorKey = "director";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-fm-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new();
    private readonly FleetOutcomeStore _outcomes;
    private readonly FleetPreferenceStore _preferences;
    private readonly TurnVerdictStore _verdicts;
    private readonly FleetManagerMarkHistory _marks;
    private readonly Dictionary<TenantId, string?> _marked = new() { [TenantA] = FleetManager, [TenantB] = null };
    private readonly FleetManagerEventStore _events;

    public FleetManagerEndpointsTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        _outcomes = new FleetOutcomeStore(_harness.Open());
        _preferences = new FleetPreferenceStore(_harness.Open());
        _verdicts = new TurnVerdictStore(_harness.Open());
        _marks = new FleetManagerMarkHistory(_harness.Open());
        _events = new FleetManagerEventStore(_harness.Open());
        SeedFleet();
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

    /// <summary>A request as the middleware would hand it on: <paramref name="caller"/> is a session id (a session
    /// key), <see cref="Owner"/> (a browser device key), <see cref="DirectorKey"/> (a Director's own key), or null.</summary>
    private static DefaultHttpContext Request(TenantId? tenant, string? caller, object? body = null, string query = "")
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
        switch (caller)
        {
            case null:
                break;
            case Owner:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "browser";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("dev-owner", tenant?.Value, "browser", "active");
                break;
            case DirectorKey:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "director-key";
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = "workstation";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
                    new DeviceCredentialIdentity("dev-director", tenant?.Value, "workstation", "active");
                break;
            default:
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "session-key";
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                    new SessionCredentialIdentity(Guid.Parse(caller), tenant ?? TenantId.Local, "director-a");
                break;
        }
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

    private FleetManagerAccess Access() => new(
        MarkedSessionId: tenant => _marked.TryGetValue(tenant, out var m) ? m : null,
        LastKnownSession: (tenant, sid) => GatewayEndpoints.LastKnownSession(_registry, _pushed, tenant, sid));

    private FleetDigestSources Sources() => new(
        FoldedRoster: tenant => GatewayEndpoints.FoldedAccountRoster(_registry, _pushed, tenant, null, null, null, null),
        SessionInAccount: (tenant, sid) => _pushed.TryLocateIgnoringFreshness(tenant, sid) is not null,
        LatestVerdict: (tenant, sid) => _verdicts.Latest(tenant, sid),
        FormerFleetManagers: tenant => _marks.List(tenant).Select(m => m.SessionId).ToList());

    private IResult Digest(TenantId? tenant, string? caller, string session)
        => FleetManagerEndpoints.Digest(Request(tenant, caller, query: "session=" + session),
            ResolveTenant, Access(), _outcomes, _preferences, Sources(), _events);

    private async Task<FleetOutcomeDto> FileAsync(TenantId tenant, FleetOutcomeFileRequest request, string caller = FleetManager)
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(Request(tenant, caller, request), ResolveTenant, Access(), _outcomes);
        Assert.Equal(StatusCodes.Status201Created, Status(result));
        return Body<FleetOutcomeDto>(result);
    }

    private async Task<IResult> AnswerAsync(TenantId tenant, string caller, string id, string words)
        => await FleetManagerEndpoints.AnswerOutcomeAsync(Request(tenant, caller, new { answer = words }), id,
            ResolveTenant, Access(), _outcomes);

    private void SeedFleet()
    {
        var now = DateTime.UtcNow;
        _registry.RegisterFromStream("director-a", "MACHINE_A", "someone", "1.0", pid: 11, startedAt: now, tenant: TenantA);
        _registry.RegisterFromStream("director-b", "MACHINE_B", "someone", "1.0", pid: 12, startedAt: now, tenant: TenantB);

        _pushed.RegisterConnection(TenantA, "director-a", "conn-a");
        Assert.True(_pushed.ApplySnapshot(TenantA, "director-a", "conn-a", 1, new List<SessionDto>
        {
            new() { SessionId = FleetManager, Name = "Fleet Manager", ActivityState = "WaitingForInput",
                    CreatedAt = now.AddHours(-2), LastActivityAt = now },
            new() { SessionId = FormerFleetManager, Name = "Fleet Manager before the reset", ActivityState = "Exited",
                    CreatedAt = now.AddHours(-5) },
            new() { SessionId = OwnedByFormer, Name = "Started by the Fleet Manager before the reset",
                    ActivityState = "Working", IsControlled = true, ControllerSessionId = FormerFleetManager,
                    CreatedAt = now.AddHours(-3), LastActivityAt = now },
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

    // ---- who may call --------------------------------------------------------------------------------------

    /// <summary>One call per route, by <paramref name="caller"/>, in account A.</summary>
    private async Task<IResult> CallAsync(string route, string? caller)
    {
        var existing = _outcomes.File(TenantA, FleetOutcomeStoreTests.Ready(), FleetManager, DateTime.UtcNow);
        var pref = _preferences.Add(TenantA, "merge docs changes on green", "owner", DateTime.UtcNow);
        return route switch
        {
            "file" => await FleetManagerEndpoints.FileOutcomeAsync(
                Request(TenantA, caller, FleetOutcomeStoreTests.Finding()), ResolveTenant, Access(), _outcomes),
            "list" => FleetManagerEndpoints.ListOutcomes(Request(TenantA, caller), ResolveTenant, Access(), _outcomes),
            "read" => FleetManagerEndpoints.GetOutcome(Request(TenantA, caller), existing.Id, ResolveTenant, Access(), _outcomes),
            "answer" => await AnswerAsync(TenantA, caller!, existing.Id, "Merge it."),
            "preferences" => FleetManagerEndpoints.ListPreferences(Request(TenantA, caller), ResolveTenant, Access(), _preferences),
            "prefer" => await FleetManagerEndpoints.AddPreferenceAsync(
                Request(TenantA, caller, new { text = "never merge on red" }), ResolveTenant, Access(), _preferences),
            "forget" => FleetManagerEndpoints.DeletePreference(Request(TenantA, caller), pref.Id, ResolveTenant, Access(), _preferences),
            "digest" => Digest(TenantA, caller, caller is not null && Guid.TryParse(caller, out _) ? caller : FleetManager),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };
    }

    public static TheoryData<string> Routes => new() { "file", "list", "read", "answer", "preferences", "prefer", "forget", "digest" };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_TheMarkedFleetManager_IsAllowed(string route)
    {
        var result = await CallAsync(route, FleetManager);

        Assert.InRange(Status(result), 200, 201);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_AnotherSessionOfTheAccount_IsRefusedAndToldWhy(string route)
    {
        var result = await CallAsync(route, NotOwned);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Equal("not_fleet_manager", Field(result, "code"));
        Assert.Contains($"only this account's Fleet Manager session ({FleetManager}) may ", (string)Field(result, "error")!);
        Assert.Contains($"session {NotOwned} is not it", (string)Field(result, "error")!);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_ADirectorKey_IsRefused(string route)
    {
        var result = await CallAsync(route, DirectorKey);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Contains("the owner on their own signed-in phone or browser", (string)Field(result, "error")!);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("read")]
    [InlineData("answer")]
    [InlineData("preferences")]
    [InlineData("prefer")]
    [InlineData("forget")]
    [InlineData("digest")]
    public async Task EveryRouteButFiling_TheOwnersDevice_IsAllowed(string route)
    {
        var result = await CallAsync(route, Owner);

        Assert.InRange(Status(result), 200, 201);
    }

    [Fact]
    public async Task FileOutcome_TheOwnersDevice_IsRefusedAndNothingIsStored()
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, Owner, FleetOutcomeStoreTests.Finding()), ResolveTenant, Access(), _outcomes);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.StartsWith("the owner does not file records", (string)Field(result, "error")!);
        Assert.Empty(_outcomes.List(TenantA, "all", null, 50));
    }

    [Fact]
    public async Task FileOutcome_NoFleetManagerMarked_IsRefusedNamingHowToMarkOne()
    {
        _marked[TenantA] = null;

        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, FleetManager, FleetOutcomeStoreTests.Ready()), ResolveTenant, Access(), _outcomes);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Contains("this account has no Fleet Manager marked", (string)Field(result, "error")!);
        Assert.Contains("cc-devthrottle fleet-manager set", (string)Field(result, "error")!);
        Assert.Empty(_outcomes.List(TenantA, "all", null, 50));
    }

    [Fact]
    public async Task FileOutcome_MarkedSessionThatIsItselfOwned_IsRefused()
    {
        // The account marked a session that another session controls - a Worker, not a Fleet Manager.
        _marked[TenantA] = OwnedWorking;

        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, OwnedWorking, FleetOutcomeStoreTests.Ready()), ResolveTenant, Access(), _outcomes);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Contains($"is owned by session {FleetManager}", (string)Field(result, "error")!);
    }

    [Fact]
    public async Task FileOutcome_MarkedSessionNoDirectorReported_IsRefused()
    {
        var unreported = Guid.NewGuid().ToString();
        _marked[TenantA] = unreported;

        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, unreported, FleetOutcomeStoreTests.Ready()), ResolveTenant, Access(), _outcomes);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Contains("no Director of this account has reported it", (string)Field(result, "error")!);
    }

    [Fact]
    public async Task Digest_TheFleetManagerAsksForAnotherSession_IsRefused()
    {
        var result = Digest(TenantA, FleetManager, NotOwned);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.StartsWith("the Fleet Manager reads its own digest only", (string)Field(result, "error")!);
    }

    // ---- outcomes ----------------------------------------------------------------------------------------

    [Fact]
    public async Task FileOutcome_FleetManager_Returns201_FiledByTheCallingSession()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Ready());

        Assert.Equal(FleetManager, filed.FiledBy);
        Assert.Equal("open", filed.Status);
        Assert.Equal("ready", filed.Kind);
    }

    [Fact]
    public async Task FileOutcome_BadKind_Is400NamingTheValidKinds()
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, FleetManager, new { kind = "update", title = "Something" }), ResolveTenant, Access(), _outcomes);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("kind 'update' is not valid; use one of: ready, finding, decision", Field(result, "error"));
        Assert.Empty(_outcomes.List(TenantA, "all", null, 50));
    }

    [Fact]
    public async Task FileOutcome_MissingRequiredField_Is400()
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, FleetManager,
                new { kind = "decision", title = "Pick one", decision = new { question = "Which?", options = new[] { "A" } } }),
            ResolveTenant, Access(), _outcomes);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("decision.options needs at least two options, got 1", Field(result, "error"));
    }

    [Fact]
    public async Task FileOutcome_BrokenJson_Is400()
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, FleetManager, "{ not json"), ResolveTenant, Access(), _outcomes);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
    }

    [Fact]
    public async Task AnyRoute_NoAccount_Is403()
    {
        Assert.Equal(StatusCodes.Status403Forbidden,
            Status(await FleetManagerEndpoints.FileOutcomeAsync(
                Request(null, FleetManager, FleetOutcomeStoreTests.Ready()), ResolveTenant, Access(), _outcomes)));
        Assert.Equal(StatusCodes.Status403Forbidden,
            Status(FleetManagerEndpoints.ListOutcomes(Request(null, Owner), ResolveTenant, Access(), _outcomes)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(Digest(null, Owner, FleetManager)));
    }

    [Fact]
    public async Task Answer_ByTheOwner_ClosesTheRecordWithTheOwnersRole_ThenASecondAnswerIs409AndChangesNothing()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Decision());

        var first = await AnswerAsync(TenantA, Owner, filed.Id, "Stable");
        var second = await AnswerAsync(TenantA, FleetManager, filed.Id, "Beta");

        Assert.Equal(StatusCodes.Status200OK, Status(first));
        var answered = Body<FleetOutcomeDto>(first);
        Assert.Equal("answered", answered.Status);
        Assert.Equal("owner", answered.AnsweredBy);
        Assert.Equal("owner", answered.AnsweredByRole);
        Assert.True(answered.AnswerMatchedOption);

        Assert.Equal(StatusCodes.Status409Conflict, Status(second));
        Assert.Equal("already_answered", Field(second, "code"));
        var stored = _outcomes.Get(TenantA, Guid.Parse(filed.Id))!;
        Assert.Equal("Stable", stored.Answer);
        Assert.Equal("owner", stored.AnsweredByRole);

        // It left the open list, which is what the list route serves by default.
        var open = FleetManagerEndpoints.ListOutcomes(Request(TenantA, Owner), ResolveTenant, Access(), _outcomes);
        Assert.Equal(0, Field(open, "count"));
        Assert.Equal(0, Field(open, "total"));
        var all = FleetManagerEndpoints.ListOutcomes(Request(TenantA, Owner, query: "status=all"), ResolveTenant, Access(), _outcomes);
        Assert.Equal(1, Field(all, "count"));
    }

    [Fact]
    public async Task Answer_ByTheFleetManager_CarriesTheFleetManagerRole()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());

        var answered = Body<FleetOutcomeDto>(await AnswerAsync(TenantA, FleetManager, filed.Id, "Thanks, noted."));

        Assert.Equal(FleetManager, answered.AnsweredBy);
        Assert.Equal("fleet-manager", answered.AnsweredByRole);
    }

    [Fact]
    public async Task AnotherAccount_ReadAndAnswer_Answer404ExactlyLikeAnUnknownId()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Ready());
        var unknown = Guid.NewGuid().ToString();

        var foreignRead = FleetManagerEndpoints.GetOutcome(Request(TenantB, Owner), filed.Id, ResolveTenant, Access(), _outcomes);
        var unknownRead = FleetManagerEndpoints.GetOutcome(Request(TenantB, Owner), unknown, ResolveTenant, Access(), _outcomes);
        var foreignAnswer = await AnswerAsync(TenantB, Owner, filed.Id, "Merge");
        var foreignList = FleetManagerEndpoints.ListOutcomes(Request(TenantB, Owner, query: "status=all"), ResolveTenant, Access(), _outcomes);

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
        var result = FleetManagerEndpoints.ListOutcomes(Request(TenantA, FleetManager, query: query), ResolveTenant, Access(), _outcomes);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal(expected, Field(result, "error"));
    }

    [Fact]
    public async Task ListOutcomes_OnePage_SaysHowManyThereAreInAll()
    {
        for (var i = 0; i < 3; i++) await FileAsync(TenantA, FleetOutcomeStoreTests.Ready($"Ready {i}"));
        await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());

        var page = FleetManagerEndpoints.ListOutcomes(
            Request(TenantA, FleetManager, query: "kind=ready&count=2"), ResolveTenant, Access(), _outcomes);

        Assert.Equal(2, Field(page, "count"));
        Assert.Equal(3, Field(page, "total"));
    }

    [Fact]
    public async Task ListOutcomes_TheCursorContinuesAfterThePage_AndSaysWhenNoneRemain()
    {
        for (var i = 0; i < 3; i++) await FileAsync(TenantA, FleetOutcomeStoreTests.Ready($"Ready {i}"));

        var first = FleetManagerEndpoints.ListOutcomes(
            Request(TenantA, FleetManager, query: "count=2"), ResolveTenant, Access(), _outcomes);
        Assert.Equal(true, Field(first, "hasMore"));
        var cursor = Assert.IsType<string>(Field(first, "nextCursor"));

        var second = FleetManagerEndpoints.ListOutcomes(
            Request(TenantA, FleetManager, query: "count=2&cursor=" + Uri.EscapeDataString(cursor)), ResolveTenant, Access(), _outcomes);
        Assert.Equal(1, Field(second, "count"));
        Assert.Equal(3, Field(second, "total"));
        Assert.Equal(false, Field(second, "hasMore"));
        Assert.Null(Field(second, "nextCursor"));
    }

    [Theory]
    [InlineData("cursor=", "cursor is empty; give the nextCursor of the page before, or leave cursor out to start from the newest")]
    [InlineData("cursor=bogus", "cursor 'bogus' is not one this Gateway issued; list again without a cursor to start from the newest")]
    public void ListOutcomes_BadCursor_Is400(string query, string expected)
    {
        var result = FleetManagerEndpoints.ListOutcomes(Request(TenantA, FleetManager, query: query), ResolveTenant, Access(), _outcomes);
        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal(expected, Field(result, "error"));
    }

    // ---- preferences -------------------------------------------------------------------------------------

    [Fact]
    public async Task Preferences_AddListDelete_KeepTheWordsAndStayInTheAccount()
    {
        const string words = "stop asking me about draft posts, just stage them";
        var added = await FleetManagerEndpoints.AddPreferenceAsync(
            Request(TenantA, FleetManager, new { text = words }), ResolveTenant, Access(), _preferences);
        Assert.Equal(StatusCodes.Status201Created, Status(added));
        var pref = Body<FleetPreferenceDto>(added);
        Assert.Equal(words, pref.Text);
        Assert.Equal(FleetManager, pref.CreatedBy);

        Assert.Equal(0, Field(FleetManagerEndpoints.ListPreferences(Request(TenantB, Owner), ResolveTenant, Access(), _preferences), "count"));
        Assert.Equal(StatusCodes.Status404NotFound,
            Status(FleetManagerEndpoints.DeletePreference(Request(TenantB, Owner), pref.Id, ResolveTenant, Access(), _preferences)));
        Assert.Equal(1, Field(FleetManagerEndpoints.ListPreferences(Request(TenantA, Owner), ResolveTenant, Access(), _preferences), "count"));

        Assert.Equal(StatusCodes.Status200OK,
            Status(FleetManagerEndpoints.DeletePreference(Request(TenantA, Owner), pref.Id, ResolveTenant, Access(), _preferences)));
        Assert.Equal(0, Field(FleetManagerEndpoints.ListPreferences(Request(TenantA, FleetManager), ResolveTenant, Access(), _preferences), "count"));
    }

    [Fact]
    public async Task Preferences_ByTheOwner_AreKeptAsTheOwners()
    {
        var added = await FleetManagerEndpoints.AddPreferenceAsync(
            Request(TenantA, Owner, new { text = "merge docs on green" }), ResolveTenant, Access(), _preferences);

        Assert.Equal("owner", Body<FleetPreferenceDto>(added).CreatedBy);
    }

    [Fact]
    public async Task Preferences_Blank_Is400()
    {
        var result = await FleetManagerEndpoints.AddPreferenceAsync(
            Request(TenantA, FleetManager, new { text = "  " }), ResolveTenant, Access(), _preferences);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
    }

    // ---- digest ------------------------------------------------------------------------------------------

    [Fact]
    public void Digest_CarriesOwnedSessionsOnly_WithTheirFoldedStateAndStoredVerdict()
    {
        _verdicts.Store(TenantA, OwnedStopped, Verdict("verdict-docs"));

        var result = Digest(TenantA, FleetManager, FleetManager);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var digest = Body<FleetDigestDto>(result);
        Assert.Equal(FleetManager, digest.SessionId);
        Assert.True(digest.IsFleetManager);
        Assert.Equal(FleetManager, digest.FleetManagerSessionId);
        Assert.Equal(new[] { FleetManager }, digest.FleetManagerSessionIds);

        // Owned only: not the owner's own session, not the exited one, not another account's that names the
        // same controlling id, and not the earlier Fleet Manager's while this account never marked it. Oldest first.
        Assert.Equal(new[] { OwnedWorking, OwnedStopped }, digest.OwnedSessions.Select(s => s.SessionId));
        Assert.All(digest.OwnedSessions, s => Assert.Equal(FleetManager, s.OwnerSessionId));

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

    /// <summary>
    /// A REPLACEMENT FLEET MANAGER STILL SEES WHAT THE OLD ONE STARTED. The account marked the former session
    /// first and the current one after it; the former one's session is still controlled by the former id (no hand
    /// over has happened), and the digest lists it with that owner.
    /// </summary>
    [Fact]
    public void Digest_AfterAReplacement_CarriesTheEarlierFleetManagersSessions_WithTheirOwner()
    {
        _marks.Record(TenantA, FormerFleetManager, DateTime.UtcNow.AddHours(-5));
        _marks.Record(TenantA, FleetManager, DateTime.UtcNow.AddHours(-2));
        _verdicts.Store(TenantA, OwnedByFormer, Verdict("verdict-former"));

        var digest = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));

        Assert.Equal(new[] { FormerFleetManager, FleetManager }, digest.FleetManagerSessionIds);
        Assert.Equal(new[] { OwnedByFormer, OwnedWorking, OwnedStopped }, digest.OwnedSessions.Select(s => s.SessionId));
        var former = digest.OwnedSessions[0];
        Assert.Equal(FormerFleetManager, former.OwnerSessionId);
        Assert.Equal("verdict-former", former.TurnVerdict!.VerdictId);
        Assert.Equal(FleetManager, digest.OwnedSessions[1].OwnerSessionId);
        Assert.Equal(3, digest.OwnedSessionCounts.Total);
    }

    /// <summary>
    /// AN EARLIER FLEET MANAGER IS NAMED ONLY WHILE IT STILL CONTROLS A LIVE SESSION. One that controls nothing is
    /// left out of the list; one that controls a live session stays in it; the current mark is always in it.
    /// </summary>
    [Fact]
    public void Digest_AnEarlierFleetManagerControllingNothingLive_IsNotNamed()
    {
        var controlsNothing = "10000000-0000-4000-8000-000000000099";
        _marks.Record(TenantA, controlsNothing, DateTime.UtcNow.AddHours(-9));
        _marks.Record(TenantA, OwnedExited, DateTime.UtcNow.AddHours(-8));
        _marks.Record(TenantA, FormerFleetManager, DateTime.UtcNow.AddHours(-5));
        _marks.Record(TenantA, FleetManager, DateTime.UtcNow.AddHours(-2));

        var digest = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));

        Assert.Equal(new[] { FormerFleetManager, FleetManager }, digest.FleetManagerSessionIds);
        Assert.Equal(new[] { OwnedByFormer, OwnedWorking, OwnedStopped }, digest.OwnedSessions.Select(s => s.SessionId));
    }

    [Fact]
    public async Task Digest_CarriesOpenRecordsOnly_AndThePreferences_AndCounts()
    {
        var ready = await FileAsync(TenantA, FleetOutcomeStoreTests.Ready());
        var decision = await FileAsync(TenantA, FleetOutcomeStoreTests.Decision());
        var finding = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding("the open one"));
        var answered = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());
        _outcomes.Answer(TenantA, Guid.Parse(answered.Id), "Thanks.", "owner", FleetOutcomeStore.RoleOwner, DateTime.UtcNow);
        _preferences.Add(TenantA, "merge docs changes on green", "owner", DateTime.UtcNow);

        var digest = Body<FleetDigestDto>(Digest(TenantA, Owner, FleetManager));

        Assert.Equal(new[] { decision.Id, ready.Id, finding.Id }.OrderBy(x => x), digest.Outcomes.Select(o => o.Id).OrderBy(x => x));
        Assert.All(digest.Outcomes, o => Assert.Equal("open", o.Status));
        Assert.Equal(3, digest.OutcomeCounts.Total);
        Assert.Equal(1, digest.OutcomeCounts.Ready);
        Assert.Equal(1, digest.OutcomeCounts.Decision);
        Assert.Equal(1, digest.OutcomeCounts.Finding);
        Assert.Equal("merge docs changes on green", Assert.Single(digest.Preferences).Text);
    }

    /// <summary>NOTHING IS CUT SHORT: more open records than one list page holds are all in the digest, and the
    /// counts say the same number.</summary>
    [Fact]
    public void Digest_MoreOpenRecordsThanAPage_CarriesEveryOne()
    {
        var over = FleetOutcomeStore.MaxCount + 1;
        for (var i = 0; i < over; i++)
            _outcomes.File(TenantA, FleetOutcomeStoreTests.Ready($"Ready {i}"), FleetManager, DateTime.UtcNow);
        _outcomes.File(TenantA, FleetOutcomeStoreTests.Finding(), FleetManager, DateTime.UtcNow);

        var digest = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));

        Assert.Equal(over + 1, digest.Outcomes.Count);
        Assert.Equal(over + 1, digest.OutcomeCounts.Total);
        Assert.Equal(over, digest.OutcomeCounts.Ready);
        Assert.Equal(1, digest.OutcomeCounts.Finding);
    }

    [Fact]
    public void Digest_TheOwnerAsksForAPlainSession_SaysItIsNotTheFleetManager()
    {
        var digest = Body<FleetDigestDto>(Digest(TenantA, Owner, NotOwned));

        Assert.False(digest.IsFleetManager);
        Assert.Equal(FleetManager, digest.FleetManagerSessionId);
    }

    [Fact]
    public void Digest_SessionOfAnotherAccountOrUnknown_Is404()
    {
        var foreign = Digest(TenantA, Owner, OtherAccountSession);
        var unknown = Digest(TenantA, Owner, Guid.NewGuid().ToString());
        // And the other account's owner asking about THIS account's Fleet Manager.
        var reverse = Digest(TenantB, Owner, FleetManager);

        Assert.Equal(StatusCodes.Status404NotFound, Status(foreign));
        Assert.Equal(StatusCodes.Status404NotFound, Status(unknown));
        Assert.Equal(StatusCodes.Status404NotFound, Status(reverse));
    }

    [Theory]
    [InlineData("", "session is required")]
    [InlineData("session=abc", "session 'abc' is not a session id")]
    public void Digest_MissingOrMalformedSession_Is400(string query, string expected)
    {
        var result = FleetManagerEndpoints.Digest(Request(TenantA, Owner, query: query),
            ResolveTenant, Access(), _outcomes, _preferences, Sources(), _events);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.StartsWith(expected, (string)Field(result, "error")!);
    }

    // ---- events (step 4) ---------------------------------------------------------------------------------

    private FleetManagerEventDto Stop(TenantId tenant, string sid, TurnVerdictDto? verdict = null)
        => _events.Enqueue(tenant, new FleetManagerEventDraft(FleetManagerEventStore.KindStop, sid, "a session " + sid,
            FleetManager, Verdict: verdict, NoVerdictReason: verdict is null ? "this account's Wingman judge switch is off" : null),
            DateTime.UtcNow)!;

    private FleetManagerEventListDto Events(TenantId tenant, string query = "", string? caller = Owner)
    {
        var result = FleetManagerEndpoints.ListEvents(Request(tenant, caller, query: query), ResolveTenant, _events);
        Assert.Equal(StatusCodes.Status200OK, Status(result));
        return Body<FleetManagerEventListDto>(result);
    }

    private async Task<IResult> AckAsync(TenantId tenant, object body)
        => await FleetManagerEndpoints.AcknowledgeEventsAsync(Request(tenant, FleetManager, body), ResolveTenant, _events);

    [Fact]
    public async Task Ack_RemovesTheEventFromTheDigestAndFromTheDefaultList_ButAllStillShowsIt()
    {
        var first = Stop(TenantA, OwnedStopped, Verdict("verdict-a"));
        var second = Stop(TenantA, OwnedWorking);

        var before = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));
        Assert.Equal(new[] { first.Id, second.Id }, before.Events.Select(e => e.Id));
        Assert.Equal("Shall I publish the change now?", before.Events[0].Verdict!.Evidence);

        var result = await AckAsync(TenantA, new { ids = new[] { first.Id } });

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var ack = Body<FleetManagerEventAckDto>(result);
        Assert.Equal(1, ack.Acknowledged);
        Assert.Equal(new[] { first.Id }, ack.Ids);
        var after = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));
        Assert.Equal(new[] { second.Id }, after.Events.Select(e => e.Id));
        Assert.Equal(new[] { second.Id }, Events(TenantA).Events.Select(e => e.Id));
        var all = Events(TenantA, "status=all");
        Assert.Equal(2, all.Count);
        Assert.NotNull(all.Events.Single(e => e.Id == first.Id).AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Ack_All_AcknowledgesEveryOpenEvent_AndCountsTheOnesAlreadyDone()
    {
        Stop(TenantA, OwnedStopped);
        Stop(TenantA, OwnedWorking);
        Stop(TenantB, OtherAccountSession);

        var ack = Body<FleetManagerEventAckDto>(await AckAsync(TenantA, new { all = true }));

        Assert.Equal(2, ack.Acknowledged);
        Assert.Equal(0, Events(TenantA).Count);
        Assert.Equal(1, Events(TenantB).Count);
    }

    [Fact]
    public async Task AnotherAccount_CannotReadOrAcknowledge_AndAMixedAckChangesNothing()
    {
        var mine = Stop(TenantA, OwnedStopped, Verdict("verdict-mine"));

        Assert.Equal(0, Events(TenantB, "status=all").Count);

        var foreign = await AckAsync(TenantB, new { ids = new[] { mine.Id } });
        Assert.Equal(StatusCodes.Status404NotFound, Status(foreign));
        Assert.Contains(mine.Id, (string)Field(foreign, "error")!);

        // One good id and one unknown: refused by name, and the good one is NOT acknowledged.
        var unknown = Guid.NewGuid().ToString();
        var mixed = await AckAsync(TenantA, new { ids = new[] { mine.Id, unknown } });
        Assert.Equal(StatusCodes.Status404NotFound, Status(mixed));
        Assert.Contains(unknown, (string)Field(mixed, "error")!);
        Assert.Equal(new[] { mine.Id }, Events(TenantA).Events.Select(e => e.Id));
    }

    [Theory]
    [InlineData("{}", "give the event ids to acknowledge, or all: true")]
    [InlineData("{\"ids\":[\"abc\"]}", "'abc' is not an id")]
    [InlineData("{\"all\":true,\"ids\":[\"5b1c2d3e-0000-4000-8000-000000000001\"]}", "give either ids or all, not both")]
    public async Task Ack_BadBody_Is400(string body, string expected)
    {
        var result = await AckAsync(TenantA, body);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.StartsWith(expected, (string)Field(result, "error")!);
    }

    [Fact]
    public void ListEvents_BadStatus_Is400NamingTheValidValues()
    {
        var result = FleetManagerEndpoints.ListEvents(Request(TenantA, Owner, query: "status=open"), ResolveTenant, _events);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("status 'open' is not valid; use one of: unacknowledged, all", Field(result, "error"));
    }

    /// <summary>The reading is served to the Fleet Manager whatever the colour switch says, as the digest is.</summary>
    [Fact]
    public void ListEvents_TheFleetManager_IsServedTheReading()
    {
        Stop(TenantA, OwnedStopped, Verdict("verdict-event"));

        var asSession = Assert.Single(Events(TenantA, caller: FleetManager).Events);

        Assert.Equal("verdict-event", asSession.Verdict!.VerdictId);
    }
}
