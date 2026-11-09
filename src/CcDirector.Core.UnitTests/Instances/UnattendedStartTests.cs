using CcDirector.Core.Instances;
using Xunit;

namespace CcDirector.Core.UnitTests.Instances;

/// <summary>
/// A START NEVER WAITS ON A WINDOW FOR EVER (issue #4552). devlinux booted with nobody at its screen on
/// 1 November 2026, found two instances registered, and stopped on the Select director window - alive,
/// behind a window, never reaching the Gateway, so every scheduled run on that machine failed saying there
/// was no Director there. The screen was 640x480 with no monitor, so the buttons were off it too.
///
/// There is no clock, no window and no wait in any of these: both sides of the race are tasks the test
/// completes itself, which is the whole reason the rule was extracted from the dialog call.
/// </summary>
public class UnattendedStartTests
{
    [Fact]
    public async Task AnAnsweredPicker_GivesThatAnswer()
    {
        var picked = new TaskCompletionSource<bool?>();
        var budget = new TaskCompletionSource();

        picked.SetResult(true);
        var (answered, value) = await UnattendedStart.AnswerOrNobodyThere(picked.Task, budget.Task);

        Assert.True(answered);
        Assert.True(value);
    }

    [Fact]
    public async Task ACancelledPicker_IsStillAnAnswer_AndNotNobodyBeingThere()
    {
        // Cancelling is a person saying "the default, thank you". It must not be reported as an
        // unattended start, because the two go on to write different lines in the log.
        var picked = new TaskCompletionSource<bool?>();
        var budget = new TaskCompletionSource();

        picked.SetResult(null);
        var (answered, value) = await UnattendedStart.AnswerOrNobodyThere(picked.Task, budget.Task);

        Assert.True(answered);
        Assert.Null(value);
    }

    [Fact]
    public async Task ABudgetThatFinishesFirst_SaysNobodyWasThere()
    {
        var picked = new TaskCompletionSource<bool?>();   // never completed: nobody is at the screen
        var budget = new TaskCompletionSource();

        budget.SetResult();
        var (answered, value) = await UnattendedStart.AnswerOrNobodyThere(picked.Task, budget.Task);

        Assert.False(answered);
        Assert.Null(value);
    }

    [Fact]
    public async Task AnAnswerAlreadyGiven_IsNotLostToABudgetThatIsAlsoDone()
    {
        // Both sides complete before the race is even entered, which is the case a person answering in
        // the last moment of the budget produces. The answer wins: giving up is only for no answer.
        var picked = new TaskCompletionSource<bool?>();
        var budget = new TaskCompletionSource();

        picked.SetResult(true);
        budget.SetResult();
        var (answered, value) = await UnattendedStart.AnswerOrNobodyThere(picked.Task, budget.Task);

        Assert.True(answered);
        Assert.True(value);
    }

    [Fact]
    public async Task APickerThatThrows_ThrowsHere_RatherThanLookingLikeNobodyWasThere()
    {
        // The caller catches it and goes on as the default, with the failure in the log under its own
        // name. Swallowing it here would file a real dialog failure as an unattended start.
        var picked = new TaskCompletionSource<bool?>();
        var budget = new TaskCompletionSource();

        picked.SetException(new InvalidOperationException("the window could not be shown"));

        var threw = await Assert.ThrowsAsync<InvalidOperationException>(
            () => UnattendedStart.AnswerOrNobodyThere(picked.Task, budget.Task));
        Assert.Equal("the window could not be shown", threw.Message);
    }

    [Fact]
    public void ThePickerBudget_IsLongEnoughForAPerson_AndShorterThanAMachinesNight()
    {
        // The number itself is a judgment, but its two ends are not: under about half a minute rushes
        // somebody reading their own instance names, and anything long enough to lose a night of
        // scheduled runs is the defect this exists to stop.
        Assert.True(UnattendedStart.PickerBudget >= TimeSpan.FromSeconds(30),
            $"{UnattendedStart.PickerBudget} rushes a person who is actually there");
        Assert.True(UnattendedStart.PickerBudget <= TimeSpan.FromMinutes(5),
            $"{UnattendedStart.PickerBudget} is long enough to lose a machine's morning");
    }
}
