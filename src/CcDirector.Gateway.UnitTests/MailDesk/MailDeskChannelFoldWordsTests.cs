using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.MailDesk;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE WORDS AND TONES THE MAIL DESK PAGE SHOWS (Mail Desk mission, phase 7 contract, section 2). Each is decided once in
/// <see cref="MailDeskChannelFold"/>; a client turns a tone into a colour and shows every label verbatim. These pin every
/// tone, every label, one machines sentence per machine count, a provider with no label refused, and today's counts in
/// the order <see cref="MailDeskWords.Decisions"/> lists them with zeros.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskChannelFoldWordsTests
{
    private static readonly Guid Channel = new("7a000000-0000-4000-8000-000000000001");

    // ---------- the provider ----------

    [Fact]
    public void ProviderLabelOf_Outlook_IsOutlook()
    {
        Assert.Equal("Outlook", MailDeskChannelFold.ProviderLabelOf(Channel, MailDeskWords.Providers.Outlook));
    }

    [Fact]
    public void ProviderLabels_EveryProvider_HasALabel()
    {
        Assert.All(MailDeskWords.Providers.All, p => Assert.False(string.IsNullOrWhiteSpace(MailDeskWords.Providers.Labels[p])));
        Assert.Equal(MailDeskWords.Providers.All.Count, MailDeskWords.Providers.Labels.Count);
    }

    [Fact]
    public void ProviderLabelOf_ProviderWithNoLabel_Throws()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => MailDeskChannelFold.ProviderLabelOf(Channel, "gmail"));

        Assert.Contains("'gmail'", thrown.Message);
        Assert.Contains(Channel.ToString(), thrown.Message);
    }

    // ---------- the machines ----------

    [Fact]
    public void MachinesLabelOf_OneMachine_IsItsName()
    {
        Assert.Equal("devlinux", MailDeskChannelFold.MachinesLabelOf(Channel, new[] { "devlinux" }));
    }

    [Fact]
    public void MachinesLabelOf_TwoMachines_NamesBothWithNoOrder()
    {
        Assert.Equal("devlinux or north-box, whichever runs the Mail Desk check",
            MailDeskChannelFold.MachinesLabelOf(Channel, new[] { "devlinux", "north-box" }));
    }

    [Fact]
    public void MachinesLabelOf_ThreeMachines_NamesAllWithNoOrder()
    {
        Assert.Equal("devlinux, north-box or south-box, whichever runs the Mail Desk check",
            MailDeskChannelFold.MachinesLabelOf(Channel, new[] { "devlinux", "north-box", "south-box" }));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void MachinesLabelOf_SeveralMachines_ClaimsNoFailoverOrder(int count)
    {
        // Review finding F1: the channel's list does not decide who reads first, so no sentence may say it does.
        var label = MailDeskChannelFold.MachinesLabelOf(Channel, new[] { "devlinux", "north-box", "south-box" }.Take(count).ToList());

        Assert.DoesNotContain("then", label);
        Assert.DoesNotContain("not answering", label);
    }

    [Fact]
    public void MachinesLabelOf_NoMachine_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => MailDeskChannelFold.MachinesLabelOf(Channel, Array.Empty<string>()));
    }

    // ---------- the tones ----------

    [Theory]
    [InlineData(MailDeskWords.Modes.Observe, MailDeskChannelFold.Tones.Neutral)]
    [InlineData(MailDeskWords.Modes.Live, MailDeskChannelFold.Tones.Attention)]
    public void ModeToneOf_EachMode_HasItsTone(string mode, string tone)
    {
        Assert.Equal(tone, MailDeskChannelFold.ModeToneOf(Channel, mode));
    }

    [Fact]
    public void ModeToneOf_NotAMode_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => MailDeskChannelFold.ModeToneOf(Channel, "paused"));
    }

    [Theory]
    [InlineData(MailDeskChannelFold.SignedInStates.Yes, MailDeskChannelFold.Tones.Good)]
    [InlineData(MailDeskChannelFold.SignedInStates.Failing, MailDeskChannelFold.Tones.Bad)]
    [InlineData(MailDeskChannelFold.SignedInStates.Unknown, MailDeskChannelFold.Tones.Neutral)]
    public void SignedInTone_EachState_HasItsTone(string state, string tone)
    {
        Assert.Equal(tone, new MailDeskSignedIn(state, "any words").Tone);
    }

    [Fact]
    public void SignedInToneOf_NotAState_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => MailDeskChannelFold.SignedInToneOf("maybe"));
    }

    [Fact]
    public void LastCheckTone_NoCheckYet_IsNeutral()
    {
        Assert.Equal(MailDeskChannelFold.Tones.Neutral, new MailDeskLastCheck(null, null, MailDeskChannelFold.NeverCheckedLabel).Tone);
    }

    [Fact]
    public void LastCheckTone_FailedCheck_IsBad()
    {
        var at = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(MailDeskChannelFold.Tones.Bad, new MailDeskLastCheck(at, TriggerRunOutcome.Failed, "Checked: failed").Tone);
    }

    [Theory]
    [InlineData(TriggerRunOutcome.NothingToDo)]
    [InlineData(TriggerRunOutcome.Started)]
    [InlineData(TriggerRunOutcome.Screened)]
    public void LastCheckTone_CheckDidItsWork_IsGood(string outcome)
    {
        var at = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(MailDeskChannelFold.Tones.Good, new MailDeskLastCheck(at, outcome, "Checked").Tone);
    }

    [Theory]
    [InlineData(TriggerRunOutcome.Paused)]
    [InlineData(TriggerRunOutcome.SkippedRunning)]
    public void LastCheckTone_NothingFailedButNothingScreened_IsNeutral(string outcome)
    {
        var at = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(MailDeskChannelFold.Tones.Neutral, new MailDeskLastCheck(at, outcome, "Checked").Tone);
    }

    [Fact]
    public void LastCheckToneOf_AnOutcomeWithNoTone_Throws()
    {
        var at = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

        var thrown = Assert.Throws<InvalidOperationException>(() => MailDeskChannelFold.LastCheckToneOf(at, "an-outcome-added-later"));

        Assert.Contains("'an-outcome-added-later'", thrown.Message);
    }

    [Fact]
    public void LastCheckToneOf_EveryTriggerOutcome_HasATone()
    {
        // Every outcome the trigger contract lists, 'screened' included, is named.
        var at = new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

        Assert.All(TriggerRunOutcome.All, o => Assert.Contains(MailDeskChannelFold.LastCheckToneOf(at, o),
            new[] { MailDeskChannelFold.Tones.Good, MailDeskChannelFold.Tones.Bad, MailDeskChannelFold.Tones.Neutral }));
    }

    // ---------- today ----------

    [Theory]
    [InlineData(0, "No emails decided today")]
    [InlineData(1, "1 email decided today")]
    [InlineData(2, "2 emails decided today")]
    [InlineData(1234, "1234 emails decided today")]
    public void TodayLabel_EachTotal_ReadsAsASentence(int total, string label)
    {
        Assert.Equal(label, new MailDeskChannelToday(total, AllDecisionsAt(0)).Label);
    }

    [Fact]
    public void TodayCounts_SomeDecisions_EveryDecisionInOrderWithZerosAndLabels()
    {
        // Arrange - given out of order, so the order can only come from the decision list
        var byDecision = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [MailDeskWords.Decisions.Failed] = 0,
            [MailDeskWords.Decisions.Archive] = 2,
            [MailDeskWords.Decisions.MailDeskAgent] = 0,
            [MailDeskWords.Decisions.Route] = 1,
            [MailDeskWords.Decisions.KeepForOwner] = 0,
            [MailDeskWords.Decisions.Junk] = 0,
        };

        // Act
        var counts = new MailDeskChannelToday(3, byDecision).Counts;

        // Assert
        Assert.Equal(MailDeskWords.Decisions.All, counts.Select(c => c.Decision));
        Assert.Equal(new[] { 1, 2, 0, 0, 0, 0 }, counts.Select(c => c.Count));
        Assert.Equal(new[] { "Route to a seat", "Archive", "Junk", "Keep for you", "Hand to the Mail Desk agent", "Could not be screened" },
            counts.Select(c => c.Label));
    }

    [Fact]
    public void DecisionLabels_EveryDecision_HasALabel()
    {
        Assert.Equal(MailDeskWords.Decisions.All.OrderBy(d => d), MailDeskChannelFold.DecisionLabels.Keys.OrderBy(d => d));
    }

    [Fact]
    public void TodayCounts_ADecisionMissingFromTheCounts_Throws()
    {
        var byDecision = new Dictionary<string, int>(StringComparer.Ordinal) { [MailDeskWords.Decisions.Route] = 1 };

        Assert.Throws<InvalidOperationException>(() => MailDeskChannelFold.CountsOf(byDecision));
    }

    private static IReadOnlyDictionary<string, int> AllDecisionsAt(int count)
        => MailDeskWords.Decisions.All.ToDictionary(d => d, _ => count, StringComparer.Ordinal);
}
