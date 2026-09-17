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
/// The screen decides: a keys answer whose read screen carries no DRAWN selection marker is stored as a reply with
/// no menu and no options. An UNREAD screen corrects nothing.
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
