using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Fleet;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;
using static CcDirector.Gateway.Tests.Fleet.FleetManagerWalkthroughFoldTests;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The walkthrough routes (the Fleet Manager mission, step 7), driven through their handlers with a real outcome store,
/// a real verdict store and handed-in roster, repository and stop sources.
///
/// What is pinned: only the owner's own device reaches them; another account's record is not found; a session's answer
/// is recorded only once the answer route marked its verdict answered, with the options that route stored - never the
/// client's - and only for the stop the record is waiting on; a snooze is
/// recorded only when the session is snoozed; and close is decided again on the server - refused without calling the
/// stop at all - and recorded only after a stop that happened.
/// </summary>
public sealed class FleetManagerWalkthroughEndpointsTests : IDisposable
{
    private const string TenantHeader = "X-Test-Account";
    private static readonly TenantId TenantA = new("acct-fm-walk-a");
    private static readonly TenantId TenantB = new("acct-fm-walk-b");
    private const string Layouts = "82000000-0000-4000-8000-000000000002";
    private const string Dirty = "82000000-0000-4000-8000-000000000003";
    private const string Owner = "owner";
    private const string DirectorKey = "director";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly FleetOutcomeStore _outcomes;
    private readonly TurnVerdictStore _verdicts;
    private readonly List<SessionDto> _live;
    private readonly SessionStopDoor _door = new();
    private readonly List<(string Sid, string Reason)> _stops = new();
    private Func<string, IResult> _stopAnswer = sid => Results.Json(new SessionStopResponse
    {
        Verdict = SessionStopVerdict.Stopped, Headline = "Stopped.", SessionId = sid,
    });

    public FleetManagerWalkthroughEndpointsTests()
    {
        _outcomes = new FleetOutcomeStore(_harness.Open());
        _verdicts = new TurnVerdictStore(_harness.Open());
        _live = new List<SessionDto>
        {
            new() { SessionId = Marked, Name = "Fleet Manager", ActivityState = "WaitingForInput", DirectorId = Director },
            Session(Layouts, "Layouts session"),
            Session(Dirty, "Dirty session", uncommitted: 2),
        };
        _door.Stop = (ctx, sid, reason, ct) =>
        {
            _stops.Add((sid, reason));
            return Task.FromResult(_stopAnswer(sid));
        };
    }

    public void Dispose() => _harness.Dispose();

    private static TenantId? ResolveTenant(HttpContext ctx)
        => ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && v.ToString().Length > 0 ? new TenantId(v.ToString()) : null;

    private static DefaultHttpContext Request(TenantId? tenant, string? caller, object? body = null, string query = "")
    {
        var ctx = new DefaultHttpContext();
        if (tenant is { } t) ctx.Request.Headers[TenantHeader] = t.Value;
        if (body is not null)
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
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
                    new SessionCredentialIdentity(Guid.Parse(caller), tenant ?? TenantId.Local, Director);
                break;
        }
        return ctx;
    }

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static T Body<T>(IResult result) => Assert.IsType<JsonHttpResult<T>>(result).Value!;

    private static object? Field(IResult result, string name)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return value.GetType().GetProperty(name)!.GetValue(value);
    }

    private FleetManagerWalkthroughSources Sources() => new(
        LiveRoster: tenant => tenant == TenantA ? _live : new List<SessionDto>(),
        MarkedSessionId: tenant => tenant == TenantA ? Marked : null,
        LastKnownSession: (tenant, sid) => tenant == TenantA ? _live.FirstOrDefault(s => s.SessionId == sid) : null,
        LatestVerdict: (tenant, sid) => _verdicts.Latest(tenant, sid),
        FindVerdict: (tenant, id) => _verdicts.FindById(tenant, id),
        NewestVerdict: (tenant, sid) => _verdicts.NewestJudged(tenant, sid),
        Repositories: tenant => tenant == TenantA ? new[] { Repo() } : Array.Empty<StoredRepoState>(),
        SnoozeMinutes: _ => 60,
        TimeZone: _ => TimeZoneInfo.Utc,
        NowUtc: () => Now,
        StopDoor: _door);

    private FleetOutcomeDto File(TenantId tenant, string? session, string title = "Pick a layout")
        => _outcomes.File(tenant, new FleetOutcomeFileRequest
        {
            Kind = "decision", Title = title, SessionId = session,
            Decision = new FleetDecisionDetails { Question = title, Options = { "Yes.", "No." } },
        }, Marked, Now.AddMinutes(-10));

    private IResult Read(TenantId? tenant, string? caller, string query = "")
        => FleetManagerWalkthroughEndpoints.Read(Request(tenant, caller, query: query), ResolveTenant, _outcomes, Sources());

    private Task<IResult> Answered(TenantId tenant, string? caller, string id, object body)
        => FleetManagerWalkthroughEndpoints.AnsweredAsync(Request(tenant, caller, body), id, ResolveTenant, _outcomes, Sources());

    private IResult Snoozed(TenantId tenant, string? caller, string id)
        => FleetManagerWalkthroughEndpoints.Snoozed(Request(tenant, caller), id, ResolveTenant, _outcomes, Sources());

    private Task<IResult> Close(TenantId tenant, string? caller, string id)
        => FleetManagerWalkthroughEndpoints.CloseAsync(Request(tenant, caller), id, ResolveTenant, _outcomes, Sources(),
            CancellationToken.None);

    // ---- who may call --------------------------------------------------------------------------------------

    [Fact]
    public void Route_IsTheMappedLiteral()
        => Assert.Equal("/gateway/fleet-manager/walkthrough", FleetManagerWalkthroughEndpoints.WalkthroughRoute);

    [Theory]
    [InlineData(Marked)]
    [InlineData(Layouts)]
    public async Task EveryRoute_ASessionKey_IsRefused_EvenTheFleetManagers(string session)
    {
        var record = File(TenantA, Layouts);

        var results = new[]
        {
            Read(TenantA, session),
            await Answered(TenantA, session, record.Id, new { verdictId = "v", optionIndexes = new[] { 0 } }),
            Snoozed(TenantA, session, record.Id),
            await Close(TenantA, session, record.Id),
        };

        Assert.All(results, r => Assert.Equal(StatusCodes.Status403Forbidden, Status(r)));
        Assert.All(results, r => Assert.Equal("The walkthrough is the owner's. A session reads GET /gateway/fleet-manager/digest.", Field(r, "error")));
        Assert.Empty(_stops);
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    [Fact]
    public async Task EveryRoute_ADirectorKey_IsRefused()
    {
        var record = File(TenantA, Layouts);

        var results = new[]
        {
            Read(TenantA, DirectorKey),
            await Answered(TenantA, DirectorKey, record.Id, new { verdictId = "v", optionIndexes = new[] { 0 } }),
            Snoozed(TenantA, DirectorKey, record.Id),
            await Close(TenantA, DirectorKey, record.Id),
        };

        Assert.All(results, r => Assert.Equal(StatusCodes.Status403Forbidden, Status(r)));
        Assert.All(results, r => Assert.StartsWith("only the owner on their own signed-in phone or browser may ", (string)Field(r, "error")!));
        Assert.Empty(_stops);
    }

    [Fact]
    public void Read_NoAccount_IsRefused()
        => Assert.Equal(StatusCodes.Status403Forbidden, Status(Read(null, Owner)));

    [Fact]
    public void Read_Owner_ServesTheFoldedRound_AndAnotherAccountSeesOnlyItsOwn()
    {
        File(TenantA, Layouts, "Account A's question");
        File(TenantB, null, "Account B's question");

        var a = Body<FleetManagerWalkthroughDto>(Read(TenantA, Owner));
        var b = Body<FleetManagerWalkthroughDto>(Read(TenantB, Owner));

        Assert.Equal("Account A's question", Assert.Single(a.Items).Title);
        Assert.Equal("Account B's question", Assert.Single(b.Items).Title);
        Assert.Null(b.FleetManagerSessionId);
    }

    [Fact]
    public void Read_ARoundNamingAnotherAccountsRecord_LeavesItOut()
    {
        var mine = File(TenantA, Layouts, "Mine");
        var theirs = File(TenantB, null, "Theirs");

        var dto = Body<FleetManagerWalkthroughDto>(Read(TenantA, Owner, $"round={mine.Id},{theirs.Id}"));

        Assert.Equal(new[] { mine.Id }, dto.RoundIds);
    }

    [Theory]
    [InlineData("round=not-an-id", "round 'not-an-id' is not a record id; send back the roundIds the walkthrough gave you, or leave round out to start a new round")]
    public void Read_ABadRound_Is400(string query, string expected)
    {
        var result = Read(TenantA, Owner, query);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal(expected, Field(result, "error"));
    }

    // ---- answered ------------------------------------------------------------------------------------------

    private TurnVerdictDto StoreMenu(TenantId tenant, string session, string id)
    {
        var verdict = Menu(id);
        _verdicts.Store(tenant, session, verdict);
        return verdict;
    }

    /// <summary>What the answer route stores when the Director confirmed its write of these options.</summary>
    private void SessionTook(TenantId tenant, TurnVerdictDto verdict, DateTime at, params int[] indexes)
        => Assert.True(_verdicts.MarkAnswered(tenant, TurnVerdictStoredAnswer.For(verdict, indexes), at));

    [Fact]
    public async Task Answered_AfterTheSessionTookIt_RecordsTheOptionsOwnWords_AsTheOwner()
    {
        var record = File(TenantA, Layouts);
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-a"), Now, 1);

        var result = await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-a", optionIndexes = new[] { 1 } });

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var stored = _outcomes.Get(TenantA, Guid.Parse(record.Id))!;
        Assert.Equal("answered", stored.Status);
        Assert.Equal("B - single column", stored.Answer);
        Assert.Equal("owner", stored.AnsweredByRole);
        // Step 7's ruling stands: a walkthrough answer goes to the session and is recorded, not queued to the Fleet Manager.
        Assert.Empty(new FleetManagerEventStore(_harness.Open()).Unacknowledged(TenantA));
    }

    [Fact]
    public async Task Answered_SeveralOptions_AreJoinedInTheOrderPicked()
    {
        var record = File(TenantA, Layouts);
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-multi"), Now, 1, 0);

        await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-multi", optionIndexes = new[] { 1, 0 } });

        Assert.Equal("B - single column, A - card grid", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Answer);
    }

    [Fact]
    public async Task Answered_NamingNoOptions_RecordsWhatTheAnswerRouteSent()
    {
        var record = File(TenantA, Layouts);
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-unnamed"), Now, 1);

        var result = await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-unnamed" });

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal("B - single column", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Answer);
    }

    /// <summary>
    /// The answer route sent option A. A delayed or mistaken request then names option B - or an option the verdict
    /// does not have, one twice, or none. Each is refused, and the record stays open: only what was sent is recorded.
    /// </summary>
    [Theory]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 2 })]
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { 1, 0 })]
    [InlineData(new int[0])]
    public async Task Answered_ARequestNamingOtherOptionsThanWereSent_IsRefusedAndRecordsNothing(int[] named)
    {
        var record = File(TenantA, Layouts);
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-sent-a"), Now, 0);

        var result = await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-sent-a", optionIndexes = named });

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("answer_mismatch", Field(result, "code"));
        Assert.StartsWith("the session was sent option [0] for verdict verdict-sent-a, not [", (string)Field(result, "error")!);
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    /// <summary>An answer to an earlier stop never closes the record once the session has stopped again - even with the
    /// options that stop was sent.</summary>
    [Fact]
    public async Task Answered_AVerdictTheSessionHasStoppedAgainSince_IsRefusedAndRecordsNothing()
    {
        var record = File(TenantA, Layouts);
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-earlier"), Now.AddMinutes(-30), 0);
        var later = Menu("verdict-later");
        later.JudgedAtUtc = Now.AddMinutes(-2);
        later.TurnEndObservedAtUtc = Now.AddMinutes(-3);
        _verdicts.Store(TenantA, Layouts, later);
        // The later stop is superseded too - the session is working again. It is still the last stop.
        _verdicts.Invalidate(TenantA, Layouts);

        var result = await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-earlier", optionIndexes = new[] { 0 } });

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("later_stop", Field(result, "code"));
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    /// <summary>A record filed after the stop was answered is about something the answer never saw.</summary>
    [Fact]
    public async Task Answered_AVerdictAnsweredBeforeTheRecordWasFiled_IsRefusedAndRecordsNothing()
    {
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-before"), Now.AddMinutes(-20), 0);
        var record = File(TenantA, Layouts); // filed ten minutes before now, after the answer

        var result = await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-before", optionIndexes = new[] { 0 } });

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("answered_before_record", Field(result, "code"));
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    /// <summary>The stored answer names the turn it was given to; a verdict re-stored under the same id for another turn
    /// is not the stop that was answered.</summary>
    [Fact]
    public async Task Answered_AnAnswerStoredForAnotherTurnOfTheSameVerdictId_IsRefused()
    {
        var record = File(TenantA, Layouts);
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-reused"), Now, 0);
        var restored = Menu("verdict-reused");
        restored.TurnEndObservedAtUtc = Now.AddMinutes(-40);
        _verdicts.Store(TenantA, Layouts, restored);

        var result = await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-reused", optionIndexes = new[] { 0 } });

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("answer_other_verdict", Field(result, "code"));
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    [Fact]
    public async Task Answered_ARefusedAnswer_TheVerdictNotMarked_RecordsNothing()
    {
        var record = File(TenantA, Layouts);
        StoreMenu(TenantA, Layouts, "verdict-refused");

        var result = await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-refused", optionIndexes = new[] { 0 } });

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("not_answered", Field(result, "code"));
        Assert.Equal("the session has not taken an answer to that verdict, so nothing was recorded; answer it first", Field(result, "error"));
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    [Fact]
    public async Task Answered_AVerdictOfAnotherSession_RecordsNothing()
    {
        var record = File(TenantA, Layouts);
        SessionTook(TenantA, StoreMenu(TenantA, Dirty, "verdict-other"), Now, 0);

        var result = await Answered(TenantA, Owner, record.Id, new { verdictId = "verdict-other", optionIndexes = new[] { 0 } });

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("verdict_other_session", Field(result, "code"));
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    [Theory]
    [InlineData("{\"optionIndexes\":[0]}", "verdictId is required: the verdict whose options the session took")]
    [InlineData("{\"verdictId\":\"  \"}", "verdictId is required: the verdict whose options the session took")]
    public async Task Answered_ABadBody_Is400AndRecordsNothing(string json, string expected)
    {
        var record = File(TenantA, Layouts);
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-bad"), Now, 0);
        var ctx = Request(TenantA, Owner);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await FleetManagerWalkthroughEndpoints.AnsweredAsync(ctx, record.Id, ResolveTenant, _outcomes, Sources());

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal(expected, Field(result, "error"));
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    [Fact]
    public async Task Answered_AnotherAccountsRecord_Is404()
    {
        var theirs = File(TenantB, null);
        SessionTook(TenantA, StoreMenu(TenantA, Layouts, "verdict-x"), Now, 0);

        var result = await Answered(TenantA, Owner, theirs.Id, new { verdictId = "verdict-x", optionIndexes = new[] { 0 } });

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Equal("open", _outcomes.Get(TenantB, Guid.Parse(theirs.Id))!.Status);
    }

    // ---- snoozed -------------------------------------------------------------------------------------------

    [Fact]
    public void Snoozed_ASnoozedSession_KeepsTheRecordOpenWithTheNote()
    {
        var record = File(TenantA, Layouts);
        _live[1] = Session(Layouts, "Layouts session", onHold: true, snoozeUntil: Now.AddHours(1));

        var result = Snoozed(TenantA, Owner, record.Id);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var stored = _outcomes.Get(TenantA, Guid.Parse(record.Id))!;
        Assert.Equal("open", stored.Status);
        Assert.Equal("The owner snoozed the session from the walkthrough, until 15:30.", stored.OwnerNote);
    }

    [Fact]
    public void Snoozed_ASessionThatIsNotSnoozed_RecordsNothing()
    {
        var record = File(TenantA, Layouts);

        var result = Snoozed(TenantA, Owner, record.Id);

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal($"session {Layouts} is not snoozed, so nothing was recorded; snooze it first", Field(result, "error"));
        Assert.Null(_outcomes.Get(TenantA, Guid.Parse(record.Id))!.OwnerNote);
    }

    // ---- close ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Close_Allowed_StopsThroughTheStopHandler_ThenRecordsIt()
    {
        var record = File(TenantA, Layouts);

        var result = await Close(TenantA, Owner, record.Id);

        Assert.Equal(StatusCodes.Status200OK, Status(result));
        Assert.Equal((Layouts, "Closed by the owner from the Fleet Manager walkthrough."), Assert.Single(_stops));
        var body = Body<FleetWalkthroughCloseResponse>(result);
        Assert.Equal("Stopped.", body.Stop.Headline);
        Assert.Null(body.RecordError);
        var stored = _outcomes.Get(TenantA, Guid.Parse(record.Id))!;
        Assert.Equal("answered", stored.Status);
        Assert.Equal("Close the session.", stored.Answer);
    }

    [Fact]
    public async Task Close_WithUnlandedWork_IsRefusedOnTheServer_WithoutStoppingOrRecording()
    {
        var record = File(TenantA, Dirty);

        var result = await Close(TenantA, Owner, record.Id);

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.Equal("close_refused", Field(result, "code"));
        Assert.Equal("Close is not offered: this session has 2 uncommitted files.", Field(result, "error"));
        Assert.Empty(_stops);
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    [Fact]
    public async Task Close_WhenTheGatewayCannotTell_IsRefused()
    {
        var record = File(TenantA, Layouts);
        _live[1] = Session(Layouts, "Layouts session", lastActivity: Now.AddMinutes(-1));

        var result = await Close(TenantA, Owner, record.Id);

        Assert.Equal(StatusCodes.Status409Conflict, Status(result));
        Assert.StartsWith("Close is not offered: the repository was last inspected at 14:25", (string)Field(result, "error")!);
        Assert.Empty(_stops);
    }

    [Fact]
    public async Task Close_AStopThatFails_IsPassedThrough_AndTheRecordStaysOpen()
    {
        var record = File(TenantA, Layouts);
        _stopAnswer = _ => Results.Json(new { error = "The computer is not connected." }, statusCode: StatusCodes.Status502BadGateway);

        var result = await Close(TenantA, Owner, record.Id);

        Assert.Equal(StatusCodes.Status502BadGateway, Status(result));
        Assert.Equal("The computer is not connected.", Field(result, "error"));
        Assert.Single(_stops);
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    [Fact]
    public async Task Close_AStopThatStoppedNothing_LeavesTheRecordOpenAndSaysSo()
    {
        var record = File(TenantA, Layouts);
        _stopAnswer = sid => Results.Json(new SessionStopResponse
        {
            Verdict = SessionStopVerdict.NotOnFleet, Headline = "Nothing in this account carries that id.", SessionId = sid,
        });

        var body = Body<FleetWalkthroughCloseResponse>(await Close(TenantA, Owner, record.Id));

        Assert.Equal("The stop answered \"Nothing in this account carries that id.\", so the record was left open.", body.RecordError);
        Assert.Equal("open", _outcomes.Get(TenantA, Guid.Parse(record.Id))!.Status);
    }

    [Fact]
    public async Task Close_AnAnsweredRecord_OrOneAboutNoSession_StopsNothing()
    {
        var answered = File(TenantA, Layouts, "Answered");
        _outcomes.Answer(TenantA, Guid.Parse(answered.Id), "Yes.", FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner, Now);
        var loose = File(TenantA, null, "Loose");

        var first = await Close(TenantA, Owner, answered.Id);
        var second = await Close(TenantA, Owner, loose.Id);

        Assert.Equal(("already_answered", 409), ((string)Field(first, "code")!, Status(first)));
        Assert.Equal(("no_session", 409), ((string)Field(second, "code")!, Status(second)));
        Assert.Empty(_stops);
    }

    [Fact]
    public async Task Close_AnotherAccountsRecord_Is404_AndStopsNothing()
    {
        var theirs = File(TenantB, Layouts);

        var result = await Close(TenantA, Owner, theirs.Id);

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Empty(_stops);
    }
}
