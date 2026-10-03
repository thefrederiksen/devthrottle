using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// An agent often names a file before it writes it. The path is checked, found missing, and
/// shown as plain text - and once the file exists it must become a link without the user
/// switching sessions. The "missing" answer used to stand until the next attach.
/// </summary>
public sealed class TerminalPathLinkRecheckTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "cc-link-recheck-" + Guid.NewGuid().ToString("N"));

    public TerminalPathLinkRecheckTests() => Directory.CreateDirectory(Path.Combine(_repo, "docs"));

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private TerminalControl NewTerminal()
    {
        var terminal = new TerminalControl();
        var window = new Window { Width = 800, Height = 400, Content = terminal };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        terminal.HarnessSetGrid(40, 5);
        terminal.HarnessRepoPath = _repo;
        return terminal;
    }

    /// <summary>Run detection, then give the background existence checks time to finish.</summary>
    private static int CountPathLinksAfterChecks(TerminalControl terminal)
    {
        terminal.HarnessCountPathLinks();
        Thread.Sleep(250);
        return terminal.HarnessCountPathLinks();
    }

    [AvaloniaFact]
    public void PathNamedBeforeItExists_BecomesALinkOnceWritten()
    {
        var terminal = NewTerminal();
        terminal.HarnessPathMissRecheckMs = 100;
        terminal.HarnessRebuild(Encoding.UTF8.GetBytes("plan goes in docs/plan.md soon\r\n"));

        Assert.Equal(0, CountPathLinksAfterChecks(terminal));

        File.WriteAllText(Path.Combine(_repo, "docs", "plan.md"), "x");
        Thread.Sleep(400);   // well past the miss's expiry

        Assert.Equal(1, CountPathLinksAfterChecks(terminal));
    }

    [AvaloniaFact]
    public void PathStillMissing_StaysPlainText()
    {
        // Negative control: expiring the answer must not turn a missing path into a link.
        var terminal = NewTerminal();
        terminal.HarnessPathMissRecheckMs = 100;
        terminal.HarnessRebuild(Encoding.UTF8.GetBytes("plan goes in docs/plan.md soon\r\n"));

        Assert.Equal(0, CountPathLinksAfterChecks(terminal));
        Thread.Sleep(400);
        Assert.Equal(0, CountPathLinksAfterChecks(terminal));
    }
}
