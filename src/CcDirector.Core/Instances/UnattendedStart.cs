namespace CcDirector.Core.Instances;

/// <summary>
/// A START NEVER WAITS ON A WINDOW FOR EVER (issue #4552).
///
/// On 1 November 2026 devlinux booted with nobody at its screen. The Director was started without
/// <c>--instance</c>, found two instances registered - the default and a small test one - and stopped on the
/// Select director window to ask which. Nothing was ever going to answer. The process looked alive the whole
/// time while the Director behind the window never reached the Gateway, so every scheduled run on that
/// machine failed with "no Director on devlinux and the launcher could not start one". The screen was
/// 640x480 with no monitor attached, so the window's buttons were off it as well: even somebody walking up
/// to the machine could not have answered.
///
/// THE REAL FIX IS UPSTREAM, AND IT IS NOT THIS. The launcher knows exactly which instance it supervises and
/// now says so on the command line (<see cref="CcDirector.Core.Instances.InstanceSelection"/> and the
/// launcher's <c>DirectorSupervisor</c>), so an automatic start never reaches the question at all. This is
/// the backstop for every start that still can: a login item from an older install, a launcher that predates
/// that change starting a newer Director, a person who opens the application and walks away.
///
/// A BACKSTOP THAT GIVES UP IS NOT A GUESS. Going on as the DEFAULT instance is the same thing the picker
/// does when it is cancelled, and it was the behaviour for every machine that has only one instance - which
/// is nearly all of them. Starting the wrong instance would be a guess; starting the one the machine has
/// always started is not.
/// </summary>
public static class UnattendedStart
{
    /// <summary>
    /// How long the instance picker is given an answer before the start goes on as the default instance.
    ///
    /// A minute is far longer than a person at the screen needs - they are looking at the window they just
    /// caused - and it is the difference between a machine losing a minute of its morning and losing the
    /// whole night, which is what #4552 cost. It is deliberately not shorter: somebody reading the instance
    /// names to tell two of their own Directors apart is doing the thing the window is for.
    /// </summary>
    public static readonly TimeSpan PickerBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The answer <paramref name="asked"/> gave, or <c>Answered: false</c> when <paramref name="budget"/>
    /// finished first and nobody was there.
    ///
    /// Both sides are tasks the caller owns, so the rule this encodes - a start waits for an answer, but not
    /// for ever - is testable with no clock, no window and no wait: a test hands it two
    /// <see cref="TaskCompletionSource"/>s and completes whichever side it is making a point about.
    /// </summary>
    public static async Task<(bool Answered, T Value)> AnswerOrNobodyThere<T>(Task<T> asked, Task budget)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(budget);

        var first = await Task.WhenAny(asked, budget).ConfigureAwait(false);
        if (!ReferenceEquals(first, asked))
            return (false, default!);

        return (true, await asked.ConfigureAwait(false));
    }
}
