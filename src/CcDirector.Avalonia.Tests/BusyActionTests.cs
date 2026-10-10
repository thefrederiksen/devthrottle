using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issues #1105 and #1107 - the shared "run this asynchronous work with a busy control" helper.
///
/// The nine sites in #1107 broke one of two rules each, and the tests here are one per rule plus the
/// failure modes that made those sites hard to spot:
///
///   - FEEDBACK BEFORE THE AWAIT, not after it. The Cockpit button ran an eight-second probe with no
///     state change at all.
///   - A REAL GUARD. Changing a label is not a guard (Compact); disabling after the network call returns
///     is not a guard (the workflow recorder).
///
/// The restore-on-failure test is the one that would otherwise bite quietly: a failure that left the
/// button disabled would turn one bad network moment into a control that stays dead until restart, which
/// is a worse bug than the one being fixed.
/// </summary>
public class BusyActionTests
{
    [AvaloniaFact]
    public async Task TheButtonIsAlreadyBusyBeforeTheWorkStarts()
    {
        var button = new Button { Content = "Cockpit", IsEnabled = true };

        bool enabledWhenWorkBegan = true;
        object? labelWhenWorkBegan = null;

        await BusyAction.RunAsync(button, "test", "run the test action", () =>
        {
            // Sampled INSIDE the work: this is the state the user is looking at for however long the work
            // takes. The Cockpit button spent eight seconds here looking completely untouched.
            enabledWhenWorkBegan = button.IsEnabled;
            labelWhenWorkBegan = button.Content;
            return Task.CompletedTask;
        }, "Opening...");

        Assert.False(enabledWhenWorkBegan);
        Assert.Equal("Opening...", labelWhenWorkBegan);
    }

    [AvaloniaFact]
    public async Task AControlIsRestoredAfterTheWork()
    {
        var button = new Button { Content = "Cockpit" };

        await BusyAction.RunAsync(button, "test", "run the test action", () => Task.CompletedTask, "Opening...");

        Assert.True(button.IsEnabled);
        Assert.Equal("Cockpit", button.Content);
    }

    [AvaloniaFact]
    public async Task AFailureStillRestoresTheControl()
    {
        var button = new Button { Content = "Cockpit" };
        var shown = new List<string>();

        var ok = await BusyAction.RunAsync(button, "test", "run the test action",
            () => throw new InvalidOperationException("gateway unreachable"),
            "Opening...",
            onFailure: shown.Add);

        // A failure that left the button disabled would be a worse bug than the one being fixed: one bad
        // network moment would produce a dead control until the application restarts.
        Assert.False(ok);
        Assert.True(button.IsEnabled);
        Assert.Equal("Cockpit", button.Content);
    }

    [AvaloniaFact]
    public async Task FiveClicksProduceOneRunAndOneFailure()
    {
        // The #1105 screenshot, reproduced: five clicks on an unreachable gateway produced FIVE stacked
        // "Cannot Open Cockpit" windows. One click in flight, one operation, at most one report.
        var button = new Button { Content = "Cockpit" };
        var started = 0;
        var shown = new List<string>();
        var release = new TaskCompletionSource();

        var clicks = new List<Task<bool>>();
        for (var i = 0; i < 5; i++)
        {
            clicks.Add(BusyAction.RunAsync(button, "test", "run the test action", async () =>
            {
                started++;
                await release.Task;
                throw new InvalidOperationException("gateway unreachable");
            }, "Opening...", onFailure: shown.Add));
        }

        release.SetResult();
        var results = await Task.WhenAll(clicks);

        Assert.Equal(1, started);
        Assert.Single(shown);
        Assert.DoesNotContain(true, results);   // the one that ran, failed; the other four never started
    }

    [AvaloniaFact]
    public async Task AFailureIsSurfacedToTheScreenAndNotOnlyToTheLog()
    {
        // Items 1, 2 and 5 of #1107 all failed to FileLog with nothing on screen. A button that fails
        // invisibly is indistinguishable from a button that does nothing.
        var button = new Button { Content = "Record" };
        var shown = new List<string>();

        await BusyAction.RunAsync(button, "test", "run the test action",
            () => throw new InvalidOperationException("the browser daemon refused to start recording"),
            "Starting...",
            onFailure: shown.Add);

        Assert.Single(shown);
        Assert.Contains("refused to start recording", shown[0]);
    }

    [AvaloniaFact]
    public async Task AnIconButtonKeepsItsContentWhenNoBusyLabelIsGiven()
    {
        // Not every control can take a text swap without deforming. Passing no label must still disable
        // and still guard - the guard is the part that is never optional.
        var button = new Button { Content = "X" };
        object? labelWhenWorkBegan = null;
        var enabledWhenWorkBegan = true;

        await BusyAction.RunAsync(button, "test", "run the test action", () =>
        {
            labelWhenWorkBegan = button.Content;
            enabledWhenWorkBegan = button.IsEnabled;
            return Task.CompletedTask;
        });

        Assert.Equal("X", labelWhenWorkBegan);
        Assert.False(enabledWhenWorkBegan);
        Assert.Equal("X", button.Content);
    }

    [AvaloniaFact]
    public async Task AControlThatWasAlreadyDisabledIsNotEnabledByRunningWork()
    {
        // Restoring to "enabled" unconditionally would quietly override a rule that had deliberately
        // disabled the control - the helper would then be handing the user a button someone else decided
        // they should not have.
        var button = new Button { Content = "Run", IsEnabled = false };

        await BusyAction.RunAsync(button, "test", "run the test action", () => Task.CompletedTask, "Running...");

        Assert.False(button.IsEnabled);
        Assert.Equal("Run", button.Content);
    }

    [AvaloniaFact]
    public async Task ABusyLabelIsClearedEvenWhenTheControlStartedWithNoContent()
    {
        // The restore used to be skipped when the original content was null, which would leave the busy
        // label welded on for the rest of the session.
        var button = new Button();

        await BusyAction.RunAsync(button, "test", "run the test action", () => Task.CompletedTask, "Working...");

        Assert.Null(button.Content);
    }

    [AvaloniaFact]
    public async Task TheGuardIsReleasedSoTheButtonWorksAgainNextTime()
    {
        // The guard must be per-RUN, not permanent. A helper that blocked the second legitimate click
        // would read as an even more broken button than the one it replaced.
        var button = new Button { Content = "Run" };
        var runs = 0;

        await BusyAction.RunAsync(button, "test", "run the test action", () => { runs++; return Task.CompletedTask; });
        await BusyAction.RunAsync(button, "test", "run the test action", () => { runs++; return Task.CompletedTask; });

        Assert.Equal(2, runs);
        Assert.False(BusyAction.IsRunning(button));
    }
}
