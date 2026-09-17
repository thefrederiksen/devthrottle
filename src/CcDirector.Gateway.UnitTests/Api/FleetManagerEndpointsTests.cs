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
    private const string NotOwnedConst = "10000000-0000-4000-8000-000000000004";
    private static readonly string NotOwned = NotOwnedConst;
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
    private readonly Dictionary<TenantId, string> _deliveryNotes = new();

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
        FormerFleetManagers: tenant => _marks.List(tenant).Select(m => m.SessionId).ToList(),
        EventsDeliveryNote: tenant => _deliveryNotes.TryGetValue(tenant, out var note) ? note : null);

    private IResult Digest(TenantId? tenant, string? caller, string session)
        => FleetManagerEndpoints.Digest(Request(tenant, caller, query: "session=" + session),
            ResolveTenant, Access(), _outcomes, _preferences, Sources(), _events);

    private async Task<FleetOutcomeDto> FileAsync(TenantId tenant, FleetOutcomeFileRequest request, string caller = FleetManager)
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(Request(tenant, caller, request), ResolveTenant, Access(), _outcomes, Sources());
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
                Request(TenantA, caller, FleetOutcomeStoreTests.Finding()), ResolveTenant, Access(), _outcomes, Sources()),
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
            Request(TenantA, Owner, FleetOutcomeStoreTests.Finding()), ResolveTenant, Access(), _outcomes, Sources());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.StartsWith("the owner does not file records", (string)Field(result, "error")!);
        Assert.Empty(_outcomes.List(TenantA, "all", null, 50));
    }

    [Fact]
    public async Task FileOutcome_NoFleetManagerMarked_IsRefusedNamingHowToMarkOne()
    {
        _marked[TenantA] = null;

        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, FleetManager, FleetOutcomeStoreTests.Ready()), ResolveTenant, Access(), _outcomes, Sources());

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
            Request(TenantA, OwnedWorking, FleetOutcomeStoreTests.Ready()), ResolveTenant, Access(), _outcomes, Sources());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Contains($"is owned by session {FleetManager}", (string)Field(result, "error")!);
    }

    [Fact]
    public async Task FileOutcome_MarkedSessionNoDirectorReported_IsRefused()
    {
        var unreported = Guid.NewGuid().ToString();
        _marked[TenantA] = unreported;

        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, unreported, FleetOutcomeStoreTests.Ready()), ResolveTenant, Access(), _outcomes, Sources());

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
            Request(TenantA, FleetManager, new { kind = "update", title = "Something" }), ResolveTenant, Access(), _outcomes, Sources());

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
            ResolveTenant, Access(), _outcomes, Sources());

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("decision.options needs at least two options, got 1", Field(result, "error"));
    }

    [Fact]
    public async Task FileOutcome_BrokenJson_Is400()
    {
        var result = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, FleetManager, "{ not json"), ResolveTenant, Access(), _outcomes, Sources());

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
    }

    [Fact]
    public async Task AnyRoute_NoAccount_Is403()
    {
        Assert.Equal(StatusCodes.Status403Forbidden,
            Status(await FleetManagerEndpoints.FileOutcomeAsync(
                Request(null, FleetManager, FleetOutcomeStoreTests.Ready()), ResolveTenant, Access(), _outcomes, Sources())));
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
    /// THE DIGEST FOLLOWS THE CURRENT OWNER (step 8). The owner's own session is handed to the Fleet Manager after it
    /// started - its Director reports the new owner - and the digest lists it; handed back, it leaves the digest.
    /// </summary>
    [Fact]
    public void Digest_SessionHandedOverAfterItStarted_IsListed_AndHandedBack_IsNot()
    {
        SetOwner(NotOwned, FleetManager, sequence: 2);
        var over = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));
        Assert.Contains(NotOwned, over.OwnedSessions.Select(s => s.SessionId));
        Assert.Equal(FleetManager, over.OwnedSessions.Single(s => s.SessionId == NotOwned).OwnerSessionId);
        Assert.Equal(3, over.OwnedSessionCounts.Total);

        SetOwner(NotOwned, null, sequence: 3);
        var back = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));
        Assert.DoesNotContain(NotOwned, back.OwnedSessions.Select(s => s.SessionId));
        Assert.Equal(2, back.OwnedSessionCounts.Total);
    }

    /// <summary>The Director's report after a hand over: the same row with its new owner, as a delta.</summary>
    private void SetOwner(string sessionId, string? owner, long sequence)
    {
        var row = _pushed.TryGetLastKnownSession(TenantA, sessionId)!.Value.Session.Clone();
        row.IsControlled = owner is not null;
        row.ControllerSessionId = owner;
        Assert.True(_pushed.ApplyDelta(TenantA, "director-a", "conn-a", sequence, row));
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
    {
        Assert.NotNull(_events.RecordStop(tenant,
            new FleetManagerStopSighting(sid, "a session " + sid, FleetManager, "director-a", DateTime.UtcNow, IsCatchUp: false),
            DateTime.UtcNow));
        var (_, events) = _events.AttachReading(tenant, sid, verdict,
            verdict is null ? "this account's Wingman judge switch is off" : null, owner: null, DateTime.UtcNow);
        return Assert.Single(events);
    }

    private IResult ListEvents(TenantId tenant, string? caller, string query = "")
        => FleetManagerEndpoints.ListEvents(Request(tenant, caller, query: query), ResolveTenant, Access(), _events,
            Sources().EventsDeliveryNote);

    private FleetManagerEventListDto Events(TenantId tenant, string query = "", string? caller = Owner)
    {
        var result = ListEvents(tenant, caller, query);
        Assert.Equal(StatusCodes.Status200OK, Status(result));
        return Body<FleetManagerEventListDto>(result);
    }

    private async Task<IResult> AckAsync(TenantId tenant, object body, string? caller = FleetManager)
        => await FleetManagerEndpoints.AcknowledgeEventsAsync(Request(tenant, caller, body), ResolveTenant, Access(), _events);

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
        Assert.Equal(new[] { second.Id }, Events(TenantA, caller: FleetManager).Events.Select(e => e.Id));
        var all = Events(TenantA, "status=all");
        Assert.Equal(2, all.Count);
        Assert.NotNull(all.Events.Single(e => e.Id == first.Id).AcknowledgedAtUtc);
    }

    /// <summary>ACKNOWLEDGING ALL CLOSES ONLY WHAT THIS SESSION WAS SENT. An event not yet delivered, or delivered to
    /// another Fleet Manager session, stays open.</summary>
    [Fact]
    public async Task Ack_All_AcknowledgesOnlyTheEventsDeliveredToTheCallingSession()
    {
        var sent = Stop(TenantA, OwnedStopped);
        var sentElsewhere = Stop(TenantA, OwnedWorking);
        var notSent = Stop(TenantA, OwnedExited);
        var other = Stop(TenantB, OtherAccountSession);
        _events.MarkDelivered(TenantA, new[] { Guid.Parse(sent.Id) }, FleetManager, DateTime.UtcNow);
        _events.MarkDelivered(TenantA, new[] { Guid.Parse(sentElsewhere.Id) }, FormerFleetManager, DateTime.UtcNow);
        _events.MarkDelivered(TenantB, new[] { Guid.Parse(other.Id) }, FleetManager, DateTime.UtcNow);

        var ack = Body<FleetManagerEventAckDto>(await AckAsync(TenantA, new { all = true }));

        Assert.Equal(new[] { sent.Id }, ack.Ids);
        Assert.Equal(new[] { sentElsewhere.Id, notSent.Id }, Events(TenantA).Events.Select(e => e.Id));
        Assert.Equal(1, Events(TenantB).Count);
    }

    /// <summary>ONLY THE MARKED FLEET MANAGER ACKNOWLEDGES: another session of the same account, the owner's device
    /// and a Director's key are each refused with a reason, and nothing is closed.</summary>
    [Theory]
    [InlineData(NotOwnedConst, "is not it")]
    [InlineData(Owner, "the owner does not acknowledge events")]
    [InlineData(DirectorKey, "credential")]
    public async Task Ack_AnyoneButTheMarkedFleetManager_IsRefusedWithAReason(string caller, string reason)
    {
        var sent = Stop(TenantA, OwnedStopped);
        _events.MarkDelivered(TenantA, new[] { Guid.Parse(sent.Id) }, FleetManager, DateTime.UtcNow);

        var byId = await AckAsync(TenantA, new { ids = new[] { sent.Id } }, caller);
        var all = await AckAsync(TenantA, new { all = true }, caller);

        foreach (var result in new[] { byId, all })
        {
            Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
            Assert.Equal("not_fleet_manager", Field(result, "code"));
            Assert.Contains(reason, (string)Field(result, "error")!);
        }
        Assert.Equal(new[] { sent.Id }, Events(TenantA).Events.Select(e => e.Id));
    }

    [Fact]
    public void ListEvents_AnotherSessionOfTheAccount_IsRefused()
    {
        Stop(TenantA, OwnedStopped);

        var result = ListEvents(TenantA, NotOwned);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.Equal("not_fleet_manager", Field(result, "code"));
    }

    [Fact]
    public async Task AnotherAccount_CannotReadOrAcknowledge_AndAMixedAckChangesNothing()
    {
        var mine = Stop(TenantA, OwnedStopped, Verdict("verdict-mine"));

        Assert.Equal(0, Events(TenantB, "status=all").Count);

        // Account B has no Fleet Manager marked, so the same session id is refused there before any lookup.
        var foreign = await AckAsync(TenantB, new { ids = new[] { mine.Id } });
        Assert.Equal(StatusCodes.Status403Forbidden, Status(foreign));

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
        var result = ListEvents(TenantA, Owner, "status=open");

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
    // ---- a stop waiting for its reading, and paging (step 4 fixes, round 3) -----------------------------

    private FleetManagerEventDto PendingStop(TenantId tenant, string sid)
    {
        var e = _events.RecordStop(tenant,
            new FleetManagerStopSighting(sid, "a session " + sid, FleetManager, "director-a", DateTime.UtcNow, IsCatchUp: false),
            DateTime.UtcNow);
        Assert.NotNull(e);
        Assert.True(e!.ReadingPending);
        return e;
    }

    /// <summary>Deaths of distinct sessions, stored at moments where every third shares the moment before it, so the id
    /// has to break the tie. Returned in the order they were stored; within one moment the Gateway's order is its own.</summary>
    private List<FleetManagerEventDto> Deaths(TenantId tenant, int n)
    {
        var t0 = new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc);
        var stored = new List<FleetManagerEventDto>();
        for (var i = 0; i < n; i++)
        {
            var at = t0.AddSeconds(i - i / 3);
            var e = _events.RecordDeath(tenant, new FleetManagerDeath(Guid.NewGuid().ToString(), "dead " + i, FleetManager,
                "director-a", Crashed: false, "it exited"), at);
            stored.Add(Assert.IsType<FleetManagerEventDto>(e));
        }
        return stored;
    }

    /// <summary>A STOP WAITING FOR ITS READING IS SHOWN AS WAITING, AND CANNOT BE ACKNOWLEDGED. The digest and the event
    /// list both carry it with the Gateway's words for that, and an acknowledgement that names it is refused by name
    /// with the reason - and closes nothing else it named either.</summary>
    [Fact]
    public async Task Ack_AStopWaitingForItsReading_IsRefusedWithTheReason_AndNothingIsAcknowledged()
    {
        var settled = Stop(TenantA, OwnedStopped, Verdict("verdict-settled"));
        var waiting = PendingStop(TenantA, OwnedWorking);

        var digest = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));
        var shown = digest.Events.Single(e => e.Id == waiting.Id);
        Assert.True(shown.ReadingPending);
        Assert.Equal(FleetManagerEventStore.PendingNote, shown.ReadingNote);
        Assert.Contains("cannot be acknowledged", shown.ReadingNote);
        Assert.Null(digest.Events.Single(e => e.Id == settled.Id).ReadingNote);
        Assert.Equal(1, digest.EventsWaitingForReading);
        Assert.Equal(FleetManagerEventStore.PendingNote,
            Events(TenantA, caller: FleetManager).Events.Single(e => e.Id == waiting.Id).ReadingNote);

        var result = await AckAsync(TenantA, new { ids = new[] { settled.Id, waiting.Id } });

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("reading_pending", Field(result, "code"));
        var error = (string)Field(result, "error")!;
        Assert.Contains(waiting.Id, error);
        Assert.Contains("still waiting for the Wingman's reading", error);
        Assert.Contains("after 5 minutes", error);
        Assert.Equal(new[] { settled.Id, waiting.Id }, Events(TenantA).Events.Select(e => e.Id));

        // Once its reading is stored, the same acknowledgement is accepted.
        _events.AttachReading(TenantA, OwnedWorking, Verdict("verdict-late"), null, owner: null, DateTime.UtcNow);
        var again = Body<FleetManagerEventAckDto>(await AckAsync(TenantA, new { ids = new[] { settled.Id, waiting.Id } }));
        Assert.Equal(2, again.Acknowledged);
    }

    /// <summary>EVERY UNACKNOWLEDGED EVENT IS REACHABLE PAST 200: the list pages oldest first with an opaque cursor,
    /// counts every event, and an acknowledgement between pages skips nothing and repeats nothing.</summary>
    [Fact]
    public void ListEvents_MoreThan200Unacknowledged_EveryOneIsReachedOnce_ByTheCursor()
    {
        var stored = Deaths(TenantA, 205);

        var first = Events(TenantA, "count=200", FleetManager);
        Assert.Equal((200, 205, true), (first.Count, first.Total, first.HasMore));
        Assert.NotNull(first.NextCursor);
        // The 200th and 201st were stored at different moments, so the first page is exactly the oldest 200.
        Assert.NotEqual(stored[199].CreatedAtUtc, stored[200].CreatedAtUtc);
        Assert.Equal(stored.Take(200).Select(e => e.Id).OrderBy(x => x), first.Events.Select(e => e.Id).OrderBy(x => x));
        AssertOldestFirst(first.Events);

        // Acknowledged between pages: the next page is not shifted by it.
        _events.MarkDelivered(TenantA, new[] { Guid.Parse(first.Events[0].Id) }, FleetManager, DateTime.UtcNow);
        _events.Acknowledge(TenantA, new[] { Guid.Parse(first.Events[0].Id) }, false, null, DateTime.UtcNow);

        var second = Events(TenantA, "count=200&cursor=" + first.NextCursor, FleetManager);
        Assert.Equal((5, 204, false), (second.Count, second.Total, second.HasMore));
        Assert.Null(second.NextCursor);
        Assert.Equal(stored.Skip(200).Select(e => e.Id).OrderBy(x => x), second.Events.Select(e => e.Id).OrderBy(x => x));
        AssertOldestFirst(second.Events);
    }

    private static void AssertOldestFirst(IReadOnlyList<FleetManagerEventDto> events)
    {
        for (var i = 1; i < events.Count; i++)
            Assert.True(events[i - 1].CreatedAtUtc <= events[i].CreatedAtUtc, $"event {i} is older than the one before it");
    }

    /// <summary>Small pages, each boundary inside a run of events stored at the same moment: the id breaks the tie,
    /// and every event is still reached exactly once.</summary>
    [Fact]
    public void ListEvents_PagesThatSplitEventsStoredAtOneMoment_ReachEveryEventOnce()
    {
        var stored = Deaths(TenantA, 20);

        var seen = new List<FleetManagerEventDto>();
        string? cursor = null;
        do
        {
            var page = Events(TenantA, "count=2" + (cursor is null ? "" : "&cursor=" + cursor), FleetManager);
            seen.AddRange(page.Events);
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(stored.Select(e => e.Id).OrderBy(x => x), seen.Select(e => e.Id).OrderBy(x => x));
        Assert.Equal(20, seen.Select(e => e.Id).Distinct().Count());
        AssertOldestFirst(seen);
    }

    /// <summary>The history pages newest first, and reaches every event.</summary>
    [Fact]
    public void ListEvents_All_PagesNewestFirst_AndReachesEveryEvent()
    {
        var stored = Deaths(TenantA, 7);

        var seen = new List<FleetManagerEventDto>();
        string? cursor = null;
        do
        {
            var page = Events(TenantA, "status=all&count=3" + (cursor is null ? "" : "&cursor=" + cursor));
            Assert.Equal(7, page.Total);
            seen.AddRange(page.Events);
            cursor = page.NextCursor;
            Assert.Equal(cursor is not null, page.HasMore);
        } while (cursor is not null);

        Assert.Equal(stored.Select(e => e.Id).OrderBy(x => x), seen.Select(e => e.Id).OrderBy(x => x));
        Assert.Equal(7, seen.Select(e => e.Id).Distinct().Count());
        for (var i = 1; i < seen.Count; i++)
            Assert.True(seen[i - 1].CreatedAtUtc >= seen[i].CreatedAtUtc, $"event {i} is newer than the one before it");
    }

    [Fact]
    public void ListEvents_ACursorNotIssuedForThatStatus_OrEmpty_Is400()
    {
        Deaths(TenantA, 3);
        var cursor = Events(TenantA, "count=1").NextCursor!;

        var otherStatus = ListEvents(TenantA, Owner, "status=all&cursor=" + cursor);
        var forged = ListEvents(TenantA, Owner, "cursor=not-a-cursor");
        var empty = ListEvents(TenantA, Owner, "cursor=");

        Assert.Equal(StatusCodes.Status400BadRequest, Status(otherStatus));
        Assert.Contains("is not one this Gateway issued for status all", (string)Field(otherStatus, "error")!);
        Assert.Equal(StatusCodes.Status400BadRequest, Status(forged));
        Assert.Equal(StatusCodes.Status400BadRequest, Status(empty));
        Assert.StartsWith("cursor is empty", (string)Field(empty, "error")!);
    }

    /// <summary>THE DIGEST SAYS WHEN MORE EVENTS REMAIN: it carries the oldest 200, the database's count of all of them,
    /// and the cursor that reaches the rest.</summary>
    [Fact]
    public void Digest_MoreThan200Events_CarriesTheOldestPage_AndSaysMoreRemain_WithTheCursor()
    {
        var stored = Deaths(TenantA, 203);

        var digest = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));

        Assert.Equal(stored.Take(200).Select(e => e.Id).OrderBy(x => x), digest.Events.Select(e => e.Id).OrderBy(x => x));
        Assert.Equal((203, true), (digest.EventsTotal, digest.EventsHasMore));
        var rest = Events(TenantA, "cursor=" + digest.EventsNextCursor, FleetManager);
        Assert.Equal(stored.Skip(200).Select(e => e.Id).OrderBy(x => x), rest.Events.Select(e => e.Id).OrderBy(x => x));
        Assert.False(rest.HasMore);
    }

    [Fact]
    public void Digest_200OrFewerEvents_SaysNoneRemain()
    {
        Deaths(TenantA, 200);

        var digest = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));

        Assert.Equal((200, 200, false), (digest.Events.Count, digest.EventsTotal, digest.EventsHasMore));
        Assert.Null(digest.EventsNextCursor);
    }

    /// <summary>
    /// WHY THE EVENTS ARE WAITING, IN THE GATEWAY'S WORDS (round 2, finding 1): when the Fleet Manager's events are held
    /// back by the owner's unsent text, the digest and the events list carry the Gateway's sentence, for the account it
    /// belongs to only. With nothing held back, there is none.
    /// </summary>
    [Fact]
    public void DigestAndEvents_CarryTheGatewaysDeliveryNote_ForTheirAccountOnly()
    {
        Deaths(TenantA, 1);
        Assert.Null(Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager)).EventsDeliveryNote);
        Assert.Null(Events(TenantA, "", FleetManager).DeliveryNote);

        const string note = "The Fleet Manager has your unsent text; 1 event is waiting. It is sent after you send your text.";
        _deliveryNotes[TenantA] = note;

        Assert.Equal(note, Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager)).EventsDeliveryNote);
        Assert.Equal(note, Body<FleetDigestDto>(Digest(TenantA, Owner, FleetManager)).EventsDeliveryNote);
        Assert.Equal(note, Events(TenantA, "", FleetManager).DeliveryNote);
        Assert.Null(Events(TenantB, "", Owner).DeliveryNote);
    }

    // ---- advice and the Fleet Manager's pick (step 7) -------------------------------------------------------

    /// <summary>A reading of <paramref name="session"/> that offers two options, stored as its current verdict.</summary>
    private void StoreMenuReading(TenantId tenant, string session)
    {
        var verdict = Verdict("verdict-menu-" + session[^4..]);
        verdict.AnswerVia = "keys";
        verdict.Menu = new TurnVerdictMenuDto { Question = "Which layout to keep?", SelectionMode = "single" };
        verdict.Options = new List<TurnVerdictOptionDto>
        {
            new() { Key = "A - card grid", Send = "1", Recommended = true, Note = "The other is deleted." },
            new() { Key = "B - single column", Send = "2", Note = "The other is deleted." },
        };
        _verdicts.Store(tenant, session, verdict);
    }

    private async Task<IResult> AdviseAsync(TenantId tenant, string? caller, string id, object body)
        => await FleetManagerEndpoints.SetAdviceAsync(Request(tenant, caller, body), id, ResolveTenant, Access(), _outcomes, Sources());

    [Fact]
    public async Task SetAdvice_TheMarkedFleetManager_StoresTheLineAndThePick()
    {
        StoreMenuReading(TenantA, OwnedStopped);
        var filed = await FileAsync(TenantA, new FleetOutcomeFileRequest
        {
            Kind = "decision", Title = "Pick a layout", SessionId = OwnedStopped,
            Decision = new FleetDecisionDetails { Question = "Which layout?", Options = { "A", "B" } },
        });

        var result = await AdviseAsync(TenantA, FleetManager, filed.Id,
            new { advice = "You picked the long column for the last two client pages. I'd pick B.", pick = "B - single column" });

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var stored = _outcomes.Get(TenantA, Guid.Parse(filed.Id))!;
        Assert.Equal("You picked the long column for the last two client pages. I'd pick B.", stored.Advice);
        Assert.Equal("B - single column", stored.FleetManagerPick);
        Assert.NotNull(stored.AdviceSetAtUtc);
    }

    [Theory]
    [InlineData(Owner, "the owner does not write the Fleet Manager's advice")]
    [InlineData(NotOwnedConst, "only this account's Fleet Manager session")]
    [InlineData(DirectorKey, "only this account's Fleet Manager session or the owner")]
    public async Task SetAdvice_AnyoneButTheMarkedFleetManager_IsRefusedAndNothingChanges(string caller, string reason)
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());

        var result = await AdviseAsync(TenantA, caller, filed.Id, new { advice = "Read the second report first." });

        Assert.Equal(StatusCodes.Status403Forbidden, Status(result));
        Assert.StartsWith(reason, (string)Field(result, "error")!);
        Assert.Null(_outcomes.Get(TenantA, Guid.Parse(filed.Id))!.Advice);
    }

    [Theory]
    [InlineData("Line one\nline two", "advice must be one line: it is shown as a single line beside the Wingman's reading, so remove the line break")]
    [InlineData("Trailing break\r\n", "advice must be one line: it is shown as a single line beside the Wingman's reading, so remove the line break")]
    [InlineData("   ", "advice is required: one line for the owner, using what you know and the Wingman does not")]
    public async Task SetAdvice_NotOneLine_Is400WithTheReason(string advice, string expected)
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());

        var result = await AdviseAsync(TenantA, FleetManager, filed.Id, new { advice });

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal(expected, Field(result, "error"));
        Assert.Null(_outcomes.Get(TenantA, Guid.Parse(filed.Id))!.Advice);
    }

    [Fact]
    public async Task SetAdvice_OverLong_Is400SayingTheLimit()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());

        var result = await AdviseAsync(TenantA, FleetManager, filed.Id, new { advice = new string('a', 301) });

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("advice is 301 characters; one line of advice is at most 300, so shorten it", Field(result, "error"));
    }

    [Fact]
    public async Task SetAdvice_APickThatIsNotAnOption_Is400NamingTheOptions()
    {
        StoreMenuReading(TenantA, OwnedStopped);
        var filed = await FileAsync(TenantA, new FleetOutcomeFileRequest
        {
            Kind = "finding", Title = "Layouts built", SessionId = OwnedStopped,
            Finding = new FleetFindingDetails { Answer = "Both run." },
        });

        var result = await AdviseAsync(TenantA, FleetManager, filed.Id, new { advice = "I'd pick C.", pick = "C" });

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal($"pick 'C' is not one of the Wingman's options for session {OwnedStopped}; use one of: 'A - card grid', 'B - single column'",
            Field(result, "error"));
        Assert.Null(_outcomes.Get(TenantA, Guid.Parse(filed.Id))!.Advice);
    }

    [Fact]
    public async Task SetAdvice_APickOnARecordAboutNoSession_Is400()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());

        var result = await AdviseAsync(TenantA, FleetManager, filed.Id, new { advice = "Go with A.", pick = "A" });

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.StartsWith("a pick names one of the Wingman's options for the record's session, and this record is about no session",
            (string)Field(result, "error")!);
    }

    [Fact]
    public async Task SetAdvice_AnAnsweredRecord_Is409AndUnchanged()
    {
        var filed = await FileAsync(TenantA, FleetOutcomeStoreTests.Finding());
        await AnswerAsync(TenantA, Owner, filed.Id, "Got it.");

        var result = await AdviseAsync(TenantA, FleetManager, filed.Id, new { advice = "Too late." });

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("already_answered", Field(result, "code"));
        Assert.Null(_outcomes.Get(TenantA, Guid.Parse(filed.Id))!.Advice);
    }

    [Fact]
    public async Task SetAdvice_AnotherAccountsRecord_Is404()
    {
        _marked[TenantB] = null;
        var theirs = _outcomes.File(TenantB, FleetOutcomeStoreTests.Finding(), OtherAccountSession, DateTime.UtcNow);

        var result = await AdviseAsync(TenantA, FleetManager, theirs.Id, new { advice = "Not yours." });

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Null(_outcomes.Get(TenantB, Guid.Parse(theirs.Id))!.Advice);
    }

    [Fact]
    public async Task FileOutcome_WithAdviceAndAPick_StoresBoth_AndAPickThatIsNotAnOptionIsRefused()
    {
        StoreMenuReading(TenantA, OwnedStopped);
        FleetOutcomeFileRequest Filing(string pick) => new()
        {
            Kind = "decision", Title = "Pick a layout", SessionId = OwnedStopped,
            Decision = new FleetDecisionDetails { Question = "Which layout?", Options = { "A", "B" } },
            Advice = "The client reads on a phone. I'd pick B.",
            FleetManagerPick = pick,
        };

        var filed = await FileAsync(TenantA, Filing("B - single column"));
        var refused = await FleetManagerEndpoints.FileOutcomeAsync(
            Request(TenantA, FleetManager, Filing("B")), ResolveTenant, Access(), _outcomes, Sources());

        Assert.Equal("The client reads on a phone. I'd pick B.", filed.Advice);
        Assert.Equal("B - single column", filed.FleetManagerPick);
        Assert.Equal(StatusCodes.Status400BadRequest, Status(refused));
        Assert.StartsWith("pick 'B' is not one of the Wingman's options", (string)Field(refused, "error")!);
        Assert.Single(_outcomes.List(TenantA, "all", null, 50));
    }

    [Fact]
    public async Task FileOutcome_APickWithoutAdvice_IsRefusedSayingAdviceGoesWithIt()
    {
        StoreMenuReading(TenantA, OwnedStopped);
        var result = await FleetManagerEndpoints.FileOutcomeAsync(Request(TenantA, FleetManager, new FleetOutcomeFileRequest
        {
            Kind = "finding", Title = "Layouts built", SessionId = OwnedStopped,
            Finding = new FleetFindingDetails { Answer = "Both run." },
            FleetManagerPick = "A - card grid",
        }), ResolveTenant, Access(), _outcomes, Sources());

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("fleetManagerPick is set together with advice; give the one line of advice that goes with the pick",
            Field(result, "error"));
    }

    [Fact]
    public void Digest_CarriesWhatTheOwnerDecidedInTheLastDay_AndAnOpenRecordsNote()
    {
        var now = DateTime.UtcNow;
        var old = _outcomes.File(TenantA, FleetOutcomeStoreTests.Finding("Answered two days ago"), FleetManager, now.AddDays(-3));
        _outcomes.Answer(TenantA, Guid.Parse(old.Id), "Old answer.", "owner", "owner", now.AddDays(-2));
        var recent = _outcomes.File(TenantA, FleetOutcomeStoreTests.Ready("Answered in the walkthrough"), FleetManager, now.AddHours(-2));
        _outcomes.Answer(TenantA, Guid.Parse(recent.Id), "Close the session.", "owner", "owner", now.AddHours(-1));
        var open = _outcomes.File(TenantA, FleetOutcomeStoreTests.Decision("Snoozed in the walkthrough"), FleetManager, now);
        _outcomes.NoteOwnerAction(TenantA, Guid.Parse(open.Id), "The owner snoozed the session from the walkthrough, until 15:30.", now);
        var theirs = _outcomes.File(TenantB, FleetOutcomeStoreTests.Finding("Another account's"), OtherAccountSession, now);
        _outcomes.Answer(TenantB, Guid.Parse(theirs.Id), "Not yours.", "owner", "owner", now);

        var digest = Body<FleetDigestDto>(Digest(TenantA, FleetManager, FleetManager));

        Assert.Equal(24, digest.AnsweredWithinHours);
        var answered = Assert.Single(digest.RecentlyAnswered);
        Assert.Equal(("Answered in the walkthrough", "Close the session.", "owner"), (answered.Title, answered.Answer, answered.AnsweredByRole));
        Assert.Equal("The owner snoozed the session from the walkthrough, until 15:30.",
            Assert.Single(digest.Outcomes).OwnerNote);
    }
}
