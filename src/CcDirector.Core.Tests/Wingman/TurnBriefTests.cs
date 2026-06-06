using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

// =====================================================================================
// TurnBriefStore - durable ring, newest first, replace-on-same-turn, restart survival.
// =====================================================================================
public sealed class TurnBriefStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccdir-turnbrief-tests-" + Guid.NewGuid().ToString("N"));

    private static TurnBriefDto Brief(int turn, string? railLine = null) => new()
    {
        SessionId = "s",
        TurnNumber = turn,
        GeneratedAtUtc = DateTime.UtcNow,
        Model = "test",
        Intent = $"intent {turn}",
        NeedsYou = railLine is null ? null : new TurnBriefNeedsYou
        {
            Statement = "do the thing",
            RailLine = railLine,
            Evidence = "quote",
        },
    };

    [Fact]
    public void Append_NewestFirst_AndLatest()
    {
        var store = new TurnBriefStore(_dir);
        var sid = Guid.NewGuid();
        store.Append(sid, Brief(1));
        store.Append(sid, Brief(2, "pick a thing"));

        var list = store.List(sid);
        Assert.Equal(2, list.Count);
        Assert.Equal(2, list[0].TurnNumber);
        Assert.NotNull(store.Latest(sid));
        Assert.Equal("pick a thing", store.Latest(sid)?.NeedsYou?.RailLine);
    }

    [Fact]
    public void Append_SameTurnNumber_Replaces()
    {
        var store = new TurnBriefStore(_dir);
        var sid = Guid.NewGuid();
        store.Append(sid, Brief(3));
        var upgraded = Brief(3);
        upgraded.Model = "wingman:opus";
        store.Append(sid, upgraded);

        var list = store.List(sid);
        Assert.Single(list);
        Assert.Equal("wingman:opus", list[0].Model);
    }

    [Fact]
    public void Ring_CapsAtRingSize()
    {
        var store = new TurnBriefStore(_dir);
        var sid = Guid.NewGuid();
        for (var i = 1; i <= TurnBriefStore.RingSize + 7; i++)
            store.Append(sid, Brief(i));
        var list = store.List(sid);
        Assert.Equal(TurnBriefStore.RingSize, list.Count);
        Assert.Equal(TurnBriefStore.RingSize + 7, list[0].TurnNumber);
    }

    [Fact]
    public void SurvivesRestart_NewStoreInstanceReadsSameFile()
    {
        var sid = Guid.NewGuid();
        new TurnBriefStore(_dir).Append(sid, Brief(9, "review the layout"));
        var reopened = new TurnBriefStore(_dir);
        Assert.Equal("review the layout", reopened.Latest(sid)?.NeedsYou?.RailLine);
    }

    [Fact]
    public void UnknownSession_EmptyAndNull()
    {
        var store = new TurnBriefStore(_dir);
        Assert.Empty(store.List(Guid.NewGuid()));
        Assert.Null(store.Latest(Guid.NewGuid()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }
}

// =====================================================================================
// TurnPackageBuilder - delta from the prior brief, caps, ReplyPending propagation,
// rolling intent carry.
// =====================================================================================
public sealed class TurnPackageBuilderTests
{
    private static TurnWidgetDto User(string c) => new() { Kind = "UserMessage", Content = c };
    private static TurnWidgetDto Text(string c) => new() { Kind = "Text", Content = c };

    [Fact]
    public void Build_CarriesRollingIntent_AndDelta()
    {
        var widgets = new List<TurnWidgetDto> { User("build it"), Text("built"), User("now test it"), Text("tested, all green. Ship it?") };
        var prior = new TurnBriefDto { TurnNumber = 2, Intent = "building the feature" };

        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, "screen", prior);

        Assert.Equal("building the feature", p.RollingIntent);
        Assert.Equal(4, p.TurnCount);
        Assert.False(p.ReplyPending);
        Assert.Contains("now test it", p.TranscriptDelta);
        Assert.DoesNotContain("0. UserMessage: build it", p.TranscriptDelta); // before the prior brief
        Assert.Equal("tested, all green. Ship it?", p.LastAssistantText);
    }

    [Fact]
    public void Build_ReplyPending_WhenPickerOnScreen()
    {
        var widgets = new List<TurnWidgetDto> { User("ask me with the picker") };
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, "Which tone? 1. Playful 2. Formal", null);
        Assert.True(p.ReplyPending);
        Assert.Contains("Which tone?", p.ScreenTail);
    }

    [Fact]
    public void Build_CapsOversizeInputs()
    {
        var widgets = new List<TurnWidgetDto> { User("go"), Text(new string('x', 60_000)) };
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, new string('s', 60_000), null);
        Assert.True(p.TranscriptDelta.Length <= TurnPackageBuilder.DeltaMaxChars + 16);
        Assert.True(p.ScreenTail.Length <= TurnPackageBuilder.ScreenTailMaxChars);
    }

    [Fact]
    public void Build_CurrentHeadline_NewestNonEmpty_SkipsStubBriefs()
    {
        var widgets = new List<TurnWidgetDto> { User("go"), Text("done") };
        // Store order: newest first. Newest brief is a stub with no headline - the standing
        // headline must come from the brief behind it, not vanish.
        var recent = new List<TurnBriefDto>
        {
            new() { TurnNumber = 6, Intent = "stub", Headline = "" },
            new() { TurnNumber = 4, Intent = "x", Headline = "Cockpit gets a session story panel" },
            new() { TurnNumber = 2, Intent = "y", Headline = "Old headline" },
        };
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, "", recent[0], recent);
        Assert.Equal("Cockpit gets a session story panel", p.CurrentHeadline);
    }

    [Fact]
    public void Build_CurrentHeadline_NullWhenNoBriefsCarryOne()
    {
        var widgets = new List<TurnWidgetDto> { User("go") };
        var p = TurnPackageBuilder.Build(Guid.NewGuid(), widgets, "", null);
        Assert.Null(p.CurrentHeadline);
    }
}

// =====================================================================================
// TurnBriefOrchestrator - the lifecycle: turn end -> settle -> brief -> store;
// watch-cancel on re-entering Working; skip when no transcript; restore on attach.
// =====================================================================================
public sealed class TurnBriefOrchestratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccdir-orch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager = new(new AgentOptions());

    private sealed class FakeGenerator : ITurnBriefGenerator
    {
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;
        public int Calls;
        public string Id => "fake";

        public async Task<TurnBriefDto?> GenerateAsync(TurnPackage package, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return new TurnBriefDto
            {
                SessionId = package.SessionId.ToString(),
                TurnNumber = package.TurnCount,
                GeneratedAtUtc = DateTime.UtcNow,
                Model = Id,
                Intent = "fake intent",
                NeedsYou = new TurnBriefNeedsYou { Statement = "answer me", RailLine = "answer the thing", Evidence = "" },
            };
        }
    }

    private sealed class FakeBackend : ISessionBackend
    {
        public int ProcessId => 1234;
        public string Status => "Fake";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static Session NewSession(ActivityState initial)
    {
        var s = new Session(
            Guid.NewGuid(), repoPath: @"C:\test\repo", workingDirectory: @"C:\test\repo",
            claudeArgs: null, backend: new FakeBackend(), claudeSessionId: "claude-test",
            activityState: initial, createdAt: DateTimeOffset.UtcNow, customName: null, customColor: null);
        s.MarkRunning();
        return s;
    }

    private static List<TurnWidgetDto> Widgets(int n)
    {
        var list = new List<TurnWidgetDto>();
        for (var i = 0; i < n; i++)
            list.Add(i % 2 == 0
                ? new TurnWidgetDto { Kind = "UserMessage", Content = $"ask {i}" }
                : new TurnWidgetDto { Kind = "Text", Content = $"reply {i}" });
        return list;
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(condition(), "condition not met within timeout");
    }

    [Fact]
    public async Task TurnEnd_GeneratesAndStoresBrief_AndSetsRailLine()
    {
        var gen = new FakeGenerator();
        var store = new TurnBriefStore(_dir);
        using var orch = new TurnBriefOrchestrator(_manager, gen, store,
            transcriptReader: _ => Widgets(4), screenReader: _ => "screen", settleDelay: TimeSpan.FromMilliseconds(10));
        using var s = NewSession(ActivityState.Working);
        orch.Attach(s);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        await WaitFor(() => store.Latest(s.Id) is not null);
        Assert.Equal(4, store.Latest(s.Id)?.TurnNumber);
        Assert.Equal("answer the thing", s.LatestBriefRailLine);
        Assert.Equal(BriefingState.Briefed, s.BriefingState);
        Assert.Equal(1, gen.Calls);
    }

    [Fact]
    public async Task WatchCancel_ReenteringWorking_DiscardsInFlightBrief()
    {
        var gen = new FakeGenerator { Delay = TimeSpan.FromSeconds(5) };
        var store = new TurnBriefStore(_dir);
        using var orch = new TurnBriefOrchestrator(_manager, gen, store,
            transcriptReader: _ => Widgets(4), screenReader: _ => "", settleDelay: TimeSpan.FromMilliseconds(10));
        using var s = NewSession(ActivityState.Working);
        orch.Attach(s);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        await WaitFor(() => s.BriefingState == BriefingState.Briefing);

        // The user replies - the session goes back to Working. The in-flight brief dies.
        s.ApplyTerminalActivityState(ActivityState.Working);

        await WaitFor(() => s.BriefingState == BriefingState.None);
        Assert.Null(store.Latest(s.Id));
    }

    [Fact]
    public async Task TurnEnd_OpensYellowWindow_BeforeSettleDelay()
    {
        // Issue #192: the briefing window must open AT turn end, not after the settle
        // delay - otherwise the badge flashes red "needs you" for the settle duration.
        // With a 10s settle, seeing Briefing within 2s proves it was set up front.
        var gen = new FakeGenerator();
        var store = new TurnBriefStore(_dir);
        using var orch = new TurnBriefOrchestrator(_manager, gen, store,
            transcriptReader: _ => Widgets(4), screenReader: _ => "", settleDelay: TimeSpan.FromSeconds(10));
        using var s = NewSession(ActivityState.Working);
        orch.Attach(s);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        await WaitFor(() => s.BriefingState == BriefingState.Briefing, timeoutMs: 2000);
        Assert.Equal(0, gen.Calls); // still inside the settle delay - nothing generated yet
    }

    [Fact]
    public async Task NoTranscript_SkipsQuietly()
    {
        var gen = new FakeGenerator();
        var store = new TurnBriefStore(_dir);
        using var orch = new TurnBriefOrchestrator(_manager, gen, store,
            transcriptReader: _ => null, screenReader: _ => "", settleDelay: TimeSpan.FromMilliseconds(10));
        using var s = NewSession(ActivityState.Working);
        orch.Attach(s);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        await Task.Delay(300);

        Assert.Equal(0, gen.Calls);
        Assert.Null(store.Latest(s.Id));
        Assert.Equal(BriefingState.None, s.BriefingState);
    }

    [Fact]
    public async Task AlreadyBriefedTurn_DoesNotRegenerate()
    {
        var gen = new FakeGenerator();
        var store = new TurnBriefStore(_dir);
        using var orch = new TurnBriefOrchestrator(_manager, gen, store,
            transcriptReader: _ => Widgets(4), screenReader: _ => "", settleDelay: TimeSpan.FromMilliseconds(10));
        using var s = NewSession(ActivityState.Working);
        orch.Attach(s);

        s.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        await WaitFor(() => store.Latest(s.Id) is not null);

        // Same turn ends again (e.g. Idle after WaitingForInput) - no second call.
        s.ApplyTerminalActivityState(ActivityState.Working);
        s.ApplyTerminalActivityState(ActivityState.Idle);
        await Task.Delay(300);
        Assert.Equal(1, gen.Calls);
    }

    [Fact]
    public void Attach_RestoresRailLineFromDurableStore()
    {
        var store = new TurnBriefStore(_dir);
        using var s = NewSession(ActivityState.Idle);
        store.Append(s.Id, new TurnBriefDto
        {
            SessionId = s.Id.ToString(),
            TurnNumber = 2,
            Model = "wingman:opus",
            Intent = "restored",
            NeedsYou = new TurnBriefNeedsYou { Statement = "x", RailLine = "restored rail line", Evidence = "" },
        });

        using var orch = new TurnBriefOrchestrator(_manager, new FakeGenerator(), store,
            transcriptReader: _ => null, screenReader: _ => "");
        orch.Attach(s);

        Assert.Equal("restored rail line", s.LatestBriefRailLine);
        Assert.Equal(BriefingState.Briefed, s.BriefingState);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }
}

// =====================================================================================
// WingmanTurnBriefGenerator - prompt assembly + the validation layer (D5/D6).
// =====================================================================================
public sealed class TurnBriefGeneratorValidationTests
{
    private static TurnPackage Package(string? reply = "the agent reply. Approve 1+2 and I'll continue.", string screen = "")
        => new(Guid.NewGuid(), 7, "first ask", "last ask", reply, ReplyPending: false,
               TranscriptDelta: "5. Text: " + (reply ?? ""), ScreenTail: screen,
               RollingIntent: "shipping the feature", PriorRailLines: new List<string>());

    [Fact]
    public void BuildPrompt_ContainsPackageParts_AndContractRules()
    {
        var prompt = WingmanTurnBriefGenerator.BuildPrompt(Package(screen: "SCREEN ROW"));
        Assert.Contains("shipping the feature", prompt);   // rolling intent
        Assert.Contains("last ask", prompt);               // this turn's prompt
        Assert.Contains("SCREEN ROW", prompt);             // grid
        Assert.Contains("selectionMode", prompt);          // contract
        Assert.Contains("pick any that apply", prompt);    // multi-select rule
        Assert.Contains("standing grant", prompt);         // permission-scope rule
        Assert.Contains("headline", prompt);               // v2.2 contract field
        Assert.Contains("turnTitle", prompt);              // v2.2 contract field
        Assert.Contains("newChapter", prompt);             // v2.3 contract field
    }

    [Fact]
    public void BuildPrompt_FeedsCurrentChapterTitle_AndChapterRule()
    {
        var p = Package() with { CurrentHeadline = "Cockpit gets a session story panel" };
        var prompt = WingmanTurnBriefGenerator.BuildPrompt(p);
        Assert.Contains("Current chapter title: Cockpit gets a session story panel", prompt);
        Assert.Contains("KEEP the current title", prompt);
        Assert.Contains("newChapter=false", prompt);
    }

    [Fact]
    public void BuildPrompt_NoHeadlineYet_SaysWriteTheFirstOne()
    {
        var prompt = WingmanTurnBriefGenerator.BuildPrompt(Package());
        Assert.Contains("(none yet - write the first one)", prompt);
    }

    [Fact]
    public void Validate_ParsesHeadlineAndTurnTitle()
    {
        var json = """
        { "headline": "Cockpit gets a session story panel",
          "turnTitle": "Added the headline field",
          "intent": "x", "did": ["y"], "needsYou": null }
        """;
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("Cockpit gets a session story panel", brief.Headline);
        Assert.Equal("Added the headline field", brief.TurnTitle);
    }

    [Fact]
    public void Validate_OmittedHeadline_CarriesCurrentForward()
    {
        var p = Package() with { CurrentHeadline = "The standing headline" };
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(
            """{ "intent": "x", "did": [], "needsYou": null }""", p, "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("The standing headline", brief.Headline);
        Assert.Equal("", brief.TurnTitle);
    }

    [Fact]
    public void Validate_NewChapterTrue_Parsed()
    {
        var p = Package() with { CurrentHeadline = "Old chapter" };
        var json = """{ "headline": "New piece of work", "newChapter": true, "intent": "x", "did": [], "needsYou": null }""";
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(json, p, "wingman:test");
        Assert.NotNull(brief);
        Assert.True(brief.NewChapter);
    }

    [Fact]
    public void Validate_NewChapterOmitted_FalseWhenChapterExists()
    {
        var p = Package() with { CurrentHeadline = "Standing chapter" };
        var json = """{ "headline": "Standing chapter", "intent": "x", "did": [], "needsYou": null }""";
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(json, p, "wingman:test");
        Assert.NotNull(brief);
        Assert.False(brief.NewChapter);
    }

    [Fact]
    public void Validate_FirstTitle_MechanicallyStartsFirstChapter()
    {
        // No current title yet: whatever the model said, the first title IS a chapter start.
        var json = """{ "headline": "First chapter", "newChapter": false, "intent": "x", "did": [], "needsYou": null }""";
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.True(brief.NewChapter);
    }

    [Fact]
    public void Validate_OverlongHeadline_Capped()
    {
        var json = $$"""{ "headline": "{{new string('h', 200)}}", "intent": "x", "did": [], "needsYou": null }""";
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal(60, brief.Headline.Length);
    }

    [Fact]
    public void Validate_GoodSingleSelect_Accepted_WithVerbatimEvidence()
    {
        var json = """
        { "intent": "shipping the feature; awaiting approval",
          "did": ["built it", "verified it"],
          "needsYou": {
            "statement": "Nothing broken. Approve to continue.",
            "answerVia": "reply", "selectionMode": "single", "submit": null,
            "options": [ {"key": "Approve 1+2", "send": "approve 1+2", "note": null} ],
            "evidence": "Approve 1+2 and I'll continue.",
            "urgency": "review", "confidence": "high", "railLine": "Approve 1+2?" } }
        """;
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.False(brief.Degraded);
        Assert.Equal("Approve 1+2 and I'll continue.", brief.NeedsYou?.Evidence);
        Assert.Single(brief.NeedsYou!.Options);
    }

    [Fact]
    public void Validate_FencedJson_Unwrapped()
    {
        var json = "```json\n{ \"intent\": \"x\", \"did\": [], \"needsYou\": null }\n```";
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Null(brief.NeedsYou);
    }

    [Fact]
    public void Validate_ParaphrasedEvidence_DropsReceiptsButKeepsBrief()
    {
        var json = """
        { "intent": "x", "did": ["y"],
          "needsYou": { "statement": "s", "answerVia": "reply", "selectionMode": "single",
            "submit": null, "options": [],
            "evidence": "Please approve options one and two so I can proceed",
            "urgency": "review", "confidence": "high", "railLine": "approve?" } }
        """;
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(json, Package(), "wingman:test");
        Assert.NotNull(brief);
        Assert.Equal("", brief.NeedsYou?.Evidence); // receipts killed, visibly
    }

    [Fact]
    public void Validate_EvidenceFromScreen_Accepted()
    {
        var json = """
        { "intent": "x", "did": [],
          "needsYou": { "statement": "pick one", "answerVia": "keys", "selectionMode": "single",
            "submit": null, "options": [ {"key":"1 Yes","send":"1"} ],
            "evidence": "Do you want to proceed? 1. Yes",
            "urgency": "blocking", "confidence": "high", "railLine": "approve the command?" } }
        """;
        var brief = WingmanTurnBriefGenerator.ParseAndValidate(
            json, Package(reply: null, screen: "This command requires approval\nDo you want to proceed?\n 1. Yes"), "wingman:test");
        Assert.NotNull(brief);
        Assert.NotEqual("", brief.NeedsYou?.Evidence);
    }

    [Fact]
    public void Validate_MultiSelectWithoutSubmit_Rejected()
    {
        var json = """
        { "intent": "x", "did": [],
          "needsYou": { "statement": "pick any", "answerVia": "keys", "selectionMode": "multiple",
            "submit": null, "options": [ {"key":"1 A","send":"1"}, {"key":"2 B","send":"2"} ],
            "evidence": "", "urgency": "blocking", "confidence": "high", "railLine": "pick" } }
        """;
        Assert.Null(WingmanTurnBriefGenerator.ParseAndValidate(json, Package(), "wingman:test"));
    }

    [Fact]
    public async Task Stub_CarriesHeadlineForward_NeverInvents()
    {
        var p = Package() with { CurrentHeadline = "The standing headline" };
        var brief = await new StubTurnBriefGenerator().GenerateAsync(p, CancellationToken.None);
        Assert.NotNull(brief);
        Assert.Equal("The standing headline", brief.Headline);
        Assert.False(brief.NewChapter); // a degrade tier never starts a chapter

        var firstBrief = await new StubTurnBriefGenerator().GenerateAsync(Package(), CancellationToken.None);
        Assert.Equal("", firstBrief!.Headline);
        Assert.False(firstBrief.NewChapter);
    }

    [Fact]
    public void Validate_NotJson_Rejected()
    {
        Assert.Null(WingmanTurnBriefGenerator.ParseAndValidate("Sure! Here's my analysis...", Package(), "wingman:test"));
    }

    [Fact]
    public void Validate_MissingIntent_Rejected()
    {
        Assert.Null(WingmanTurnBriefGenerator.ParseAndValidate("""{ "did": [], "needsYou": null }""", Package(), "wingman:test"));
    }

    [Fact]
    public void Validate_NeedsYouMissingRailLine_Rejected()
    {
        var json = """
        { "intent": "x", "did": [],
          "needsYou": { "statement": "s", "answerVia": "reply", "selectionMode": "single",
            "submit": null, "options": [], "evidence": "", "urgency": "review",
            "confidence": "high", "railLine": "" } }
        """;
        Assert.Null(WingmanTurnBriefGenerator.ParseAndValidate(json, Package(), "wingman:test"));
    }
}
