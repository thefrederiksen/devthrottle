using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// What the turn-verdict seat writes for the Wingman inspector (devthrottle_internal#2029). The rule, in one line: a
/// trace for every STOP whatever happened to it, and for every JUDGE CALL whatever asked for it - carrying the
/// package the judge was given, the exact prompt, the answer as received and how long it took, or, for a stop that
/// stood down, its cause and no content.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
///
/// Every screen and conversation below is written from scratch. Not a byte of a real session is here.
/// </summary>
public sealed class TurnVerdictTraceTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-1";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private static readonly DateTime ObservedAt = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
    private const string Spoken = "The retention sweep. The branch is pushed and nothing is waiting on you.";
    private static readonly string FinishedAnswer = FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static TurnEndSignal Signal(DateTime? at = null) => new(Sid, "dir-1", Tenant, at ?? ObservedAt, IsNewTurn: true);

    private static FakeTurnVerdictEnvironment Env() => new()
    {
        Screen = () => Screen(Sid, ReplyText, "> "),
        Conversation = _ => Reply("push it", ReplyText),
        Judge = (_, _) => Task.FromResult(FinishedAnswer),
    };

    // ================================================================= a judge call

    [Fact]
    public async Task AJudgedStop_LeavesOneTrace_WithTheExactPromptTheRawReplyTheReplyTimeAndThePackage()
    {
        var env = Env();
        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Judged, trace.Outcome);
        Assert.Equal("turn-end", trace.Trigger);
        Assert.Equal(Sid, trace.SessionId);
        Assert.Equal("dir-1", trace.DirectorId);
        Assert.Equal(ObservedAt, trace.TurnEndObservedAtUtc);
        Assert.Equal(outcome.Verdict!.VerdictId, trace.VerdictId);
        Assert.Equal(outcome.Verdict.VerdictId, trace.Verdict!.VerdictId);
        // The prompt the judge was ACTUALLY asked, not a rebuild of it.
        Assert.Equal(Assert.Single(env.Prompts), trace.Prompt);
        Assert.Equal(FinishedAnswer, trace.RawReply);
        Assert.Equal(0.2, trace.ReplySeconds);
        Assert.Contains(ReplyText, trace.Package!.ScreenRows);
        Assert.Equal(ReplyText, trace.Package.LatestReply);
        Assert.False(trace.ColourEnabled);
    }

    [Fact]
    public async Task ARefusedAnswer_LeavesATrace_WithTheRawReplyThatFailed_AndTheReason()
    {
        var env = Env();
        // A receipt that is on neither the screen nor the reply: the contract refuses it.
        var invented = FakeTurnVerdictEnvironment.Finished("I deployed the Gateway before tagging.", Spoken);
        env.Judge = (_, _) => Task.FromResult(invented);

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Refused, trace.Outcome);
        Assert.Equal(invented, trace.RawReply);
        Assert.True(trace.Verdict!.Failed);
        Assert.False(string.IsNullOrWhiteSpace(trace.Verdict.FailureReason));
        Assert.NotNull(trace.Prompt);
        Assert.NotNull(trace.Package);
    }

    [Fact]
    public async Task AJudgeThatDoesNotAnswer_LeavesATrace_WithThePromptAndNoReply()
    {
        var env = Env();
        env.Judge = (_, _) => throw new TimeoutException("the judge took longer than thirty seconds");

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.DidNotAnswer, trace.Outcome);
        Assert.NotNull(trace.Prompt);
        Assert.Null(trace.RawReply);
        Assert.Null(trace.ReplySeconds);
    }

    [Fact]
    public async Task WhenTheVerdictStoreThrowsAfterTheJudgeAnswered_TheTraceStillKeepsThePromptAndTheWholeAnswer()
    {
        // Found in review: the evidence lived in locals the flight's exception boundary could not see, so this trace
        // carried only the exception type - exactly the case where what the judge said is the thing that was lost.
        var env = Env();
        env.NextStoreThrows = new IOException("the database file is locked");

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Unavailable, trace.Outcome);
        Assert.Equal(nameof(IOException), trace.Cause);
        Assert.Equal(outcome.Verdict!.VerdictId, trace.VerdictId);
        Assert.Equal(Assert.Single(env.Prompts), trace.Prompt);
        Assert.Equal(FinishedAnswer, trace.RawReply);
        Assert.Equal(0.2, trace.ReplySeconds);
        Assert.Contains(ReplyText, trace.Package!.ScreenRows);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterTheSessionWorked_LeavesACancelledTrace_WithThePromptAndTheAnswer()
    {
        var env = Env();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.OnSessionWorking(Tenant, Sid);
        release.SetResult();

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Cancelled, trace.Outcome);
        Assert.Equal(ActivityCauses.WorkingObservation, trace.Cause);
        Assert.Null(trace.VerdictId);
        Assert.Null(trace.Verdict);
        Assert.Equal(Assert.Single(env.Prompts), trace.Prompt);
        Assert.Equal(FinishedAnswer, trace.RawReply);
    }

    [Fact]
    public async Task ACancelledTrace_CarriesItsOwnStopsMoment_EvenWhenALaterStopIsObservedBeforeTheCancellationLands()
    {
        // Found in review: the cancellation looked up "the latest observed stop" again, after the session had moved on,
        // and stamped the old stop's screen and answer with the new stop's moment.
        var env = Env();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.OnSessionWorking(Tenant, Sid);
        var later = ObservedAt.AddMinutes(3);
        var second = await service.StartTurnEnd(Signal(later));
        Assert.Equal(ActivityCauses.AlreadyJudging, second.SkipCause);
        release.SetResult();

        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Kind);

        // One trace: the already-judging stop records nothing of its own, so one stop never has two outcomes.
        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Cancelled, trace.Outcome);
        Assert.Equal(ObservedAt, trace.TurnEndObservedAtUtc);
    }

    [Fact]
    public async Task ACancellation_TracesUnderTheSettingsItsFlightStartedWith_AndCompletes_EvenWhenASettingsReadWouldNowThrow()
    {
        // Found in review: the cancellation read the settings again; a throw there escaped the flight's boundary and
        // left every joined caller waiting forever, and a switch flipped mid-flight changed what was recorded.
        var env = Env();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        env.SettingsOverride = () => throw new InvalidOperationException("the settings store is unreachable");
        service.OnSessionWorking(Tenant, Sid);
        release.SetResult();

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, outcome.Kind);
        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Cancelled, trace.Outcome);
        Assert.Equal(FinishedAnswer, trace.RawReply);
    }

    // ================================================================= a stop that stood down

    [Fact]
    public async Task AHeldStop_LeavesASkippedTrace_WithItsCause_AndNothingFromItsScreen()
    {
        var env = Env();
        env.Held = _ => true;

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Skipped, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Skipped, trace.Outcome);
        Assert.Equal(ActivityCauses.Held, trace.Cause);
        Assert.Equal(ObservedAt, trace.TurnEndObservedAtUtc);
        Assert.Null(trace.VerdictId);
        Assert.Null(trace.Package);
        Assert.Null(trace.Prompt);
        Assert.Null(trace.RawReply);
        Assert.Equal(0, env.ScreenReads);
    }

    [Fact]
    public async Task TheVoicePathSkippingAHeldSession_LeavesNoTrace_BecauseItIsNotAStop()
    {
        var env = Env();
        env.Held = _ => true;

        var outcome = await new TurnVerdictService(env).VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice);

        Assert.Equal(TurnVerdictOutcomeKind.Skipped, outcome.Kind);
        Assert.Empty(env.Traces);
    }

    [Fact]
    public async Task AnAccountWithItsJudgeSwitchOff_LeavesNoTrace_ForASkippedStop_OrForAJudgedVoiceSession()
    {
        var env = Env();
        env.Knobs = env.Knobs with { JudgeEnabled = false };
        var service = new TurnVerdictService(env);

        var skipped = await service.StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Skipped, skipped.Kind);

        // A voice session is still judged with the switch off, because its narration IS the verdict.
        env.VoiceSession = _ => true;
        var judged = await service.StartTurnEnd(Signal(ObservedAt.AddMinutes(1)));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, judged.Kind);

        Assert.Empty(env.Traces);
    }

    [Fact]
    public async Task ANewStopOnAnUnchangedScreen_LeavesAReusedTrace_WithNoPromptBecauseNobodyWasAsked()
    {
        var env = Env();
        var service = new TurnVerdictService(env);
        var first = await service.StartTurnEnd(Signal());

        var later = ObservedAt.AddMinutes(2);
        var second = await service.StartTurnEnd(Signal(later));
        Assert.Equal(TurnVerdictOutcomeKind.Reused, second.Kind);

        Assert.Equal(2, env.Traces.Count);
        var reused = env.Traces.Last();
        Assert.Equal(TurnVerdictTraceOutcomes.Reused, reused.Outcome);
        Assert.Equal(first.Verdict!.VerdictId, reused.VerdictId);
        Assert.Equal(later, reused.TurnEndObservedAtUtc);
        Assert.Null(reused.Prompt);
        Assert.Null(reused.RawReply);
    }

    [Fact]
    public async Task TheVoicePathComingPastAnUnchangedScreen_LeavesNoFurtherTrace()
    {
        var env = Env();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal());

        var again = await service.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice);

        Assert.Equal(TurnVerdictOutcomeKind.Reused, again.Kind);
        Assert.Single(env.Traces);
    }

    // ================================================================= the carrying-on clock

    [Fact]
    public void ACarryingOnClockThatRunsOut_LeavesAnExpiredTrace_NamingTheVerdictItReplaced()
    {
        var now = ObservedAt;
        var env = new FakeTurnVerdictEnvironment
        {
            Knobs = TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true, SettleMs = 0 },
            Clock = () => now,
        };
        env.Store(Tenant, Sid, new TurnVerdictDto
        {
            VerdictId = "carry-1",
            JudgedAtUtc = ObservedAt,
            TurnEndObservedAtUtc = ObservedAt.AddSeconds(-30),
            ScreenHash = "hash-1",
            Model = FakeTurnVerdictEnvironment.Model,
            ContractVersion = TurnVerdictContract.Version,
            PackageKind = "agent-reply",
            Verdict = TurnVerdictVocabulary.ContinuesAlone,
            Confidence = "high",
            Evidence = "I will keep watching the nightly build.",
            Label = "Watching the nightly build",
            Summary = "It is watching the nightly build by itself.",
            AnswerVia = "reply",
            Risk = "none",
            Spoken = "The nightly build. It is watching the build.",
        });
        var service = new TurnVerdictService(env);

        now = ObservedAt.AddMinutes(10);
        Assert.Equal(1, service.ExpireCarryingOn(Tenant));

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Expired, trace.Outcome);
        Assert.Equal("clock", trace.Trigger);
        Assert.Equal("carry-1", trace.ReplacedVerdictId);
        Assert.Equal(env.Latest(Tenant, Sid)!.VerdictId, trace.VerdictId);
        Assert.Equal(TurnVerdictVocabulary.NeededYou, trace.Verdict!.Verdict);
        Assert.Equal(ObservedAt.AddSeconds(-30), trace.TurnEndObservedAtUtc);
        Assert.True(trace.ColourEnabled);
    }

    // ================================================================= through the production environment

    [Fact]
    public async Task ThroughTheProductionEnvironment_AWorkingEdgeClearsTheVerdict_ButTheTraceStays()
    {
        // The real environment, the real writer and the real stores, so the claim is about what the Gateway writes
        // and keeps: the verdict row goes when the session works, and the inspector's record does not.
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Tenant, "dir-1", "conn-1");
        Assert.True(pushed.ApplySnapshot(Tenant, "dir-1", "conn-1", 1, new[]
        {
            new SessionDto { SessionId = Sid, Name = "the pushing session", Agent = "ClaudeCode", ActivityState = "WaitingForInput" },
        }));

        var db = _harness.Open();
        var traces = new TurnVerdictTraceStore(db);
        using var writer = new TurnVerdictTraceWriter(traces.Append);
        var env = new GatewayTurnVerdictEnvironment(
            settings: _ => TurnVerdictSettings.Defaults with { JudgeEnabled = true, SettleMs = 0 },
            pushedSessions: pushed,
            streamStale: TimeSpan.FromMinutes(5),
            route: (_, directorId) => RouteServing(directorId, () => Screen(Sid, ReplyText, "> ")),
            conversation: (_, _) => Reply("push it", ReplyText),
            judgeBrain: (_, _) => new CountingBrain(() => FinishedAnswer),
            judgeModel: _ => FakeTurnVerdictEnvironment.Model,
            store: new TurnVerdictStore(db),
            traces: writer,
            language: _ => SpokenLanguages.English,
            customSpokenRules: () => null,
            isVoiceSession: (_, _) => false);
        var service = new TurnVerdictService(env);

        var judged = await service.StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Judged, judged.Kind);
        Assert.NotNull(env.Latest(Tenant, Sid));

        service.OnSessionWorking(Tenant, Sid);
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(env.Latest(Tenant, Sid));
        Assert.Equal(0, writer.Failed);
        var trace = Assert.Single(traces.History(Tenant, Sid));
        Assert.Equal(judged.Verdict!.VerdictId, trace.VerdictId);
        Assert.Equal(TurnVerdictTraceOutcomes.Judged, trace.Outcome);
        Assert.Contains(ReplyText, trace.Package!.ScreenRows);
        Assert.False(string.IsNullOrWhiteSpace(trace.Prompt));
        Assert.Equal(FinishedAnswer, trace.RawReply);
    }
}
