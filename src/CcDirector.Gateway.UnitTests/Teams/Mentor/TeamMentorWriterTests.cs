using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Teams.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>
/// The Mentor's weekly writer (devthrottle_internal#2305) over a real migrated database and a real prompt log, with a
/// FAKE model. The issue's tests 2 and 4 are the first facts; the rest pin every rule the writer keeps.
/// </summary>
public sealed class TeamMentorWriterTests : IDisposable
{
    private readonly MentorRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ---- devthrottle_internal#2305, test 2: every quoted prompt is the person's own, from that week ------------------

    [Fact]
    public async Task WriteWeekAsync_AnAcceptedAnswer_QuotesOnlyThePersonsOwnPromptsOfThatWeek_CopiedVerbatimByTheGateway()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        var first = _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 9), "fix the signup thing so it doesnt break on mobile");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 10), "no, the OTHER signup thing");
        _rig.Brain.Answer = _ => FakeBrain.HardWeek("P1");

        var run = await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Equal(1, run.BlocksWritten);
        var block = Assert.Single(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
        Assert.Equal(MentorRig.Rob, block.PersonSubject);
        var quote = Assert.Single(block.Quotes);
        Assert.Equal(first.PromptId, quote.PromptId);
        Assert.Equal("fix the signup thing so it doesnt break on mobile", quote.Text);
        Assert.Equal(first.TsUtc, quote.AtUtc);
    }

    [Fact]
    public async Task WriteWeekAsync_AnswerNamingAnotherPersonsPromptId_IsRefused_AndWritesNoBlock()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(1), "s-dana");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "rob's prompt");
        var danas = _rig.PromptOf(MentorRig.Dana, MentorRig.InWeek(2), "dana's private prompt");
        // The model, asked about Rob, names Dana's prompt by its real id.
        _rig.Brain.Answer = prompt => prompt.Contains("rob's prompt", StringComparison.Ordinal)
            ? FakeBrain.HardWeek(danas.PromptId!)
            : FakeBrain.GoodWeek();

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        var blocks = _rig.Store.Blocks(_rig.Team, MentorRig.Week);
        Assert.DoesNotContain(blocks, b => b.PersonSubject == MentorRig.Rob);
        Assert.DoesNotContain(blocks.SelectMany(b => b.Quotes), q => q.Text == "dana's private prompt");
        var outcome = _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!;
        Assert.Equal(MentorOutcomes.Refused, outcome.Outcome);
        Assert.Contains("not one of this person's prompts of this week", outcome.Reason);
        // And the model was never shown Dana's prompt while writing about Rob.
        Assert.DoesNotContain(_rig.Brain.Asked.Where(a => a.Contains("rob's prompt", StringComparison.Ordinal)),
            a => a.Contains("dana's private prompt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteWeekAsync_APromptFromAnotherWeek_IsNeverShown_AndCannotBeQuoted()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "this week's prompt");
        var lastWeek = _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(-3), "last week's prompt");
        var nextWeek = _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(8), "next week's prompt");
        _rig.Brain.Answer = _ => FakeBrain.HardWeek("P2");   // only one prompt of this week exists, so P2 is not one

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        var asked = Assert.Single(_rig.Brain.Asked);
        Assert.Contains("this week's prompt", asked);
        Assert.DoesNotContain(lastWeek.Text, asked);
        Assert.DoesNotContain(nextWeek.Text, asked);
        Assert.Empty(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
        Assert.Equal(MentorOutcomes.Refused, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!.Outcome);
    }

    // ---- devthrottle_internal#2305, test 4: no sessions, no block, no model call -------------------------------------

    [Fact]
    public async Task WriteWeekAsync_APersonWithNoSessionsThatWeek_GetsNoBlock_AndNoModelCall()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "rob's prompt");
        // Dana has a prompt in the log but ran no session this week: a session last week only.
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(-3), "s-dana-old");
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.DoesNotContain(_rig.Store.Blocks(_rig.Team, MentorRig.Week), b => b.PersonSubject == MentorRig.Dana);
        Assert.Equal(MentorOutcomes.NoSessions, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Dana)!.Outcome);
        // One model call, for Rob, and none for anyone else.
        var asked = Assert.Single(_rig.Brain.Asked);
        Assert.Contains("rob's prompt", asked);
    }

    [Fact]
    public async Task WriteWeekAsync_NobodyRanSessions_WritesNoBlockAtAll_AndCallsNoModel()
    {
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();

        var run = await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Equal(0, run.BlocksWritten);
        Assert.Empty(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
        Assert.Empty(_rig.Brain.Asked);
        Assert.Equal(5, run.Outcomes.Count);
        Assert.All(run.Outcomes.Where(o => o.PersonSubject != MentorRig.Collaborator), o => Assert.Equal(MentorOutcomes.NoSessions, o.Outcome));
        Assert.Equal(MentorOutcomes.MayNotRead, Assert.Single(run.Outcomes, o => o.PersonSubject == MentorRig.Collaborator).Outcome);
        Assert.True(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task WriteWeekAsync_SessionsButNoPrompts_GetsNoBlock_AndNoModelCall()
    {
        // The Mentor has nothing to read: a person who ran sessions but sent no prompt the Gateway holds.
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "only the agent spoke", role: "assistant");

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Empty(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
        Assert.Empty(_rig.Brain.Asked);
        Assert.Equal(MentorOutcomes.NoPrompts, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!.Outcome);
    }

    [Fact]
    public async Task WriteWeekAsync_ASessionInAnotherTenant_DoesNotCountAsRunningSessionsInTheTeam()
    {
        var personal = _rig.Tenants.LookupBySubject(MentorRig.Rob)!.Value;
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-personal", personal);
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a prompt");

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Empty(_rig.Brain.Asked);
        Assert.Equal(MentorOutcomes.NoSessions, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!.Outcome);
    }

    // ---- refusals and failures write no block, and are recorded -------------------------------------------------------

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"tone\":\"great\",\"workedOn\":\"x\",\"howItWent\":\"y\",\"wentBadlyAndWhy\":null,\"quotes\":[],\"oneThingToTry\":\"z\"}")]
    [InlineData("{\"tone\":\"good\",\"workedOn\":\"x\",\"howItWent\":\"y\",\"wentBadlyAndWhy\":null,\"quotes\":[],\"oneThingToTry\":\"z\",\"score\":7}")]
    public async Task WriteWeekAsync_AnAnswerNotTheShapeAskedFor_IsRefusedWithItsReason_AndWritesNoBlock(string answer)
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a prompt");
        _rig.Brain.Answer = _ => answer;

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Empty(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
        var outcome = _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!;
        Assert.Equal(MentorOutcomes.Refused, outcome.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
        Assert.Single(_rig.Brain.Asked);   // no retry
    }

    [Fact]
    public async Task WriteWeekAsync_AnAnswerCarryingModelText_IsRefused_AndNeitherTheRowNorTheLogHoldsThatText()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a prompt");
        _rig.Brain.Answer = _ => "{\"the person typed fix the login page please\": 1}";

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        var outcome = _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!;
        Assert.Equal(MentorOutcomes.Refused, outcome.Outcome);
        Assert.DoesNotContain("login", outcome.Reason);
    }

    [Fact]
    public async Task WriteWeekAsync_TheModelCannotBeReached_RecordsNothing_LeavesTheWeekUnmarked_AndALaterRunWritesIt()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a prompt");
        _rig.Brain.Answer = _ => throw new HttpRequestException("the model host is down");

        var first = await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Equal(1, first.Unfinished);
        Assert.Null(_rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob));
        Assert.False(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
        Assert.Empty(_rig.Store.Blocks(_rig.Team, MentorRig.Week));

        // The model is back on a later tick, still before the following week closes.
        _rig.Now = _rig.Now.AddHours(1);
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();
        var second = await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Equal(0, second.Unfinished);
        Assert.Equal(MentorOutcomes.Written, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!.Outcome);
        Assert.True(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
        // Rob was asked twice; nobody else was asked at all.
        Assert.Equal(2, _rig.Brain.Asked.Count);
    }

    [Fact]
    public async Task WriteWeekAsync_TheModelStillFailsAfterTheFollowingWeekClosed_RecordsModelFailedForGood_WithTheExceptionTypeOnly()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a prompt");
        _rig.Brain.Answer = _ => throw new HttpRequestException("upstream said: a prompt");
        _rig.Now = MentorRig.Week.Next.UtcBounds(MentorRig.Zone).ToUtc;

        var run = await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Equal(0, run.Unfinished);
        Assert.Empty(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
        var outcome = _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!;
        Assert.Equal(MentorOutcomes.ModelFailed, outcome.Outcome);
        Assert.Contains(nameof(HttpRequestException), outcome.Reason);
        Assert.DoesNotContain("upstream said", outcome.Reason);
        Assert.True(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task WriteWeekAsync_NoModelCanBeResolved_Throws_BeforeAnyMemberIsVisited()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a prompt");
        var writer = _rig.Writer((_, _) => throw new InvalidOperationException("no key for the hosted model"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone));

        Assert.Empty(_rig.Store.Outcomes(_rig.Team, MentorRig.Week));
        Assert.False(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
    }

    // ---- whose prompts, and about whom (review G8, G9) ------------------------------------------------------------------

    [Fact]
    public async Task WriteWeekAsync_AUserMessageThePersonDidNotTypeOrSpeak_IsNotRead()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        // A message another session put into Rob's session: role "user", but tied to no submission of his.
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a message from another session", modality: null);

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Empty(_rig.Brain.Asked);
        Assert.Equal(MentorOutcomes.NoPrompts, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!.Outcome);
    }

    [Fact]
    public async Task WriteWeekAsync_TypedAndSpokenPrompts_AreBothRead_AndNothingElse()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "typed by rob", modality: "typed");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 11), "spoken by rob", modality: "voice");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1, 12), "a doorbell line", modality: null);
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        var asked = Assert.Single(_rig.Brain.Asked);
        Assert.Contains("typed by rob", asked);
        Assert.Contains("spoken by rob", asked);
        Assert.DoesNotContain("a doorbell line", asked);
    }

    [Fact]
    public async Task WriteWeekAsync_AMemberWhoMayNotReadTheirOwnPage_GetsNoBlock_AndNoModelCall()
    {
        // A Collaborator who ran sessions and typed prompts - for example a Developer moved to Collaborator before the run.
        _rig.SessionOf(MentorRig.Collaborator, MentorRig.InWeek(1), "s-collab");
        _rig.PromptOf(MentorRig.Collaborator, MentorRig.InWeek(1), "collaborator's prompt");
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Empty(_rig.Brain.Asked);
        Assert.Empty(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
        Assert.Equal(MentorOutcomes.MayNotRead, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Collaborator)!.Outcome);
    }

    // ---- what the model is given ----------------------------------------------------------------------------------------

    [Fact]
    public async Task WriteWeekAsync_TheModelIsGivenOnlyThatPersonsOwnPrompts_AndNoNameOrAccount()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(1), "s-dana");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "rob writes this");
        _rig.PromptOf(MentorRig.Dana, MentorRig.InWeek(1), "dana writes this");
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.Equal(2, _rig.Brain.Asked.Count);
        var aboutRob = Assert.Single(_rig.Brain.Asked, a => a.Contains("rob writes this", StringComparison.Ordinal));
        Assert.DoesNotContain("dana writes this", aboutRob);
        var aboutDana = Assert.Single(_rig.Brain.Asked, a => a.Contains("dana writes this", StringComparison.Ordinal));
        Assert.DoesNotContain("rob writes this", aboutDana);
        // The model is never told anyone's email or account: the block never names a person.
        Assert.All(_rig.Brain.Asked, a => Assert.DoesNotContain("@example.com", a));
        Assert.All(_rig.Brain.Asked, a => Assert.DoesNotContain("sub-", a));
        Assert.Equal(2, _rig.Store.Blocks(_rig.Team, MentorRig.Week).Count);
    }

    // ---- once per week ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WriteWeekAsync_AWeekAlreadyRun_IsNotWrittenAgain()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "a prompt");
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();
        var writer = _rig.Writer();
        await writer.WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        var again = await writer.WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        Assert.True(again.AlreadyRan);
        Assert.Single(_rig.Brain.Asked);
        Assert.Single(_rig.Store.Blocks(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task WriteWeekAsync_ARunCutShort_FinishesWithoutWritingAnyoneTwice()
    {
        _rig.SessionOf(MentorRig.Rob, MentorRig.InWeek(1), "s-rob");
        _rig.SessionOf(MentorRig.Dana, MentorRig.InWeek(1), "s-dana");
        _rig.PromptOf(MentorRig.Rob, MentorRig.InWeek(1), "rob");
        _rig.PromptOf(MentorRig.Dana, MentorRig.InWeek(1), "dana");
        // As if a previous run wrote Rob's block and stopped before marking the week.
        _rig.Store.SaveBlock(_rig.Team, new MentorBlock(MentorRig.Week.ToString(), MentorRig.Rob, "good", "x", "y", null,
            Array.Empty<MentorQuote>(), "z", _rig.Now, "fake-model"));
        _rig.Brain.Answer = _ => FakeBrain.GoodWeek();

        await _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone);

        var asked = Assert.Single(_rig.Brain.Asked);
        Assert.Contains("UTC: dana", asked);
        Assert.DoesNotContain("UTC: rob", asked);
        Assert.Equal(2, _rig.Store.Blocks(_rig.Team, MentorRig.Week).Count);
        Assert.True(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task WriteWeekAsync_AWeekNotYetClosed_Throws()
    {
        _rig.Now = MentorRig.InWeek(6);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _rig.Writer().WriteWeekAsync(_rig.Team, MentorRig.Week, MentorRig.Zone));
        Assert.False(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
    }

    [Fact]
    public async Task WriteWeekAsync_APersonalTenant_Throws_BecauseTheMentorWritesOnlyForTeams()
    {
        var personal = _rig.Tenants.LookupBySubject(MentorRig.Rob)!.Value;

        await Assert.ThrowsAsync<ArgumentException>(() => _rig.Writer().WriteWeekAsync(personal, MentorRig.Week, MentorRig.Zone));
    }
}
