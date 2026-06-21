using System.Text.Json;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

// ============================================================================
// GatewayTurnBriefStore - append-only on disk, replace-by-turn on read.
//
// Issue #549 retired the always-on turn-brief STAMPING machine (GatewayTurnBriefAgent), so
// nothing writes new briefs going forward. The store itself is kept (read-only-serving) and
// still round-trips append/list/feedback/explain, so the read endpoints and the interrupted/
// restore paths that read it degrade cleanly - these tests cover that durable behavior.
// ============================================================================
public sealed class GatewayTurnBriefStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gw-briefs-tests", Guid.NewGuid().ToString("N"));
    private static readonly string Sid = Guid.NewGuid().ToString();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static TurnBriefDto Brief(int turn, string headline = "Working the thing", string? railLine = null) => new()
    {
        SessionId = Sid,
        TurnNumber = turn,
        Headline = headline,
        Intent = "intent",
        NeedsYou = railLine is null ? null : new TurnBriefNeedsYou { Statement = "s", RailLine = railLine },
    };

    [Fact]
    public void List_Empty_WhenNothingStored()
    {
        var store = new GatewayTurnBriefStore(_dir);
        Assert.Empty(store.List(Sid));
        Assert.Null(store.Latest(Sid));
    }

    [Fact]
    public void Append_ListsNewestFirst_AndSurvivesReopen()
    {
        var store = new GatewayTurnBriefStore(_dir);
        store.Append(Sid, Brief(1));
        store.Append(Sid, Brief(2));
        store.Append(Sid, Brief(3));

        var reopened = new GatewayTurnBriefStore(_dir);
        var list = reopened.List(Sid);
        Assert.Equal(new[] { 3, 2, 1 }, list.Select(b => b.TurnNumber));
        Assert.Equal(3, reopened.Latest(Sid)!.TurnNumber);
    }

    [Fact]
    public void Append_IsAppendOnly_NoRingCap()
    {
        // The whole point vs the Director's 50-ring: chapter-opening cards never age out.
        var store = new GatewayTurnBriefStore(_dir);
        for (var i = 1; i <= 60; i++)
            store.Append(Sid, Brief(i));

        Assert.Equal(60, store.List(Sid).Count);
        Assert.Equal(1, store.List(Sid)[^1].TurnNumber); // the opening card is still there
    }

    [Fact]
    public void Append_SameTurnNumber_LastGenerationWinsOnRead_FileKeepsBoth()
    {
        var store = new GatewayTurnBriefStore(_dir);
        store.Append(Sid, Brief(5, headline: "first attempt"));
        store.Append(Sid, Brief(5, headline: "regenerated"));

        var list = store.List(Sid);
        Assert.Single(list);
        Assert.Equal("regenerated", list[0].Headline);

        // Disk history is append-only: both generations remain as lines.
        var file = Directory.GetFiles(_dir, "*.jsonl").Single();
        Assert.Equal(2, File.ReadAllLines(file).Count(l => !string.IsNullOrWhiteSpace(l)));
    }

    [Fact]
    public void List_SkipsCorruptLines_KeepsTheRest()
    {
        var store = new GatewayTurnBriefStore(_dir);
        store.Append(Sid, Brief(1));
        var file = Directory.GetFiles(_dir, "*.jsonl").Single();
        File.AppendAllText(file, "{torn-line-from-power-loss" + Environment.NewLine);
        store.Append(Sid, Brief(2));

        var list = store.List(Sid);
        Assert.Equal(new[] { 2, 1 }, list.Select(b => b.TurnNumber));
    }

    [Fact]
    public void AppendExplain_LatestExplain_RoundTrips_LastLineWins()
    {
        var store = new GatewayTurnBriefStore(_dir);
        Assert.Null(store.LatestExplain(Sid));

        store.AppendExplain(Sid, new ExplainReportDto
        {
            SessionId = Sid, TurnNumber = 5,
            WhatHappened = "first", WhatWeDid = new List<string> { "a" }, WhatNext = "next",
        });
        store.AppendExplain(Sid, new ExplainReportDto
        {
            SessionId = Sid, TurnNumber = 9,
            WhatHappened = "second", WhatWeDid = new List<string> { "b" }, WhatNext = "close",
        });

        var latest = store.LatestExplain(Sid);
        Assert.NotNull(latest);
        Assert.Equal(9, latest.TurnNumber);
        Assert.Equal("second", latest.WhatHappened); // append-only: last line wins
    }

    [Fact]
    public void SaveFeedback_IncludesTurnPackage_AndUpdatesSameRecordReason()
    {
        var store = new GatewayTurnBriefStore(_dir);
        var brief = Brief(7, railLine: "pick one");
        var package = new TurnPackage(Guid.Parse(Sid), 7, "first", "last ask", "reply", false,
            "delta", "screen", "intent", new[] { "old rail" }, "headline");
        store.SavePackage(Sid, package);

        var created = store.SaveFeedback(Sid, brief, "down", "");
        try
        {
            var json = File.ReadAllText(created.File);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("down", doc.RootElement.GetProperty("vote").GetString());
            Assert.True(doc.RootElement.TryGetProperty("turnPackage", out var turnPackage));
            Assert.Equal("delta", turnPackage.GetProperty("transcriptDelta").GetString());

            var updated = store.SaveFeedback(Sid, brief, "down", "too cluttered", created.FeedbackId);
            Assert.Equal(created.File, updated.File);
            var updatedJson = File.ReadAllText(updated.File);
            using var updatedDoc = JsonDocument.Parse(updatedJson);
            Assert.Equal("too cluttered", updatedDoc.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            if (File.Exists(created.File)) File.Delete(created.File);
        }
    }
}

// ============================================================================
// TurnEndWatcher - the pure boundary decision + the push-fed Observe (issue #186).
//
// Issue #549: the watcher STAYS and runs unconditionally - its only job now is firing voice
// auto-refresh on turn-end for voice sessions (and clearing the stale voice/text cache on the
// Working transition). Its boundary detection is unchanged; these tests pin that behavior.
// The end-to-end "turn-end fires voice GenerateAsync without a brief agent" wiring is proven in
// TurnEndWatcherVoiceRefreshTests.
// ============================================================================
public sealed class TurnEndWatcherTests
{
    private static (TurnEndWatcher Watcher, List<TurnEndSignal> TurnEnds, List<string> Working) BuildObserved()
    {
        var turnEnds = new List<TurnEndSignal>();
        var working = new List<string>();
        // Registry/client are only used by the sweep; Observe never touches them.
        var registry = new CcDirector.Gateway.Discovery.DirectorRegistry(
            Path.Combine(Path.GetTempPath(), "tew-tests", Guid.NewGuid().ToString("N")));
        var client = new CcDirector.Gateway.Discovery.DirectorEndpointClient("test-token");
        var watcher = new TurnEndWatcher(registry, client, turnEnds.Add, working.Add);
        return (watcher, turnEnds, working);
    }

    [Fact]
    public void Observe_DoorbellSequence_FiresTurnEndOnTheBoundary()
    {
        var (watcher, turnEnds, working) = BuildObserved();
        using (watcher)
        {
            watcher.Observe("s1", "Working", "http://d1");
            watcher.Observe("s1", "WaitingForInput", "http://d1");

            Assert.Single(working);
            Assert.Single(turnEnds);
            Assert.Equal("s1", turnEnds[0].SessionId);
            Assert.Equal("http://d1", turnEnds[0].DirectorEndpoint);
        }
    }

    [Fact]
    public void Observe_HeartbeatReplayOfSameState_IsIdempotent()
    {
        // A lost doorbell ping is reconciled by the heartbeat snapshot; a NOT-lost ping
        // followed by the same snapshot must not double-fire.
        var (watcher, turnEnds, working) = BuildObserved();
        using (watcher)
        {
            watcher.Observe("s1", "Working", "http://d1");           // doorbell
            watcher.Observe("s1", "WaitingForInput", "http://d1");   // doorbell -> turn end
            watcher.Observe("s1", "WaitingForInput", "http://d1");   // heartbeat replay
            watcher.Observe("s1", "Working", "http://d1");           // user replied
            watcher.Observe("s1", "Working", "http://d1");           // heartbeat replay

            Assert.Single(turnEnds);
            Assert.Equal(2, working.Count); // entered Working twice, replays ignored
        }
    }

    [Fact]
    public void Registry_MarkStateReporting_FlagsPushCapableDirectors()
    {
        // The 15s reconcile poll must skip Directors that push their own signals (#186).
        var registry = new CcDirector.Gateway.Discovery.DirectorRegistry(
            Path.Combine(Path.GetTempPath(), "tew-tests", Guid.NewGuid().ToString("N")));

        Assert.False(registry.IsStateReporting("d1"));
        registry.MarkStateReporting("d1");
        Assert.True(registry.IsStateReporting("d1"));
        Assert.False(registry.IsStateReporting("d2")); // file-discovered locals stay polled
    }

    [Fact]
    public void Observe_LostDoorbell_HeartbeatReconciles()
    {
        // The Working ping was lost entirely; the next heartbeat snapshot shows the
        // session already waiting again -> the boundary is still detected.
        var (watcher, turnEnds, _) = BuildObserved();
        using (watcher)
        {
            watcher.Observe("s1", "Working", "http://d1");
            watcher.Observe("s1", "WaitingForInput", "http://d1");
            turnEnds.Clear();

            // Working -> (lost) -> heartbeat says Working -> doorbell says WaitingForInput
            watcher.Observe("s1", "Working", "http://d1");
            watcher.Observe("s1", "WaitingForInput", "http://d1");
            Assert.Single(turnEnds);
        }
    }

    [Theory]
    [InlineData("Working", "WaitingForInput", true)]   // the live boundary
    [InlineData("Working", "Idle", true)]
    [InlineData(null, "WaitingForInput", true)]        // first sighting already waiting (boot backfill)
    [InlineData(null, "Idle", true)]
    [InlineData("Idle", "WaitingForInput", false)]     // no turn happened
    [InlineData("WaitingForInput", "WaitingForInput", false)]
    [InlineData("Working", "Working", false)]
    [InlineData("Working", "WaitingForPerm", false)]   // still mid-turn (permission gate)
    [InlineData("Working", "Exited", false)]
    [InlineData(null, "Starting", false)]
    public void IsTurnEnd_DecidesTheBoundary(string? prev, string current, bool expected)
    {
        Assert.Equal(expected, TurnEndWatcher.IsTurnEnd(prev, current));
    }
}
