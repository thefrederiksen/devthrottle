using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// CALL B WRITES THE LABEL AND THE NARRATION, AND NOTHING ELSE (the turn pipeline mission, phase 4). Its answer is
/// read mechanically into those two parts; an answer in any other shape is a failed Call B that leaves Call A's colour
/// standing and the row on its plain state label; a menu stop is told to end with "open the session to choose"; no
/// prompt anywhere tells a person to press a button; and nothing about the stop is stored until both calls are done.
///
/// Every screen and conversation below is written from scratch. Not a byte of a real session is here.
/// </summary>
public sealed class CallBLabelAndNarrationTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-callb";
    private static readonly DateTime ObservedAt = new(2026, 9, 26, 11, 0, 0, DateTimeKind.Utc);
    private const string Report = "The retention sweep now deletes rows older than seven days. The tests are green.";
    private const string Label = "Retention sweep done, tests green";
    private const string Words = "The retention sweep is finished and its tests pass, so nothing is waiting on you.";

    private static TurnEndSignal Signal() => new(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);

    private static string Wrapped(string body)
        => $"{Core.Drivers.SessionAskRunner.AnswerBeginMarker}\n{body}\n{Core.Drivers.SessionAskRunner.AnswerEndMarker}";

    private static string Shaped(string label, string words)
        => Wrapped($"{NarrationCall.LabelTag} {label}\n{NarrationCall.NarrationTag}\n{words}");

    /// <summary>A plain finished report: no code step fires, so the model answers "done" and Call B follows.</summary>
    private static FakeTurnVerdictEnvironment Env(Func<string, CancellationToken, Task<string>> narrator) => new()
    {
        Screen = () => Screen(Sid, "  " + Report, "", "> "),
        Conversation = _ => Reply("tidy the retention timer", Report),
        Judge = (_, _) => Task.FromResult("done"),
        Narrator = narrator,
    };

    private static SessionDto RowFor(TurnVerdictDto verdict)
    {
        var row = new SessionDto { SessionId = Sid, ActivityState = "WaitingForInput" };
        TurnVerdictRowStamp.Stamp(new[] { row }, new OneRecord(verdict), Tenant);
        return row;
    }

    private sealed class OneRecord : ITurnVerdictRowSource
    {
        private readonly TurnVerdictDto _verdict;
        public OneRecord(TurnVerdictDto verdict) => _verdict = verdict;
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant)
            => new Dictionary<string, TurnVerdictDto> { [Sid] = _verdict };
        public bool IsReading(TenantId tenant, string sessionId) => false;
    }

    // ================================================================= the answer is read mechanically

    [Fact]
    public void ParseAnswer_ALabelLineAndANarration_AreReadIntoTheirTwoParts()
    {
        var parsed = NarrationCall.ParseAnswer(Shaped(Label, Words));

        Assert.Null(parsed.FailureReason);
        Assert.Equal(Label, parsed.Label);
        Assert.Equal(Words, parsed.Spoken);
    }

    [Fact]
    public void ParseAnswer_TheNarrationMayStartOnTheNarrationLineItself_AndRunOverSeveralLines()
    {
        var parsed = NarrationCall.ParseAnswer(Wrapped(
            $"{NarrationCall.LabelTag} {Label}\n\n{NarrationCall.NarrationTag} The sweep is finished.\nIts tests pass."));

        Assert.Null(parsed.FailureReason);
        Assert.Equal(Label, parsed.Label);
        Assert.Contains("The sweep is finished.", parsed.Spoken);
        Assert.Contains("Its tests pass.", parsed.Spoken);
    }

    [Fact]
    public void ParseAnswer_ALabelOfExactlyTenWords_IsKept()
    {
        var ten = "one two three four five six seven eight nine ten";
        Assert.Equal(ten, NarrationCall.ParseAnswer(Shaped(ten, Words)).Label);
    }

    [Theory]
    // No label line at all - the old shape, the words alone between the markers.
    [InlineData("The retention sweep is finished.", "did not start with the LABEL: line")]
    // A label line with nothing on it.
    [InlineData("LABEL:\nNARRATION:\nThe retention sweep is finished.", "label was empty")]
    // Eleven words: over the bound.
    [InlineData("LABEL: one two three four five six seven eight nine ten eleven\nNARRATION:\nThe sweep is finished.", "ran to 11 words")]
    // A label and no narration line.
    [InlineData("LABEL: Retention sweep done\nThe retention sweep is finished.", "no NARRATION: line")]
    // A narration line and no words.
    [InlineData("LABEL: Retention sweep done\nNARRATION:\n   ", "narration held no words")]
    // The parts the other way round.
    [InlineData("NARRATION:\nThe sweep is finished.\nLABEL: Retention sweep done", "did not start with the LABEL: line")]
    public void ParseAnswer_AnAnswerInAnyOtherShape_IsAFailedCallB_WithNoLabelAndNoWords(string body, string reason)
    {
        var parsed = NarrationCall.ParseAnswer(Wrapped(body));

        Assert.NotNull(parsed.FailureReason);
        Assert.Contains(reason, parsed.FailureReason);
        Assert.Equal("", parsed.Label);
        Assert.Equal("", parsed.Spoken);
    }

    [Fact]
    public void ParseAnswer_NothingAtAll_IsAFailedCallB()
    {
        Assert.Equal("the narration call answered with no words", NarrationCall.ParseAnswer("").FailureReason);
        Assert.NotNull(NarrationCall.ParseAnswer(null).FailureReason);
    }

    // ================================================================= through the real seat: the label moves to Call B

    [Fact]
    public async Task AReading_CarriesCallBsLabel_AndTheRowShowsIt_BesideCallAsColour()
    {
        var env = Env((_, _) => Task.FromResult(Shaped(Label, Words)));

        await new TurnVerdictService(env).StartTurnEnd(Signal());

        var stored = env.Latest(Tenant, Sid)!;
        Assert.False(stored.Failed, stored.FailureReason);
        Assert.Equal("finished-done", stored.State);
        Assert.Equal(Label, stored.Label);
        Assert.Equal(Words, stored.Narration);
        Assert.Null(stored.NarrationFailureReason);
        var row = RowFor(stored);
        Assert.Equal("cyan", SessionOrdering.EffectiveColor(row));
        Assert.Equal(Label, row.VerdictLabel);
        Assert.Contains(Label, SessionOrdering.StateLabel(row));
    }

    /// <summary>
    /// A MALFORMED CALL B IS A FAILED CALL B, AND CALL A'S COLOUR STANDS. The reading is stored - not failed, still
    /// cyan - with no label, so the row falls back to its plain state label, and no words. The narration's own
    /// failure is recorded so the card can say the words are missing.
    /// </summary>
    [Fact]
    public async Task AMalformedCallB_LeavesCallAsColour_ThePlainStateLabel_AndNoWords()
    {
        // CONTROL: the plain state label a v4 row with no label shows, read off a reading whose Call B was never made.
        var control = RowFor(new TurnVerdictDto { VerdictId = "v-control", ContractVersion = "v4", Verdict = "finished", FinishedKind = "done" });
        var plainLabel = SessionOrdering.StateLabel(control);

        var env = Env((_, _) => Task.FromResult(Wrapped("The retention sweep is finished and nothing is waiting.")));

        await new TurnVerdictService(env).StartTurnEnd(Signal());

        var stored = env.Latest(Tenant, Sid)!;
        Assert.False(stored.Failed, stored.FailureReason);
        Assert.Equal("finished-done", stored.State);
        Assert.Equal("", stored.Label);
        Assert.Null(stored.Narration);
        Assert.Equal(WingmanFailureKinds.NarrationFailed, stored.FailureKind);
        Assert.Contains("LABEL:", stored.NarrationFailureReason);
        var row = RowFor(stored);
        Assert.Equal("cyan", SessionOrdering.EffectiveColor(row));
        Assert.Null(row.VerdictLabel);
        Assert.Equal(plainLabel, SessionOrdering.StateLabel(row));
    }

    [Fact]
    public async Task ACallBThatDidNotAnswer_LeavesCallAsColour_AndThePlainStateLabel()
    {
        var env = Env((_, _) => throw new TimeoutException("the narration call did not answer"));

        await new TurnVerdictService(env).StartTurnEnd(Signal());

        var stored = env.Latest(Tenant, Sid)!;
        Assert.False(stored.Failed);
        Assert.Equal("", stored.Label);
        Assert.Equal("cyan", SessionOrdering.EffectiveColor(RowFor(stored)));
    }

    /// <summary>A person asking again after a failed Call B gets the label as well as the words.</summary>
    [Fact]
    public async Task SaveNarration_WithALabel_PutsItOnTheReading()
    {
        var env = Env((_, _) => Task.FromResult(Wrapped("no shape at all")));
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal());
        var stored = env.Latest(Tenant, Sid)!;
        Assert.Equal("", stored.Label);   // CONTROL: Call B failed inside the reading

        Assert.True(service.SaveNarration(Tenant, Sid, stored.VerdictId, Words, Label));

        var after = env.Latest(Tenant, Sid)!;
        Assert.Equal(Label, after.Label);
        Assert.Equal(Words, after.Narration);
        Assert.Null(after.NarrationFailureReason);
    }

    // ================================================================= timing: nothing until both calls are done

    [Fact]
    public async Task NothingIsStored_UntilCallBHasAnswered_AndThenColourLabelAndWordsArriveTogether()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var env = Env((_, _) => release.Task);

        var reading = new TurnVerdictService(env).StartTurnEnd(Signal());
        // Call A has answered and Call B is running: nothing about this stop is on record yet.
        for (var i = 0; i < 200 && env.NarratorCalls == 0; i++) await Task.Delay(10);
        Assert.Equal(1, env.NarratorCalls);
        Assert.Equal(1, env.JudgeCalls);
        Assert.Null(env.Latest(Tenant, Sid));

        release.SetResult(Shaped(Label, Words));
        await reading;

        var stored = env.Latest(Tenant, Sid)!;
        Assert.Equal("finished-done", stored.State);
        Assert.Equal(Label, stored.Label);
        Assert.Equal(Words, stored.Narration);
        Assert.Equal(1, env.StoredCount(Tenant, Sid));
    }

    // ================================================================= what Call B is given

    [Fact]
    public void TheCallBPrompt_CarriesTheWordAndTheCodeStepsReason_AndNoMenuOptionsRecommendationOrKeysLine()
    {
        var package = new TurnVerdictPackage { ScreenRows = new[] { "Shall I backfill?" }, LatestReply = "Shall I backfill?", AgentKind = "ClaudeCode" };
        var verdict = new TurnVerdictDto
        {
            ContractVersion = "v4",
            Verdict = "needed-you",
            AnswerVia = "reply",
            DecidedBy = CallACodeSteps.QuestionStep,
            DecisionReason = "the reply asks a question: \"Shall I backfill?\"",
        };

        var prompt = NarrationCall.BuildPrompt(SpokenLanguages.English, null, package, verdict);

        Assert.Contains("What the stop is: needs-you", prompt);
        Assert.Contains("Why: the reply asks a question: \"Shall I backfill?\"", prompt);
        Assert.DoesNotContain("How the person answers", prompt);
        Assert.DoesNotContain("KEYS", prompt);
        Assert.DoesNotContain("What the agent recommends", prompt);
        Assert.DoesNotContain("The menu's", prompt);
        Assert.DoesNotContain("(recommended)", prompt);
        // A question is not a menu: no open-the-session sentence.
        Assert.DoesNotContain(NarrationCall.MenuClosingInstruction, prompt);
        // The output shape both parts are read from, asked for after everything else.
        Assert.Contains(NarrationCall.LabelTag + " <label>", prompt);
        Assert.Contains(NarrationCall.NarrationTag, prompt);
        Assert.True(prompt.LastIndexOf(NarrationCall.LabelTag, StringComparison.Ordinal) > prompt.IndexOf("LATEST reply", StringComparison.Ordinal));
    }

    [Fact]
    public void AModelDecision_HandsOnNoReason_OnlyTheWord()
    {
        var package = new TurnVerdictPackage { LatestReply = Report, AgentKind = "ClaudeCode" };
        var verdict = new TurnVerdictDto
        {
            ContractVersion = "v4", Verdict = "finished", FinishedKind = "done", AnswerVia = "reply",
            DecidedBy = CallACodeSteps.ModelStep, DecisionReason = "no code step fired; the model answered 'done'",
        };

        var prompt = NarrationCall.BuildPrompt(SpokenLanguages.English, null, package, verdict);

        Assert.Contains("What the stop is: finished-done", prompt);
        Assert.DoesNotContain("Why:", prompt);
    }

    /// <summary>
    /// A MENU NEEDS-YOU STOP IS TOLD TO SAY "OPEN THE SESSION TO CHOOSE" - a v4 picker decision and a v3 keys record
    /// alike, and on an account with its own instructions too, because the sentence is code-owned.
    /// </summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "Speak briefly and plainly.")]
    [InlineData(true, "Speak briefly and plainly.")]
    public void AMenuStop_IsToldToEndWithOpenTheSessionToChoose(bool storedUnderV3, string? ownInstructions)
    {
        var package = new TurnVerdictPackage
        {
            ScreenRows = new[] { " Do you want to proceed?", "   1. Yes", "   2. No" },
            LatestReply = "I will clear the build folder.",
            AgentKind = "ClaudeCode",
        };
        var verdict = storedUnderV3
            ? new TurnVerdictDto
            {
                ContractVersion = "v3.1", Verdict = "needed-you", AnswerVia = "keys",
                Menu = new TurnVerdictMenuDto { Question = "Proceed?" },
                Options = { new TurnVerdictOptionDto { Key = "Yes", Send = "1", Recommended = true }, new TurnVerdictOptionDto { Key = "No", Send = "2" } },
            }
            : new TurnVerdictDto
            {
                ContractVersion = "v4", Verdict = "needed-you", AnswerVia = "reply",
                DecidedBy = CallACodeSteps.PickerStep, DecisionReason = "a picker or permission prompt is on the screen",
            };

        var prompt = NarrationCall.BuildPrompt(SpokenLanguages.English, ownInstructions, package, verdict);

        Assert.Contains(NarrationCall.MenuClosingInstruction, prompt);
        Assert.Contains("open the session to choose", prompt);
        Assert.DoesNotContain("press a button", prompt, StringComparison.OrdinalIgnoreCase);
        // The v3 record's menu and options are NOT handed in: Call B does not read them any more.
        Assert.DoesNotContain("Proceed?", prompt);
        Assert.DoesNotContain("(recommended)", prompt);
    }

    // ================================================================= no prompt anywhere says "press a button"

    [Fact]
    public void NoPromptAnywhere_TellsAPersonToPressAButton()
    {
        const string banned = "press a button";
        // The shipped narration rules, which an account replaces or keeps.
        Assert.DoesNotContain(banned, WingmanTranslator.FidelityPrompt, StringComparison.OrdinalIgnoreCase);
        // Call A's prompt template.
        Assert.DoesNotContain(banned, TurnVerdictContract.PromptTemplate, StringComparison.OrdinalIgnoreCase);
        // Every registered spoken path, in every language it is built for.
        foreach (var path in SpokenPaths.All)
            foreach (var language in SpokenLanguages.All)
                Assert.DoesNotContain(banned, path.Render(language), StringComparison.OrdinalIgnoreCase);
        // Call B for each shape of stop.
        var package = new TurnVerdictPackage { ScreenRows = new[] { "   1. Yes", "   2. No" }, LatestReply = "Choose.", AgentKind = "ClaudeCode" };
        foreach (var verdict in new[]
                 {
                     new TurnVerdictDto { Verdict = "needed-you", DecidedBy = CallACodeSteps.PickerStep, DecisionReason = "a picker" },
                     new TurnVerdictDto { Verdict = "needed-you", AnswerVia = "keys", Menu = new TurnVerdictMenuDto { Question = "Pick" } },
                     new TurnVerdictDto { Verdict = "needed-you", AnswerVia = "reply" },
                     new TurnVerdictDto { Verdict = "finished", FinishedKind = "done", AnswerVia = "reply" },
                     new TurnVerdictDto { Verdict = "continues-alone", AnswerVia = "reply" },
                 })
            Assert.DoesNotContain(banned, NarrationCall.BuildPrompt(SpokenLanguages.English, null, package, verdict), StringComparison.OrdinalIgnoreCase);
    }
}
