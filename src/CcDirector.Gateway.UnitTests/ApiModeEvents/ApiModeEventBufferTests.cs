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
        StoreA(Texts(1, 5));
        var seen = new List<long>();

        using var sub = _buffer.Subscribe(Alice, Sid, new ApiModeEventCursor("gen-a", 3), e => seen.Add(e.Event.Sequence));
        StoreA(Texts(6, 2));

        Assert.Equal(new long[] { 4, 5, 6, 7 }, seen);
        Assert.True(sub.Replay.Complete);
        Assert.Equal(new long[] { 4, 5 }, sub.Replay.Events.Select(e => e.Event.Sequence));
    }

    [Fact]
    public void Subscribe_ToASessionWithNothingYet_FollowsFromItsFirstEvent()
    {
        var seen = new List<long>();
        using var sub = _buffer.Subscribe(Alice, Sid, null, e => seen.Add(e.Event.Sequence));

        StoreA(Texts(1, 3));

        Assert.Null(sub.Replay.Generation);
        Assert.False(sub.Replay.Complete);   // nothing held here: this process cannot vouch for what came before
        Assert.Equal(new long[] { 1, 2, 3 }, seen);
    }

    [Fact]
    public void Subscribe_WithACursorOfAnEarlierGeneration_ReplaysAllAndSaysItIsNotComplete()
    {
        StoreA(Texts(1, 3));
        Store("gen-b", GenBStarted, Texts(1, 2));

        var seen = new List<ApiModeHeldEvent>();
        using var sub = _buffer.Subscribe(Alice, Sid, new ApiModeEventCursor("gen-a", 3), seen.Add);

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
        StoreA(Texts(1, 1));

        sub.Dispose();
        sub.Dispose();
        StoreA(Texts(2, 1));

        Assert.Equal(new long[] { 1 }, seen);
    }

    [Fact]
    public void Apply_ASubscriberThatThrows_StopsNeitherTheOthersNorTheStore()
    {
        var seen = new List<long>();
        using var bad = _buffer.Subscribe(Alice, Sid, null, _ => throw new InvalidOperationException("a broken subscriber"));
        using var good = _buffer.Subscribe(Alice, Sid, null, e => seen.Add(e.Event.Sequence));
        var everyone = new List<(TenantId, long)>();
        using var all = _buffer.SubscribeAll((t, e) => everyone.Add((t, e.Event.Sequence)));

        var result = StoreA(Texts(1, 2));

        Assert.Equal(2, result.Stored);
        Assert.Equal(new long[] { 1, 2 }, seen);
        Assert.Equal(new[] { (Alice, 1L), (Alice, 2L) }, everyone);
    }

    [Fact]
    public void Apply_PastTheEventBound_LetsTheOldestGo_AndAReaderBehindThemIsToldItMissedSome()
    {
        var total = ApiModeEventBuffer.MaxEventsPerSession + 10;
        for (long from = 1; from <= total; from += 500)
            StoreA(Texts(from, (int)Math.Min(500, total - from + 1)));

        var all = _buffer.Read(Alice, Sid);
        Assert.Equal(ApiModeEventBuffer.MaxEventsPerSession, all.Events.Count);
        Assert.Equal(11, all.Events[0].Event.Sequence);
        Assert.False(all.Complete);
        Assert.False(_buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 3)).Complete);
        Assert.True(_buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 10)).Complete);
    }

    [Fact]
    public void Apply_PastTheCharacterBound_LetsTheOldestGo()
    {
        var big = new string('x', (int)(ApiModeEventBuffer.MaxCharactersPerSession / 3));
        StoreA(Text(1, "t1", big), Text(2, "t1", big), Text(3, "t1", big), Text(4, "t1", big));

        var held = _buffer.Read(Alice, Sid).Events.Select(e => e.Event.Sequence).ToList();
        Assert.Equal(new long[] { 3, 4 }, held);
    }

    [Fact]
    public void Apply_PastTheSessionBound_LetsTheLeastRecentUnwatchedSessionGo()
    {
        using var watched = _buffer.Subscribe(Alice, "watched", null, _ => { });
        _buffer.Apply(Alice, "first", "gen-a", GenAStarted, new[] { In(Ready(1, "first")) }, _ => { });
        for (var i = 0; i < ApiModeEventBuffer.MaxSessions; i++)
            _buffer.Apply(Alice, "s" + i, "gen-a", GenAStarted, new[] { In(Ready(1, "s" + i)) }, _ => { });

        Assert.Equal(ApiModeEventBuffer.MaxSessions, _buffer.SessionCount);
        Assert.Null(_buffer.Read(Alice, "first").Generation);
        Assert.False(_buffer.Read(Alice, "first", new ApiModeEventCursor("gen-a", 1)).Complete);
        Assert.Single(_buffer.Read(Alice, "s999").Events);
    }

    [Fact]
    public void Apply_WhenTheDurableWriteThrows_NothingIsHeldAndTheSameBatchCanBeSentAgain()
    {
        var events = Texts(1, 3).Select(In).ToList();

        Assert.Throws<InvalidOperationException>(() =>
            _buffer.Apply(Alice, Sid, "gen-a", GenAStarted, events, _ => throw new InvalidOperationException("database unreachable")));
        Assert.Empty(_buffer.Read(Alice, Sid).Events);

        var again = _buffer.Apply(Alice, Sid, "gen-a", GenAStarted, events, _ => { });
        Assert.Equal(3, again.Stored);
    }

    [Fact]
    public void Read_ACursorAtTheLatestEvent_AnswersNothingAndComplete()
    {
        StoreA(Texts(1, 3));

        var replay = _buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 3));

        Assert.Empty(replay.Events);
        Assert.True(replay.Complete);
    }

    // ----- review of pull request #4082, finding 1: a Gateway restarted in the middle of a generation -----

    /// <summary>The Gateway restarted while the Director did not: the first batch this process sees carries sequences
    /// 50 to 52 of the same generation. A reader coming back with the cursor it held before the restart (30) must be
    /// told that 31 to 49 were never held here.</summary>
    [Fact]
    public void Read_ACursorBelowTheFirstSequenceThisProcessHeld_IsNotComplete()
    {
        StoreA(Texts(50, 3));

        var replay = _buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 30));

        Assert.Equal(new long[] { 50, 51, 52 }, replay.Events.Select(e => e.Event.Sequence));
        Assert.False(replay.Complete);
    }

    [Fact]
    public void Read_NoCursorWhenThisProcessBeganInTheMiddleOfAGeneration_IsNotComplete()
    {
        StoreA(Texts(50, 3));

        Assert.False(_buffer.Read(Alice, Sid).Complete);
    }

    [Fact]
    public void Read_ACursorJustBeforeTheFirstSequenceThisProcessHeld_IsComplete()
    {
        StoreA(Texts(50, 3));

        var replay = _buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 49));

        Assert.Equal(3, replay.Events.Count);
        Assert.True(replay.Complete);
    }

    [Fact]
    public void Subscribe_WithACursorBelowTheFirstSequenceThisProcessHeld_SaysTheReplayIsNotComplete()
    {
        StoreA(Texts(50, 3));

        using var sub = _buffer.Subscribe(Alice, Sid, new ApiModeEventCursor("gen-a", 30), _ => { });

        Assert.False(sub.Replay.Complete);
    }

    [Fact]
    public void Read_NoCursorWhenThisProcessHoldsTheGenerationFromItsFirstSequence_IsComplete()
    {
        StoreA(Texts(1, 3));

        Assert.True(_buffer.Read(Alice, Sid).Complete);
    }

    [Fact]
    public void Read_ASessionThisProcessHoldsNothingFor_IsNotComplete()
    {
        Assert.False(_buffer.Read(Alice, Sid).Complete);
        Assert.False(_buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-a", 30)).Complete);
    }

    [Fact]
    public void Apply_ALaterGenerationFromItsFirstSequence_IsCompleteAgain()
    {
        StoreA(Texts(50, 3));

        Store("gen-b", GenBStarted, Texts(1, 2));

        Assert.True(_buffer.Read(Alice, Sid).Complete);
        Assert.True(_buffer.Read(Alice, Sid, new ApiModeEventCursor("gen-b", 1)).Complete);
    }
}
