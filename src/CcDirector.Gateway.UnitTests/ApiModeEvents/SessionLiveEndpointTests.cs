using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeConversation;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests.ApiModeEvents;

/// <summary>
/// The live relay of an API-mode session's events (mission #4070, phase 2): <c>GET /sessions/{sid}/live</c> and its
/// poll form <c>GET /sessions/{sid}/live/items</c>, driven over HTTP on an in-memory host (TestServer: no listener, no
/// port) with the REAL pieces around it: the Gateway's ingest, which writes each batch to the REAL receipt store (over the
/// harness database) and then the REAL in-memory event buffer; the REAL live-item ruling
/// (<see cref="CcDirector.Gateway.ApiModeDisplay.ApiModeLiveRuling"/>); a REAL pushed-session roster and a REAL tenant
/// boundary; and the Gateway's own compression middleware in front, as in production. A turn starting shows nothing in
/// the ruling, which is how these tests prove an event with nothing to show sends no frame.
///
/// No sleep and no clock decides a result: every wait is on the stream itself or on a signal the code raises, and the
/// keep-alive is timed by a fake clock the tests move by hand. The <see cref="HangGuard"/> only turns a hang into a
/// failure.
/// </summary>
public sealed class SessionLiveEndpointTests : IAsyncDisposable
{
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly TenantId Bob = new("bbbbbbbb-0000-4000-8000-00000000000b");
    private const string AliceDirector = "dir-alice";
    private const string ApiSession = "5d1c0f3e-8a2b-4c6d-9e0f-1a2b3c4d5e6f";
    private const string TerminalSession = "6e2d1f4a-9b3c-4d7e-8f10-2b3c4d5e6f70";
    private const string TenantHeader = "X-Test-Tenant";
    private static readonly DateTime GenAStarted = new(2026, 10, 16, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GenBStarted = new(2026, 10, 16, 12, 30, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _ambient = new();
    private readonly HostedTenantBoundary _boundary;
    // A Gateway process: replaced whole by RestartGatewayAsync, over the same database file.
    private ApiModeEventBuffer _buffer = new();
    private ApiModeReceiptStore _receipts;
    private ApiModeConversationStore _conversation;
    private ApiModeEventIngest _ingest;
    private CountingFeed _feed;
    private DateTime _now = T0.UtcDateTime;
    private readonly PushedSessionStore _pushed;
    private readonly Gate _gate = new();
    private readonly SignallingClock _clock = new(T0);
    private WebApplication? _app;
    private HttpClient? _client;

    public SessionLiveEndpointTests()
    {
        _boundary = new HostedTenantBoundary(_ambient, _harness.OpenDevices(), hosted: false);
        _receipts = new ApiModeReceiptStore(_harness.Open(_ambient), () => T0.UtcDateTime);
        _conversation = new ApiModeConversationStore(_harness.Open(_ambient), () => T0.UtcDateTime);
        _ingest = new ApiModeEventIngest(_buffer, _receipts, _conversation, _boundary);
        _feed = new CountingFeed(_buffer);
        _pushed = new PushedSessionStore(() => _now);
        _pushed.RegisterConnection(Alice, AliceDirector, "conn-alice");
        Assert.True(_pushed.ApplySnapshot(Alice, AliceDirector, "conn-alice", 1, new List<SessionDto>
        {
            new() { SessionId = ApiSession, Name = "Pi in API mode", Agent = "Pi", ActivityState = "Working", LastActivityAt = _now, ConnectionMode = ConnectionModes.Api, WheelHolder = WheelHolders.Protocol },
            new() { SessionId = TerminalSession, Name = "Pi in a terminal", Agent = "Pi", ActivityState = "Working", LastActivityAt = _now, ConnectionMode = ConnectionModes.Terminal },
        }));
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Open();
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        _harness.Dispose();
    }

    // ----- the stream: replay, then live -----

    [Fact]
    public async Task Stream_NoCursor_OpensWithTheStoredConversationThenFollowsLive()
    {
        await StartAsync();
        PushA(Ready(1), Delivered(2, "p1"), Started(3));

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var opening = await stream.NextAsync();
        PushA(Text(4, "PONG"));
        var live = await stream.NextAsync();

        // ONE frame opens the stream: the stored conversation with the receipts in their places, under the cursor of the
        // last event it reflects (the turn starting, which shows nothing). Then each live event is its own frame.
        Assert.Equal(("items", "gen-a:3"), (opening!.Event, opening.Id));
        Assert.Equal(new[]
        {
            ("connection:gen-a:1", ApiModeLiveItemKinds.Connection, "Agent connected (gpt-5.6-sol)"),
            ("receipt:p1", ApiModeLiveItemKinds.Receipt, "Delivered"),
        }, Items(opening).Select(i => (i.ItemId, i.Kind, i.Text)));
        Assert.Equal(("items", "gen-a:4", ApiModeLiveItemKinds.ReplyText, "PONG", ApiModeLiveItemModes.Append),
            (live!.Event, live.Id, Single(live).Kind, Single(live).Text, Single(live).Mode));
        Assert.Equal(1, _feed.Active);
    }

    [Fact]
    public async Task Stream_CursorInTheLastEventIdHeader_ResumesAfterIt()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2), Text(3, "a"), Text(4, "b"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:2");

        Assert.Equal("gen-a:3", (await stream.NextAsync())!.Id);
        Assert.Equal("gen-a:4", (await stream.NextAsync())!.Id);
    }

    [Fact]
    public async Task Stream_CursorInTheQuery_ResumesAfterIt()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2), Text(3, "a"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, after: "gen-a:2");

        Assert.Equal("gen-a:3", (await stream.NextAsync())!.Id);
    }

    [Fact]
    public async Task Stream_HeaderAndQueryBothSent_TheHeaderWins()
    {
        // A browser reconnecting keeps the address it first opened (an old after) and sends its newest cursor in the
        // header: the header is the one to honour.
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2), Text(3, "a"), Text(4, "b"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, after: "gen-a:1", lastEventId: "gen-a:3");

        Assert.Equal("gen-a:4", (await stream.NextAsync())!.Id);
    }

    // ----- the stream: never a silent gap -----

    [Fact]
    public async Task Stream_CursorOfAnOlderGeneration_SendsOneResetThenTheCurrentGeneration()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2));
        PushB(Ready(1), Delivered(2, "p2"));   // the Director restarted: a new generation, its sequence starting again

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:2");
        var reset = await stream.NextAsync();
        var opening = await stream.NextAsync();
        PushB(Text(3, "hello"));
        var live = await stream.NextAsync();

        Assert.Equal("reset", reset!.Event);
        Assert.Equal("", reset.Id);   // clears the browser's Last-Event-ID
        var body = Reset(reset);
        Assert.Equal(ApiModeLiveResetCodes.NewGeneration, body.Code);
        Assert.Equal(new[] { $"/sessions/{ApiSession}/live" }, body.RebuildFrom);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
        // Then the opening, under the cursor of the last event it reflects: the receipts and the new generation's
        // connection line in the order they arrived. p1's turn never ended in generation A, and generation B's
        // ConnectionReady proves it is over, so its end was lost (item 7)...
        Assert.Equal("gen-b:2", opening!.Id);
        Assert.Equal(new[] { "receipt:p1", "connection:gen-b:1", "receipt:p2" }, Items(opening).Select(i => i.ItemId));
        Assert.Equal(new[] { ApiModeLiveRuling.LostWords, "Delivered" },
            Items(opening).Where(i => i.Kind == ApiModeLiveItemKinds.Receipt).Select(i => i.Text));
        // ...and the live items after it.
        Assert.Equal(("gen-b:3", "hello"), (live!.Id, Single(live).Text));
    }

    [Fact]
    public async Task Stream_CursorTheBufferHasLetGo_SendsAMissedEventsResetThenWhatIsHeld()
    {
        await StartAsync();
        // Five more than the buffer holds: sequences 1 to 5 are let go.
        PushA(Enumerable.Range(1, ApiModeEventBuffer.MaxEventsPerSession + 5).Select(i => (SessionEvent)Text(i, "x")).ToArray());

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:1");
        var reset = await stream.NextAsync();
        var opening = await stream.NextAsync();

        Assert.Equal(("reset", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, Reset(reset).Code));
        // The reply has no final yet: the opening shows it at its place with what the held deltas streamed.
        Assert.Equal($"gen-a:{ApiModeEventBuffer.MaxEventsPerSession + 5}", opening!.Id);
        Assert.Equal(("reply:m1", ApiModeLiveItemModes.Replace, ApiModeEventBuffer.MaxEventsPerSession),
            (Single(opening).ItemId, Single(opening).Mode, Single(opening).Text.Length));
    }

    [Fact]
    public async Task Stream_CursorAfterAGatewayRestart_SendsAMissedEventsReset()
    {
        // The Gateway holds nothing for the session (its buffer is in-process): a client carrying a cursor is told to
        // rebuild rather than left waiting for events that will never be replayed.
        await StartAsync();

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:7");
        var reset = await stream.NextAsync();
        PushA(Delivered(8, "p3"));
        var live = await stream.NextAsync();

        Assert.Equal(("reset", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, Reset(reset).Code));
        Assert.Equal(("items", "gen-a:8", "Delivered"), (live!.Event, live.Id, Single(live).Text));
    }

    [Fact]
    public async Task Stream_TheDirectorRestartsWhileFollowing_SendsAResetThenTheNewGeneration()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        Assert.Equal("gen-a:1", (await stream.NextAsync())!.Id);
        PushB(Delivered(1, "p2"), Text(2, "hello"));
        var reset = await stream.NextAsync();
        var opening = await stream.NextAsync();
        PushB(Text(3, " there"));
        var next = await stream.NextAsync();

        Assert.Equal(("reset", "", ApiModeLiveResetCodes.NewGeneration), (reset!.Event, reset.Id, Reset(reset).Code));
        // The opening is read under the feed's lock after the whole batch was stored, so it already holds both of the
        // new generation's events - the reply with what it has streamed - and neither is sent again after it.
        Assert.Equal("gen-b:2", opening!.Id);
        Assert.Equal(new[] { ("receipt:p1", "Delivered"), ("receipt:p2", "Delivered"), ("reply:m1", "hello") },
            Items(opening).Select(i => (i.ItemId, i.Text)));
        Assert.Equal(("items", "gen-b:3", " there", ApiModeLiveItemModes.Append), (next!.Event, next.Id, Single(next).Text, Single(next).Mode));
    }

    [Fact]
    public async Task Stream_CursorBeforeWhatThisGatewayHeld_SendsAMissedEventsReset()
    {
        // This Gateway began holding the generation at sequence 5 (it restarted mid-generation): it never held 3 and 4,
        // so a client at 2 cannot be continued, even though nothing was let go.
        await StartAsync();
        PushA(Text(5, "a"), Text(6, "b"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:2");
        var reset = await stream.NextAsync();
        var opening = await stream.NextAsync();

        Assert.Equal(("reset", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, Reset(reset).Code));
        Assert.Equal(("gen-a:6", "ab"), (opening!.Id, Single(opening).Text));
    }

    [Fact]
    public async Task Stream_NoCursorWhileTheGenerationIsHeldFromTheMiddle_SendsNoReset()
    {
        // A client with no cursor holds nothing, so it starts from durable state whatever the feed holds: the held
        // items follow with no reset in front of them.
        await StartAsync();
        PushA(Text(5, "a"), Text(6, "b"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var first = await stream.NextAsync();

        Assert.Equal(("items", "gen-a:6", "ab"), (first!.Event, first.Id, Single(first).Text));
    }

    [Fact]
    public async Task Stream_ABrandNewSessionWithNothingHeld_StreamsCleanlyFromItsFirstEvent()
    {
        // The feed answers a session with nothing held as not complete. To a client with no cursor that is not a gap:
        // it is sent no reset, and the first event arrives as an ordinary item.
        await StartAsync();

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        Assert.Equal(1, _feed.Active);
        PushA(Delivered(1, "p1"));
        var first = await stream.NextAsync();

        Assert.Equal(("items", "gen-a:1", ApiModeLiveItemKinds.Receipt), (first!.Event, first.Id, Single(first).Kind));
    }

    // ----- the ruling, per reader -----

    [Fact]
    public async Task Stream_ATurnThatFails_SendsItsLineAndTheReceiptBesideDeliveredInOneFrame()
    {
        // Event model rule 5: a receipt shows its turn's outcome beside Delivered. One event, two items, one frame.
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2));

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        Assert.Equal("gen-a:2", (await stream.NextAsync())!.Id);
        PushA(Failed(3, "Pi exited during the turn (exit code 137)"));
        var frame = await stream.NextAsync();

        Assert.Equal("gen-a:3", frame!.Id);
        Assert.Equal(new[]
        {
            (ApiModeLiveItemKinds.TurnEnded, "Turn failed: Pi exited during the turn (exit code 137)"),
            (ApiModeLiveItemKinds.Receipt, "Delivered - the turn failed: Pi exited during the turn (exit code 137)"),
        }, Items(frame).Select(i => (i.Kind, i.Text)));
    }

    [Fact]
    public async Task Stream_AToolThatStartedBeforeTheCursor_KeepsItsTitleWhenItFinishes()
    {
        // The finish carries no tool name. A reader resuming between the start and the finish still gets the real title,
        // because its ruling is fed the held events up to its cursor before anything is sent.
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2), ToolStart(3, "bash", "ls -la"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:3");
        PushA(ToolEnd(4));
        var card = Single((await stream.NextAsync())!);

        Assert.Equal((ApiModeLiveItemKinds.ToolCard, "bash", "done", "ls -la"), (card.Kind, card.Text, card.Status, card.Detail));
    }

    [Fact]
    public async Task Items_AToolAcrossTwoPolls_KeepsItsTitleWhenItFinishes()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2), ToolStart(3, "bash", "ls -la"));
        var first = await GetPageAsync(Alice, ApiSession);
        PushA(ToolEnd(4));

        var second = await GetPageAsync(Alice, ApiSession, after: first.Next);

        var card = Assert.Single(second.Items);
        Assert.Equal(("bash", "done"), (card.Text, card.Status));
    }

    [Fact]
    public async Task Stream_NoCursorOnAGenerationHeldFromItsMiddle_OpensWithTheStoredReceipts()
    {
        // The Gateway holds this generation only from sequence 5 (it restarted): the stored receipts open the stream with
        // what is held, and no reset, since the client holds nothing to reset.
        await StartAsync();
        RecordDelivered("p0");
        PushA(Text(5, "a"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var opening = await stream.NextAsync();

        Assert.Equal(("items", "gen-a:5"), (opening!.Event, opening.Id));
        Assert.Equal(new[] { ("receipt:p0", "Delivered"), ("reply:m1", "a") }, Items(opening).Select(i => (i.ItemId, i.Text)));
    }

    [Fact]
    public async Task Stream_StartedFromAStoredRefusal_ARetryRefusedLiveShowsTheReasonTheReceiptsRouteAnswers()
    {
        // The review of pull request #4142, finding 1. The Gateway holds nothing for the session (it restarted), and the
        // store holds gw-1 refused while the Director restarted Pi. The user presses again: the retry is queued behind a
        // running turn, then refused by Pi itself. The stream must show the second reason, not keep the first.
        const string piNotRunning = "Pi is not running: the process exited";
        const string busy = "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.";
        await StartAsync();
        using (_boundary.EnterScope(Alice))
            _receipts.RecordRefused(AliceDirector, ApiSession, "gw-1", piNotRunning, "director", new ApiModeEventPosition("gen-a", GenAStarted, 40), T0.UtcDateTime);

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var opening = await stream.NextAsync();
        PushA(Queued(41, "gw-1"), Refused(42, "gw-1", busy, PromptRefusedBy.Agent));
        var queued = await stream.NextAsync();
        var refused = await stream.NextAsync();

        Assert.Equal("Refused: " + piNotRunning, Single(opening!).Text);
        Assert.Equal(("gen-a:41", "Queued, 1st in line: a turn is running"), (queued!.Id, Single(queued).Text));
        Assert.Equal(("gen-a:42", "Refused: " + busy), (refused!.Id, Single(refused).Text));
        // What the receipts route answers: the stored receipt, worded by the one ruling.
        using (_boundary.EnterScope(Alice))
            Assert.Equal(Single(refused).Text, ApiModeLiveRuling.ReceiptWords(Assert.Single(_receipts.Read(ApiSession))));
    }

    [Fact]
    public async Task Stream_TheOneAheadIsDelivered_TheWaitingPromptMovesUpAndAgreesWithTheReceiptsRoute()
    {
        await StartAsync();
        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        PushA(Queued(1, "p1"), Queued(2, "p2"));
        var first = await stream.NextAsync();
        var second = await stream.NextAsync();
        PushA(Delivered(3, "p1"));
        var delivered = await stream.NextAsync();

        Assert.Equal("Queued, 1st in line: a turn is running", Single(first!).Text);
        Assert.Equal("Queued, 2nd in line: a turn is running", Single(second!).Text);
        // One frame for the delivery: p1's receipt, then p2 at its new place.
        Assert.Equal(new[] { ("p1", "Delivered"), ("p2", "Queued, 1st in line: a turn is running") },
            Items(delivered!).Select(i => (i.PromptId!, i.Text)));
        // What the receipts route answers for the same receipts, from the store the ingest wrote.
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("device-" + Alice.Value[..4], Alice.Value, "browser", "active");
        var route = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<SessionReceiptsDto>>(
            SessionReceiptsEndpoint.Read(ctx, ApiSession, _receipts, _pushed, StaleAfter, _boundary)).Value!;
        Assert.Equal(Items(delivered!).Select(i => (i.PromptId!, i.Text)), route.Receipts.Select(r => (r.PromptId, r.Words)));
    }

    // ----- the stream: a continued cursor never shows a wrong picture (review of pull request #4083, finding 1) -----

    [Fact]
    public async Task Stream_AToolWhoseStartTheBufferLetGo_ResetsRatherThanReplaceItsTitle()
    {
        // The client followed from the start, so it shows the card "bash". 2,000 more events push the tool's start out of
        // the buffer while the tool runs; the client reconnects at its newest cursor, which the buffer can still continue.
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2), ToolStart(3, "bash", "ls -la"));
        PushA(Enumerable.Range(4, ApiModeEventBuffer.MaxEventsPerSession).Select(i => (SessionEvent)Text(i, "x")).ToArray());

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:2003");
        PushA(ToolEnd(2004));
        var reset = await stream.NextAsync();
        var opening = await stream.NextAsync();

        // A reset, then the opening, which already holds the finish: the stored card kept the title its start gave it.
        Assert.Equal(("reset", "", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, reset.Id, Reset(reset).Code));
        Assert.Equal("gen-a:2004", opening!.Id);
        var items = Items(opening);
        Assert.Equal(new[] { "receipt:p1", "tool:tc1", "reply:m1" }, items.Select(i => i.ItemId));
        Assert.Equal(("bash", "done", "ls -la"), (items[1].Text, items[1].Status, items[1].Detail));
    }

    [Fact]
    public async Task Stream_ATurnWhoseStartTheBufferLetGo_ResetsAndTheReceiptShowsTheFailedTurn()
    {
        // The turn's start and its prompt's delivery are let go while the turn runs; the client reconnects, and the
        // turn then fails. Continued, the client would keep a plain "Delivered" (event model rule 5 forbids it).
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2));
        PushA(Enumerable.Range(3, ApiModeEventBuffer.MaxEventsPerSession).Select(i => (SessionEvent)Text(i, "x")).ToArray());

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:2002");
        PushA(Failed(2003, "Pi exited during the turn (exit code 137)"));
        var reset = await stream.NextAsync();
        var opening = await stream.NextAsync();

        Assert.Equal(("reset", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, Reset(reset).Code));
        Assert.Equal("gen-a:2003", opening!.Id);
        var items = Items(opening);
        Assert.Equal(new[] { "receipt:p1", "reply:m1", "turn:t1" }, items.Select(i => i.ItemId));
        Assert.Equal("Delivered - the turn failed: Pi exited during the turn (exit code 137)", items[0].Text);
        Assert.Equal((ApiModeLiveItemKinds.TurnEnded, "Turn failed: Pi exited during the turn (exit code 137)"), (items[2].Kind, items[2].Text));
    }

    [Fact]
    public async Task Items_ATurnWhoseStartTheBufferLetGo_AnswersAResetWithTheFailedTurnBesideDelivered()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2));
        PushA(Enumerable.Range(3, ApiModeEventBuffer.MaxEventsPerSession).Select(i => (SessionEvent)Text(i, "x")).ToArray());
        PushA(Text(2003, "y"), Failed(2004, "Pi exited during the turn (exit code 137)"));

        var page = await GetPageAsync(Alice, ApiSession, after: "gen-a:2002");

        // The page is the opening: the receipt beside the failed turn, the reply with what the held deltas streamed, and
        // the turn's line.
        Assert.Equal(ApiModeLiveResetCodes.MissedEvents, page.Reset!.Code);
        Assert.Equal(new[] { "receipt:p1", "reply:m1", "turn:t1" }, page.Items.Select(i => i.ItemId));
        Assert.Equal("Delivered - the turn failed: Pi exited during the turn (exit code 137)", page.Items[0].Text);
        Assert.EndsWith("xy", page.Items[1].Text, StringComparison.Ordinal);
        Assert.Equal("Turn failed: Pi exited during the turn (exit code 137)", page.Items[2].Text);
        Assert.Equal("gen-a:2004", page.Next);
    }

    [Fact]
    public async Task Stream_HeldFromTheMiddleButEverythingOpenIsHeld_ContinuesWithNoReset()
    {
        // The buffer let the oldest events go, but the turn, its prompt and its tool all started after them: the
        // ruling remembers what the client shows, so the client is continued.
        await StartAsync();
        PushA(Enumerable.Range(1, ApiModeEventBuffer.MaxEventsPerSession + 5).Select(i => (SessionEvent)Text(i, "x")).ToArray());
        PushA(Delivered(2006, "p1"), Started(2007), ToolStart(2008, "bash", "ls -la"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:2008");
        PushA(ToolEnd(2009), Failed(2010, "the model errored"));
        var card = await stream.NextAsync();
        var ended = await stream.NextAsync();

        Assert.Equal(("gen-a:2009", "bash", "done", "ls -la"), (card!.Id, Single(card).Text, Single(card).Status, Single(card).Detail));
        Assert.Equal(new[]
        {
            ("turn:t1", "Turn failed: the model errored"),
            ("receipt:p1", "Delivered - the turn failed: the model errored"),
        }, Items(ended!).Select(i => (i.ItemId, i.Text)));
    }

    // ----- the stream: the keep-alive (review of pull request #4083, finding 3) -----

    [Fact]
    public async Task Stream_QuietForTheKeepAliveInterval_SendsAKeepAliveCommentEachTime()
    {
        await StartAsync();
        await using var stream = await OpenStreamAsync(Alice, ApiSession);

        await _clock.TimerCreatedAsync();
        _clock.Advance(SessionLiveEndpoint.KeepAliveInterval);
        var first = (await stream.NextLineAsync(), await stream.NextLineAsync());
        await _clock.TimerCreatedAsync();
        _clock.Advance(SessionLiveEndpoint.KeepAliveInterval);
        var second = (await stream.NextLineAsync(), await stream.NextLineAsync());
        PushA(Text(1, "a"));
        var live = await stream.NextAsync();

        Assert.Equal((": keep-alive", ""), first);
        Assert.Equal((": keep-alive", ""), second);
        Assert.Equal("gen-a:1", live!.Id);   // the stream carries on after them
    }

    [Fact]
    public async Task Stream_AnEventBeforeTheInterval_IsSentWithNoKeepAliveBeforeIt()
    {
        await StartAsync();
        await using var stream = await OpenStreamAsync(Alice, ApiSession);

        await _clock.TimerCreatedAsync();
        _clock.Advance(SessionLiveEndpoint.KeepAliveInterval - TimeSpan.FromTicks(1));
        PushA(Text(1, "a"));

        Assert.Equal("id: gen-a:1", await stream.NextLineAsync());
    }

    [Fact]
    public async Task Stream_AClientThatVanishedWithoutClosing_IsFoundByTheKeepAliveAndReleased()
    {
        // The session is quiet, so no event would ever write to the client. The keep-alive's write fails, which ends
        // the response and lets the subscription go.
        await StartAsync();
        PushA(Delivered(1, "p1"));
        var stream = await OpenStreamAsync(Alice, ApiSession);
        Assert.Equal("gen-a:1", (await stream.NextAsync())!.Id);
        await _clock.TimerCreatedAsync();

        _gate.Break();
        _clock.Advance(SessionLiveEndpoint.KeepAliveInterval);
        await _feed.Released.WaitAsync(HangGuard);

        Assert.Equal(0, _feed.Active);
        await stream.DisposeAsync();
    }

    // ----- the stream: the client's side -----

    [Fact]
    public async Task Stream_TheClientDisconnects_TheSubscriptionIsReleased()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"));
        var stream = await OpenStreamAsync(Alice, ApiSession);
        Assert.Equal("gen-a:1", (await stream.NextAsync())!.Id);
        Assert.Equal(1, _feed.Active);

        await stream.DisposeAsync();
        await _feed.Released.WaitAsync(HangGuard);

        Assert.Equal(0, _feed.Active);
        PushA(Started(2));   // nothing is listening any more, and the feed carries on
    }

    [Fact]
    public async Task Stream_AClientThatFallsBehind_IsDroppedWithAResetAndNeverHoldsUpTheFeed()
    {
        await StartAsync(liveBacklog: 2);
        PushA(Delivered(1, "p1"));
        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        Assert.Equal("gen-a:1", (await stream.NextAsync())!.Id);

        // The client stops reading: every write to its connection now waits. The session then produces more than a
        // whole replay plus the backlog. The feed hands each event over synchronously, so if the relay ever waited on
        // the client this call would never return.
        _gate.Close();
        PushA(Enumerable.Range(2, ApiModeEventBuffer.MaxEventsPerSession + 10).Select(i => (SessionEvent)Text(i, "x")).ToArray());
        Assert.Equal(1, _feed.Active);   // still attached until the response loop notices

        _gate.Open();
        var frames = await stream.ReadToEndAsync();
        await _feed.Released.WaitAsync(HangGuard);

        // At most the one event that was being written when the client stopped, then the reset, then the end.
        Assert.InRange(frames.Count, 1, 2);
        var reset = frames[^1];
        Assert.Equal(("reset", ApiModeLiveResetCodes.FellBehind), (reset.Event, Reset(reset).Code));
        Assert.All(frames.SkipLast(1), f => Assert.Equal("gen-a:2", f.Id));
        Assert.Equal(0, _feed.Active);
    }

    [Fact]
    public async Task Stream_RequestedWithCompression_IsSentUncompressedEventByEvent()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, acceptCompression: true);
        Assert.Equal("gen-a:1", (await stream.NextAsync())!.Id);
        PushA(Text(2, "a"));

        Assert.Equal("gen-a:2", (await stream.NextAsync())!.Id);
        Assert.Empty(stream.Response.Content.Headers.ContentEncoding);
        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType!.MediaType);
    }

    // ----- the poll form -----

    [Fact]
    public async Task Items_NoCursor_ListsTheOpeningWithTheNextCursor()
    {
        await StartAsync();
        PushA(Ready(1), Delivered(2, "p1"), Started(3));

        var page = await GetPageAsync(Alice, ApiSession);

        // The stored connection line carries the sequence of the event that wrote it; a receipt from durable state, 0.
        Assert.Null(page.Reset);
        Assert.Equal(new[] { (ApiModeLiveItemKinds.Connection, 1L), (ApiModeLiveItemKinds.Receipt, 0L) }, page.Items.Select(i => (i.Kind, i.Sequence)));
        Assert.Equal("gen-a:3", page.Next);   // past the turn starting, which shows nothing
    }

    [Fact]
    public async Task Items_AReceipt_CarriesThePromptTextVerbatim()
    {
        await StartAsync();
        const string spoken = "  run the tests\nthen tell me what failed  \n";
        PushA(new PromptQueued { SessionId = ApiSession, Sequence = 1, At = T0, PromptId = "p1", PromptText = spoken, Reason = PromptQueuedReason.TurnRunning, Position = 1 });

        var page = await GetPageAsync(Alice, ApiSession);

        var item = Assert.Single(page.Items);
        Assert.Equal(ApiModeLiveItemKinds.Receipt, item.Kind);
        Assert.Equal(spoken, item.PromptText);
    }

    [Fact]
    public async Task Items_AResetStartsFromAStoredReceiptWithoutText_PromptTextIsNull()
    {
        await StartAsync();
        RecordDelivered("p0");   // stored with no prompt text, as from an older Director

        var page = await GetPageAsync(Alice, ApiSession, after: "gen-a:7");

        Assert.Equal(ApiModeLiveResetCodes.MissedEvents, page.Reset!.Code);
        Assert.Null(Assert.Single(page.Items).PromptText);
    }

    [Fact]
    public async Task Items_ABrandNewSessionWithNothingHeld_AnswersEmptyWithNoReset()
    {
        await StartAsync();

        var page = await GetPageAsync(Alice, ApiSession);

        Assert.Null(page.Reset);
        Assert.Empty(page.Items);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task Items_WithTheNextCursor_ListsOnlyWhatCameAfterIt()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2));
        var first = await GetPageAsync(Alice, ApiSession);
        PushA(Text(3, "PONG"));

        var second = await GetPageAsync(Alice, ApiSession, after: first.Next);
        var third = await GetPageAsync(Alice, ApiSession, after: second.Next);

        Assert.Equal(new[] { ("PONG", 3L) }, second.Items.Select(i => (i.Text, i.Sequence)));
        Assert.Empty(third.Items);
        Assert.Null(third.Reset);
        Assert.Equal("gen-a:3", third.Next);   // nothing new: keep the same cursor
    }

    [Fact]
    public async Task Items_LastEventRuledToNothing_NextStillMovesPastIt()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2));

        var page = await GetPageAsync(Alice, ApiSession);

        Assert.Equal(new[] { "receipt:p1" }, page.Items.Select(i => i.ItemId));
        Assert.Equal("gen-a:2", page.Next);
    }

    [Fact]
    public async Task Items_CursorOfAnOlderGeneration_AnswersAResetWithTheCurrentItems()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"));
        PushB(Delivered(1, "p2"), Started(2));

        var page = await GetPageAsync(Alice, ApiSession, after: "gen-a:1");

        Assert.Equal(ApiModeLiveResetCodes.NewGeneration, page.Reset!.Code);
        // The stored receipts lead; the new generation's events add nothing they do not already say.
        Assert.Equal(new[] { ("p1", 0L), ("p2", 0L) }, page.Items.Select(i => (i.PromptId!, i.Sequence)));
        Assert.Equal("gen-b:2", page.Next);
    }

    [Fact]
    public async Task Stream_AQueuedPromptTheLaterGenerationDidNotReannounce_IsSentUnknownAsTheReceiptsEndpointSaysIt()
    {
        // Item 9: p1 and p2 queued in generation A; generation B re-announces p2 only, then the agent connects.
        await StartAsync();
        PushA(Queued(1, "p1"), Queued(2, "p2"));
        PushB(Queued(1, "p2"), Ready(2));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:2");
        var reset = await stream.NextAsync();
        var receipts = await stream.NextAsync();
        var (status, body) = await GetAsync(Alice, ApiSession, "/receipts");

        Assert.Equal(("reset", ApiModeLiveResetCodes.NewGeneration), (reset!.Event, Reset(reset).Code));
        Assert.Equal(HttpStatusCode.OK, status);
        var streamed = Items(receipts!).Where(i => i.PromptId is not null).ToDictionary(i => i.PromptId!, i => (i.Text, i.Tone));
        var served = JsonSerializer.Deserialize<SessionReceiptsDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Receipts.ToDictionary(r => r.PromptId, r => (r.Words, r.Tone));
        Assert.Equal(("Not known - the Director reconnected without this prompt in its queue or any record of sending it", "neutral"), streamed["p1"]);
        Assert.Equal(("Queued, 1st in line: a turn is running", "neutral"), streamed["p2"]);
        Assert.Equal(streamed.OrderBy(kv => kv.Key, StringComparer.Ordinal), served.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Stream_AQueuedPromptALaterTurnCarries_IsSentDeliveredAsTheReceiptsEndpointSaysIt()
    {
        // Item 9: p1 queued in generation A; its delivery fell in the hole, generation B opens with the turn that carries
        // it, and the user queues p2 behind that turn.
        await StartAsync();
        PushA(Queued(1, "p1"));
        PushB(Started(1), Queued(2, "p2"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:1");
        var reset = await stream.NextAsync();
        var receipts = await stream.NextAsync();
        var (status, body) = await GetAsync(Alice, ApiSession, "/receipts");

        Assert.Equal(("reset", ApiModeLiveResetCodes.NewGeneration), (reset!.Event, Reset(reset).Code));
        Assert.Equal(HttpStatusCode.OK, status);
        var streamed = Items(receipts!).Where(i => i.PromptId is not null).ToDictionary(i => i.PromptId!, i => (i.Text, i.Tone));
        var served = JsonSerializer.Deserialize<SessionReceiptsDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Receipts.ToDictionary(r => r.PromptId, r => (r.Words, r.Tone));
        Assert.Equal(("Delivered", "green"), streamed["p1"]);
        Assert.Equal(("Queued, 1st in line: a turn is running", "neutral"), streamed["p2"]);
        Assert.Equal(streamed.OrderBy(kv => kv.Key, StringComparer.Ordinal), served.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Items_CursorAfterAGatewayRestart_AnswersAResetAndNoCursor()
    {
        await StartAsync();

        var page = await GetPageAsync(Alice, ApiSession, after: "gen-a:7");
        var again = await GetPageAsync(Alice, ApiSession, after: page.Next);

        Assert.Equal(ApiModeLiveResetCodes.MissedEvents, page.Reset!.Code);
        Assert.Null(page.Next);   // ask for everything held next time, not the same cursor again
        Assert.Null(again.Reset);   // which does not reset a second time
    }

    [Fact]
    public async Task Items_RequestedWithCompression_IsCompressedLikeAnyJsonRead()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{ApiSession}/live/items");
        request.Headers.Add(TenantHeader, Alice.Value);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
    }

    // ----- who may read, and what is refused -----

    [Theory]
    [InlineData("/live")]
    [InlineData("/live/items")]
    public async Task AnotherAccountsSession_IsNotFoundAndServesNothing(string route)
    {
        await StartAsync();
        PushA(Delivered(1, "p1"));

        var (status, body) = await GetAsync(Bob, ApiSession, route);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.DoesNotContain("gen-a", body);
        Assert.Equal(0, _feed.Subscribed);
    }

    [Theory]
    [InlineData("/live")]
    [InlineData("/live/items")]
    public async Task TerminalModeSession_IsRefusedWithAPlainSentence(string route)
    {
        await StartAsync();

        var (status, body) = await GetAsync(Alice, TerminalSession, route);

        Assert.Equal(HttpStatusCode.Conflict, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(SessionLiveEndpoint.TerminalModeRefusal, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(SessionLiveEndpoint.NotApiModeCode, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, _feed.Subscribed);
    }

    [Theory]
    [InlineData("/live")]
    [InlineData("/live/items")]
    public async Task UnknownSession_IsNotFound(string route)
    {
        await StartAsync();

        var (status, _) = await GetAsync(Alice, Guid.NewGuid().ToString(), route);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData("/live")]
    [InlineData("/live/items")]
    public async Task RequestWithNoBoundTenant_IsForbidden(string route)
    {
        await StartAsync();

        var (status, _) = await GetAsync(null, ApiSession, route);

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Theory]
    [InlineData("gen-a")]
    [InlineData("gen-a:")]
    [InlineData(":4")]
    [InlineData("gen-a:-1")]
    [InlineData("gen-a:x")]
    public async Task UnreadableCursor_IsABadRequestThatSaysWhatToSend(string cursor)
    {
        await StartAsync();

        var (status, body) = await GetAsync(Alice, ApiSession, "/live/items?after=" + Uri.EscapeDataString(cursor));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("generation:sequence", body);
    }

    [Fact]
    public async Task SessionIdThatIsNotAnIdentifier_IsABadRequest()
    {
        await StartAsync();

        var (status, _) = await GetAsync(Alice, "not-a-session", "/live/items");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task DirectorAwayButEventsHeld_StillServesThem()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"));
        _now += TimeSpan.FromMinutes(5);   // the roster row goes stale: its computer is away

        var page = await GetPageAsync(Alice, ApiSession);

        Assert.Equal(new[] { "receipt:p1" }, page.Items.Select(i => i.ItemId));
    }

    [Fact]
    public async Task DirectorAwayAndNothingHeld_IsRefusedWithTheReason()
    {
        await StartAsync();
        _now += TimeSpan.FromMinutes(5);

        var (status, body) = await GetAsync(Alice, ApiSession, "/live/items");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains(SessionLiveEndpoint.DirectorAwayRefusal, JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    // ----- the feed's side of a connection, on its own -----

    [Fact]
    public void Offer_PastCapacity_FallsBehindAtOnceAndIgnoresTheRest()
    {
        var connection = new SessionLiveEndpoint.LiveConnection(capacity: 2);

        connection.Offer(Held(1));
        connection.Offer(Held(2));
        Assert.False(connection.FellBehind);
        connection.Offer(Held(3));
        connection.Offer(Held(4));

        Assert.True(connection.FellBehind);
        Assert.True(connection.Reader.TryRead(out var first));
        Assert.True(connection.Reader.TryRead(out var second));
        Assert.False(connection.Reader.TryRead(out _));
        Assert.True(connection.Reader.Completion.IsCompleted);
        Assert.Equal((1L, 2L), (first!.Event.Sequence, second!.Event.Sequence));
    }

    [Theory]
    [InlineData("gen-a:0", "gen-a", 0L)]
    [InlineData("gen-a:2004", "gen-a", 2004L)]
    [InlineData("2026-10-16T12:00:00Z:5", "2026-10-16T12:00:00Z", 5L)]   // a generation may hold a colon
    public void TryParseCursor_AFormattedCursor_RoundTrips(string text, string generation, long sequence)
    {
        Assert.True(SessionLiveEndpoint.TryParseCursor(text, out var cursor));
        Assert.Equal(new ApiModeEventCursor(generation, sequence), cursor);
        Assert.Equal(text, SessionLiveEndpoint.Format(cursor));
    }

    // ----- the opening: the durable conversation -----

    [Fact]
    public async Task Stream_NoCursorAfterAGatewayRestart_OpensWithWhatAReaderSawLive()
    {
        // Arrange: a reader follows a whole turn live, applying every item as a client does.
        await StartAsync();
        var sawLive = new ClientView();
        await using (var stream = await OpenStreamAsync(Alice, ApiSession))
        {
            PushA(Ready(1), Queued(2, "p1"), Delivered(3, "p1"), Started(4), Text(5, "Lis"), Text(6, "ting"), Reply(7, "m1", "Listing"),
                ToolStart(8, "bash", "ls -la"), ToolEnd(9), Reply(10, "m2", "Two files."), Done(11));
            Frame? frame;
            do
            {
                frame = await stream.NextAsync();
                sawLive.Apply(Items(frame!));
            }
            while (frame!.Id != "gen-a:11");
        }

        // Act: the Gateway restarts (its buffer is in memory, so it holds no event of the session), and a new reader
        // attaches with no cursor; another polls.
        await RestartGatewayAsync();
        await using var reopened = await OpenStreamAsync(Alice, ApiSession);
        var opening = await reopened.NextAsync();
        var page = await GetPageAsync(Alice, ApiSession);

        // Assert: one frame, with no cursor since no event is held, that shows exactly what the live reader shows. The
        // one field apart is a receipt's turn: the live stream sends a receipt again only when its words change, and
        // starting a turn does not change "Delivered", so the live reader never got the turn id the stored receipt has.
        Assert.Equal(("items", (string?)null), (opening!.Event, opening.Id));
        var reopenedView = new ClientView();
        reopenedView.Apply(Items(opening));
        Assert.Equal(WithoutReceiptTurns(sawLive.Shown), WithoutReceiptTurns(reopenedView.Shown));
        Assert.Equal(((string?)null, "t1"), (sawLive.Shown[1].TurnId, reopenedView.Shown[1].TurnId));
        Assert.Equal(new[] { "connection:gen-a:1", "receipt:p1", "reply:m1", "tool:tc1", "reply:m2", "turn:t1" }, reopenedView.Shown.Select(i => i.ItemId));
        var polled = new ClientView();
        polled.Apply(page.Items);
        Assert.Equal(reopenedView.Shown, polled.Shown);
        Assert.Null(page.Next);

        static IEnumerable<object> WithoutReceiptTurns(IEnumerable<(string ItemId, string Kind, string Text, string? Status, string? Detail, string Tone, string? TurnId, string? PromptId, string? PromptText)> shown)
            => shown.Select(i => (object)(i.Kind == ApiModeLiveItemKinds.Receipt ? i with { TurnId = null } : i));
    }

    [Fact]
    public async Task Stream_NoCursorAfterATurnsEndWasLost_OpensWithTheReceiptAsTheLiveReaderSawIt()
    {
        // Arrange: p1's turn never ends in generation A. A reader opens in generation B before anything proves that turn
        // over, then follows live while a different turn starts, which does (item 7, ApiModeLostTurnEnd).
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2));
        PushB(Text(1, "x"));
        var sawLive = new ClientView();
        await using (var stream = await OpenStreamAsync(Alice, ApiSession))
        {
            var first = await stream.NextAsync();
            Assert.Equal("Delivered", Items(first!).Single(i => i.ItemId == "receipt:p1").Text);
            sawLive.Apply(Items(first!));
            PushB(new TurnStarted { SessionId = ApiSession, Sequence = 2, At = T0, TurnId = "t2", PromptIds = new[] { "p2" } });
            var lost = await stream.NextAsync();
            Assert.Equal(("gen-b:2", "receipt:p1", ApiModeLiveRuling.LostWords), (lost!.Id, Single(lost).ItemId, Single(lost).Text));
            sawLive.Apply(Items(lost));
        }

        // Act: a new reader attaches with no cursor; then the Gateway restarts and another does.
        await using var reopened = await OpenStreamAsync(Alice, ApiSession);
        var opening = await reopened.NextAsync();
        await RestartGatewayAsync();
        await using var afterRestart = await OpenStreamAsync(Alice, ApiSession);
        var restartedOpening = await afterRestart.NextAsync();

        // Assert: the receipt comes from the receipt store, which ruled the same lost ending, so each opening shows it
        // exactly as the live reader shows it - its words, its tone, its turn and its prompt text.
        var live = sawLive.Shown.Single(i => i.ItemId == "receipt:p1");
        Assert.Equal((ApiModeLiveRuling.LostWords, "neutral", "t1", "text of p1"), (live.Text, live.Tone, live.TurnId, live.PromptText));
        foreach (var frame in new[] { opening, restartedOpening })
        {
            var view = new ClientView();
            view.Apply(Items(frame!));
            Assert.Equal(live, view.Shown.Single(i => i.ItemId == "receipt:p1"));
        }
    }

    [Fact]
    public async Task Stream_NoCursorMidReply_OpensWithWhatHasStreamedThenFollowsWithNoGapAndNoDouble()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2), Text(3, "Hel"), Text(4, "lo"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var opening = await stream.NextAsync();
        PushA(Text(5, " world"), Reply(6, "m1", "Hello world"));
        var delta = await stream.NextAsync();
        var final = await stream.NextAsync();

        // The reply is in the opening with what streamed so far, as one replace; the deltas after it append to it.
        Assert.Equal("gen-a:4", opening!.Id);
        Assert.Equal(new[] { ("receipt:p1", "Delivered", ApiModeLiveItemModes.Replace), ("reply:m1", "Hello", ApiModeLiveItemModes.Replace) },
            Items(opening).Select(i => (i.ItemId, i.Text, i.Mode)));
        Assert.Equal(("gen-a:5", " world", ApiModeLiveItemModes.Append), (delta!.Id, Single(delta).Text, Single(delta).Mode));
        Assert.Equal(("gen-a:6", "Hello world"), (final!.Id, Single(final).Text));
        var view = new ClientView();
        view.Apply(Items(opening));
        view.Apply(Items(delta));
        Assert.Equal("Hello world", view.Shown.Single(i => i.ItemId == "reply:m1").Text);
    }

    [Fact]
    public async Task Stream_AToolRunningAcrossAGatewayRestart_FinishesLiveWithItsTitle()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2), ToolStart(3, "bash", "ls -la"));
        await RestartGatewayAsync();

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var opening = await stream.NextAsync();
        PushA(ToolEnd(4));
        var finished = await stream.NextAsync();

        // The opening shows the card running; the finish carries no tool name and the new Gateway never saw the start,
        // but the reader's ruling started from the stored card, so the title stays.
        Assert.Equal(("tool:tc1", "bash", "running"), (Items(opening!)[1].ItemId, Items(opening!)[1].Text, Items(opening!)[1].Status));
        Assert.Equal(("gen-a:4", "bash", "done", "ls -la"), (finished!.Id, Single(finished).Text, Single(finished).Status, Single(finished).Detail));
    }

    [Fact]
    public async Task Stream_ACursorReaderWithAStoredConversation_GetsOnlyTheEventsAfterItsCursor()
    {
        // Nothing changes for a reader that sends a cursor the held events continue: no opening, just what follows.
        await StartAsync();
        PushA(Ready(1), Delivered(2, "p1"), Started(3), Reply(4, "m1", "PONG"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:2");
        var first = await stream.NextAsync();

        Assert.Equal(("items", "gen-a:4", "reply:m1", "PONG"), (first!.Event, first.Id, Single(first).ItemId, Single(first).Text));
    }

    [Fact]
    public async Task Stream_NoCursorOnAConversationOverTheBound_OpensWithTheLineThatEarlierIsNotKept()
    {
        // Arrange: a receipt, then one reply more than the stored conversation keeps.
        await StartAsync();
        PushA(Delivered(1, "p1"));
        PushA(Enumerable.Range(2, ApiModeConversationStore.MaxItemsPerSession + 1).Select(i => (SessionEvent)Reply(i, "m" + i, "reply " + i)).ToArray());

        // Act
        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var items = Items((await stream.NextAsync())!);

        // Assert: the oldest reply was let go, and the opening says so first, never a silent gap. The receipt came
        // before what is kept, so it is left out of the opening (it is still on the receipts route).
        Assert.Equal((ApiModeLiveItemKinds.Notice, ApiModeConversationOpening.EarlierNotKeptWords), (items[0].Kind, items[0].Text));
        Assert.Equal("reply:m3", items[1].ItemId);
        Assert.Equal(ApiModeConversationStore.MaxItemsPerSession + 1, items.Count);
        Assert.DoesNotContain(items, i => i.Kind == ApiModeLiveItemKinds.Receipt);
    }

    [Fact]
    public async Task Stream_NoCursorInAnotherAccount_OpensWithOnlyThatAccountsConversation()
    {
        // Bob's Director runs a session with the same id; each account's reader opens with its own conversation only.
        await StartAsync();
        _pushed.RegisterConnection(Bob, "dir-bob", "conn-bob");
        Assert.True(_pushed.ApplySnapshot(Bob, "dir-bob", "conn-bob", 1, new List<SessionDto>
        {
            new() { SessionId = ApiSession, Name = "Bob's Pi", Agent = "Pi", ActivityState = "Working", LastActivityAt = _now, ConnectionMode = ConnectionModes.Api, WheelHolder = WheelHolders.Protocol },
        }));
        PushA(Reply(1, "m1", "for Alice"));
        Assert.NotNull(_ingest.Accept(Bob, "dir-bob", new ApiModeEventBatch
        {
            SessionId = ApiSession, Generation = "gen-a", GenerationStartedUtc = GenAStarted,
            Events = new List<string> { JsonSerializer.Serialize<SessionEvent>(Reply(1, "m1", "for Bob"), SessionEventJson.Options) },
        }));

        await using var alice = await OpenStreamAsync(Alice, ApiSession);
        await using var bob = await OpenStreamAsync(Bob, ApiSession);

        Assert.Equal("for Alice", Single((await alice.NextAsync())!).Text);
        Assert.Equal("for Bob", Single((await bob.NextAsync())!).Text);
    }

    // ----- the host -----

    /// <summary>The Gateway restarts: its in-memory buffer is gone, and new stores open the same database file.</summary>
    private async Task RestartGatewayAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        _buffer = new ApiModeEventBuffer();
        _receipts = new ApiModeReceiptStore(_harness.Open(_ambient), () => T0.UtcDateTime);
        _conversation = new ApiModeConversationStore(_harness.Open(_ambient), () => T0.UtcDateTime);
        _ingest = new ApiModeEventIngest(_buffer, _receipts, _conversation, _boundary);
        _feed = new CountingFeed(_buffer);
        await StartAsync();
    }

    private async Task StartAsync(int liveBacklog = SessionLiveEndpoint.DefaultLiveBacklog)
    {
        // The empty builder reads no settings files, so it watches none: a host that set up file watchers would
        // depend on the machine's watcher limit, which is the machine deciding whether a test passes.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        GatewayResponseCompression.AddTo(builder.Services);
        var app = builder.Build();

        // The credential the auth middleware would have verified, stamped the way it stamps it; and the client's
        // side of the connection, which a test can stop reading.
        app.Use(async (ctx, next) =>
        {
            var tenant = ctx.Request.Headers[TenantHeader].ToString();
            if (tenant.Length > 0)
            {
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "device-key";
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("device-" + tenant[..4], tenant, "browser", "active");
            }
            ctx.Response.Body = new GatedBody(ctx.Response.Body, _gate);
            await next();
        });
        GatewayResponseCompression.Use(app);
        SessionLiveEndpoint.Map(app, _feed, _receipts, _conversation, _pushed, StaleAfter, _boundary, _clock, liveBacklog);
        SessionReceiptsEndpoint.Map(app, _receipts, _pushed, StaleAfter, _boundary);

        await app.StartAsync();
        _app = app;
        _client = app.GetTestServer().CreateClient();
    }

    private async Task<LiveStream> OpenStreamAsync(TenantId tenant, string sid, string? after = null, string? lastEventId = null, bool acceptCompression = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{sid}/live" + (after is null ? "" : "?after=" + Uri.EscapeDataString(after)));
        request.Headers.Add(TenantHeader, tenant.Value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        if (acceptCompression)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        }
        var response = await _client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(HangGuard);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new LiveStream(request, response, await response.Content.ReadAsStreamAsync());
    }

    private async Task<ApiModeLivePageDto> GetPageAsync(TenantId tenant, string sid, string? after = null)
    {
        var (status, body) = await GetAsync(tenant, sid, "/live/items" + (after is null ? "" : "?after=" + Uri.EscapeDataString(after)));
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonSerializer.Deserialize<ApiModeLivePageDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(TenantId? tenant, string sid, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{sid}{route}");
        if (tenant is { } t) request.Headers.Add(TenantHeader, t.Value);
        using var response = await _client!.SendAsync(request).WaitAsync(HangGuard);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // ----- events -----

    private void PushA(params SessionEvent[] events) => Push("gen-a", GenAStarted, events);
    private void PushB(params SessionEvent[] events) => Push("gen-b", GenBStarted, events);

    /// <summary>Through the Gateway's own ingest, as the Director's batches arrive: receipts first, then the buffer.</summary>
    private void Push(string generation, DateTime started, SessionEvent[] events)
    {
        foreach (var chunk in events.Chunk(ApiModeEventBatch.MaxEvents))
        {
            var watermark = _ingest.Accept(Alice, AliceDirector, new ApiModeEventBatch
            {
                SessionId = ApiSession,
                Generation = generation,
                GenerationStartedUtc = started,
                Events = chunk.Select(e => JsonSerializer.Serialize(e, SessionEventJson.Options)).ToList(),
            });
            Assert.NotNull(watermark);
        }
    }

    /// <summary>A receipt already in the durable store, from before what the buffer holds.</summary>
    private void RecordDelivered(string prompt)
    {
        using (_boundary.EnterScope(Alice))
            _receipts.RecordDelivered(AliceDirector, ApiSession, prompt, "started", "{}", new ApiModeEventPosition("gen-a", GenAStarted, 0), T0.UtcDateTime);
    }

    private static ConnectionReady Ready(long seq) => new() { SessionId = ApiSession, Sequence = seq, At = T0, AgentSessionId = "e173b676-b5d6-4605-bc6c-cdbc82fd42a7", Model = "gpt-5.6-sol" };
    private static PromptDelivered Delivered(long seq, string prompt) => new() { SessionId = ApiSession, Sequence = seq, At = T0, PromptId = prompt, PromptText = $"text of {prompt}", Disposition = PromptDisposition.Started, Acknowledgement = """{"type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""" };
    private static PromptQueued Queued(long seq, string prompt) => new() { SessionId = ApiSession, Sequence = seq, At = T0, PromptId = prompt, Reason = PromptQueuedReason.TurnRunning, Position = 1 };
    private static PromptRefused Refused(long seq, string prompt, string reason, PromptRefusedBy by) => new() { SessionId = ApiSession, Sequence = seq, At = T0, PromptId = prompt, Reason = reason, RefusedBy = by };
    private static TurnStarted Started(long seq) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", PromptIds = new[] { "p1" } };
    private static MessageText Text(long seq, string text) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", MessageId = "m1", Channel = MessageTextChannel.Reply, Kind = MessageTextKind.Delta, Text = text };
    private static ToolCallStarted ToolStart(long seq, string name, string input) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", ToolCallId = "tc1", ToolName = name, Kind = ToolCallKind.Execute, Input = input };
    private static ToolCallFinished ToolEnd(long seq) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", ToolCallId = "tc1", Outcome = ToolCallOutcome.Succeeded, Output = "total 0", DurationMilliseconds = 12 };
    private static TurnEnded Failed(long seq, string reason) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", Outcome = TurnOutcome.Failed, Reason = reason, AgentStopReason = null, Usage = new TurnUsage { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 } };
    private static MessageText Reply(long seq, string messageId, string text) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", MessageId = messageId, Channel = MessageTextChannel.Reply, Kind = MessageTextKind.Final, Text = text };
    private static TurnEnded Done(long seq) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", Outcome = TurnOutcome.Done, Reason = null, AgentStopReason = "stop", Usage = new TurnUsage { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 } };
    private static ApiModeHeldEvent Held(long seq) => new("gen-a", GenAStarted, Text(seq, "x"));

    // ----- reading frames -----

    private static List<ApiModeLiveItem> Items(Frame frame) =>
        JsonSerializer.Deserialize<List<ApiModeLiveItem>>(frame.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    private static ApiModeLiveItem Single(Frame frame) => Assert.Single(Items(frame));
    private static ApiModeLiveResetDto Reset(Frame frame) => JsonSerializer.Deserialize<ApiModeLiveResetDto>(frame.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>What a client shows, built the way a client builds it: each item found by its id and replaced or appended
    /// to, as its mode says, in the order the items first arrived.</summary>
    private sealed class ClientView
    {
        private readonly List<string> _order = new();
        private readonly Dictionary<string, ApiModeLiveItem> _items = new(StringComparer.Ordinal);

        public void Apply(IEnumerable<ApiModeLiveItem> items)
        {
            foreach (var item in items)
            {
                if (!_items.TryGetValue(item.ItemId, out var shown))
                {
                    _order.Add(item.ItemId);
                    _items[item.ItemId] = Copy(item, item.Text);
                }
                else
                    _items[item.ItemId] = Copy(item, item.Mode == ApiModeLiveItemModes.Append ? shown.Text + item.Text : item.Text);
            }
        }

        /// <summary>Every field a client renders; the sequence is for logs, so it is left out.</summary>
        public List<(string ItemId, string Kind, string Text, string? Status, string? Detail, string Tone, string? TurnId, string? PromptId, string? PromptText)> Shown
            => _order.Select(id => _items[id]).Select(i => (i.ItemId, i.Kind, i.Text, i.Status, i.Detail, i.Tone, i.TurnId, i.PromptId, i.PromptText)).ToList();

        private static ApiModeLiveItem Copy(ApiModeLiveItem i, string text) => new()
        {
            ItemId = i.ItemId, Kind = i.Kind, Mode = ApiModeLiveItemModes.Replace, Text = text, Status = i.Status, Detail = i.Detail,
            Tone = i.Tone, TurnId = i.TurnId, PromptId = i.PromptId, PromptText = i.PromptText, Sequence = i.Sequence,
        };
    }

    /// <summary>One event of the stream. <see cref="Id"/> is null when the frame had no id line, empty when it cleared it.</summary>
    private sealed record Frame(string? Id, string Event, string Data);

    private sealed class LiveStream(HttpRequestMessage request, HttpResponseMessage response, Stream body) : IAsyncDisposable
    {
        private readonly StreamReader _reader = new(body);

        public HttpResponseMessage Response => response;

        /// <summary>The next line exactly as sent, comments included.</summary>
        public Task<string?> NextLineAsync() => _reader.ReadLineAsync().WaitAsync(HangGuard);

        /// <summary>The next event, or null at the end of the stream. Comment lines (the keep-alive) are skipped, as
        /// every Server-Sent Events parser skips them.</summary>
        public async Task<Frame?> NextAsync()
        {
            string? id = null, type = null, data = null;
            while (true)
            {
                var line = await _reader.ReadLineAsync().WaitAsync(HangGuard);
                if (line is null) return type is null ? null : new Frame(id, type, data ?? "");
                if (line.Length == 0)
                {
                    if (type is null && data is null && id is null) continue;
                    return new Frame(id, type ?? "message", data ?? "");
                }
                var colon = line.IndexOf(':');
                var field = colon < 0 ? line : line[..colon];
                var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
                switch (field)
                {
                    case "id": id = value; break;
                    case "event": type = value; break;
                    case "data": data = data is null ? value : data + "\n" + value; break;
                }
            }
        }

        public async Task<List<Frame>> ReadToEndAsync()
        {
            var frames = new List<Frame>();
            while (await NextAsync() is { } frame) frames.Add(frame);
            return frames;
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            response.Dispose();
            request.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ----- the instruments -----

    /// <summary>The real buffer, counting the subscriptions the relay holds on it and signalling each release.</summary>
    private sealed class CountingFeed(IApiModeEventFeed inner) : IApiModeEventFeed
    {
        private int _active;
        private int _subscribed;
        private TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Active => Volatile.Read(ref _active);
        public int Subscribed => Volatile.Read(ref _subscribed);
        public Task Released => _released.Task;

        public ApiModeEventReplay Read(TenantId tenant, string sessionId, ApiModeEventCursor? after = null) => inner.Read(tenant, sessionId, after);

        public T Snapshot<T>(TenantId tenant, string sessionId, Func<ApiModeEventReplay, T> withHeld) => inner.Snapshot(tenant, sessionId, withHeld);

        public ApiModeEventSubscription Subscribe(TenantId tenant, string sessionId, ApiModeEventCursor? after, Action<ApiModeHeldEvent> onEvent)
        {
            var real = inner.Subscribe(tenant, sessionId, after, onEvent);
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _subscribed);
            return new ApiModeEventSubscription(real.Replay, () =>
            {
                real.Dispose();
                Interlocked.Decrement(ref _active);
                _released.TrySetResult();
            });
        }

        public IDisposable SubscribeAll(Action<TenantId, ApiModeHeldEvent> onEvent) => inner.SubscribeAll(onEvent);
    }

    /// <summary>Whether the client is reading. While closed, every write to a response waits, as on a socket nobody reads.</summary>
    private sealed class Gate
    {
        private volatile TaskCompletionSource _open = Opened();

        public Task WhenOpen => _open.Task;
        public void Close() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() => _open.TrySetResult();

        /// <summary>The client is gone: every write from now on fails, as on a connection the network dropped.</summary>
        public void Break()
        {
            var broken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            broken.SetException(new IOException("the client is gone"));
            _open = broken;
        }

        private static TaskCompletionSource Opened()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult();
            return tcs;
        }
    }

    /// <summary>A fake clock that signals each timer created on it, so a test moves time only once the relay is waiting.</summary>
    private sealed class SignallingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly SemaphoreSlim _created = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _created.Release();
            return timer;
        }

        public async Task TimerCreatedAsync() => Assert.True(await _created.WaitAsync(HangGuard), "the relay never started waiting");
    }

    /// <summary>One response's body, behind the <see cref="Gate"/>.</summary>
    private sealed class GatedBody(Stream inner, Gate gate) : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WhenOpen.WaitAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await gate.WhenOpen.WaitAsync(cancellationToken);
            await inner.FlushAsync(cancellationToken);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException("the response is written asynchronously");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("the response is written asynchronously");
    }
}
