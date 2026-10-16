using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts.ApiMode;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeEvents;

/// <summary>
/// The per-session buffer of API-mode events and its subscriptions (mission #4070, phase 2) - the one interface the
/// display fold and the live relay build on. These pin what a reader may rely on: a subscription replays after its
/// cursor and then follows with no gap and no duplicate; a reader that has missed events is told so (Complete is
/// false); the bounds hold; one failing subscriber stops nobody; disposing stops delivery.
/// </summary>
public sealed class ApiModeEventBufferTests
{
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");

    private readonly ApiModeEventBuffer _buffer = new();

    private static ApiModeEventBuffer.Incoming In(SessionEvent e) => new(e, Json(e).Length);

    private ApiModeEventBuffer.ApplyResult Store(string generation, DateTime started, params SessionEvent[] events)
        => _buffer.Apply(Alice, Sid, generation, started, events.Select(In).ToList(), _ => { });

    private ApiModeEventBuffer.ApplyResult StoreA(params SessionEvent[] events) => Store("gen-a", GenAStarted, events);

    private static SessionEvent[] Texts(long from, int count) => Enumerable.Range(0, count).Select(i => (SessionEvent)Text(from + i, "t1", "x")).ToArray();

    [Fact]
    public void Subscribe_WithACursor_ReplaysWhatCameAfterItThenFollowsLive()
    {
        StoreA(Texts(0, 5));
        var seen = new List<long>();

        using var sub = _buffer.Subscribe(Alice, Sid, new ApiModeEventCursor("gen-a", 2), e => seen.Add(e.Event.Sequence));
        StoreA(Texts(5, 2));

        Assert.Equal(new long[] { 3, 4, 5, 6 }, seen);
        Assert.True(sub.Replay.Complete);
        Assert.Equal(new long[] { 3, 4 }, sub.Replay.Events.Select(e => e.Event.Sequence));
    }

    [Fact]
    public void Subscribe_ToASessionWithNothingYet_FollowsFromItsFirstEvent()
    {
        var seen = new List<long>();
        using var sub = _buffer.Subscribe(Alice, Sid, null, e => seen.Add(e.Event.Sequence));

        StoreA(Texts(0, 3));

        Assert.Null(sub.Replay.Generation);
        Assert.Equal(new long[] { 0, 1, 2 }, seen);
    }

    [Fact]
    public void Subscribe_WithACursorOfAnEarlierGeneration_ReplaysAllAndSaysItIsNotComplete()
    {
        StoreA(Texts(0, 3));
        Store("gen-b", GenBStarted, Texts(0, 2));

        var seen = new List<ApiModeHeldEvent>();
        using var sub = _buffer.Subscribe(Alice, Sid, new ApiModeEventCursor("gen-a", 2), seen.Add);

        Assert.False(sub.Replay.Complete);
        Assert.Equal("gen-b", sub.Replay.Generation);
        Assert.All(seen, e => Assert.Equal("gen-b", e.Generation));
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Dispose_StopsDelivery()
    {
        var seen = new List<long>();
        var sub = _buffer.Subscribe(Alice, Sid, null, e => seen.Add(e.Event.Sequence));
        StoreA(Texts(0, 1));

        sub.Dispose();
        sub.Dispose();
        StoreA(Texts(1, 1));

        Assert.Equal(new long[] { 0 }, seen);
    }

    [Fact]
    public void Apply_ASubscriberThatThrows_StopsNeitherTheOthersNorTheStore()
    {
        var seen = new List<long>();
        using var bad = _buffer.Subscribe(Alice, Sid, null, _ => throw new InvalidOperationException("a broken subscriber"));
        using var good = _buffer.Subscribe(Alice, Sid, null, e => seen.Add(e.Event.Sequence));
        var everyone = new List<(TenantId, long)>();
        using var all = _buffer.SubscribeAll((t, e) => everyone.Add((t, e.Event.Sequence)));

        var result = StoreA(Texts(0, 2));

        Assert.Equal(2, result.Stored);
        Assert.Equal(new long[] { 0, 1 }, seen);
        Assert.Equal(new[] { (Alice, 0L), (Alice, 1L) }, everyone);
    }

    [Fact]
    public void Apply_PastTheEventBound_LetsTheOldestGo_AndAReaderBehindThemIsToldItMissedSome()
    {
        var total = ApiModeEventBuffer.MaxEventsPerSession + 10;
        for (long from = 0; from < total; from += 500)
            StoreA(Texts(from, (int)Math.Min(500, total - from)));

        var all = _buffer.Read(Alice, Sid);
        Assert.Equal(ApiModeEventBuffer.MaxEventsPerSession, all.Events.Count);
        Assert.Equal(10, all.Events[0].Event.Sequence);
        Assert.False(all.Complete);
        Assert.False(_buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 3)).Complete);
        Assert.True(_buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 9)).Complete);
    }

    [Fact]
    public void Apply_PastTheCharacterBound_LetsTheOldestGo()
    {
        var big = new string('x', (int)(ApiModeEventBuffer.MaxCharactersPerSession / 3));
        StoreA(Text(0, "t1", big), Text(1, "t1", big), Text(2, "t1", big), Text(3, "t1", big));

        var held = _buffer.Read(Alice, Sid).Events.Select(e => e.Event.Sequence).ToList();
        Assert.Equal(new long[] { 2, 3 }, held);
    }

    [Fact]
    public void Apply_PastTheSessionBound_LetsTheLeastRecentUnwatchedSessionGo()
    {
        using var watched = _buffer.Subscribe(Alice, "watched", null, _ => { });
        _buffer.Apply(Alice, "first", "gen-a", GenAStarted, new[] { In(Ready(0, "first")) }, _ => { });
        for (var i = 0; i < ApiModeEventBuffer.MaxSessions; i++)
            _buffer.Apply(Alice, "s" + i, "gen-a", GenAStarted, new[] { In(Ready(0, "s" + i)) }, _ => { });

        Assert.Equal(ApiModeEventBuffer.MaxSessions, _buffer.SessionCount);
        Assert.Null(_buffer.Read(Alice, "first").Generation);
        Assert.False(_buffer.Read(Alice, "first", new ApiModeEventCursor("gen-a", 0)).Complete);
        Assert.Single(_buffer.Read(Alice, "s999").Events);
    }

    [Fact]
    public void Apply_WhenTheDurableWriteThrows_NothingIsHeldAndTheSameBatchCanBeSentAgain()
    {
        var events = Texts(0, 3).Select(In).ToList();

        Assert.Throws<InvalidOperationException>(() =>
            _buffer.Apply(Alice, Sid, "gen-a", GenAStarted, events, _ => throw new InvalidOperationException("database unreachable")));
        Assert.Empty(_buffer.Read(Alice, Sid).Events);

        var again = _buffer.Apply(Alice, Sid, "gen-a", GenAStarted, events, _ => { });
        Assert.Equal(3, again.Stored);
    }

    [Fact]
    public void Read_ACursorAtTheLatestEvent_AnswersNothingAndComplete()
    {
        StoreA(Texts(0, 3));

        var replay = _buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 2));

        Assert.Empty(replay.Events);
        Assert.True(replay.Complete);
    }
}
