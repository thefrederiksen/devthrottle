using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE JUDGE DOES NOT GET TO INVENT A PICKER (issue 2976). Code decides whether a picker is drawn
/// (<see cref="PickerOnScreen"/>), and a menu with options on a screen where none is drawn is stored as a reply with
/// no menu and no options. An UNREAD screen corrects nothing, and neither does the typed-but-unsent confirm.
///
/// Two behaviours CHANGED on 2026-09-20 and are tested as changed, not quietly:
///   - a menu whose labels appear in the agent's prose is now corrected. The previous check read labels, called this
///     "the known limit", and left 20 of the corpus's 32 invented menus alone. Reading the picker's own furniture has
///     no such limit.
///   - the feedback survey no longer counts as a picker. It is optional and does not block, and it sat on one stop in
///     fifty; counting it put rating buttons on those stops.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class InventedMenuCheckTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-invented-menu";
    private const string ReplyText = "Shall I apply the migration to the local database now?";
    private const string Spoken = "It asks whether to apply the migration now.";
    private const string MenuQuestion = "Apply the migration?";
    private const string ClaudeCode = "ClaudeCode";
    private static readonly DateTime ObservedAt = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string Marker = ((char)0x276F).ToString();
    private static readonly string CodexMarker = ((char)0x203A).ToString();
    private static readonly string NoBreakSpace = ((char)0x00A0).ToString();

    // A real permission prompt: its own question line, drawn at the start of a row.
    private static readonly string[] RealPickerRows = { "Do you want to proceed?", "> 1. Proceed", "  2. Stop" };

    // A prose question above an empty composer.
    private static readonly string[] ProseRows = { ReplyText, "> " };

    private static TurnVerdictDto Keys(params string[] keys)
    {
        var verdict = new TurnVerdictDto
        {
            VerdictId = "v1",
            AnswerVia = "keys",
            Menu = new TurnVerdictMenuDto { Question = MenuQuestion, SelectionMode = "single", Submit = "" },
            Options = new List<TurnVerdictOptionDto>(),
            Label = "Asks before applying the migration",
        };
        var send = 1;
        foreach (var key in keys.Length == 0 ? new[] { "Proceed" } : keys)
            verdict.Options.Add(new TurnVerdictOptionDto { Key = key, Send = (send++).ToString(), Note = "n" });
        return verdict;
    }

    // ================================================================= the rule, by itself

    [Fact]
    public void Correct_AKeysAnswerOnAProseScreen_BecomesAReply_AndSaysWhyOnTheRecord()
    {
        var verdict = Keys();

        Assert.True(InventedMenuCheck.Correct(verdict, ProseRows, ClaudeCode, out var reading));

        Assert.Equal("reply", verdict.AnswerVia);
        Assert.Null(verdict.Menu);
        Assert.Empty(verdict.Options);
        Assert.False(reading.Drawn);
        Assert.Contains("no picker is drawn", verdict.OptionsDroppedReason);
        // Everything that is not the picker claim is untouched.
        Assert.Equal("Asks before applying the migration", verdict.Label);
    }

    [Fact]
    public void Correct_AKeysAnswerOnARealPicker_IsLeftAlone()
    {
        var verdict = Keys("Proceed", "Stop");

        Assert.False(InventedMenuCheck.Correct(verdict, RealPickerRows, ClaudeCode, out var reading));

        Assert.True(reading.Drawn);
        Assert.Equal("keys", verdict.AnswerVia);
        Assert.Equal(2, verdict.Options.Count);
        Assert.Null(verdict.OptionsDroppedReason);
    }

    /// <summary>CHANGED 2026-09-20. The previous check could not correct this and said so in a test of its own:
    /// the judge's labels are words from the agent's prose, so a label search finds them. The screen still has no
    /// picker drawn on it, and that is what decides now.</summary>
    [Fact]
    public void Correct_AnInventedMenuWhoseLabelsAreInTheProse_IsNowCorrected()
    {
        string[] rows = { "I can proceed with the migration, or stop here and leave it unmade.", "> " };
        var verdict = Keys("Proceed", "Stop");

        Assert.True(InventedMenuCheck.Correct(verdict, rows, ClaudeCode, out _));

        Assert.Equal("reply", verdict.AnswerVia);
    }

    /// <summary>The shape that fooled the judge most, from the corpus: the agent ends on numbered yes/no questions and
    /// the composer already holds a one-line answer. Composer text follows the marker with a NO-BREAK space, which is
    /// what keeps it from reading as a picker's selected row - even when the text starts with a number.</summary>
    [Fact]
    public void Correct_NumberedQuestionsWithAnAnswerSittingInTheComposer_IsCorrected()
    {
        string[] rows =
        {
            "  1. Merge pldos-scraper PR #38? (yes/no)",
            "  2. Rotate the shared internal key? (yes/no)",
            Marker + NoBreakSpace + "2. yes, merge 38",
            "  bypass permissions on (shift+tab to cycle)",
        };
        var verdict = Keys("Yes", "No");

        Assert.True(InventedMenuCheck.Correct(verdict, rows, ClaudeCode, out var reading));

        Assert.False(reading.Drawn);
    }

    [Fact]
    public void Correct_TheTypedButUnsentConfirm_IsKept_BecauseItHasNoOptionsToPress()
    {
        var verdict = Keys();
        verdict.Options = new List<TurnVerdictOptionDto>();
        verdict.Menu = new TurnVerdictMenuDto { Question = "yes, merge 38", SelectionMode = "single", Submit = "\r" };

        Assert.False(InventedMenuCheck.Correct(verdict, ProseRows, ClaudeCode, out _));

        Assert.Equal("keys", verdict.AnswerVia);
        Assert.NotNull(verdict.Menu);
    }

    // ================================================================= the real pickers that broke on 2026-09-17

    [Fact]
    public void Correct_TheFolderTrustPrompt_KeepsItsButtons()
    {
        // Corpus stop b2a10ee7: a real picker whose options carry NO numbers.
        string[] rows =
        {
            " Quick safety check: Is this a project you created or one you trust?",
            " Security guide",
            " " + Marker + " No, exit",
            "   Yes, I trust this folder",
            " Enter to confirm - Esc to cancel",
        };
        var verdict = Keys("No, exit", "Yes, I trust this folder");

        Assert.False(InventedMenuCheck.Correct(verdict, rows, ClaudeCode, out _));

        Assert.Equal(2, verdict.Options.Count);
    }

    /// <summary>A permission prompt whose last row is a stale composer status line left by a repaint. Corpus screen
    /// 1e7d9846, in substance. A rule that looked for "the composer is active" would strip these buttons, on a
    /// prompt asking permission to delete a directory.</summary>
    [Fact]
    public void Correct_APermissionPromptUnderAStaleComposerStatusLine_KeepsItsButtons()
    {
        string[] rows =
        {
            "Dangerous rm operation on critical path: D:/pw_tmp_clean",
            " Do you want to proceed?",
            " " + Marker + " 1. Yes",
            "   2. No",
            " Esc to cancel - Tab to amend",
            "  bypass permissions on (shift+tab to cycle) - esc to interrupt",
        };
        var verdict = Keys("Yes", "No");

        Assert.False(InventedMenuCheck.Correct(verdict, rows, ClaudeCode, out var reading));

        Assert.True(reading.Drawn);
    }

    /// <summary>CHANGED 2026-09-20. Corpus stop 0215b94e. The survey is optional and a person types an ordinary
    /// message beneath it, so it is not a picker the Wingman offers buttons for.</summary>
    [Fact]
    public void Correct_TheFeedbackSurvey_IsNotAPicker()
    {
        string[] rows =
        {
            "  How is Claude doing this session? (optional)",
            "  1: Bad    2: Fine   3: Good   0: Dismiss",
            "  auto mode on (shift+tab to cycle)",
            Marker,
        };
        var verdict = Keys("Bad", "Fine", "Good", "Dismiss");

        Assert.True(InventedMenuCheck.Correct(verdict, rows, ClaudeCode, out _));
    }

    // ================================================================= other agents

    [Fact]
    public void Correct_ACodexPicker_KeepsItsButtons()
    {
        string[] rows =
        {
            "  Do you trust the contents of this directory?",
            CodexMarker + " 1. Yes, continue",
            "  2. No, quit",
            "  Press enter to continue",
        };
        var verdict = Keys("Yes, continue", "No, quit");

        Assert.False(InventedMenuCheck.Correct(verdict, rows, "Codex", out var reading));

        Assert.True(reading.Drawn);
        Assert.True(reading.Calibrated);
    }

    /// <summary>The owner's ruling: an agent the list was never checked against is judged by it anyway, and the
    /// reading says so, so the log shows which decisions were a guess.</summary>
    [Fact]
    public void Correct_AnUncheckedAgent_IsJudgedByTheSameList_AndMarkedUncalibrated()
    {
        var verdict = Keys();

        Assert.True(InventedMenuCheck.Correct(verdict, ProseRows, "Pi", out var reading));

        Assert.False(reading.Calibrated);
        Assert.Contains("UNCALIBRATED", reading.Describe());
        Assert.Contains("UNCALIBRATED", verdict.OptionsDroppedReason);
    }

    // ================================================================= what is never touched

    [Fact]
    public void Correct_AnUnreadScreen_CorrectsNothing()
    {
        var verdict = Keys();

        Assert.False(InventedMenuCheck.Correct(verdict, Array.Empty<string>(), ClaudeCode, out var empty));
        Assert.False(InventedMenuCheck.Correct(verdict, null, ClaudeCode, out var missing));

        Assert.False(empty.ScreenRead);
        Assert.False(missing.ScreenRead);
        Assert.Equal("keys", verdict.AnswerVia);
        Assert.NotNull(verdict.Menu);
    }

    [Fact]
    public void Correct_AReplyAnswer_IsNotTouched()
    {
        var verdict = new TurnVerdictDto { AnswerVia = "reply" };

        Assert.False(InventedMenuCheck.Correct(verdict, ProseRows, ClaudeCode, out _));

        Assert.Equal("reply", verdict.AnswerVia);
    }

    // ================================================================= through the judgement

    [Fact]
    public async Task AJudgedStop_WhoseMenuHasNoPickerOnScreen_IsStoredAsAReply()
    {
        var env = new FakeTurnVerdictEnvironment
        {
            Knobs = TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true, SettleMs = 0 },
            Screen = () => Screen(Sid, ProseRows),
            Conversation = _ => Reply("migrate it", ReplyText),
            Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Menu(MenuQuestion, ReplyText, Spoken)),
        };
        var service = new TurnVerdictService(env);

        var outcome = await service.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        var stored = env.Latest(Tenant, Sid)!;
        Assert.Equal("reply", stored.AnswerVia);
        Assert.Null(stored.Menu);
        Assert.Empty(stored.Options);
        Assert.NotNull(stored.OptionsDroppedReason);
        // Still the judge's reading of the stop: the verdict word and the spoken text are its own.
        Assert.Equal(TurnVerdictVocabulary.NeededYou, stored.Verdict);
        Assert.Equal(Spoken, stored.Spoken);
    }

    [Fact]
    public async Task AJudgedStop_OnARealPicker_KeepsItsKeysAnswerAndItsButtons()
    {
        var env = new FakeTurnVerdictEnvironment
        {
            Knobs = TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true, SettleMs = 0 },
            Screen = () => Screen(Sid, RealPickerRows),
            Conversation = _ => Reply("proceed?", "Do you want to proceed?"),
            Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Menu("Do you want to proceed?", "Do you want to proceed?", Spoken)),
        };
        var service = new TurnVerdictService(env);

        var outcome = await service.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        var stored = env.Latest(Tenant, Sid)!;
        Assert.Equal("keys", stored.AnswerVia);
        Assert.NotNull(stored.Menu);
        Assert.Equal(2, stored.Options.Count);
    }
}
