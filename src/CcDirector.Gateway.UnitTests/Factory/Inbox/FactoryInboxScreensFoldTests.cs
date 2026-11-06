using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Inbox;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// THE WORDS AND TONES THE FACTORY INBOX SCREENS PRINT (Factory Inbox mission, phase 4; CLAUDE.md rule 7). The screens
/// colour a message and a factory's row by the one tone word the Gateway hands them, and print its row lines as given; so
/// every state and decision must map to exactly one tone here, and a row with no messages must say so in words.
/// </summary>
public sealed class FactoryInboxScreensFoldTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    private static FactoryInboxMessageEntity Message(string state, string text = "Rewrite the LinkedIn draft") => new()
    {
        TenantId = "acct-a",
        Factory = "marketing",
        Text = text,
        Note = FactoryInboxWords.Notes.None,
        Source = FactoryInboxWords.Sources.Typed,
        Surface = FactoryInboxWords.Surfaces.Phone,
        SentBy = "owner-phone",
        SentAtUtc = T0,
        State = state,
    };

    private static FactoryInboxDecisionEntity Decision(string kind) => new()
    {
        TenantId = "acct-a",
        Factory = "marketing",
        Kind = kind,
        Why = FactoryInboxWords.Decisions.NeedsWhy(kind) ? "the contract fixes the date" : null,
        DecidedBySessionId = "33333333-3333-4333-8333-333333333333",
        DecidedAtUtc = T0.AddMinutes(5),
    };

    [Theory]
    [InlineData(FactoryInboxWords.States.Sent)]
    [InlineData(FactoryInboxWords.States.Received)]
    [InlineData(FactoryInboxWords.States.WithBoss)]
    public void Tone_OnItsWay_IsAmber(string state)
        => Assert.Equal(FactoryInboxWords.Tones.Amber, FactoryInboxWords.Tone(state, null));

    [Theory]
    [InlineData(FactoryInboxWords.Decisions.Delegated, FactoryInboxWords.Tones.Ok)]
    [InlineData(FactoryInboxWords.Decisions.Todo, FactoryInboxWords.Tones.Ok)]
    [InlineData(FactoryInboxWords.Decisions.Noted, FactoryInboxWords.Tones.Ok)]
    [InlineData(FactoryInboxWords.Decisions.Declined, FactoryInboxWords.Tones.Red)]
    [InlineData(FactoryInboxWords.Decisions.Cannot, FactoryInboxWords.Tones.Red)]
    [InlineData(FactoryInboxWords.Decisions.Pushback, FactoryInboxWords.Tones.Red)]
    public void Tone_Decided_IsOkOrRedByWhetherItTellsTheOwner(string decision, string tone)
        => Assert.Equal(tone, FactoryInboxWords.Tone(FactoryInboxWords.States.Decided, decision));

    [Fact]
    public void Tone_EveryDecision_HasATone()
    {
        foreach (var kind in FactoryInboxWords.Decisions.All)
            Assert.Contains(FactoryInboxWords.Tone(FactoryInboxWords.States.Decided, kind),
                new[] { FactoryInboxWords.Tones.Ok, FactoryInboxWords.Tones.Red });
    }

    [Fact]
    public void Tone_UnknownState_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => FactoryInboxWords.Tone("lost", null));

    [Fact]
    public void Message_CarriesItsTone()
    {
        var folded = FactoryInboxFold.Message(Message(FactoryInboxWords.States.Decided), Decision(FactoryInboxWords.Decisions.Declined));

        Assert.Equal(FactoryInboxWords.Tones.Red, folded.Tone);
        Assert.Equal("Declined", folded.StateLabel);
    }

    [Theory]
    [InlineData(FactoryInboxWords.Sources.Typed, "Typed")]
    [InlineData(FactoryInboxWords.Sources.Spoken, "Spoken")]
    public void Message_CarriesHowItWasPutInInWords(string source, string label)
    {
        var m = Message(FactoryInboxWords.States.Sent);
        m.Source = source;

        Assert.Equal(label, FactoryInboxFold.Message(m, null).SourceLabel);
    }

    [Fact]
    public void Row_NoMessages_SaysSoInGrey()
    {
        var row = FactoryInboxFold.Row(0, null, null);

        Assert.Equal("No messages yet", row.Line);
        Assert.Null(row.WaitingLine);
        Assert.Equal("No messages", row.Badge);
        Assert.Equal(FactoryInboxWords.Tones.Grey, row.Tone);
    }

    [Fact]
    public void Row_NewestWithTheBoss_ShowsItsTextWaitingLineAndAmberBadge()
    {
        var row = FactoryInboxFold.Row(2, Message(FactoryInboxWords.States.WithBoss, "Love the new thumbnails"), null);

        Assert.Equal("Love the new thumbnails", row.Line);
        Assert.Equal("2 waiting for a decision", row.WaitingLine);
        Assert.Equal("With the Boss", row.Badge);
        Assert.Equal(FactoryInboxWords.Tones.Amber, row.Tone);
    }

    [Fact]
    public void Row_NewestDelegatedAndNothingWaiting_IsAllDealtWithInOk()
    {
        var row = FactoryInboxFold.Row(0, Message(FactoryInboxWords.States.Decided), Decision(FactoryInboxWords.Decisions.Delegated));

        Assert.Equal("All dealt with", row.WaitingLine);
        Assert.Equal("Delegated", row.Badge);
        Assert.Equal(FactoryInboxWords.Tones.Ok, row.Tone);
    }

    [Theory]
    [InlineData(0, "All dealt with")]
    [InlineData(1, "1 waiting for a decision")]
    [InlineData(7, "7 waiting for a decision")]
    public void WaitingLine_SaysTheCountInWords(int waiting, string expected)
        => Assert.Equal(expected, FactoryInboxWords.WaitingLine(waiting));
}
