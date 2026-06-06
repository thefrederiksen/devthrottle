using CcDirector.AgentBrain;
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
}

// ============================================================================
// TurnEndWatcher - the pure boundary decision.
// ============================================================================
public sealed class TurnEndWatcherTests
{
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
        TimeSpan? settle = null)
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
            settle ?? TimeSpan.FromMilliseconds(20));
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
        Assert.Equal(1, _brain.ClearCount);                 // stamping machine: cleared per brief
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
