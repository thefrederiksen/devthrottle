using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Tenancy;
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
    private readonly ApiModeEventBuffer _buffer = new();
    private readonly ApiModeReceiptStore _receipts;
    private readonly ApiModeEventIngest _ingest;
    private readonly CountingFeed _feed;
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
        _ingest = new ApiModeEventIngest(_buffer, _receipts, _boundary);
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
    public async Task Stream_NoCursor_ReplaysWhatIsHeldThenFollowsLive()
    {
        await StartAsync();
        PushA(Ready(1), Delivered(2, "p1"), Started(3));

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var first = await stream.NextAsync();
        var second = await stream.NextAsync();
        PushA(Text(4, "PONG"));
        var live = await stream.NextAsync();

        // Each frame is the ruling's items for one event, under that event's cursor. The turn starting (sequence 3)
        // shows nothing, so it sends no frame.
        Assert.Equal(("items", "gen-a:1", ApiModeLiveItemKinds.Connection), (first!.Event, first.Id, Single(first).Kind));
        Assert.Equal(("items", "gen-a:2", ApiModeLiveItemKinds.Receipt, "Delivered", "p1", 2L),
            (second!.Event, second.Id, Single(second).Kind, Single(second).Text, Single(second).PromptId, Single(second).Sequence));
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
        var next = await stream.NextAsync();

        var after = await stream.NextAsync();

        Assert.Equal("reset", reset!.Event);
        Assert.Equal("", reset.Id);   // clears the browser's Last-Event-ID
        var body = Reset(reset);
        Assert.Equal(ApiModeLiveResetCodes.NewGeneration, body.Code);
        Assert.Equal(new[] { $"/sessions/{ApiSession}/receipts", $"/sessions/{ApiSession}/history" }, body.RebuildFrom);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
        // Then the stored receipts, with no id (they are not an event), so what follows continues from them...
        Assert.Null(next!.Id);
        Assert.Equal(new[] { ("p1", "Delivered", 0L), ("p2", "Delivered", 0L) }, Items(next).Select(i => (i.PromptId!, i.Text, i.Sequence)));
        // ...and the new generation's events: its connection line; p2's delivery is already in the receipts, so it adds nothing.
        Assert.Equal(("gen-b:1", ApiModeLiveItemKinds.Connection), (after!.Id, Single(after).Kind));
    }

    [Fact]
    public async Task Stream_CursorTheBufferHasLetGo_SendsAMissedEventsResetThenWhatIsHeld()
    {
        await StartAsync();
        // Five more than the buffer holds: sequences 1 to 5 are let go.
        PushA(Enumerable.Range(1, ApiModeEventBuffer.MaxEventsPerSession + 5).Select(i => (SessionEvent)Text(i, "x")).ToArray());

        await using var stream = await OpenStreamAsync(Alice, ApiSession, lastEventId: "gen-a:1");
        var reset = await stream.NextAsync();
        var first = await stream.NextAsync();

        Assert.Equal(("reset", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, Reset(reset).Code));
        Assert.Equal("gen-a:6", first!.Id);
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
        var receipts = await stream.NextAsync();
        var next = await stream.NextAsync();

        Assert.Equal(("reset", "", ApiModeLiveResetCodes.NewGeneration), (reset!.Event, reset.Id, Reset(reset).Code));
        // The ruling starts again from the stored receipts, which the ingest wrote before the buffer handed the events
        // on: p2 is already Delivered there, so its event adds nothing and the reply follows.
        Assert.Null(receipts!.Id);
        Assert.Equal(new[] { "p1", "p2" }, Items(receipts).Select(i => i.PromptId));
        Assert.Equal(("items", "gen-b:2", "hello"), (next!.Event, next.Id, Single(next).Text));
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
        var first = await stream.NextAsync();

        Assert.Equal(("reset", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, Reset(reset).Code));
        Assert.Equal("gen-a:5", first!.Id);
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

        Assert.Equal(("items", "gen-a:5"), (first!.Event, first.Id));
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
        Assert.Equal("gen-a:1", (await stream.NextAsync())!.Id);
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
        // The Gateway holds this generation only from sequence 5 (it restarted), so the ruling starts from the stored
        // receipts and they open the stream, with no reset: the client holds nothing to reset.
        await StartAsync();
        RecordDelivered("p0");
        PushA(Text(5, "a"));

        await using var stream = await OpenStreamAsync(Alice, ApiSession);
        var opening = await stream.NextAsync();
        var first = await stream.NextAsync();

        Assert.Equal(("items", (string?)null), (opening!.Event, opening.Id));
        Assert.Equal(("receipt:p0", "Delivered"), (Single(opening).ItemId, Single(opening).Text));
        Assert.Equal("gen-a:5", first!.Id);
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
        Assert.Equal(("gen-a:41", "Queued: a turn is running"), (queued!.Id, Single(queued).Text));
        Assert.Equal(("gen-a:42", "Refused: " + busy), (refused!.Id, Single(refused).Text));
        // What the receipts route answers: the stored receipt, worded by the one ruling.
        using (_boundary.EnterScope(Alice))
            Assert.Equal(Single(refused).Text, ApiModeLiveRuling.ReceiptWords(Assert.Single(_receipts.Read(ApiSession))));
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
        var receipts = await stream.NextAsync();
        var card = await stream.NextAsync();

        // A reset, then the stored receipts, and only then the card: the client rebuilt before it was told the finish.
        Assert.Equal(("reset", "", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, reset.Id, Reset(reset).Code));
        Assert.Equal(((string?)null, "receipt:p1", "Delivered"), (receipts!.Id, Single(receipts).ItemId, Single(receipts).Text));
        Assert.Equal(("gen-a:2004", "tool:tc1", "done"), (card!.Id, Single(card).ItemId, Single(card).Status));
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
        var receipts = await stream.NextAsync();
        var ended = await stream.NextAsync();

        Assert.Equal(("reset", ApiModeLiveResetCodes.MissedEvents), (reset!.Event, Reset(reset).Code));
        Assert.Equal(("receipt:p1", "Delivered - the turn failed: Pi exited during the turn (exit code 137)"),
            (Single(receipts!).ItemId, Single(receipts!).Text));
        Assert.Equal(("gen-a:2003", ApiModeLiveItemKinds.TurnEnded, "Turn failed: Pi exited during the turn (exit code 137)"),
            (ended!.Id, Single(ended).Kind, Single(ended).Text));
    }

    [Fact]
    public async Task Items_ATurnWhoseStartTheBufferLetGo_AnswersAResetWithTheFailedTurnBesideDelivered()
    {
        await StartAsync();
        PushA(Delivered(1, "p1"), Started(2));
        PushA(Enumerable.Range(3, ApiModeEventBuffer.MaxEventsPerSession).Select(i => (SessionEvent)Text(i, "x")).ToArray());
        PushA(Text(2003, "y"), Failed(2004, "Pi exited during the turn (exit code 137)"));

        var page = await GetPageAsync(Alice, ApiSession, after: "gen-a:2002");

        // The reply piece before the gap is dropped with the rest of the continued page: the client rebuilds instead.
        Assert.Equal(ApiModeLiveResetCodes.MissedEvents, page.Reset!.Code);
        Assert.Equal(new[]
        {
            ("receipt:p1", "Delivered - the turn failed: Pi exited during the turn (exit code 137)"),
            ("turn:t1", "Turn failed: Pi exited during the turn (exit code 137)"),
        }, page.Items.Select(i => (i.ItemId, i.Text)));
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
    public async Task Items_NoCursor_ListsWhatIsHeldWithTheNextCursor()
    {
        await StartAsync();
        PushA(Ready(1), Delivered(2, "p1"), Started(3));

        var page = await GetPageAsync(Alice, ApiSession);

        Assert.Null(page.Reset);
        Assert.Equal(new[] { (ApiModeLiveItemKinds.Connection, 1L), (ApiModeLiveItemKinds.Receipt, 2L) }, page.Items.Select(i => (i.Kind, i.Sequence)));
        Assert.Equal("gen-a:3", page.Next);   // past the turn starting, which shows nothing
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

        Assert.Equal(new[] { 1L }, page.Items.Select(i => i.Sequence));
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

    // ----- the host -----

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
        SessionLiveEndpoint.Map(app, _feed, _receipts, _pushed, StaleAfter, _boundary, _clock, liveBacklog);

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
    private static PromptDelivered Delivered(long seq, string prompt) => new() { SessionId = ApiSession, Sequence = seq, At = T0, PromptId = prompt, Disposition = PromptDisposition.Started, Acknowledgement = """{"type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""" };
    private static PromptQueued Queued(long seq, string prompt) => new() { SessionId = ApiSession, Sequence = seq, At = T0, PromptId = prompt, Reason = PromptQueuedReason.TurnRunning, Position = 1 };
    private static PromptRefused Refused(long seq, string prompt, string reason, PromptRefusedBy by) => new() { SessionId = ApiSession, Sequence = seq, At = T0, PromptId = prompt, Reason = reason, RefusedBy = by };
    private static TurnStarted Started(long seq) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", PromptIds = new[] { "p1" } };
    private static MessageText Text(long seq, string text) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", MessageId = "m1", Channel = MessageTextChannel.Reply, Kind = MessageTextKind.Delta, Text = text };
    private static ToolCallStarted ToolStart(long seq, string name, string input) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", ToolCallId = "tc1", ToolName = name, Kind = ToolCallKind.Execute, Input = input };
    private static ToolCallFinished ToolEnd(long seq) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", ToolCallId = "tc1", Outcome = ToolCallOutcome.Succeeded, Output = "total 0", DurationMilliseconds = 12 };
    private static TurnEnded Failed(long seq, string reason) => new() { SessionId = ApiSession, Sequence = seq, At = T0, TurnId = "t1", Outcome = TurnOutcome.Failed, Reason = reason, AgentStopReason = null, Usage = new TurnUsage { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 } };
    private static ApiModeHeldEvent Held(long seq) => new("gen-a", GenAStarted, Text(seq, "x"));

    // ----- reading frames -----

    private static List<ApiModeLiveItem> Items(Frame frame) =>
        JsonSerializer.Deserialize<List<ApiModeLiveItem>>(frame.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    private static ApiModeLiveItem Single(Frame frame) => Assert.Single(Items(frame));
    private static ApiModeLiveResetDto Reset(Frame frame) => JsonSerializer.Deserialize<ApiModeLiveResetDto>(frame.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

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
