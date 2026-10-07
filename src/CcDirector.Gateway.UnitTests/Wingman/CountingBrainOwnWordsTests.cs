using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE COUNTING BRAIN'S NARRATION WORDS ARE ITS OWN (issue #3630). It used to answer the narration call from the shared
/// static the canned judge answers write, so a reading whose judge was never asked narrated whatever an earlier test had
/// left - empty when run alone, which made OwnedSessionsAreNotReadTests count two narration calls where it expected one.
/// </summary>
public sealed class CountingBrainOwnWordsTests
{
    private static readonly string NarrationPrompt = "Write the narration. " + NarrationCall.LabelTag + " <label>";

    [Fact]
    public async Task Narration_IsTheBrainsOwnWords_WhateverAnotherCannedAnswerWroteLast()
    {
        var brain = new CountingBrain(() => "done", spoken: "My own words.");
        // A sibling test's canned answer writes the shared static with other words, and then empties it.
        FakeTurnVerdictEnvironment.Finished("evidence", "Somebody else's words.");
        var afterOther = await brain.AskAsync(NarrationPrompt);
        FakeTurnVerdictEnvironment.RefusedButReadableMenu("nothing");
        var afterEmpty = await brain.AskAsync(NarrationPrompt);

        Assert.Equal(FakeTurnVerdictEnvironment.NarratedAnswer("My own words."), afterOther.Text);
        Assert.Equal(afterOther.Text, afterEmpty.Text);
        Assert.Equal(2, brain.Narrations);
        Assert.Equal(0, brain.Asks);
    }

    [Fact]
    public async Task Narration_WithNoWordsGiven_IsNeverEmpty()
    {
        var brain = new CountingBrain(() => "done");
        FakeTurnVerdictEnvironment.RefusedButReadableMenu("nothing");   // the shared static is now empty

        var answer = await brain.AskAsync(NarrationPrompt);

        Assert.Equal(FakeTurnVerdictEnvironment.NarratedAnswer(CountingBrain.DefaultSpoken), answer.Text);
    }
}
