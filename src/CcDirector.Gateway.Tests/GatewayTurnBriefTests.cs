using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

// ============================================================================
// GatewayTurnBriefStore - append-only on disk, replace-by-turn on read.
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
// ============================================================================
public sealed class TurnEndWatcherTests
{
    private static (TurnEndWatcher Watcher, List<TurnEndSignal> TurnEnds, List<string> Working) BuildObserved()
    {
        var turnEnds = new List<TurnEndSignal>();
        var working = new List<string>();
        // Registry/client are only used by CatchUpAsync; Observe never touches them.
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

// ============================================================================
// SessionAssessments - the Gateway-owned half of the #186 two-owner model.
// ============================================================================
public sealed class SessionAssessmentsTests
{
    private static TurnBriefDto Brief(bool needsYou, bool degraded = false) => new()
    {
        SessionId = "s1",
        TurnNumber = 7,
        Degraded = degraded,
        Intent = "intent",
        NeedsYou = needsYou ? new TurnBriefNeedsYou { Statement = "pick one", RailLine = "pick one", Urgency = "blocking" } : null,
    };

    [Fact]
    public void RecordBrief_NeedsYou_AssessesWaitingForInput()
    {
        var a = new SessionAssessments();
        Assert.Equal("WaitingForInput", a.RecordBrief("s1", Brief(needsYou: true)));
        Assert.Equal("WaitingForInput", a.For("s1"));
    }

    [Fact]
    public void RecordBrief_NothingNeeded_RefutesTheQuietSignal_AsIdle()
    {
        // THE refutation: mechanically quiet (raw WaitingForInput) but the brain read the
        // turn and nothing needs the user -> assessed Idle.
        var a = new SessionAssessments();
        Assert.Equal("Idle", a.RecordBrief("s1", Brief(needsYou: false)));
        Assert.Equal("Idle", a.For("s1"));
    }

    [Fact]
    public void RecordBrief_DegradedStub_AssessesNothing()
    {
        var a = new SessionAssessments();
        Assert.Null(a.RecordBrief("s1", Brief(needsYou: false, degraded: true)));
        Assert.Null(a.For("s1"));
    }

    [Fact]
    public void Invalidate_NewActivity_ClearsTheStandingAssessment()
    {
        var a = new SessionAssessments();
        a.RecordBrief("s1", Brief(needsYou: true));
        a.Invalidate("s1");
        Assert.Null(a.For("s1"));
    }
}

// ============================================================================
// GatewayTurnBriefAgent - the stamping machine: queue, brain cycle, degrade.
// ============================================================================
public sealed class GatewayTurnBriefAgentTests : IDisposable
{
    private const string Endpoint = "http://127.0.0.1:9";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gw-brief-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly string _sid = Guid.NewGuid().ToString();
    private readonly GatewayTurnBriefStore _store;
    private readonly FakeBrain _brain = new();
    private int _recoverCalls;

    public GatewayTurnBriefAgentTests()
    {
        _store = new GatewayTurnBriefStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeBrain : IAgentBrain
    {
        public string? SessionId => "fake-brain-session";
        public List<string> Asks { get; } = new();
        public int ClearCount { get; private set; }
        public string ReplyJson { get; set; } =
            """{"headline":"Fix login bug","newChapter":true,"turnTitle":"Patched auth flow","intent":"Get the login bug fixed.","did":["patched the auth flow"],"needsYou":null}""";
        public Exception? AskThrows { get; set; }
        public TimeSpan AskDelay { get; set; } = TimeSpan.Zero;

        public async Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Asks.Add(prompt);
            if (AskDelay > TimeSpan.Zero) await Task.Delay(AskDelay, ct);
            if (AskThrows is not null) throw AskThrows;
            return new AskResult { Text = ReplyJson, ReplySeconds = 0.1 };
        }

        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<ClearResult> ClearAsync(CancellationToken ct = default)
        {
            ClearCount++;
            return Task.FromResult(new ClearResult());
        }

        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    private static List<TurnWidgetDto> Widgets(int turns)
    {
        var w = new List<TurnWidgetDto>();
        for (var i = 0; i < turns; i++)
        {
            w.Add(new TurnWidgetDto { Kind = "UserMessage", Content = $"prompt {i}" });
            w.Add(new TurnWidgetDto { Kind = "Text", Content = $"reply {i}" });
        }
        return w;
    }

    private GatewayTurnBriefAgent BuildAgent(
        Func<int, List<TurnWidgetDto>>? widgetsPerFetch = null,
        TimeSpan? settle = null,
        string? generatorId = null)
    {
        var fetches = 0;
        widgetsPerFetch ??= _ => Widgets(1);
        return new GatewayTurnBriefAgent(
            _store,
            _ => Task.FromResult<IAgentBrain>(_brain),
            () => { Interlocked.Increment(ref _recoverCalls); return Task.CompletedTask; },
            (ep, sid, ct) => Task.FromResult<TurnsResponse?>(new TurnsResponse
            {
                SessionId = sid,
                Status = "ok",
                Widgets = widgetsPerFetch(Interlocked.Increment(ref fetches)),
            }),
            (ep, sid, ct) => Task.FromResult("the screen tail"),
            settle ?? TimeSpan.FromMilliseconds(20),
            generatorId: generatorId);
    }

    private async Task WaitUntilAsync(Func<bool> condition, double timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "condition not reached within timeout");
    }

    [Fact]
    public async Task TurnEnd_AsksTheBrain_StoresValidatedBrief_AndClears()
    {
        using var agent = BuildAgent();
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));

        await WaitUntilAsync(() => _store.Latest(_sid) is not null);

        var brief = _store.Latest(_sid)!;
        Assert.False(brief.Degraded);
        Assert.Equal(GatewayTurnBriefAgent.GeneratorId, brief.Model);
        Assert.Equal("Fix login bug", brief.Headline);
        Assert.True(brief.NewChapter); // first stored title mechanically starts chapter 1
        Assert.Single(_brain.Asks);
        Assert.Contains("the screen tail", _brain.Asks[0]); // the package reached the prompt
        Assert.True(Directory.GetFiles(_dir, "t*.json", SearchOption.AllDirectories).Any()); // #207 replay package persisted
        Assert.Equal(1, _brain.ClearCount);                 // stamping machine: cleared per brief
    }

    [Fact]
    public async Task CustomGeneratorId_IsRecordedOnStoredBriefs()
    {
        // Issue #204: production passes "gateway-brain/<model>" so every brief records
        // which model actually wrote it.
        using var agent = BuildAgent(generatorId: "gateway-brain/opus");
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));

        await WaitUntilAsync(() => _store.Latest(_sid) is not null);

        Assert.Equal("gateway-brain/opus", _store.Latest(_sid)!.Model);
    }

    [Fact]
    public async Task InvalidBrainReply_StoresHonestStub_AndStillClears()
    {
        _brain.ReplyJson = "I am not JSON at all";
        using var agent = BuildAgent();
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));

        await WaitUntilAsync(() => _store.Latest(_sid) is not null);

        var brief = _store.Latest(_sid)!;
        Assert.True(brief.Degraded);
        Assert.Equal("stub", brief.Model);
        Assert.Equal(1, _brain.ClearCount); // a rejected brief must never leak context onward
    }

    [Fact]
    public async Task DeadBrain_Degrades_AndFiresRecovery()
    {
        _brain.AskThrows = new AgentBrainException("the hosted agent is dead");
        using var agent = BuildAgent();
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));

        await WaitUntilAsync(() => _store.Latest(_sid) is not null && Volatile.Read(ref _recoverCalls) > 0);

        Assert.True(_store.Latest(_sid)!.Degraded);
        Assert.Equal(1, _recoverCalls); // RestartAsync is the one recovery verb
    }

    [Fact]
    public async Task WatchCancel_DuringSettle_StoresNothing()
    {
        using var agent = BuildAgent(settle: TimeSpan.FromSeconds(2));
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));
        await Task.Delay(100); // inside the settle window
        agent.OnSessionWorking(_sid);

        await Task.Delay(300);
        Assert.Null(_store.Latest(_sid));
        Assert.Empty(_brain.Asks);
    }

    [Fact]
    public async Task CoalescedTurnEnds_BriefOnce()
    {
        using var agent = BuildAgent(settle: TimeSpan.FromMilliseconds(150));
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint)); // newer turn replaces the queued job
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));

        await WaitUntilAsync(() => _store.Latest(_sid) is not null);
        await Task.Delay(300); // give a hypothetical duplicate time to appear

        Assert.Single(_store.List(_sid));
        Assert.Single(_brain.Asks);
    }

    [Fact]
    public async Task AlreadyBriefedTurn_Skips()
    {
        using var agent = BuildAgent();
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));
        await WaitUntilAsync(() => _store.Latest(_sid) is not null);

        // Same turn count again (e.g. Gateway boot backfill): no second generation.
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));
        await Task.Delay(300);

        Assert.Single(_brain.Asks);
        Assert.Single(_store.List(_sid));
    }

    [Fact]
    public async Task StoredBrief_FiresOnBriefStored_WithTheOwningEndpoint()
    {
        // The host hangs the assessedState derivation + Director push-down on this hook (#186).
        using var agent = BuildAgent();
        var fired = new List<(string Sid, string Endpoint, TurnBriefDto Brief)>();
        agent.OnBriefStored = (sid, ep, brief) => fired.Add((sid, ep, brief));
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));

        await WaitUntilAsync(() => fired.Count == 1);

        Assert.Equal(_sid, fired[0].Sid);
        Assert.Equal(Endpoint, fired[0].Endpoint);
        Assert.False(fired[0].Brief.Degraded);
    }

    [Fact]
    public async Task TranscriptAdvancedDuringGeneration_DiscardsTheBrief()
    {
        // First fetch sees 1 turn, the staleness re-fetch sees 2: the brief is moot.
        using var agent = BuildAgent(widgetsPerFetch: n => Widgets(n == 1 ? 1 : 2));
        agent.OnTurnEnd(new TurnEndSignal(_sid, Endpoint));

        await WaitUntilAsync(() => _brain.Asks.Count == 1);
        await Task.Delay(300);

        Assert.Null(_store.Latest(_sid));
    }
}
