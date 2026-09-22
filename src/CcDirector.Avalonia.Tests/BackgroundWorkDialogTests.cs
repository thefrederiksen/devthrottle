using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using CcDirector.Avalonia;
using CcDirector.Core.Background;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Background work page shows one row per job on the scheduler with its tier, cadence, last
/// run and runs against its ceiling, marks an over-ceiling job, and is itself a job on the
/// scheduler while it is open: registered on open, gone on close.
/// </summary>
public sealed class BackgroundWorkDialogTests
{
    private static List<string> Texts(Control control) =>
        control.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public async Task ARow_PerJob_WithItsTierCadenceAndRunsAgainstTheCeiling()
    {
        var jobs = new BackgroundJobs();
        var slow = jobs.Register(new BackgroundJobSpec("Backup cleaner", BackgroundJobTier.SlowAndSteady, TimeSpan.FromSeconds(45), "disposed"), _ => Task.CompletedTask);
        jobs.Register(new BackgroundJobSpec("Repository recompute", BackgroundJobTier.OnChange, null, "watcher disposed"), _ => Task.CompletedTask);
        slow.Trigger();
        await Task.Delay(200);

        var dialog = new BackgroundWorkDialog(jobs);
        dialog.Render(jobs.Snapshot());
        var texts = Texts(dialog);

        Assert.Contains("Backup cleaner", texts);
        Assert.Contains("slow and steady", texts);
        Assert.Contains("every 45 s", texts);
        Assert.Contains("1 of 80 allowed", texts);
        Assert.Contains("Repository recompute", texts);
        Assert.Contains("on change", texts);
        Assert.Contains("just now", texts);
    }

    [AvaloniaFact]
    public void AJobOverItsCeiling_SaysSo()
    {
        var jobs = new BackgroundJobs();
        var dialog = new BackgroundWorkDialog(jobs);
        var over = new BackgroundJobSnapshot("Runaway", BackgroundJobTier.SlowAndSteady, TimeSpan.FromMinutes(1), 60,
            DateTime.UtcNow, TimeSpan.FromMilliseconds(5), RunsInLastHour: 300, SkippedOff: 0, SkippedTooSoon: 0, Failures: 0, Running: false);

        dialog.Render(new[] { over });

        Assert.Contains("300 of 60 allowed - OVER", Texts(dialog));
    }

    [AvaloniaFact]
    public void ThePage_IsAJobOnTheSchedulerWhileOpen_AndGoneWhenClosed()
    {
        var jobs = new BackgroundJobs();
        var dialog = new BackgroundWorkDialog(jobs);

        dialog.Show();
        var row = Assert.Single(jobs.Snapshot());
        Assert.Equal("Background work page refresh", row.Name);
        Assert.Equal(BackgroundJobTier.OnView, row.Tier);
        Assert.Equal(TimeSpan.FromSeconds(2), row.Cadence);

        dialog.Close();
        Assert.Empty(jobs.Snapshot());
    }

    [AvaloniaFact]
    public void AnEmptyScheduler_SaysSoInWords()
    {
        var dialog = new BackgroundWorkDialog(new BackgroundJobs());
        dialog.Render(Array.Empty<BackgroundJobSnapshot>());
        Assert.Contains("No job has registered with the scheduler yet.", Texts(dialog));
    }
}
