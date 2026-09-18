using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE JUDGE DOES NOT GET TO INVENT A PICKER (issue 2976). It answered a plain prose question as a menu on 32 of the
/// corpus's 381 gradable turns, and with the narration call that becomes a spoken "press a button on your phone" for
/// a question voice could have answered - about a button the answer route would refuse to press.
///
/// The screen decides, on the judge's OWN OPTION LABELS: a keys answer whose labels are not all on the read screen
/// is stored as a reply with no menu and no options. An UNREAD screen corrects nothing.
///
/// The first version of this check asked for a drawn marker on a NUMBERED option row and took the buttons off two
/// of the three real pickers in the corpus. Those two screens are tests here, verbatim.
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
    private static readonly DateTime ObservedAt = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    // A real picker: an option row carrying the drawn selection marker, which is what the send-time guards read.
    private static readonly string[] RealPickerRows = { "Do you want to proceed?", "> 1. Proceed", "  2. Stop" };

    // A prose question on a composer: no option rows, no marker.
    private static readonly string[] ProseRows = { ReplyText, "> " };

    private static TurnVerdictDto Keys()
    {
        var verdict = new TurnVerdictDto
        {
            VerdictId = "v1",
            AnswerVia = "keys",
            Menu = new TurnVerdictMenuDto { Question = MenuQuestion, SelectionMode = "single", Submit = "" },
            Options = new List<TurnVerdictOptionDto>
            {
                new() { Key = "Proceed", Send = "1", Recommended = true, Note = "Applies it." },
            },
            Label = "Asks before applying the migration",
        };
        return verdict;
    }

    // ================================================================= the rule, by itself

    [Fact]
    public void Correct_AKeysAnswerOnAProseScreen_BecomesAReplyWithNoMenuAndNoOptions()
    {
        var verdict = Keys();

        Assert.True(InventedMenuCheck.Correct(verdict, ProseRows));

        Assert.Equal("reply", verdict.AnswerVia);
        Assert.Null(verdict.Menu);
        Assert.Empty(verdict.Options);
        // Everything that is not the picker claim is untouched.
        Assert.Equal("Asks before applying the migration", verdict.Label);
    }

    [Fact]
    public void Correct_AKeysAnswerOnARealPicker_IsLeftAlone()
    {
        var verdict = Keys();

        Assert.False(InventedMenuCheck.Correct(verdict, RealPickerRows));

        Assert.Equal("keys", verdict.AnswerVia);
        Assert.NotNull(verdict.Menu);
        Assert.Single(verdict.Options);
    }

    // ================================================================= the two real pickers the first version broke

    [Fact]
    public void Correct_TheFolderTrustPrompt_KeepsItsButtons()
    {
        // Corpus stop b2a10ee7, verbatim: a real picker whose options carry NO numbers, so the drawn marker sits
        // on an unnumbered row. The first version of this check read that as "no picker" and took the buttons off.
        string[] rows =
        {
            " Quick safety check: Is this a project you created or one you trust?",
            " Security guide",
            " ❯ No, exit",
            "   Yes, I trust this folder",
            " Enter to confirm · Esc to cancel",
        };
        var verdict = Keys();
        verdict.Options = new List<TurnVerdictOptionDto>
        {
            new() { Key = "No, exit", Send = "1" },
            new() { Key = "Yes, I trust this folder", Send = "2" },
        };

        Assert.False(InventedMenuCheck.Correct(verdict, rows));

        Assert.Equal("keys", verdict.AnswerVia);
        Assert.Equal(2, verdict.Options.Count);
    }

    [Fact]
    public void Correct_TheFeedbackSurvey_KeepsItsButtons()
    {
        // Corpus stop 0215b94e, verbatim: a real picker that puts every choice on ONE line with colons, and draws
        // its marker elsewhere on the grid. No option row, no marker on an option - and still a real picker.
        string[] rows =
        {
            "● How is Claude doing this session? (optional)",
            "  1: Bad    2: Fine   3: Good   0: Dismiss",
            "  auto mode on (shift+tab to cycle)",
            "❯",
        };
        var verdict = Keys();
        verdict.Options = new List<TurnVerdictOptionDto>
        {
            new() { Key = "Bad", Send = "1" },
            new() { Key = "Fine", Send = "2" },
            new() { Key = "Good", Send = "3" },
            new() { Key = "Dismiss", Send = "0" },
        };

        Assert.False(InventedMenuCheck.Correct(verdict, rows));

        Assert.Equal("keys", verdict.AnswerVia);
        Assert.Equal(4, verdict.Options.Count);
    }

    [Fact]
    public void Correct_AnInventedMenuWhoseLabelsAreInTheProse_IsLeftAlone_AndThatIsTheKnownLimit()
    {
        // Honest about what this check cannot do. Twenty of the corpus's thirty-two invented menus label their
        // options with words the agent's own prose used, and no screen check can tell those from a picker. They
        // belong to the judge's prompt. This test exists so a later reader does not mistake the gap for a defect.
        string[] rows = { "I can proceed with the migration, or stop here and leave it unmade.", "> " };
        var verdict = Keys();
        verdict.Options = new List<TurnVerdictOptionDto>
        {
            new() { Key = "Proceed", Send = "1" },
            new() { Key = "Stop", Send = "2" },
        };

        Assert.False(InventedMenuCheck.Correct(verdict, rows));

        Assert.Equal("keys", verdict.AnswerVia);
    }

    [Fact]
    public void Correct_AnUnreadScreen_CorrectsNothing()
    {
        var verdict = Keys();

        Assert.False(InventedMenuCheck.Correct(verdict, Array.Empty<string>()));
        Assert.False(InventedMenuCheck.Correct(verdict, null));

        Assert.Equal("keys", verdict.AnswerVia);
        Assert.NotNull(verdict.Menu);
    }

    [Fact]
    public void Correct_AReplyAnswer_IsNotTouched()
    {
        var verdict = new TurnVerdictDto { AnswerVia = "reply" };

        Assert.False(InventedMenuCheck.Correct(verdict, ProseRows));

        Assert.Equal("reply", verdict.AnswerVia);
    }

    // ================================================================= through the judgement

    [Fact]
    public async Task AJudgedStop_WhoseKeysAnswerTheScreenDoesNotSupport_IsStoredAsAReply()
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
